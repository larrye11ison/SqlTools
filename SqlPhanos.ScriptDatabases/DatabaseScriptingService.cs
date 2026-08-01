using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;

namespace SqlPhanos.ScriptDatabases;

public sealed class DatabaseScriptingService : IDatabaseScriptingService
{
    public async Task<ScriptDatabaseResult> ScriptDatabaseAsync(ScriptDatabaseRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();

        LoadedDatabaseContext loaded = await Task.Run(
            () => LoadDatabaseContext(request),
            request.CancellationToken);

        try
        {
            string sanitisedServer = FileNameUtil.SanitiseFileName(loaded.Catalogue.ActualServerName);
            string sanitisedDatabase = FileNameUtil.SanitiseFileName(loaded.Catalogue.ActualDatabaseName);
            string serverOutputDirectory = OutputPathCaseCorrector.EnsureChildFolderCase(
                request.BaseOutputDirectory,
                sanitisedServer);
            string outputDirectory = OutputPathCaseCorrector.EnsureChildFolderCase(
                serverOutputDirectory,
                sanitisedDatabase);

            request.OnOutputDirectoryResolved(outputDirectory);

            Directory.CreateDirectory(outputDirectory);
            request.CancellationToken.ThrowIfCancellationRequested();

            List<ObjectWorkItem> workItems = BuildWorkItems(loaded.Objects, outputDirectory);

            // Release the catalogue SMO session before scripting so DAC decrypt is not blocked
            // by an existing open user-database connection (SQL error 924).
            loaded.Server.ConnectionContext.Disconnect();

            var session = new ScriptingSession(request, loaded.Catalogue);
            await Task.Run(
                () => ScriptWorkItems(session, workItems),
                request.CancellationToken);

            return new ScriptDatabaseResult(
                outputDirectory,
                serverOutputDirectory,
                session.NormalizedNameCounter.Count);
        }
        finally
        {
            loaded.Server.ConnectionContext.Disconnect();
        }
    }

    private static LoadedDatabaseContext LoadDatabaseContext(ScriptDatabaseRequest request)
    {
        Server server = CreateServer(request.ConnectionString);
        Database database = server.Databases[request.DatabaseName]
            ?? throw new InvalidOperationException(
                $"Database '{request.DatabaseName}' was not found.");

        DataTable objects = database.EnumObjects(SqlObjectFilters.GetScriptedObjectTypes());
        ObjectCatalogue catalogue = ObjectCatalogue.LoadFromDatabase(database);
        return new LoadedDatabaseContext(server, objects, catalogue);
    }

    /// <summary>
    /// Builds the SMO server connection from the app's own ADO.NET connection string, supporting
    /// both Windows and SQL auth - mirrors SqlSearchService.ScriptObjectAsync's exact
    /// decomposition rather than assuming integrated security.
    /// </summary>
    private static Server CreateServer(string connectionString)
    {
        SqlConnectionStringBuilder builder = new(connectionString);
        SqlConnectionInfo connectionInfo = new()
        {
            ServerName = builder.DataSource,
            UseIntegratedSecurity = builder.IntegratedSecurity,
            ConnectionTimeout = builder.ConnectTimeout,
            TrustServerCertificate = builder.TrustServerCertificate
        };

        if (!builder.IntegratedSecurity)
        {
            connectionInfo.UserName = builder.UserID;
            connectionInfo.Password = builder.Password;
        }

        return new Server(new ServerConnection(connectionInfo));
    }

    private static List<ObjectWorkItem> BuildWorkItems(DataTable objects, string outputPath)
    {
        return objects.Rows
            .Cast<DataRow>()
            .Where(row => SqlObjectFilters.IsUserObject(
                row["Schema"].ToString() ?? string.Empty,
                row["Name"].ToString() ?? string.Empty))
            .Select(row => CreateWorkItem(row, outputPath))
            .ToList();
    }

    private static ObjectWorkItem CreateWorkItem(DataRow row, string outputPath)
    {
        string objectType = row["DatabaseObjectTypes"].ToString() ?? string.Empty;
        string schema = row["Schema"].ToString() ?? string.Empty;
        string objectName = row["Name"].ToString() ?? string.Empty;
        return new ObjectWorkItem(
            row["URN"].ToString() ?? string.Empty,
            schema,
            objectName,
            objectType,
            BuildOutputFilePath(outputPath, schema, objectName, objectType));
    }

    private static void ScriptWorkItems(ScriptingSession session, List<ObjectWorkItem> workItems)
    {
        if (workItems.Count == 0)
        {
            session.Progress.Report(new ScriptingProgressReport(0, 0, 0));
            return;
        }

        List<ObjectWorkItem> objectsToScript = workItems;
        int completed = 0;
        if (session.Mode == ScriptingMode.Delta)
        {
            (objectsToScript, completed) = PartitionDeltaWork(session, workItems);
            session.Progress.Report(new ScriptingProgressReport(completed, workItems.Count, 0));
            if (objectsToScript.Count == 0)
            {
                return;
            }
        }

        (List<ObjectWorkItem> encryptedItems, List<ObjectWorkItem> normalItems) =
            PartitionByEncryption(session, objectsToScript);
        EnsureEncryptedDecryptConsent(session, encryptedItems.Count);

        DateTimeOffset scriptedOn = DateTimeOffset.Now;
        int inFlight = 0;
        // completed starts at the Delta-mode skip count (0 for Full mode) so progress correctly
        // reflects "N of ALL objects in this database" as newly-scripted objects finish, not just
        // "N of the ones actually being scripted this pass."
        ProgressCounters counters = new(
            workItems.Count,
            () => Interlocked.Increment(ref completed),
            () => Volatile.Read(ref completed),
            () => Interlocked.Increment(ref inFlight),
            () => Interlocked.Decrement(ref inFlight),
            () => Volatile.Read(ref inFlight));

        ScriptNormalObjects(session, normalItems, scriptedOn, counters);
        ScriptEncryptedObjects(session, encryptedItems, scriptedOn, counters);
    }

    private static (List<ObjectWorkItem> ToScript, int SkippedCount) PartitionDeltaWork(
        ScriptingSession session,
        List<ObjectWorkItem> workItems)
    {
        List<ObjectWorkItem> toScript = [];
        int skipped = 0;
        foreach (ObjectWorkItem workItem in workItems)
        {
            session.CancellationToken.ThrowIfCancellationRequested();
            ObjectDates dates = session.Catalogue.LookupDates(workItem.Schema, workItem.ObjectName);
            if (ScriptFileTimestampMatcher.IsUnchanged(workItem.OutputFile, dates))
            {
                skipped++;
                continue;
            }

            toScript.Add(workItem);
        }

        return (toScript, skipped);
    }

    private static (List<ObjectWorkItem> Encrypted, List<ObjectWorkItem> Normal) PartitionByEncryption(
        ScriptingSession session,
        List<ObjectWorkItem> workItems)
    {
        List<ObjectWorkItem> encrypted = [];
        List<ObjectWorkItem> normal = [];
        foreach (ObjectWorkItem workItem in workItems)
        {
            if (session.Catalogue.IsEncrypted(workItem.Schema, workItem.ObjectName))
            {
                encrypted.Add(workItem);
            }
            else
            {
                normal.Add(workItem);
            }
        }

        return (encrypted, normal);
    }

    private static void EnsureEncryptedDecryptConsent(ScriptingSession session, int encryptedCount)
    {
        if (encryptedCount == 0 || session.AllowEncryptedModuleDecrypt)
        {
            return;
        }

        throw new EncryptedModulesConsentRequiredException(
            session.ActualDatabaseName,
            encryptedCount);
    }

    private static void ScriptNormalObjects(
        ScriptingSession session,
        List<ObjectWorkItem> normalItems,
        DateTimeOffset scriptedOn,
        ProgressCounters counters)
    {
        if (normalItems.Count == 0)
        {
            return;
        }

        int maxConcurrency = Math.Max(1, Math.Min(session.MaxConcurrency, normalItems.Count));
        BlockingCollection<Scripter> scripters = CreateScripterPool(
            session.ConnectionString,
            maxConcurrency);
        try
        {
            Parallel.ForEach(
                normalItems,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrency,
                    CancellationToken = session.CancellationToken
                },
                workItem => ScriptOneObject(new ObjectScriptContext(
                    session,
                    scripters,
                    workItem,
                    scriptedOn,
                    counters)));
        }
        finally
        {
            DrainScripterPool(scripters);
        }
    }

    private static void ScriptEncryptedObjects(
        ScriptingSession session,
        List<ObjectWorkItem> encryptedItems,
        DateTimeOffset scriptedOn,
        ProgressCounters counters)
    {
        if (encryptedItems.Count == 0)
        {
            return;
        }

        using EncryptedModuleDecryptor decryptor = EncryptedModuleDecryptor.Connect(
            session.ConnectionString,
            session.ActualDatabaseName);
        foreach (ObjectWorkItem workItem in encryptedItems)
        {
            session.CancellationToken.ThrowIfCancellationRequested();
            ScriptOneEncryptedObject(
                new EncryptedScriptContext(session, decryptor, workItem, scriptedOn, counters));
        }
    }

    private static void ScriptOneObject(ObjectScriptContext context)
    {
        context.Session.CancellationToken.ThrowIfCancellationRequested();
        ObjectDates dates = context.Session.Catalogue.LookupDates(
            context.WorkItem.Schema,
            context.WorkItem.ObjectName);

        int parallelTasks = context.Counters.IncrementInFlight();
        context.Session.Progress.Report(new ScriptingProgressReport(
            context.Counters.ReadCompleted(),
            context.Counters.TotalObjects,
            parallelTasks));

        Scripter scripter = context.Scripters.Take(context.Session.CancellationToken);
        try
        {
            WriteObjectScript(scripter, context, dates);
        }
        finally
        {
            context.Scripters.Add(scripter);
            context.Counters.DecrementInFlight();
        }

        ReportObjectCompleted(context.Session, context.Counters);
    }

    private static void ScriptOneEncryptedObject(EncryptedScriptContext context)
    {
        ObjectDates dates = context.Session.Catalogue.LookupDates(
            context.WorkItem.Schema,
            context.WorkItem.ObjectName);

        int parallelTasks = context.Counters.IncrementInFlight();
        context.Session.Progress.Report(new ScriptingProgressReport(
            context.Counters.ReadCompleted(),
            context.Counters.TotalObjects,
            parallelTasks));

        try
        {
            WriteEncryptedObjectScript(context, dates);
        }
        finally
        {
            context.Counters.DecrementInFlight();
        }

        ReportObjectCompleted(context.Session, context.Counters);
    }

    private static void ReportObjectCompleted(ScriptingSession session, ProgressCounters counters)
    {
        int finished = counters.IncrementCompleted();
        session.Progress.Report(new ScriptingProgressReport(
            finished,
            counters.TotalObjects,
            counters.ReadInFlight()));
    }

    private static void WriteObjectScript(
        Scripter scripter,
        ObjectScriptContext context,
        ObjectDates dates)
    {
        var headerRequest = new ObjectHeaderRequest(
            context.WorkItem.ObjectName,
            context.Session,
            context.ScriptedOn,
            dates);
        UrnCollection urns = [context.WorkItem.Urn];

        try
        {
            // ScriptingOptions.Permissions (set via ScriptingOptionsFactory) doesn't actually
            // produce GRANT text for a single Urn-identified object - see
            // ObjectPermissionScriptBuilder's own doc comment for why - so permissions are
            // enumerated and appended separately here, for both branches below.
            var permissionScript = ObjectPermissionScriptBuilder.BuildPermissionScript(
                scripter.Server.GetSmoObject(context.WorkItem.Urn));

            var needsText = context.Session.FormatSqlText is not null || context.Session.NormalizeAutoGeneratedNames;
            if (!needsText)
            {
                // Unchanged from before reformatting/normalizing existed: SMO writes the object
                // script straight to disk, and the header/metadata comment get prepended
                // afterward. Kept as a separate, minimally-touched path since it's the default
                // (and far more common) case - the paths below need the script as text first,
                // which is a materially different (less proven under this app's actual usage)
                // way of driving the same Scripter.
                scripter.Options.FileName = context.WorkItem.OutputFile;
                scripter.Script(urns);
                if (permissionScript.Length > 0)
                {
                    File.AppendAllText(context.WorkItem.OutputFile, permissionScript);
                }

                ApplyScriptFileMetadata(context.WorkItem.OutputFile, headerRequest);
            }
            else
            {
                // Scripters come fresh from the pool (see CreateScripter) with FileName unset,
                // so Script(urns) returns the text instead of writing straight to disk.
                StringCollection batches = scripter.Script(urns);
                var body = new StringBuilder();
                foreach (var batch in batches)
                {
                    body.AppendLine(batch);
                    body.AppendLine("GO");
                }

                var text = ApplyTextTransforms(context.Session, body.ToString(), context.WorkItem.ObjectName);
                // Appended after reformatting/normalizing, not before: those transforms are
                // built for the object's own DDL body, and GRANT/DENY statements have no
                // auto-generated constraint names or formatting quirks for them to act on.
                // The reformatter trims trailing whitespace off the very end of whatever text it's
                // given - since text here ends in "...GO" with nothing after it, that strips the
                // newline a plain "+" needs, gluing the next line straight onto "GO" (e.g.
                // "GOGRANT EXECUTE ON ..."). TrimEnd + an explicit blank line restores the same
                // separation every other batch boundary already has.
                var combinedText = permissionScript.Length > 0
                    ? text.TrimEnd() + Environment.NewLine + Environment.NewLine + permissionScript
                    : text;
                WriteRawScriptFile(context.WorkItem.OutputFile, combinedText, headerRequest);
            }
        }
        catch (Exception ex)
        {
            WriteScriptFailure(context.WorkItem.OutputFile, ex);
        }
    }

    private static void WriteEncryptedObjectScript(EncryptedScriptContext context, ObjectDates dates)
    {
        try
        {
            string definition = context.Decryptor.DecryptModule(
                context.WorkItem.Schema,
                context.WorkItem.ObjectName);
            definition = ApplyTextTransforms(context.Session, definition, context.WorkItem.ObjectName);

            WriteRawScriptFile(
                context.WorkItem.OutputFile,
                definition,
                new ObjectHeaderRequest(
                    context.WorkItem.ObjectName,
                    context.Session,
                    context.ScriptedOn,
                    dates));
        }
        catch (Exception ex)
        {
            WriteScriptFailure(context.WorkItem.OutputFile, ex);
        }
    }

    /// <summary>
    /// Applies the optional normalize-then-reformat pipeline to a scripted object's body text.
    /// Normalization runs first since it only touches constraint-name identifiers, leaving
    /// reformatting free to operate on the result without caring what the names actually are.
    /// </summary>
    private static string ApplyTextTransforms(ScriptingSession session, string sql, string objectName)
    {
        if (session.NormalizeAutoGeneratedNames)
        {
            var result = AutoGeneratedConstraintNameNormalizer.Normalize(sql, objectName);
            sql = result.Sql;
            session.NormalizedNameCounter.Add(result.RenamedCount);
        }

        return session.FormatSqlText is null ? sql : session.FormatSqlText(sql, objectName);
    }

    private static void WriteRawScriptFile(
        string outputFile,
        string definition,
        ObjectHeaderRequest headerRequest)
    {
        string header = BuildObjectHeader(headerRequest);
        string? metadataComment = ScriptFileTimestampMatcher.BuildMetadataComment(headerRequest.Dates);
        using (StreamWriter writer = new(outputFile, append: false, Encoding.UTF8))
        {
            if (metadataComment is not null)
            {
                writer.WriteLine(metadataComment);
            }

            writer.Write(header);
            writer.Write(definition);
            if (!definition.EndsWith('\n') && !definition.EndsWith('\r'))
            {
                writer.WriteLine();
            }
        }

        ScriptFileTimestampMatcher.ApplyObjectDates(outputFile, headerRequest.Dates);
    }

    private static void ApplyScriptFileMetadata(string outputFile, ObjectHeaderRequest headerRequest)
    {
        FileNameUtil.InjectTextAtTopOfFile(outputFile, BuildObjectHeader(headerRequest));
        string? metadataComment = ScriptFileTimestampMatcher.BuildMetadataComment(headerRequest.Dates);
        if (metadataComment is not null)
        {
            FileNameUtil.InjectTextAtTopOfFile(outputFile, metadataComment);
        }

        ScriptFileTimestampMatcher.ApplyObjectDates(outputFile, headerRequest.Dates);
    }

    private static BlockingCollection<Scripter> CreateScripterPool(string connectionString, int size)
    {
        BlockingCollection<Scripter> pool = new(size);
        for (int index = 0; index < size; index++)
        {
            pool.Add(CreateScripter(CreateServer(connectionString)));
        }

        return pool;
    }

    private static void DrainScripterPool(BlockingCollection<Scripter> pool)
    {
        pool.CompleteAdding();
        while (pool.TryTake(out Scripter? scripter))
        {
            scripter.Server.ConnectionContext.Disconnect();
        }

        pool.Dispose();
    }

    private static Scripter CreateScripter(Server server)
    {
        // includeTriggersInTableScript: true - there's no separate Trigger entry in
        // SqlObjectFilters.GetScriptedObjectTypes(), so a table's triggers only ever appear at
        // all if embedded in the table's own script here.
        return new Scripter(server)
        {
            Options = ScriptingOptionsFactory.Create(includeTriggersInTableScript: true)
        };
    }

    private static void WriteScriptFailure(string outputFile, Exception ex)
    {
        using StreamWriter writer = new(outputFile, append: true, Encoding.Unicode);
        writer.WriteLine("/**********************");
        writer.WriteLine("UNABLE TO CREATE SCRIPT");
        writer.WriteLine("**********************/");
        writer.WriteLine(ex.ToString());
    }

    private static string BuildOutputFilePath(
        string outputDirectory,
        string schema,
        string name,
        string objectType)
    {
        IEnumerable<string> parts = new[]
        {
            FileNameUtil.SanitiseFileName(schema),
            FileNameUtil.SanitiseFileName(name)
        }.Where(part => !string.IsNullOrEmpty(part));

        string fileName = $"{SqlObjectFilters.AbbreviateObjectType(objectType)} ⋮ {string.Join(".", parts)}.sql";
        return Path.Combine(outputDirectory, fileName);
    }

    private static string BuildObjectHeader(ObjectHeaderRequest request)
    {
        return ObjectScriptHeaderBuilder.Build(
            request.ObjectName,
            request.Session.ActualServerName,
            request.Session.ActualDatabaseName,
            request.ScriptedOn,
            request.Dates);
    }

    private sealed record LoadedDatabaseContext(
        Server Server,
        DataTable Objects,
        ObjectCatalogue Catalogue);

    private sealed record ObjectHeaderRequest(
        string ObjectName,
        ScriptingSession Session,
        DateTimeOffset ScriptedOn,
        ObjectDates Dates);

    private sealed record ObjectWorkItem(
        string Urn,
        string Schema,
        string ObjectName,
        string ObjectType,
        string OutputFile);

    private sealed class ProgressCounters(
        int totalObjects,
        Func<int> incrementCompleted,
        Func<int> readCompleted,
        Func<int> incrementInFlight,
        Func<int> decrementInFlight,
        Func<int> readInFlight)
    {
        public int TotalObjects { get; } = totalObjects;

        public int IncrementCompleted() => incrementCompleted();

        public int ReadCompleted() => readCompleted();

        public int IncrementInFlight() => incrementInFlight();

        public int DecrementInFlight() => decrementInFlight();

        public int ReadInFlight() => readInFlight();
    }

    private sealed class ObjectScriptContext(
        ScriptingSession session,
        BlockingCollection<Scripter> scripters,
        ObjectWorkItem workItem,
        DateTimeOffset scriptedOn,
        ProgressCounters counters)
    {
        public ScriptingSession Session { get; } = session;

        public BlockingCollection<Scripter> Scripters { get; } = scripters;

        public ObjectWorkItem WorkItem { get; } = workItem;

        public DateTimeOffset ScriptedOn { get; } = scriptedOn;

        public ProgressCounters Counters { get; } = counters;
    }

    private sealed class EncryptedScriptContext(
        ScriptingSession session,
        EncryptedModuleDecryptor decryptor,
        ObjectWorkItem workItem,
        DateTimeOffset scriptedOn,
        ProgressCounters counters)
    {
        public ScriptingSession Session { get; } = session;

        public EncryptedModuleDecryptor Decryptor { get; } = decryptor;

        public ObjectWorkItem WorkItem { get; } = workItem;

        public DateTimeOffset ScriptedOn { get; } = scriptedOn;

        public ProgressCounters Counters { get; } = counters;
    }

    private sealed class ScriptingSession
    {
        public ScriptingSession(ScriptDatabaseRequest request, ObjectCatalogue catalogue)
        {
            ConnectionString = request.ConnectionString;
            ActualServerName = catalogue.ActualServerName;
            ActualDatabaseName = catalogue.ActualDatabaseName;
            MaxConcurrency = request.MaxConcurrentObjectScripts;
            Mode = request.Mode;
            Progress = request.Progress;
            CancellationToken = request.CancellationToken;
            Catalogue = catalogue;
            AllowEncryptedModuleDecrypt = request.AllowEncryptedModuleDecrypt;
            FormatSqlText = request.FormatSqlText;
            NormalizeAutoGeneratedNames = request.NormalizeAutoGeneratedNames;
        }

        public string ConnectionString { get; }

        public string ActualServerName { get; }

        public string ActualDatabaseName { get; }

        public int MaxConcurrency { get; }

        public ScriptingMode Mode { get; }

        public IProgress<ScriptingProgressReport> Progress { get; }

        public CancellationToken CancellationToken { get; }

        public ObjectCatalogue Catalogue { get; }

        public bool AllowEncryptedModuleDecrypt { get; }

        public Func<string, string, string>? FormatSqlText { get; }

        public bool NormalizeAutoGeneratedNames { get; }

        // Objects within one database are scripted in parallel (see ScriptNormalObjects), so
        // this needs to be safe for concurrent increments from multiple worker threads.
        public NormalizedNameCounter NormalizedNameCounter { get; } = new();
    }

    /// <summary>Thread-safe running total of constraint names normalized during one database's scripting pass.</summary>
    private sealed class NormalizedNameCounter
    {
        private int _count;

        public void Add(int amount) => Interlocked.Add(ref _count, amount);

        public int Count => Volatile.Read(ref _count);
    }
}
