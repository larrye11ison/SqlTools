using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using SqlPhanos.CodeFormatting;
using SqlPhanos.Enums;
using SqlPhanos.Messages;
using SqlPhanos.ScriptDatabases;
using SqlPhanos.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace SqlPhanos.ViewModels;

public enum SqlDisplayMode
{
    Original,
    Formatted,
}

internal sealed record TextEditorViewState(
    int CaretOffset,
    double HorizontalOffset,
    double VerticalOffset);

/// <summary>
/// View model for a single SQL script document. Owns its own scripting lifecycle: it opens
/// immediately in a busy state and scripts itself (rather than the Search pane scripting the
/// object first and only opening the tab once done), so the tab is the sole place showing
/// progress/cancel/error state for this object - the Search Results pane no longer tracks any
/// of that.
/// </summary>
public partial class SqlDocumentViewModel : Document, IHasTabHeaderLines
{
    private readonly SqlCanonicalizationService _sqlCanonicalizationService = new();
    private readonly SqlObjectReferenceAnalyzer _sqlObjectReferenceAnalyzer = new();
    private static readonly SqlObjectReferenceResolver SqlObjectReferenceResolver = new();
    private readonly SqlSearchService? _searchService;
    private readonly string _connectionString = "";
    private readonly SearchResultViewModel? _sourceResult;
    private string _currentSqlText = "";
    private SqlDisplayMode _displayMode = SqlDisplayMode.Original;
    private string _serverName = "";
    private string _dbName = "";
    private string _schemaName = "";
    private string _objectName = "";
    private string _formattedSqlText = "";
    private ObservableCollection<SearchResultViewModel> _dependentObjects = new();
    private string _originalSqlText = "";
    private IReadOnlyList<SqlDocumentReference> _originalReferences = Array.Empty<SqlDocumentReference>();
    private IReadOnlyList<SqlDocumentReference> _formattedReferences = Array.Empty<SqlDocumentReference>();
    private IReadOnlyList<SqlTokenPosition>? _tokenPositions;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _encryptedConsentTcs;
    private string? _clrAssemblyName;
    private byte[]? _clrAssemblyBytes;
    // Signaled when LoadAsync's own finally block flips IsScripting back to false - lets
    // ConfirmCloseWhileRunningAsync wait for the in-flight load to actually stop after
    // CancelScripting() (which only requests cancellation; it doesn't itself wait for anything).
    private TaskCompletionSource? _operationCompletionTcs;
    private TaskCompletionSource<bool>? _closeConfirmationTcs;

    internal TextEditorViewState? SqlEditorViewState { get; set; }
    internal TextEditorViewState? ClrEditorViewState { get; set; }

    public string CurrentSqlText
    {
        get => _currentSqlText;
        private set => SetProperty(ref _currentSqlText, value);
    }

    public string ServerName
    {
        get => _serverName;
        private set => SetProperty(ref _serverName, value);
    }

    public string DbName
    {
        get => _dbName;
        private set => SetProperty(ref _dbName, value);
    }

    public string SchemaName
    {
        get => _schemaName;
        private set => SetProperty(ref _schemaName, value);
    }

    public string ObjectName
    {
        get => _objectName;
        private set => SetProperty(ref _objectName, value);
    }

    // IHasTabHeaderLines - backs the shared DocumentTabStrip.HeaderTemplate (see
    // ShellView.axaml), which replaces the object-name text that used to be repeated in this
    // document's own header bar.
    public string TabHeaderLine1 => $"{ServerName} | {DbName}";

    public string TabHeaderLine2 => $"{SchemaName}.{ObjectName}";

    public DocumentTabIconState TabIconState =>
        IsScripting ? DocumentTabIconState.Busy :
        IsInErrorState ? DocumentTabIconState.Error :
        DocumentTabIconState.None;

    public string FormattedSqlText
    {
        get => _formattedSqlText;
        private set => SetProperty(ref _formattedSqlText, value);
    }

    public string OriginalSqlText
    {
        get => _originalSqlText;
        private set => SetProperty(ref _originalSqlText, value);
    }

    // A CLR-backed proc/function/trigger's own T-SQL is just a thin "AS EXTERNAL NAME" wrapper -
    // OriginalSqlText/FormattedSqlText above still show that (unchanged), and this holds the
    // actual decompiled implementation underneath it, shown as a second section in the same tab.
    [ObservableProperty]
    private bool _isClrObject;

    [ObservableProperty]
    private string _decompiledClrSource = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClrDecompileError))]
    private string _clrDecompileError = "";

    public bool HasClrDecompileError => !string.IsNullOrEmpty(ClrDecompileError);

    /// <summary>
    /// Objects that depend on the one scripted into this document (e.g. the triggers on a
    /// table). Populated asynchronously after the document opens, since discovering them
    /// requires an extra round trip to the server.
    /// </summary>
    public ObservableCollection<SearchResultViewModel> DependentObjects => _dependentObjects;

    public bool HasDependentObjects => DependentObjects.Count > 0;

    public IReadOnlyList<SqlDocumentReference> ActiveReferences =>
        IsShowingFormatted ? _formattedReferences : _originalReferences;

    public SqlDisplayMode DisplayMode
    {
        get => _displayMode;
        private set
        {
            if (SetProperty(ref _displayMode, value))
            {
                OnPropertyChanged(nameof(IsShowingOriginal));
                OnPropertyChanged(nameof(IsShowingFormatted));
                OnPropertyChanged(nameof(ActiveReferences));
            }
        }
    }

    public bool IsShowingOriginal => DisplayMode == SqlDisplayMode.Original;

    public bool IsShowingFormatted => DisplayMode == SqlDisplayMode.Formatted;

    public string SyntaxScopeName => "source.sql";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabIconState))]
    [NotifyCanExecuteChangedFor(nameof(CancelScriptingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isScripting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabIconState))]
    private bool _isInErrorState;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentInteractive))]
    private bool _pendingEncryptedConsent;

    [ObservableProperty]
    private string _pendingEncryptedObjectName = "";

    [ObservableProperty]
    private int _pendingEncryptedCount;

    // Set from LoadAsync whenever SqlCanonicalizationService's own round-trip safety check
    // rejected the formatted output (see IsRoundTripSafe) - FormattedSqlText is the original,
    // unformatted text in that case, so there is nothing to toggle to. Gates ShowFormattedCommand
    // shut for the rest of this load rather than just showing the warning once and leaving a
    // dead "Reformatted" button that quietly does nothing if clicked.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowFormattedCommand))]
    private bool _formattingSafetyCheckFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentInteractive))]
    private bool _pendingFormattingSafetyWarning;

    // Shown when the user tries to close this tab (X, Ctrl+W, or the Close Document menu item)
    // while this object is still being scripted - see OnClose().
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentInteractive))]
    private bool _pendingCloseConfirmation;

    public bool IsContentInteractive => !PendingEncryptedConsent && !PendingFormattingSafetyWarning && !PendingCloseConfirmation;

    // Parameterless constructor exists only for the XAML Design.DataContext tag.
    public SqlDocumentViewModel()
    {
        Title = "SQL Script";
    }

    /// <summary>
    /// Opens immediately in a busy state (TabIconState shows Busy right away) and scripts the
    /// object itself - see LoadAsync.
    /// </summary>
    public SqlDocumentViewModel(SearchResultViewModel result, string connectionString, SqlSearchService searchService)
    {
        ServerName = result.ServerName;
        DbName = result.DbName;
        SchemaName = result.SchemaName;
        ObjectName = result.ObjectName;
        Title = $"{result.SchemaName}.{result.ObjectName}";
        _sourceResult = result;
        _connectionString = connectionString;
        _searchService = searchService;
        IsScripting = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _cts = new CancellationTokenSource();
        _operationCompletionTcs = new TaskCompletionSource();
        SetReferences(Array.Empty<SqlDocumentReference>(), Array.Empty<SqlDocumentReference>());
        SetDependentObjects(Array.Empty<SearchResultViewModel>());
        try
        {
            var scripted = await ScriptWithConsentAsync(_cts.Token);
            var script = scripted.ScriptText;
            var formatResult = _sqlCanonicalizationService.FormatForDisplayWithPositions(script, FormattingSettingsService.OpeningParenOnNewLine);
            OriginalSqlText = script;
            FormattedSqlText = formatResult.Text;
            _tokenPositions = formatResult.TokenPositions;
            CurrentSqlText = OriginalSqlText;
            DisplayMode = SqlDisplayMode.Original;
            FormattingSafetyCheckFailed = !formatResult.SafetyCheckPassed;
            PendingFormattingSafetyWarning = FormattingSafetyCheckFailed;

            try
            {
                await LoadReferencesAsync(script, formatResult.Text, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Object reference lookup failed: {ex}");
            }

            if (scripted.ClrAssemblyName is not null)
            {
                await LoadClrArtifactsAsync(scripted.ClrAssemblyName);
            }

            try
            {
                var dependents = await _searchService!.GetDependentObjectsAsync(_connectionString, _sourceResult!);
                SetDependentObjects(dependents);
            }
            catch (Exception ex)
            {
                // Dependent-object discovery is best-effort - a failure here must not
                // invalidate the script that already loaded successfully.
                System.Diagnostics.Debug.WriteLine($"Dependent object lookup failed: {ex}");
            }
        }

        catch (OperationCanceledException)
        {
            // Matches QueryXLeratorDocumentViewModel's convention: a user-initiated cancel is
            // not an error state, just an empty/incomplete document.
            StatusMessage = "Scripting cancelled.";
        }
        catch (Exception ex)
        {
            IsInErrorState = true;
            StatusMessage = $"Scripting failed: {ex.Message}";
        }
        finally
        {
            IsScripting = false;
            _cts?.Dispose();
            _cts = null;
            _operationCompletionTcs?.TrySetResult();
            _operationCompletionTcs = null;
        }
    }

    private async Task LoadReferencesAsync(
        string originalSql,
        string formattedSql,
        CancellationToken cancellationToken)
    {
        var originalAnalysisTask = Task.Run(
            () => _sqlObjectReferenceAnalyzer.Analyze(originalSql),
            cancellationToken);
        Task<SqlObjectReferenceAnalysisResult> formattedAnalysisTask;
        if (string.Equals(originalSql, formattedSql, StringComparison.Ordinal))
        {
            formattedAnalysisTask = originalAnalysisTask;
        }
        else
        {
            formattedAnalysisTask = Task.Run(
                () => _sqlObjectReferenceAnalyzer.Analyze(formattedSql),
                cancellationToken);
        }

        await Task.WhenAll(originalAnalysisTask, formattedAnalysisTask);
        var originalAnalysis = await originalAnalysisTask;
        var formattedAnalysis = await formattedAnalysisTask;
        if (!originalAnalysis.ParseSucceeded)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Original SQL reference analysis reported {originalAnalysis.ParseErrors.Count} parse error(s).");
        }
        if (!formattedAnalysis.ParseSucceeded)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Formatted SQL reference analysis reported {formattedAnalysis.ParseErrors.Count} parse error(s).");
        }

        if (originalAnalysis.References.Count == 0 && formattedAnalysis.References.Count == 0)
        {
            return;
        }

        var lookupIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var lookups = new List<SqlObjectReferenceLookup>();

        void AddLookups(IEnumerable<SqlObjectReference> references)
        {
            foreach (var reference in references)
            {
                if (reference.Classification != SqlObjectReferenceClassification.Local)
                {
                    continue;
                }

                var key = CreateReferenceKey(reference);
                if (lookupIds.ContainsKey(key))
                {
                    continue;
                }

                var id = lookupIds.Count;
                lookupIds.Add(key, id);
                lookups.Add(new SqlObjectReferenceLookup(
                    id,
                    reference.Database,
                    reference.Schema,
                    reference.Object,
                    reference.Kind));
            }
        }

        AddLookups(originalAnalysis.References);
        AddLookups(formattedAnalysis.References);

        var resolved = await SqlObjectReferenceResolver.ResolveAsync(
            _connectionString,
            DbName,
            lookups,
            cancellationToken);

        SetReferences(
            CreateDocumentReferences(originalAnalysis.References, lookupIds, resolved),
            CreateDocumentReferences(formattedAnalysis.References, lookupIds, resolved));
    }

    private IReadOnlyList<SqlDocumentReference> CreateDocumentReferences(
        IReadOnlyList<SqlObjectReference> references,
        IReadOnlyDictionary<string, int> lookupIds,
        IReadOnlyDictionary<int, SqlObjectReferenceResolution> resolutions)
    {
        var documentReferences = new List<SqlDocumentReference>();

        foreach (var reference in references)
        {
            if (reference.Classification != SqlObjectReferenceClassification.Local)
            {
                documentReferences.Add(new SqlDocumentReference(
                    reference.Offset,
                    reference.Length,
                    reference.Text,
                    target: null,
                    isLinkedServer:
                        reference.Classification == SqlObjectReferenceClassification.LinkedServer,
                    isRemoteDataSource:
                        reference.Classification == SqlObjectReferenceClassification.RemoteDataSource));
                continue;
            }

            var key = CreateReferenceKey(reference);
            if (!lookupIds.TryGetValue(key, out var lookupId) ||
                !resolutions.TryGetValue(lookupId, out var resolution))
            {
                documentReferences.Add(CreateUnresolvedReference(
                    reference,
                    "Referenced object could not be resolved. The script may fail until the reference is corrected."));
                continue;
            }

            if (resolution.Target is { } target)
            {
                if (IsCurrentObject(target))
                {
                    continue;
                }

                documentReferences.Add(new SqlDocumentReference(
                    reference.Offset,
                    reference.Length,
                    reference.Text,
                    target,
                    isLinkedServer: false));
                continue;
            }

            documentReferences.Add(CreateUnresolvedReference(
                reference,
                GetResolutionWarning(reference, resolution)));
        }

        return documentReferences;
    }

    private static SqlDocumentReference CreateUnresolvedReference(
        SqlObjectReference reference,
        string warning) =>
        new(
            reference.Offset,
            reference.Length,
            reference.Text,
            target: null,
            isLinkedServer: false,
            warning);

    private string GetResolutionWarning(
        SqlObjectReference reference,
        SqlObjectReferenceResolution resolution)
    {
        var databaseName = reference.Database ?? DbName;
        return resolution.Status switch
        {
            SqlObjectReferenceResolutionStatus.DatabaseUnavailable =>
                $"Database '{databaseName}' could not be inspected: {resolution.Detail} The script may fail until access is restored.",
            SqlObjectReferenceResolutionStatus.Ambiguous =>
                "More than one possible object matched this reference, so it could not be scripted safely.",
            _ =>
                $"No scriptable object named '{reference.Text}' was found or visible to this connection in database '{databaseName}'. The script may fail until the reference is corrected.",
        };
    }

    private static string CreateReferenceKey(SqlObjectReference reference) =>
        $"{(int)reference.Kind}\u001f{reference.Database ?? ""}\u001f{reference.Schema ?? ""}\u001f{reference.Object}";

    private bool IsCurrentObject(SearchResultViewModel target) =>
        _sourceResult is not null && IsSameObject(target, _sourceResult);

    internal static bool IsSameObject(
        SearchResultViewModel candidate,
        SearchResultViewModel source) =>
        string.Equals(candidate.DbName, source.DbName, StringComparison.Ordinal) &&
        string.Equals(candidate.SchemaName, source.SchemaName, StringComparison.Ordinal) &&
        string.Equals(candidate.ObjectName, source.ObjectName, StringComparison.Ordinal) &&
        string.Equals(candidate.TypeDesc, source.TypeDesc, StringComparison.Ordinal) &&
        string.Equals(candidate.ParentFqName, source.ParentFqName, StringComparison.Ordinal);

    /// <summary>
    /// Fetches the CLR assembly's raw bytes and decompiles them into the standalone C# source
    /// shown alongside the T-SQL wrapper - best-effort: a failure here (missing rights to
    /// sys.assembly_files, an assembly decompilation chokes on) must not invalidate the SQL script
    /// that already loaded successfully, so it's surfaced as a small inline note rather than
    /// failing the whole document load.
    /// </summary>
    private async Task LoadClrArtifactsAsync(string assemblyName)
    {
        try
        {
            var bytes = await Task.Run(() =>
                ClrAssemblyExporter.GetPrimaryAssemblyBytes(_connectionString, DbName, assemblyName, out _));
            if (bytes is null)
            {
                ClrDecompileError = $"Assembly '{assemblyName}' has no rows in sys.assembly_files.";
                return;
            }

            DecompiledClrSource = await Task.Run(() => ClrAssemblyDecompiler.Decompile(bytes, assemblyName));
            // Prefer the class actually defining the SQL-facing API over the assembly's own SQL
            // name (often unrelated to any type name in it) for the "Save As DLL" suggestion -
            // same naming DatabaseScriptingService uses for the whole-DB export path.
            _clrAssemblyName = ClrAssemblyExporter.TryGetPrimarySqlClrTypeName(bytes) ?? assemblyName;
            _clrAssemblyBytes = bytes;
            IsClrObject = true;
        }
        catch (Exception ex)
        {
            ClrDecompileError = $"Failed to fetch/decompile assembly '{assemblyName}': {ex.Message}";
        }
    }

    /// <summary>
    /// Raw bytes + a suggested file name for the "Save As DLL" button's code-behind file picker -
    /// null when the loaded object isn't CLR-backed, or the fetch above failed.
    /// </summary>
    public bool TryGetClrAssemblyBytes(out byte[] bytes, out string suggestedFileName)
    {
        if (_clrAssemblyBytes is not null && _clrAssemblyName is not null)
        {
            bytes = _clrAssemblyBytes;
            suggestedFileName = _clrAssemblyName + ".dll";
            return true;
        }

        bytes = [];
        suggestedFileName = "";
        return false;
    }

    /// <summary>
    /// Wraps SqlSearchService.ScriptObjectAsync in a catch-and-retry loop for encrypted
    /// objects, mirroring ScriptDatabasesDocumentViewModel.ScriptOneDatabaseWithConsentAsync:
    /// the underlying engine throws EncryptedModulesConsentRequiredException the first time
    /// (before opening a DAC connection to decrypt), and this asks the user via the same
    /// in-tab overlay pattern before retrying with consent granted.
    /// </summary>
    private async Task<SingleObjectScriptResult> ScriptWithConsentAsync(CancellationToken cancellationToken)
    {
        var allowEncryptedModuleDecrypt = false;
        while (true)
        {
            try
            {
                return await _searchService!.ScriptObjectAsync(
                    _connectionString,
                    _sourceResult!,
                    allowEncryptedModuleDecrypt,
                    cancellationToken);
            }
            catch (EncryptedModulesConsentRequiredException consent)
            {
                var accepted = await RequestEncryptedConsentAsync(consent.DatabaseName, consent.EncryptedCount);
                if (!accepted)
                {
                    throw new OperationCanceledException("Encrypted module decrypt declined.", cancellationToken);
                }

                allowEncryptedModuleDecrypt = true;
            }
        }
    }

    private Task<bool> RequestEncryptedConsentAsync(string objectName, int encryptedCount)
    {
        _encryptedConsentTcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(() =>
        {
            PendingEncryptedObjectName = objectName;
            PendingEncryptedCount = encryptedCount;
            PendingEncryptedConsent = true;
        });
        return _encryptedConsentTcs.Task;
    }

    [RelayCommand]
    private void ConfirmDecrypt()
    {
        PendingEncryptedConsent = false;
        _encryptedConsentTcs?.TrySetResult(true);
    }

    [RelayCommand]
    private void DeclineDecrypt()
    {
        PendingEncryptedConsent = false;
        _encryptedConsentTcs?.TrySetResult(false);
    }

    [RelayCommand(CanExecute = nameof(IsScripting))]
    private void CancelScripting()
    {
        _cts?.Cancel();
    }

    // See FactoryBase.OnDockableClosing (Dock.Model) - returning false here blocks the close (X
    // button, Ctrl+W, and the "Close Document" menu item all funnel through this one hook)
    // without cancelling anything yet, and ConfirmCloseWhileRunningAsync re-issues the close
    // itself once the user actually confirms and the load has stopped.
    public override bool OnClose()
    {
        if (!IsScripting)
        {
            return true;
        }

        if (_closeConfirmationTcs is null)
        {
            _ = ConfirmCloseWhileRunningAsync();
        }

        return false;
    }

    private async Task ConfirmCloseWhileRunningAsync()
    {
        _closeConfirmationTcs = new TaskCompletionSource<bool>();
        PendingCloseConfirmation = true;
        var confirmed = await _closeConfirmationTcs.Task;
        PendingCloseConfirmation = false;
        _closeConfirmationTcs = null;

        if (!confirmed)
        {
            return;
        }

        CancelScripting();
        if (_operationCompletionTcs is { } completion)
        {
            await completion.Task;
        }

        Factory?.CloseDockable(this);
    }

    [RelayCommand]
    private void ConfirmClose() => _closeConfirmationTcs?.TrySetResult(true);

    [RelayCommand]
    private void KeepWorking() => _closeConfirmationTcs?.TrySetResult(false);

    // Re-scripts the object from the server. Not a special "no-cache" path - it's just calling
    // ScriptWithConsentAsync again, which is already enough: SqlSearchService.ScriptObjectAsync
    // and EncryptedModuleDecryptor both open a brand new SMO Server/connection per call rather
    // than reusing one across calls, and SMO's own object-model cache lives on the Server
    // instance itself (no static/cross-instance cache), so a new Server means no stale metadata
    // regardless of what changed on the actual database since the last load. Reuses LoadAsync
    // unchanged - it already fully resets Original/Formatted/dependents and is itself
    // cancelable via the same IsScripting/_cts/CancelScriptingCommand machinery the initial
    // load uses, which is also why the header row (Original/Reformatted/Refresh) hides itself
    // and the Scripting.../Cancel banner takes over while a refresh is running.
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        IsInErrorState = false;
        IsScripting = true;
        _ = LoadAsync();
    }

    private bool CanRefresh => !IsScripting;

    [RelayCommand(CanExecute = nameof(CanShowFormatted))]
    private void ShowFormatted()
    {
        DisplayMode = SqlDisplayMode.Formatted;
        CurrentSqlText = FormattedSqlText;
    }

    private bool CanShowFormatted => !FormattingSafetyCheckFailed;

    [RelayCommand]
    private void ShowOriginal()
    {
        DisplayMode = SqlDisplayMode.Original;
        CurrentSqlText = OriginalSqlText;
    }

    [RelayCommand]
    private void DismissFormattingSafetyWarning()
    {
        PendingFormattingSafetyWarning = false;
    }

    /// <summary>
    /// Maps a caret offset in the text currently shown (Formatted if <paramref name="fromFormatted"/>,
    /// Original otherwise) to the closest equivalent offset in the *other* rendering, using the
    /// token position map captured when this script was last (re)loaded - lets the view keep the
    /// caret "in the same place" across an Original/Formatted toggle. Returns null when no
    /// mapping is available (e.g. this script went through one of the canonicalization service's
    /// fallback formatting paths, which don't produce token positions).
    /// </summary>
    public int? MapCaretOffset(int offset, bool fromFormatted)
    {
        if (_tokenPositions is null || _tokenPositions.Count == 0)
        {
            return null;
        }

        SqlTokenPosition? nearest = null;
        var bestDistance = int.MaxValue;

        foreach (var tokenPosition in _tokenPositions)
        {
            var tokenStart = fromFormatted ? tokenPosition.FormattedOffset : tokenPosition.SourceOffset;
            var distance = Math.Abs(tokenStart - offset);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = tokenPosition;
            }
        }

        if (nearest is not { } match)
        {
            return null;
        }

        var matchedStart = fromFormatted ? match.FormattedOffset : match.SourceOffset;
        var mappedStart = fromFormatted ? match.SourceOffset : match.FormattedOffset;

        // Preserve the caret's position *within* the matched token (e.g. 3 characters into an
        // identifier) rather than always snapping to the token's own start.
        return mappedStart + (offset - matchedStart);
    }

    [RelayCommand]
    private void ToggleDisplayMode()
    {
        if (IsShowingOriginal && CanShowFormatted)
        {
            ShowFormatted();
        }
        else
        {
            ShowOriginal();
        }
    }

    public void SetDependentObjects(IEnumerable<SearchResultViewModel> dependents)
    {
        _dependentObjects.Clear();
        foreach (var dependent in dependents)
        {
            _dependentObjects.Add(dependent);
        }

        OnPropertyChanged(nameof(HasDependentObjects));
    }

    private void SetReferences(
        IReadOnlyList<SqlDocumentReference> originalReferences,
        IReadOnlyList<SqlDocumentReference> formattedReferences)
    {
        _originalReferences = originalReferences;
        _formattedReferences = formattedReferences;
        OnPropertyChanged(nameof(ActiveReferences));
    }

    public void OpenReference(SqlDocumentReference reference)
    {
        if (!reference.IsClickable || reference.Target is null || _searchService is null)
        {
            return;
        }

        var doc = new SqlDocumentViewModel(reference.Target, _connectionString, _searchService);
        WeakReferenceMessenger.Default.Send(new OpenDocumentMessage(doc));
    }

    [RelayCommand]
    private void ScriptDependentObject(SearchResultViewModel? dependent)
    {
        if (dependent is null || _searchService is null)
        {
            return;
        }

        var doc = new SqlDocumentViewModel(dependent, _connectionString, _searchService);
        WeakReferenceMessenger.Default.Send(new OpenDocumentMessage(doc));
    }
}
