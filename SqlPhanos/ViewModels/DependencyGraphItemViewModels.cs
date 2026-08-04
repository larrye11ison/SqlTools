using CommunityToolkit.Mvvm.ComponentModel;
using SqlPhanos.DependencyIndex;
using System.Linq;

namespace SqlPhanos.ViewModels;

public partial class DependencyGraphNodeViewModel : ObservableObject
{
    public required string Id { get; init; }
    public long? ObjectKey { get; init; }
    public required string Server { get; init; }
    public required string Database { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public int Depth { get; init; }
    public bool IsRoot { get; init; }
    public bool IsExternal { get; init; }
    public bool IsUnresolved { get; init; }
    public bool IsEncrypted { get; init; }
    public bool HasCoverageWarning { get; init; }
    public ObjectSearchDto? IndexedObject { get; init; }

    // Explains why this node isn't a plain resolved object - shown as a tooltip in the Objects
    // list. Null for ordinary nodes with nothing to explain.
    public string? WarningText { get; init; }

    [ObservableProperty]
    private bool _isExpanded = true;

    // Precedence: a reference that could not be found/disambiguated at all is a harder problem
    // than a reference we deliberately can't inspect (linked server/remote) or one whose source
    // object has a known analysis limitation (parse error, dynamic SQL, encrypted, inaccessible) -
    // so those two softer cases share one "caution" treatment rather than three visual tiers.
    public bool ShowUnresolvedIndicator => IsUnresolved && !IsExternal;
    public bool ShowCautionIndicator => !ShowUnresolvedIndicator && (IsExternal || HasCoverageWarning);
    public bool IsNormalStatus => !ShowUnresolvedIndicator && !ShowCautionIndicator;

    // External/unresolved nodes have no real indexed object to script or open a Dependencies
    // tab for - gates both the Objects list buttons and the graph canvas's button hot-zones.
    public bool HasIndexedObject => IndexedObject is not null;

    // Drives the "dynamic SQL only" filter toggle - external/unresolved nodes have no
    // AnalysisStatus at all and are never included regardless of the toggle.
    public bool IsDynamicSql => IndexedObject?.AnalysisStatus == AnalysisStatus.DynamicSql;

    public string QualifiedName =>
        string.Join(
            ".",
            new[] { Server, Database, Schema, Name }
                .Where(static part => !string.IsNullOrWhiteSpace(part)));

    public string DisplayName => string.IsNullOrWhiteSpace(Schema)
        ? Name
        : $"{Schema}.{Name}";

    public string Subtitle => string.Join(
        " | ",
        new[] { Database, Type }
            .Where(static part => !string.IsNullOrWhiteSpace(part)));

    // DisplayName/Subtitle both get truncated with an ellipsis in the Objects list and the
    // graph canvas alike - this is what both surfaces show on hover so the full name is always
    // reachable, plus WarningText when there's something to explain.
    public string TooltipText => WarningText is null
        ? QualifiedName
        : $"{QualifiedName}\n{WarningText}";
}

public sealed record DependencyGraphEdgeViewModel(
    long EdgeId,
    string SourceNodeId,
    string TargetNodeId,
    string Label,
    ReferenceClassification Classification,
    ResolutionStatus ResolutionStatus,
    EvidenceKind EvidenceKind,
    DependencyEdgeRecord IndexedEdge)
{
    public string DisplayText =>
        $"{Label}: " +
        string.Join(
            ".",
            new[]
            {
                IndexedEdge.RawServerPart,
                IndexedEdge.TargetDatabaseName ?? IndexedEdge.RawDatabasePart,
                IndexedEdge.TargetSchemaName ?? IndexedEdge.RawSchemaPart,
                IndexedEdge.TargetObjectName,
            }.Where(static part => !string.IsNullOrWhiteSpace(part)));

    public string StatusText =>
        $"{Classification} | {ResolutionStatus} | {EvidenceKind}";

    public bool HasWarning =>
        ResolutionStatus != ResolutionStatus.Resolved ||
        Classification is ReferenceClassification.LinkedServer or
            ReferenceClassification.RemoteDataSource;
}
