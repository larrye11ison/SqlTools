using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using SqlPhanos.DependencyIndex;
using SqlPhanos.Enums;
using SqlPhanos.Messages;
using SqlPhanos.ScriptDatabases;
using SqlPhanos.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SqlPhanos.ViewModels;

public partial class DependencyExplorerDocumentViewModel : Document, IHasTabHeaderLines
{
    private readonly string _connectionString;
    private readonly Guid _connectionProfileId;
    private readonly SqlConnectionViewModel _connection;
    private readonly SearchResultViewModel _root;
    private readonly SqlSearchService _searchService;
    private readonly DependencyIndexStore _store;
    private readonly DependencyIndexBuilder _builder;
    private readonly DependencyGraphQueryService _graph;
    private CancellationTokenSource? _operationCancellation;
    private TaskCompletionSource<bool>? _encryptedConsentTcs;
    private IReadOnlyList<DependencyGraphNodeViewModel> _allNodes =
        Array.Empty<DependencyGraphNodeViewModel>();
    private IReadOnlyList<DependencyGraphEdgeViewModel> _allEdges =
        Array.Empty<DependencyGraphEdgeViewModel>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuildIndex))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(TabIconState))]
    [NotifyCanExecuteChangedFor(nameof(BuildIndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(FullRebuildCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabIconState))]
    private bool _isInErrorState;

    [ObservableProperty]
    private bool _hasIndex;

    [ObservableProperty]
    private string _status = "Opening dependency index...";

    [ObservableProperty]
    private string _coverageSummary = "";

    [ObservableProperty]
    private string _freshnessSummary = "";

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterPlaceholderText))]
    private bool _showDynamicSqlOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDynamicSqlObjects))]
    private int _dynamicSqlCount;

    [ObservableProperty]
    private GraphDirection _selectedDirection = GraphDirection.UsedBy;

    [ObservableProperty]
    private int _maxDepth = 2;

    [ObservableProperty]
    private int _nodeLimit = 300;

    [ObservableProperty]
    private bool _includeDriDependencies;

    [ObservableProperty]
    private IReadOnlyList<DependencyGraphNodeViewModel> _nodes =
        Array.Empty<DependencyGraphNodeViewModel>();

    [ObservableProperty]
    private IReadOnlyList<DependencyGraphEdgeViewModel> _edges =
        Array.Empty<DependencyGraphEdgeViewModel>();

    [ObservableProperty]
    private DependencyGraphNodeViewModel? _selectedNode;

    [ObservableProperty]
    private DependencyGraphEdgeViewModel? _selectedEdge;

    [ObservableProperty]
    private bool _pendingEncryptedConsent;

    [ObservableProperty]
    private string _pendingEncryptedObjectName = "";

    [ObservableProperty]
    private int _pendingEncryptedCount;

    public DependencyExplorerDocumentViewModel(
        SearchResultViewModel root,
        SqlConnectionViewModel connection,
        SqlSearchService searchService)
    {
        _root = root;
        _connectionString = connection.ConnectionString;
        _connectionProfileId = connection.ProfileId;
        _connection = connection;
        _searchService = searchService;
        var indexDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SqlPhanos",
            "dependency-indexes");
        _store = new DependencyIndexStore(
            Path.Combine(indexDirectory, $"{connection.ProfileId:N}.sqlite3"));
        _builder = new DependencyIndexBuilder(_store);
        _graph = new DependencyGraphQueryService(_store);

        Id = $"DependencyExplorer:{Guid.NewGuid():N}";
        Title = $"Dependencies: {root.SchemaName}.{root.ObjectName}";
        _ = InitializeAsync();
    }

    public DependencyExplorerDocumentViewModel()
    {
        _root = new SearchResultViewModel
        {
            ServerName = "Server",
            DbName = "Database",
            SchemaName = "dbo",
            ObjectName = "ObjectA",
            TypeDesc = "VIEW",
        };
        _connectionProfileId = Guid.Empty;
        _connectionString = "";
        _connection = new SqlConnectionViewModel();
        _searchService = new SqlSearchService();
        _store = new DependencyIndexStore(Path.Combine(
            Path.GetTempPath(),
            "SqlPhanos-design-dependency-index.sqlite3"));
        _builder = new DependencyIndexBuilder(_store);
        _graph = new DependencyGraphQueryService(_store);
        Id = "DependencyExplorerDesign";
        Title = "Dependencies: dbo.ObjectA";
        Status = "Build the server dependency index to begin.";
    }

    public IReadOnlyList<GraphDirection> Directions { get; } =
        Enum.GetValues<GraphDirection>();
    public IReadOnlyList<int> DepthOptions { get; } =
        Enumerable.Range(1, 12).ToArray();
    public IReadOnlyList<int> NodeLimitOptions { get; } =
        new[] { 100, 300, 500, 1000, 2000 };

    public bool CanBuildIndex => !IsBusy && !string.IsNullOrWhiteSpace(_connectionString);
    public bool CanCancel => IsBusy;
    public bool HasNodes => Nodes.Count > 0;
    public bool HasDynamicSqlObjects => DynamicSqlCount > 0;

    // The free-text filter and the "dynamic SQL only" toggle apply together (AND'ed in
    // ApplyFilter) - this is the one visible hint that the text box is now searching within an
    // already-narrowed set rather than the whole graph, alongside the toggle button's own
    // pressed/checked state.
    public string FilterPlaceholderText =>
        ShowDynamicSqlOnly ? "Filter dynamic-SQL objects..." : "Filter visible objects...";

    public string TabHeaderLine1 => _root.ServerName;
    public string TabHeaderLine2 => $"Dependencies: {_root.SchemaName}.{_root.ObjectName}";

    public DocumentTabIconState TabIconState =>
        IsBusy ? DocumentTabIconState.Busy :
        IsInErrorState ? DocumentTabIconState.Error :
        DocumentTabIconState.None;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnShowDynamicSqlOnlyChanged(bool value) => ApplyFilter();

    partial void OnSelectedDirectionChanged(GraphDirection value)
    {
        if (!IsBusy)
        {
            _ = LoadGraphAsync();
        }
    }

    partial void OnMaxDepthChanged(int value)
    {
        MaxDepth = Math.Clamp(value, 1, 12);
        if (!IsBusy)
        {
            _ = LoadGraphAsync();
        }
    }

    partial void OnNodeLimitChanged(int value)
    {
        NodeLimit = Math.Clamp(value, 25, 2000);
        if (!IsBusy)
        {
            _ = LoadGraphAsync();
        }
    }

    partial void OnIncludeDriDependenciesChanged(bool value)
    {
        if (!IsBusy)
        {
            _ = LoadGraphAsync();
        }
    }

    partial void OnNodesChanged(IReadOnlyList<DependencyGraphNodeViewModel> value) =>
        OnPropertyChanged(nameof(HasNodes));

    private async Task InitializeAsync()
    {
        try
        {
            await Task.Run(() => _store.InitializeAsync());
            await LoadGraphAsync();
        }
        catch (Exception exception)
        {
            Status = $"Dependency index could not be opened: {exception.Message}";
            IsInErrorState = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBuildIndex))]
    private Task BuildIndexAsync() => BuildIndexCoreAsync(fullRebuild: false);

    [RelayCommand(CanExecute = nameof(CanBuildIndex))]
    private Task FullRebuildAsync() => BuildIndexCoreAsync(fullRebuild: true);

    private async Task BuildIndexCoreAsync(bool fullRebuild)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;
        IsBusy = true;
        IsInErrorState = false;
        Status = fullRebuild
            ? "Rebuilding dependency index..."
            : "Refreshing dependency index...";
        // Constructed on the UI thread so Progress<T> captures its SynchronizationContext -
        // Report() calls made from the background Task.Run below marshal back automatically.
        var progress = new Progress<IndexProgress>(ReportBuildProgress);
        try
        {
            var decryptMode = EncryptedDecryptConsentSessionStore.TryGet(_connectionProfileId) switch
            {
                true => EncryptedModuleDecryptMode.Allow,
                false => EncryptedModuleDecryptMode.Skip,
                null => EncryptedModuleDecryptMode.PromptIfNeeded,
            };

            // Catalog collection (the SQL Server network round trips) happens once regardless of
            // how many times the consent prompt below needs an answer - only the build/promote
            // step, which is where the consent gate actually lives, gets retried.
            var snapshot = await Task.Run(() =>
            {
                var collector = new SqlServerCatalogCollector(
                    _connectionString,
                    maxConcurrentDatabaseScans: 3);
                return collector.CollectAsync(progress, cancellationToken);
            }, cancellationToken);

            IndexBuildResult result;
            while (true)
            {
                try
                {
                    result = await Task.Run(() => _builder.BuildAsync(
                        snapshot,
                        new IndexBuildOptions(FullRebuild: fullRebuild, EncryptedModuleDecrypt: decryptMode),
                        _connectionString,
                        cancellationToken,
                        progress), cancellationToken);
                    break;
                }
                catch (EncryptedModulesConsentRequiredException consent)
                {
                    var accepted = await RequestEncryptedConsentAsync(consent.DatabaseName, consent.EncryptedCount);
                    EncryptedDecryptConsentSessionStore.Remember(_connectionProfileId, accepted);
                    decryptMode = accepted ? EncryptedModuleDecryptMode.Allow : EncryptedModuleDecryptMode.Skip;
                }
            }

            var serverId = await Task.Run(() => _store.UpsertServerAsync(
                snapshot.Identity, cancellationToken: cancellationToken), cancellationToken);
            await Task.Run(() => _store.UpsertConnectionBindingAsync(
                serverId,
                _connectionProfileId.ToString("D"),
                isPreferred: true,
                DateTimeOffset.UtcNow,
                cancellationToken), cancellationToken);

            Status =
                $"Index complete: {result.ObjectsIndexed:N0} objects and " +
                $"{result.EdgesIndexed:N0} changed dependencies across " +
                $"{result.DatabasesSucceeded:N0} database(s)." +
                (result.DatabasesFailed > 0
                    ? $" {result.DatabasesFailed:N0} database(s) failed; the last good snapshots were retained. " +
                      string.Join(" ", result.FailureDetails.Select(detail =>
                          $"[{detail.DatabaseName}: {detail.Error ?? detail.Outcome.ToString()}]"))
                    : "");
            if (result.DatabasesFailed > 0)
            {
                IsInErrorState = true;
            }
            await LoadGraphAsync(preserveStatus: true);
        }
        catch (OperationCanceledException)
        {
            Status = "Dependency indexing cancelled. Existing successful snapshots were retained.";
        }
        catch (Exception exception)
        {
            Status = $"Dependency indexing failed: {exception.Message}";
            IsInErrorState = true;
        }
        finally
        {
            IsBusy = false;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _operationCancellation?.Cancel();
    }

    // Mirrors SqlDocumentViewModel's identical encrypted-module consent flow - see that type for
    // why the prompt is shown in-tab rather than as a modal dialog.
    private Task<bool> RequestEncryptedConsentAsync(string scopeDescription, int encryptedCount)
    {
        _encryptedConsentTcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(() =>
        {
            PendingEncryptedObjectName = scopeDescription;
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

    private void ReportBuildProgress(IndexProgress progress)
    {
        var databaseContext = progress.TotalDatabases > 0
            ? $" [{progress.CurrentDatabase}/{progress.TotalDatabases}" +
              (progress.DatabaseName is { } name ? $": {name}" : "") + "]"
            : "";
        var counts = progress.ObjectCount > 0 || progress.EdgeCount > 0
            ? $" ({progress.ObjectCount:N0} objects, {progress.EdgeCount:N0} edges so far)"
            : "";
        Status = (progress.Message ?? progress.Phase.ToString()) + databaseContext + counts;
    }

    [RelayCommand]
    private async Task RefreshGraphAsync()
    {
        await LoadGraphAsync();
    }

    private async Task LoadGraphAsync(bool preserveStatus = false)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var candidates = await Task.Run(
                () => _graph.SearchObjectsAsync(_root.ObjectName, 500));
            var indexedRoot = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Database, _root.DbName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Schema, _root.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Name, _root.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                IsCompatibleType(candidate.Type, _root.TypeDesc));
            if (indexedRoot is null)
            {
                HasIndex = candidates.Count > 0;
                _allNodes = Array.Empty<DependencyGraphNodeViewModel>();
                _allEdges = Array.Empty<DependencyGraphEdgeViewModel>();
                ApplyFilter();
                Status = HasIndex
                    ? "This object is not in the current index. Refresh the index to include it."
                    : "No dependency index exists for this connection. Select Build Index to create it.";
                CoverageSummary = "";
                DynamicSqlCount = 0;
                FreshnessSummary = "";
                return;
            }

            HasIndex = true;
            var result = await Task.Run(() => _graph.TraverseAsync(
                indexedRoot.ObjectKey,
                SelectedDirection,
                MaxDepth,
                NodeLimit,
                new GraphFilter(IncludeDriDependencies: IncludeDriDependencies),
                CancellationToken.None));
            MapGraph(result, indexedRoot.ObjectKey);
            if (!preserveStatus)
            {
                Status = result.Truncated
                    ? $"Showing the first {result.Nodes.Count:N0} nodes; increase the node limit to see more."
                    : $"Showing {result.Nodes.Count:N0} objects and {result.Edges.Count:N0} dependencies.";
            }
            CoverageSummary = FormatCoverage(result.Coverage);
            DynamicSqlCount = result.Coverage.DynamicSql;
            FreshnessSummary = result.Freshness.LastSuccessfulScanUtc is { } last
                ? $"Last indexed {last.LocalDateTime:g}" +
                  (result.Freshness.IsStale ? " (stale)" : "")
                : "No successful index scan.";
        }
        catch (Exception exception)
        {
            Status = $"Dependency graph could not be loaded: {exception.Message}";
            IsInErrorState = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void MapGraph(GraphResult graph, long rootObjectKey)
    {
        var nodes = graph.Nodes
            .Select(item => CreateNode(item.Object, item.Depth, item.Object.ObjectKey == rootObjectKey))
            .ToDictionary(static node => node.Id, StringComparer.Ordinal);
        var edges = new List<DependencyGraphEdgeViewModel>();

        foreach (var graphEdge in graph.Edges)
        {
            var sourceId = ObjectNodeId(graphEdge.SourceObjectKey);
            if (!nodes.ContainsKey(sourceId))
            {
                continue;
            }

            string targetId;
            if (graphEdge.TargetObjectKey is { } targetKey)
            {
                targetId = ObjectNodeId(targetKey);
                if (!nodes.ContainsKey(targetId))
                {
                    continue;
                }
            }
            else
            {
                targetId = $"edge:{graphEdge.Edge.Id}";
                nodes[targetId] = CreateExternalNode(
                    graphEdge.Edge,
                    nodes[sourceId].Depth + 1,
                    targetId);
            }

            edges.Add(new DependencyGraphEdgeViewModel(
                graphEdge.Edge.Id,
                sourceId,
                targetId,
                graphEdge.Edge.ReferenceKind,
                graphEdge.Edge.Classification,
                graphEdge.Edge.ResolutionStatus,
                graphEdge.Edge.EvidenceKind,
                graphEdge.Edge));
        }

        _allNodes = nodes.Values
            .OrderBy(static node => node.Depth)
            .ThenBy(static node => node.Database, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _allEdges = edges;
        ApplyFilter();
        SelectedNode = _allNodes.FirstOrDefault(static node => node.IsRoot);
    }

    private static DependencyGraphNodeViewModel CreateNode(
        ObjectSearchDto item,
        int depth,
        bool isRoot) =>
        new()
        {
            Id = ObjectNodeId(item.ObjectKey),
            ObjectKey = item.ObjectKey,
            Server = item.Server,
            Database = item.Database,
            Schema = item.Schema,
            Name = item.Name,
            Type = item.Type,
            Depth = depth,
            IsRoot = isRoot,
            IsEncrypted = item.Encrypted,
            HasCoverageWarning =
                item.AnalysisStatus is AnalysisStatus.ParseError or
                    AnalysisStatus.DynamicSql or
                    AnalysisStatus.Encrypted or
                    AnalysisStatus.Inaccessible,
            WarningText = FormatCoverageWarning(item.AnalysisStatus),
            IndexedObject = item,
        };

    private static DependencyGraphNodeViewModel CreateExternalNode(
        DependencyEdgeRecord edge,
        int depth,
        string id) =>
        new()
        {
            Id = id,
            Server = edge.RawServerPart ?? "",
            Database = edge.TargetDatabaseName ?? edge.RawDatabasePart ?? "",
            Schema = edge.TargetSchemaName ?? edge.RawSchemaPart ?? "",
            Name = edge.TargetObjectName,
            Type = FormatReferenceKind(edge.ReferenceKind),
            Depth = depth,
            IsExternal =
                edge.Classification is ReferenceClassification.LinkedServer or
                    ReferenceClassification.RemoteDataSource,
            IsUnresolved = edge.ResolutionStatus != ResolutionStatus.Resolved,
            HasCoverageWarning = true,
            WarningText = FormatUnresolvedWarning(edge),
        };

    private static string FormatReferenceKind(string referenceKind) => referenceKind switch
    {
        "TableOrView" => "Table or View",
        "SchemaObject" => "Schema Object",
        _ => referenceKind,
    };

    private static string FormatUnresolvedWarning(DependencyEdgeRecord edge) =>
        edge.Classification switch
        {
            ReferenceClassification.LinkedServer =>
                "References a linked server; the target could not be inspected and is not tracked as a real object.",
            ReferenceClassification.RemoteDataSource =>
                "References an external/remote data source; the target could not be inspected and is not tracked as a real object.",
            _ => edge.ResolutionStatus switch
            {
                ResolutionStatus.Ambiguous =>
                    "Multiple matching objects were found in the index, so this reference could not be resolved unambiguously.",
                ResolutionStatus.Inaccessible =>
                    "The target database was not accessible during the last index scan.",
                _ =>
                    "Not found in the index - this object may not exist on the server, may not have been indexed yet, or the reference may be misspelled.",
            },
        };

    private static string? FormatCoverageWarning(AnalysisStatus status) => status switch
    {
        AnalysisStatus.ParseError =>
            "This object's definition could not be parsed, so its outgoing references could not be analyzed.",
        AnalysisStatus.DynamicSql =>
            "This object builds SQL dynamically; references inside dynamic SQL are not analyzed.",
        AnalysisStatus.Encrypted =>
            "This object is encrypted (WITH ENCRYPTION) and could not be decrypted, so its outgoing references could not be analyzed.",
        AnalysisStatus.Inaccessible =>
            "This object's database was not accessible during the last index scan.",
        _ => null,
    };

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(FilterText) && !ShowDynamicSqlOnly)
        {
            Nodes = _allNodes;
            Edges = _allEdges;
            return;
        }

        // The two filters AND together (both must pass) - the root is always kept as an anchor
        // point regardless of either one, matching the free-text filter's existing behavior.
        var visibleIds = _allNodes
            .Where(node =>
                node.IsRoot ||
                ((string.IsNullOrWhiteSpace(FilterText) ||
                  node.QualifiedName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                  node.Type.Contains(FilterText, StringComparison.OrdinalIgnoreCase)) &&
                 (!ShowDynamicSqlOnly || node.IsDynamicSql)))
            .Select(static node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        Nodes = _allNodes.Where(node => visibleIds.Contains(node.Id)).ToArray();
        Edges = _allEdges
            .Where(edge =>
                visibleIds.Contains(edge.SourceNodeId) &&
                visibleIds.Contains(edge.TargetNodeId))
            .ToArray();
    }

    [RelayCommand]
    private void OpenNode(DependencyGraphNodeViewModel? node)
    {
        if (node?.IndexedObject is not { } item)
        {
            return;
        }

        var document = new SqlDocumentViewModel(CreateSearchResult(item), _connectionString, _searchService);
        WeakReferenceMessenger.Default.Send(new OpenDocumentMessage(document));
    }

    [RelayCommand]
    private void OpenNodeDependencies(DependencyGraphNodeViewModel? node)
    {
        if (node?.IndexedObject is not { } item)
        {
            return;
        }

        var document = new DependencyExplorerDocumentViewModel(
            CreateSearchResult(item), _connection, _searchService);
        WeakReferenceMessenger.Default.Send(new OpenDocumentMessage(document));
    }

    private static SearchResultViewModel CreateSearchResult(ObjectSearchDto item) =>
        new()
        {
            ServerName = item.Server,
            DbName = item.Database,
            SchemaName = item.Schema,
            ObjectName = item.Name,
            TypeDesc = item.Type,
            ParentFqName = item.Parent ?? "",
            IsEncrypted = item.Encrypted,
        };

    [RelayCommand]
    private void OpenEdgeSource(DependencyGraphEdgeViewModel? edge)
    {
        if (edge is null)
        {
            return;
        }

        OpenNode(_allNodes.FirstOrDefault(node =>
            string.Equals(node.Id, edge.SourceNodeId, StringComparison.Ordinal)));
    }

    [RelayCommand]
    private void IncreaseDepth()
    {
        if (MaxDepth < 12)
        {
            MaxDepth++;
        }
    }

    private static string ObjectNodeId(long objectKey) => $"object:{objectKey}";

    private static bool IsCompatibleType(string indexedType, string sourceType) =>
        string.Equals(indexedType, sourceType, StringComparison.Ordinal) ||
        sourceType == "TYPE_TABLE" && indexedType == "TABLE_TYPE";

    private static string FormatCoverage(CoverageSummary coverage)
    {
        var warnings = new List<string>();
        if (coverage.ParseErrors > 0)
        {
            warnings.Add($"{coverage.ParseErrors:N0} parse error(s)");
        }
        if (coverage.DynamicSql > 0)
        {
            warnings.Add($"{coverage.DynamicSql:N0} dynamic-SQL object(s)");
        }
        if (coverage.Encrypted > 0)
        {
            warnings.Add($"{coverage.Encrypted:N0} encrypted object(s)");
        }
        if (coverage.UnresolvedEdges > 0)
        {
            warnings.Add($"{coverage.UnresolvedEdges:N0} unresolved reference(s)");
        }
        if (coverage.AmbiguousEdges > 0)
        {
            warnings.Add($"{coverage.AmbiguousEdges:N0} ambiguous reference(s)");
        }
        return warnings.Count == 0
            ? "Coverage complete for the displayed graph."
            : "Coverage warnings: " + string.Join(", ", warnings) + ".";
    }
}
