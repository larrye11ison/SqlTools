using SqlPhanos.CodeFormatting;
using SqlPhanos.ScriptDatabases;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SqlPhanos.DependencyIndex;

public sealed class DependencyIndexBuilder
{
    private readonly DependencyIndexStore _store;
    private readonly IEncryptedModuleDecryptorFactory _decryptorFactory;

    public DependencyIndexBuilder(DependencyIndexStore store)
        : this(store, new SqlServerEncryptedModuleDecryptorFactory())
    {
    }

    public DependencyIndexBuilder(DependencyIndexStore store, IEncryptedModuleDecryptorFactory decryptorFactory)
    {
        _store = store;
        _decryptorFactory = decryptorFactory;
    }

    public async Task<IndexBuildResult> BuildAsync(
        ICatalogCollector collector, IndexBuildOptions? options = null, string? connectionString = null,
        CancellationToken cancellationToken = default,
        IProgress<IndexProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(collector);
        progress?.Report(new IndexProgress(IndexProgressPhase.Initializing, 0, 0,
            null, 0, 0, "Starting dependency catalog collection."));
        ServerCatalogSnapshot snapshot;
        try
        {
            snapshot = await collector.CollectAsync(progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new IndexProgress(IndexProgressPhase.Cancelled,
                0, 0, null, 0, 0, "Dependency index collection was cancelled."));
            throw;
        }
        return await BuildAsync(snapshot, options, connectionString, cancellationToken, progress);
    }

    public async Task<IndexBuildResult> BuildAsync(
        ServerCatalogSnapshot snapshot, IndexBuildOptions? options = null, string? connectionString = null,
        CancellationToken cancellationToken = default,
        IProgress<IndexProgress>? progress = null)
    {
        options ??= new IndexBuildOptions();
        progress?.Report(new IndexProgress(IndexProgressPhase.Initializing, 0,
            snapshot.Databases.Count, null, 0, 0, "Initializing dependency index storage."));
        long serverId;
        long scanId;
        try
        {
            await _store.InitializeAsync(cancellationToken);
            serverId = await _store.UpsertServerAsync(snapshot.Identity, cancellationToken: cancellationToken);
            await _store.ReplaceLinkedServersAsync(serverId, snapshot.LinkedServers, cancellationToken);
            scanId = await _store.BeginScanAsync(serverId, options.FullRebuild, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new IndexProgress(IndexProgressPhase.Cancelled,
                0, snapshot.Databases.Count, null, 0, 0,
                "Dependency index initialization was cancelled."));
            throw;
        }
        var succeeded = 0;
        var failed = 0;
        var totalObjects = 0;
        var totalEdges = 0;
        var parseFailures = 0;
        var inaccessible = 0;
        var failureDetails = new List<DatabaseFailureDetail>();

        var catalog = CatalogMap.Create(snapshot.Databases);
        var currentDatabase = 0;
        try
        {
            // Prefetched once up front (rather than per-database inside the loop below) so the
            // consent gate below can be checked, and answered, before any database in this run
            // has been touched - matching the existing per-database "ask once per operation"
            // consent pattern used elsewhere in SqlPhanos, rather than surprising the user
            // partway through a large multi-database build.
            var priorAnalysisByDatabase = new Dictionary<int, IReadOnlyDictionary<int, ObjectAnalysisState>>();
            foreach (var database in snapshot.Databases.Where(item => item.Outcome == ScanOutcome.Succeeded))
            {
                priorAnalysisByDatabase[database.Database.SqlDatabaseId] = options.FullRebuild
                    ? new Dictionary<int, ObjectAnalysisState>()
                    : await _store.GetObjectAnalysisAsync(serverId, database.Database.SqlDatabaseId, cancellationToken);
            }

            if (options.EncryptedModuleDecrypt == EncryptedModuleDecryptMode.PromptIfNeeded)
            {
                var pendingEncryptedCount = snapshot.Databases
                    .Where(item => item.Outcome == ScanOutcome.Succeeded)
                    .Sum(item => item.Objects.Count(o => o.IsEncrypted &&
                        NeedsEncryptedReanalysis(o, priorAnalysisByDatabase[item.Database.SqlDatabaseId], options.FullRebuild)));
                if (pendingEncryptedCount > 0)
                    throw new EncryptedModulesConsentRequiredException(snapshot.Identity.DisplayName, pendingEncryptedCount);
            }

            foreach (var database in snapshot.Databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentDatabase++;
                if (database.Outcome != ScanOutcome.Succeeded)
                {
                    failed++;
                    if (database.Outcome == ScanOutcome.Inaccessible)
                        inaccessible++;
                    failureDetails.Add(new DatabaseFailureDetail(
                        database.Database.Name, database.Outcome, database.Error));
                    await _store.RecordDatabaseResultAsync(scanId, database.Database.SqlDatabaseId,
                        database.Database.Name, database.Outcome, 0, 0, 0, database.Error, cancellationToken);
                    progress?.Report(new IndexProgress(IndexProgressPhase.DatabaseFailed,
                        currentDatabase, snapshot.Databases.Count, database.Database.Name,
                        totalObjects, totalEdges, database.Error ?? database.Outcome.ToString()));
                    continue;
                }

                try
                {
                    progress?.Report(new IndexProgress(IndexProgressPhase.AnalyzingDatabase,
                        currentDatabase, snapshot.Databases.Count, database.Database.Name,
                        0, 0, "Analyzing catalog definitions and structural facts."));
                    var priorAnalysis = priorAnalysisByDatabase[database.Database.SqlDatabaseId];
                    var analysis = await AnalyzeDatabaseAsync(
                        serverId, database, catalog, snapshot.LinkedServers, priorAnalysis,
                        options.FullRebuild, options.MaxAnalysisConcurrency, options.EncryptedModuleDecrypt,
                        connectionString, progress, currentDatabase, snapshot.Databases.Count, cancellationToken);
                    progress?.Report(new IndexProgress(IndexProgressPhase.PromotingDatabase,
                        currentDatabase, snapshot.Databases.Count, database.Database.Name,
                        database.Objects.Count, analysis.Edges.Count,
                        "Atomically promoting the completed database snapshot."));
                    await _store.PromoteDatabaseSnapshotAsync(
                        serverId, scanId, database, analysis.ObjectAnalyses, analysis.AnalyzedObjectIds,
                        analysis.Edges, options.FullRebuild, cancellationToken);
                    progress?.Report(new IndexProgress(IndexProgressPhase.Reconciling,
                        currentDatabase, snapshot.Databases.Count, database.Database.Name,
                        database.Objects.Count, analysis.Edges.Count,
                        "Reconciling same-server cross-database targets."));
                    await _store.ReconcileResolvedLocalEdgesAsync(serverId, CancellationToken.None);
                    succeeded++;
                    totalObjects += database.Objects.Count;
                    totalEdges += analysis.Edges.Count;
                    parseFailures += analysis.ParseFailures;
                    await _store.RecordDatabaseResultAsync(scanId, database.Database.SqlDatabaseId,
                        database.Database.Name, ScanOutcome.Succeeded, database.Objects.Count,
                        analysis.Edges.Count, analysis.ParseFailures, null, cancellationToken);
                    progress?.Report(new IndexProgress(IndexProgressPhase.DatabaseCompleted,
                        currentDatabase, snapshot.Databases.Count, database.Database.Name,
                        totalObjects, totalEdges, "Database snapshot promoted."));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    failureDetails.Add(new DatabaseFailureDetail(
                        database.Database.Name, ScanOutcome.Failed, exception.Message));
                    await _store.RecordDatabaseResultAsync(scanId, database.Database.SqlDatabaseId,
                        database.Database.Name, ScanOutcome.Failed, 0, 0, 0, exception.Message, CancellationToken.None);
                    progress?.Report(new IndexProgress(IndexProgressPhase.DatabaseFailed,
                        currentDatabase, snapshot.Databases.Count, database.Database.Name,
                        totalObjects, totalEdges, exception.Message));
                }
            }

            var outcome = failed == 0 ? ScanOutcome.Succeeded : ScanOutcome.Failed;
            await _store.CompleteScanAsync(scanId, outcome, totalObjects, totalEdges, parseFailures,
                inaccessible, cancellationToken: cancellationToken);
            progress?.Report(new IndexProgress(IndexProgressPhase.Completed,
                snapshot.Databases.Count, snapshot.Databases.Count, null, totalObjects, totalEdges,
                outcome == ScanOutcome.Succeeded ? "Dependency index build completed." :
                "Dependency index build completed with database failures."));
            return new IndexBuildResult(scanId, outcome, succeeded, failed, totalObjects, totalEdges, serverId, failureDetails);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _store.CompleteScanAsync(scanId, ScanOutcome.Cancelled, totalObjects, totalEdges,
                parseFailures, inaccessible, "Index build was cancelled.", CancellationToken.None);
            progress?.Report(new IndexProgress(IndexProgressPhase.Cancelled,
                currentDatabase, snapshot.Databases.Count,
                currentDatabase is > 0 && currentDatabase <= snapshot.Databases.Count
                    ? snapshot.Databases[currentDatabase - 1].Database.Name : null,
                totalObjects, totalEdges, "Dependency index build was cancelled."));
            throw;
        }
        catch (Exception exception)
        {
            await _store.CompleteScanAsync(scanId, ScanOutcome.Failed, totalObjects, totalEdges,
                parseFailures, inaccessible, exception.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task<DatabaseAnalysis> AnalyzeDatabaseAsync(
        long serverId, DatabaseCatalogSnapshot database, CatalogMap catalog,
        IReadOnlyList<CatalogLinkedServer> linkedServers,
        IReadOnlyDictionary<int, ObjectAnalysisState> priorAnalysis,
        bool fullRebuild, int requestedConcurrency, EncryptedModuleDecryptMode decryptMode,
        string? connectionString, IProgress<IndexProgress>? progress,
        int currentDatabase, int totalDatabases, CancellationToken token)
    {
        var decryptedDefinitions = await DecryptNeededModulesAsync(
            database, priorAnalysis, fullRebuild, decryptMode, connectionString, progress,
            currentDatabase, totalDatabases, token);

        var analyses = new ConcurrentDictionary<int, ObjectAnalysisState>();
        var drafts = new ConcurrentBag<DependencyEdgeDraft>();
        var analyzed = new ConcurrentBag<int>();
        var parseFailures = 0;
        var maxConcurrency = requestedConcurrency > 0
            ? requestedConcurrency
            : Math.Max(1, Environment.ProcessorCount - 1);
        var linkedNames = linkedServers.Select(item => item.AliasName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var processedObjects = 0;

        await Parallel.ForEachAsync(database.Objects,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = maxConcurrency },
            (item, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // SQL Server never returns a WITH ENCRYPTION object's definition, so effectiveDefinition
                // is either the catalog's plaintext definition, a definition decrypted this run via DAC
                // above, or null (encrypted and not decrypted this run, or genuinely has no definition).
                var effectiveDefinition = item.Definition ??
                    (decryptedDefinitions.TryGetValue(item.SqlObjectId, out var decrypted) ? decrypted : null);
                var hash = effectiveDefinition is null ? null : Hash(effectiveDefinition);
                var hasPrevious = priorAnalysis.TryGetValue(item.SqlObjectId, out var previous);

                // Hash-based change detection doesn't work for encrypted objects: hash is always
                // null for them (there is never text to hash), so a hash comparison would read as
                // "unchanged" forever even after the object's real logic changed. modify_date is the
                // only signal available for them, so it drives whether a re-decrypt is worth doing.
                var changed = item.IsEncrypted
                    ? fullRebuild || !hasPrevious || previous.Status != AnalysisStatus.Complete ||
                      previous.ModifyDate != item.ModifyDate
                    : fullRebuild || !hasPrevious ||
                      !string.Equals(previous.Hash, hash, StringComparison.Ordinal);
                if (changed)
                    analyzed.Add(item.SqlObjectId);

                // Order matters: an unchanged object must reuse its prior analysis (including a
                // previously successful encrypted-object decrypt) even though effectiveDefinition
                // is null this run - decrypted text is never cached between scans, so "unchanged"
                // is the only way an encrypted object's earlier Complete/edges survive a refresh
                // that didn't need to re-decrypt it.
                if (!changed)
                {
                    analyses[item.SqlObjectId] = previous with { ModifyDate = item.ModifyDate };
                    ReportAnalysisProgress();
                    return ValueTask.CompletedTask;
                }

                if (effectiveDefinition is null)
                {
                    analyses[item.SqlObjectId] = new ObjectAnalysisState(
                        hash, item.IsEncrypted ? AnalysisStatus.Encrypted : AnalysisStatus.NoDefinition,
                        false, item.ModifyDate);
                    ReportAnalysisProgress();
                    return ValueTask.CompletedTask;
                }

                var result = new SqlObjectReferenceAnalyzer().Analyze(effectiveDefinition);
                var dynamic = ContainsDynamicSql(effectiveDefinition);
                var status = !result.ParseSucceeded
                    ? AnalysisStatus.ParseError
                    : dynamic ? AnalysisStatus.DynamicSql : AnalysisStatus.Complete;
                analyses[item.SqlObjectId] = new ObjectAnalysisState(hash, status, dynamic, item.ModifyDate);
                if (!result.ParseSucceeded)
                    Interlocked.Increment(ref parseFailures);

                foreach (var group in result.References.GroupBy(reference =>
                         ReferenceGroupKey.Create(reference), ReferenceGroupKey.Comparer))
                {
                    var first = group.First();
                    var resolution = Resolve(database.Database, first, catalog, linkedNames, serverId);
                    drafts.Add(new DependencyEdgeDraft(
                        item.SqlObjectId, resolution.TargetObjectId, resolution.TargetDatabaseId,
                        first.Kind.ToString(), resolution.Classification, resolution.Status,
                        resolution.TargetServerId, resolution.LinkedAlias, resolution.DatabaseName,
                        resolution.SchemaName, first.Object, first.Server, first.Database, first.Schema,
                        first.Object, first.PartCount, EvidenceKind.ScriptDom,
                        group.Select(reference => ToOccurrence(effectiveDefinition, reference)).ToArray()));
                }
                ReportAnalysisProgress();
                return ValueTask.CompletedTask;

                void ReportAnalysisProgress()
                {
                    var processed = Interlocked.Increment(ref processedObjects);
                    if (processed == database.Objects.Count || processed % 25 == 0)
                        progress?.Report(new IndexProgress(IndexProgressPhase.AnalyzingDatabase,
                            currentDatabase, totalDatabases, database.Database.Name,
                            processed, drafts.Count, $"Analyzed {processed} of {database.Objects.Count} objects."));
                }
            });

        foreach (var item in database.Objects)
            analyses.TryAdd(item.SqlObjectId, new ObjectAnalysisState(
                item.Definition is null ? null : Hash(item.Definition),
                item.IsEncrypted ? AnalysisStatus.Encrypted : AnalysisStatus.NoDefinition, false, item.ModifyDate));

        AddStructuralEdges(database, catalog, drafts, analyzed);
        return new DatabaseAnalysis(analyses, analyzed.ToHashSet(), drafts.ToArray(), parseFailures);
    }

    // Decrypted text is used only in-memory for this one analysis pass (hashed and fed through the
    // same ScriptDom pipeline as any other object) and is never persisted - only the resulting hash
    // and derived edges are written to the index, matching how ordinary definitions are handled.
    // Synchronous by design: EncryptedModuleDecryptor's DAC calls have no async API, so there is
    // nothing to meaningfully await here - callers still await the Task for composability.
    private Task<Dictionary<int, string>> DecryptNeededModulesAsync(
        DatabaseCatalogSnapshot database, IReadOnlyDictionary<int, ObjectAnalysisState> priorAnalysis,
        bool fullRebuild, EncryptedModuleDecryptMode decryptMode, string? connectionString,
        IProgress<IndexProgress>? progress, int currentDatabase, int totalDatabases, CancellationToken token)
    {
        var decrypted = new Dictionary<int, string>();
        if (decryptMode != EncryptedModuleDecryptMode.Allow || string.IsNullOrWhiteSpace(connectionString))
            return Task.FromResult(decrypted);

        var needingDecrypt = database.Objects
            .Where(item => item.IsEncrypted && NeedsEncryptedReanalysis(item, priorAnalysis, fullRebuild))
            .ToList();
        if (needingDecrypt.Count == 0)
            return Task.FromResult(decrypted);

        progress?.Report(new IndexProgress(IndexProgressPhase.DecryptingModules, currentDatabase, totalDatabases,
            database.Database.Name, 0, 0,
            $"Decrypting {needingDecrypt.Count} encrypted module(s) via DAC..."));
        try
        {
            using var decryptor = _decryptorFactory.Connect(connectionString, database.Database.Name);
            var completed = 0;
            foreach (var item in needingDecrypt)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    decrypted[item.SqlObjectId] = decryptor.DecryptModule(item.SchemaName, item.Name);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Per-object decrypt failure (e.g. dropped mid-scan) - leave it out; falls back
                    // to AnalysisStatus.Encrypted like any other undecryptable object.
                }
                completed++;
                // DAC decrypt is strictly sequential (one connection, one object at a time), so on
                // a database with a lot of encrypted modules this phase can genuinely take a while -
                // report after every object rather than throttling like the parallel analysis phase.
                // ObjectCount/EdgeCount are left at 0 here (unlike AnalyzingDatabase's use of them)
                // since the X-of-Y count is already in Message and those fields would otherwise
                // render as a misleading "(N objects, M edges so far)" in the UI's generic formatter.
                progress?.Report(new IndexProgress(IndexProgressPhase.DecryptingModules, currentDatabase,
                    totalDatabases, database.Database.Name, 0, 0,
                    $"Decrypting {completed} of {needingDecrypt.Count} in {database.Database.Name}..."));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The DAC connection itself failed - no sysadmin rights, remote admin connections
            // disabled, another DAC session already in use, etc. None of this database's encrypted
            // objects decrypt this run; they fall back to AnalysisStatus.Encrypted exactly as if
            // decrypt were never attempted. This must never fail the whole database scan.
        }
        return Task.FromResult(decrypted);
    }

    private static bool NeedsEncryptedReanalysis(
        CatalogObject item, IReadOnlyDictionary<int, ObjectAnalysisState> priorAnalysis, bool fullRebuild)
    {
        if (fullRebuild)
            return true;
        if (!priorAnalysis.TryGetValue(item.SqlObjectId, out var previous))
            return true;
        if (previous.Status != AnalysisStatus.Complete)
            return true;
        return previous.ModifyDate != item.ModifyDate;
    }

    private static void AddStructuralEdges(
        DatabaseCatalogSnapshot database, CatalogMap catalog, ConcurrentBag<DependencyEdgeDraft> drafts,
        ConcurrentBag<int> analyzed)
    {
        foreach (var foreignKey in database.ForeignKeys)
        {
            analyzed.Add(foreignKey.SourceSqlObjectId);
            drafts.Add(Structural(database, catalog, foreignKey.SourceSqlObjectId,
                foreignKey.TargetSqlObjectId, "ForeignKey", EvidenceKind.ForeignKey));
        }
        foreach (var item in database.Objects.Where(item => item.ParentSqlObjectId is not null))
        {
            analyzed.Add(item.SqlObjectId);
            // sys.objects rows with a parent_object_id aren't only triggers - CHECK and DEFAULT
            // constraints are first-class sys.objects rows (types 'C'/'D') with a parent too, so
            // without this split they were previously mislabeled and edge-classified as
            // TriggerParent, making them indistinguishable from real triggers and impossible to
            // filter out as DRI noise.
            var (kind, evidence) = item.TypeDescription switch
            {
                "CHECK_CONSTRAINT" => ("CheckConstraint", EvidenceKind.Constraint),
                "DEFAULT_CONSTRAINT" => ("DefaultConstraint", EvidenceKind.Constraint),
                _ => ("TriggerParent", EvidenceKind.TriggerParent),
            };
            drafts.Add(Structural(database, catalog, item.SqlObjectId,
                item.ParentSqlObjectId!.Value, kind, evidence));
        }
        foreach (var use in database.TypeUses)
        {
            analyzed.Add(use.SourceSqlObjectId);
            drafts.Add(Structural(database, catalog, use.SourceSqlObjectId,
                use.TypeSqlObjectId, "Type", EvidenceKind.Type));
        }
        foreach (var synonym in database.Synonyms)
        {
            analyzed.Add(synonym.SourceSqlObjectId);
            var parsed = new SqlObjectReferenceAnalyzer().Analyze($"SELECT * FROM {synonym.BaseObjectName};")
                .References.FirstOrDefault();
            if (parsed is null)
                continue;
            var resolved = Resolve(database.Database, parsed, catalog,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
            drafts.Add(new DependencyEdgeDraft(synonym.SourceSqlObjectId, resolved.TargetObjectId,
                resolved.TargetDatabaseId, "Synonym", resolved.Classification, resolved.Status,
                resolved.TargetServerId, resolved.LinkedAlias, resolved.DatabaseName, resolved.SchemaName,
                parsed.Object, parsed.Server, parsed.Database, parsed.Schema, parsed.Object,
                parsed.PartCount, EvidenceKind.Synonym, []));
        }
    }

    private static DependencyEdgeDraft Structural(DatabaseCatalogSnapshot database, CatalogMap catalog,
        int sourceId, int targetId, string kind, EvidenceKind evidence)
    {
        if (catalog.ByIdentity.TryGetValue((database.Database.SqlDatabaseId, targetId), out var target))
        {
            return new DependencyEdgeDraft(sourceId, targetId, database.Database.SqlDatabaseId, kind,
                ReferenceClassification.Structural, ResolutionStatus.Resolved, null, null,
                database.Database.Name, target.SchemaName, target.Name, null, null, target.SchemaName,
                target.Name, 2, evidence, []);
        }

        // The catalog snapshot's structural facts (sys.foreign_keys, parent_object_id, sys.types)
        // and its object list (sys.objects) come from separate queries on a live connection, not
        // one consistent point-in-time snapshot - on a busy production database, concurrent DDL
        // between those queries can leave a structural reference pointing at an object_id this
        // scan never captured. Surface that as an unresolved structural edge, the same way every
        // other unresolvable reference in this codebase is handled, instead of throwing and
        // losing the rest of this database's analysis.
        var placeholderName = $"<object_id {targetId}>";
        return new DependencyEdgeDraft(sourceId, null, database.Database.SqlDatabaseId, kind,
            ReferenceClassification.Structural, ResolutionStatus.NotFound, null, null,
            database.Database.Name, null, placeholderName, null, null, null,
            placeholderName, 2, evidence, []);
    }

    private static ResolvedReference Resolve(CatalogDatabase sourceDatabase, SqlObjectReference reference,
        CatalogMap catalog, IReadOnlySet<string> linkedNames, long serverId)
    {
        if (reference.Classification == SqlObjectReferenceClassification.LinkedServer)
            return new ResolvedReference(null, null, ReferenceClassification.LinkedServer,
                ResolutionStatus.ExternalUnmapped, null,
                linkedNames.Contains(reference.Server!) ? reference.Server : reference.Server,
                reference.Database, reference.Schema);
        if (reference.Classification == SqlObjectReferenceClassification.RemoteDataSource)
            return new ResolvedReference(null, null, ReferenceClassification.RemoteDataSource,
                ResolutionStatus.ExternalUnmapped, null, null, reference.Database, reference.Schema);

        var databaseName = reference.Database ?? sourceDatabase.Name;
        if (!catalog.DatabaseByName.TryGetValue(databaseName, out var targetDatabase))
            return new ResolvedReference(null, null, ReferenceClassification.Local,
                ResolutionStatus.NotFound, serverId, null, databaseName, reference.Schema);
        if (!targetDatabase.IsAccessible)
            return new ResolvedReference(null, null, ReferenceClassification.Local,
                ResolutionStatus.Inaccessible, serverId, null, databaseName, reference.Schema);

        var candidates = catalog.Find(targetDatabase.SqlDatabaseId, reference.Schema, reference.Object,
            reference.Kind);
        if (candidates.Count == 1)
            return new ResolvedReference(candidates[0].SqlObjectId, targetDatabase.SqlDatabaseId,
                ReferenceClassification.Local, ResolutionStatus.Resolved, serverId, null,
                targetDatabase.Name, candidates[0].SchemaName);
        return new ResolvedReference(null, null, ReferenceClassification.Local,
            candidates.Count == 0 ? ResolutionStatus.NotFound : ResolutionStatus.Ambiguous,
            serverId, null, targetDatabase.Name, reference.Schema);
    }

    private static DependencyOccurrence ToOccurrence(string definition, SqlObjectReference reference)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < reference.Offset; i++)
        {
            if (definition[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
                column++;
        }
        return new DependencyOccurrence(reference.Offset, reference.Length, line, column, reference.Text);
    }

    private static string Hash(string definition)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition)));

    private static bool ContainsDynamicSql(string definition)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(definition);
        var tokens = parser.GetTokenStream(reader, out _);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!string.Equals(tokens[i].Text, "EXEC", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tokens[i].Text, "EXECUTE", StringComparison.OrdinalIgnoreCase))
                continue;
            var next = NextSignificantToken(tokens, i + 1);
            if (next < 0)
                continue;
            if (tokens[next].Text == "(")
                next = NextSignificantToken(tokens, next + 1);
            var nextText = next >= 0 ? tokens[next].Text : null;
            if (nextText is not null && (nextText.StartsWith("@", StringComparison.Ordinal) ||
                                         nextText.StartsWith("'", StringComparison.Ordinal) ||
                                         nextText.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static int NextSignificantToken(IList<TSqlParserToken> tokens, int start)
    {
        for (var i = start; i < tokens.Count; i++)
        {
            var text = tokens[i].Text;
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith("--", StringComparison.Ordinal) ||
                text.StartsWith("/*", StringComparison.Ordinal))
                continue;
            return i;
        }
        return -1;
    }

    private sealed record DatabaseAnalysis(
        IReadOnlyDictionary<int, ObjectAnalysisState> ObjectAnalyses,
        IReadOnlyCollection<int> AnalyzedObjectIds, IReadOnlyList<DependencyEdgeDraft> Edges,
        int ParseFailures);

    private sealed record ResolvedReference(
        int? TargetObjectId, int? TargetDatabaseId, ReferenceClassification Classification,
        ResolutionStatus Status, long? TargetServerId, string? LinkedAlias,
        string? DatabaseName, string? SchemaName);

    private readonly record struct ReferenceGroupKey(
        string Kind, string? Server, string? Database, string? Schema, string Object, int PartCount)
    {
        public static ReferenceGroupKey Create(SqlObjectReference reference)
            => new(reference.Kind.ToString(), reference.Server, reference.Database, reference.Schema,
                reference.Object, reference.PartCount);

        public static IEqualityComparer<ReferenceGroupKey> Comparer { get; } =
            new ReferenceGroupKeyComparer();

        private sealed class ReferenceGroupKeyComparer : IEqualityComparer<ReferenceGroupKey>
        {
            public bool Equals(ReferenceGroupKey x, ReferenceGroupKey y)
                => x.PartCount == y.PartCount &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.Kind, y.Kind) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.Server, y.Server) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.Database, y.Database) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.Schema, y.Schema) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.Object, y.Object);
            public int GetHashCode(ReferenceGroupKey value)
            {
                var hash = new HashCode();
                hash.Add(value.Kind, StringComparer.OrdinalIgnoreCase);
                hash.Add(value.Server, StringComparer.OrdinalIgnoreCase);
                hash.Add(value.Database, StringComparer.OrdinalIgnoreCase);
                hash.Add(value.Schema, StringComparer.OrdinalIgnoreCase);
                hash.Add(value.Object, StringComparer.OrdinalIgnoreCase);
                hash.Add(value.PartCount);
                return hash.ToHashCode();
            }
        }
    }

    private sealed class CatalogMap
    {
        private readonly Dictionary<(int DatabaseId, string Object), List<CatalogObject>> _byName;
        public Dictionary<string, CatalogDatabase> DatabaseByName { get; }
        public Dictionary<(int DatabaseId, int ObjectId), CatalogObject> ByIdentity { get; }

        private CatalogMap(Dictionary<string, CatalogDatabase> databases,
            Dictionary<(int, int), CatalogObject> identities,
            Dictionary<(int, string), List<CatalogObject>> names)
        {
            DatabaseByName = databases;
            ByIdentity = identities;
            _byName = names;
        }

        public static CatalogMap Create(IReadOnlyList<DatabaseCatalogSnapshot> databases)
        {
            var dbs = databases.ToDictionary(item => item.Database.Name, item => item.Database,
                StringComparer.OrdinalIgnoreCase);
            var identities = new Dictionary<(int, int), CatalogObject>();
            var names = new Dictionary<(int, string), List<CatalogObject>>(new DatabaseNameKeyComparer());
            foreach (var database in databases.Where(item => item.Outcome == ScanOutcome.Succeeded))
            foreach (var item in database.Objects)
            {
                identities[(database.Database.SqlDatabaseId, item.SqlObjectId)] = item;
                var key = (database.Database.SqlDatabaseId, item.Name);
                if (!names.TryGetValue(key, out var list))
                    names.Add(key, list = []);
                list.Add(item);
            }
            return new CatalogMap(dbs, identities, names);
        }

        public IReadOnlyList<CatalogObject> Find(int databaseId, string? schema, string name,
            SqlObjectReferenceKind kind)
        {
            if (!_byName.TryGetValue((databaseId, name), out var candidates))
                return [];
            return candidates.Where(item =>
                (schema is null || string.Equals(schema, item.SchemaName, StringComparison.OrdinalIgnoreCase)) &&
                IsCompatible(kind, item.TypeDescription)).ToArray();
        }

        private static bool IsCompatible(SqlObjectReferenceKind kind, string type)
            => kind switch
            {
                SqlObjectReferenceKind.Procedure or SqlObjectReferenceKind.Executable =>
                    type.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase),
                SqlObjectReferenceKind.Function =>
                    type.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase),
                SqlObjectReferenceKind.Trigger =>
                    type.Contains("TRIGGER", StringComparison.OrdinalIgnoreCase),
                SqlObjectReferenceKind.Type =>
                    type.Contains("TYPE", StringComparison.OrdinalIgnoreCase),
                SqlObjectReferenceKind.TableOrView =>
                    type.Contains("TABLE", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("VIEW", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("SYNONYM", StringComparison.OrdinalIgnoreCase),
                _ => true,
            };

        private sealed class DatabaseNameKeyComparer : IEqualityComparer<(int, string)>
        {
            public bool Equals((int, string) x, (int, string) y)
                => x.Item1 == y.Item1 && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);
            public int GetHashCode((int, string) value)
                => HashCode.Combine(value.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Item2));
        }
    }
}
