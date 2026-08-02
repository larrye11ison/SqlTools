namespace SqlPhanos.DependencyIndex;

public enum DiscoveryStatus { Connected, LinkedOnly, ManuallyMapped, Unavailable }
public enum AnalysisStatus { Complete, NoDefinition, Encrypted, ParseError, DynamicSql, Inaccessible }
public enum ScanOutcome { Running, Succeeded, Failed, Cancelled, Inaccessible }
public enum ReferenceClassification { Local, LinkedServer, RemoteDataSource, Structural }
public enum ResolutionStatus { Resolved, NotFound, Ambiguous, ExternalUnmapped, Inaccessible }
public enum EvidenceKind { ScriptDom, ForeignKey, TriggerParent, Synonym, Type, Constraint }
public enum GraphDirection { Uses, UsedBy, Both }
public enum IndexProgressPhase
{
    Initializing,
    DiscoveringServer,
    EnumeratingDatabases,
    CollectingDatabase,
    CollectionCompleted,
    AnalyzingDatabase,
    DecryptingModules,
    PromotingDatabase,
    Reconciling,
    DatabaseCompleted,
    DatabaseFailed,
    Completed,
    Cancelled,
}

/// <summary>
/// How the builder should handle objects that are WITH ENCRYPTION and therefore need a
/// Dedicated Administrator Connection (DAC) decrypt to derive any dependency edges at all.
/// </summary>
public enum EncryptedModuleDecryptMode
{
    /// <summary>Throw EncryptedModulesConsentRequiredException if any object needs a decrypt attempt.</summary>
    PromptIfNeeded,

    /// <summary>Attempt DAC decrypt for objects that need it, without asking.</summary>
    Allow,

    /// <summary>Never attempt DAC decrypt; leave such objects as AnalysisStatus.Encrypted.</summary>
    Skip,
}

/// <summary>
/// Per-object analysis state as of a scan - both the freshly computed state written for this
/// scan, and (when read back via GetObjectAnalysisAsync) the prior scan's state used to decide
/// whether an object needs re-analysis. ModifyDate is carried here (in addition to being stored
/// directly on schema_objects) because it is the only change-detection signal available for
/// encrypted objects: SQL Server never returns their definition text, so DefinitionHash is
/// always null for them and can't be compared the way it is for everything else.
/// </summary>
public readonly record struct ObjectAnalysisState(
    string? Hash, AnalysisStatus Status, bool Dynamic, DateTimeOffset? ModifyDate);

public sealed record IndexProgress(
    IndexProgressPhase Phase, int CurrentDatabase, int TotalDatabases,
    string? DatabaseName, int ObjectCount, int EdgeCount, string? Message = null);

public sealed record ServerEndpoint(
    long Id, string? CanonicalKey, string DisplayName, string? DataSource,
    string? ActualServerName, string? MachineName, string? InstanceName,
    int? EngineEdition, string? ProductVersion, DiscoveryStatus DiscoveryStatus,
    DateTimeOffset FirstObservedUtc, DateTimeOffset LastObservedUtc);

public sealed record ConnectionBinding(
    long Id, long ServerId, string ConnectionProfileId, bool IsPreferred,
    DateTimeOffset? LastVerifiedUtc);

public sealed record LinkedServerAlias(
    long Id, long SourceServerId, string AliasName, string? Product, string? Provider,
    string? DataSource, string? Location, string? DefaultCatalog,
    bool IsDataAccessEnabled, bool IsRpcOutEnabled, long? RemoteServerId,
    DateTimeOffset CapturedUtc);

public sealed record DatabaseRecord(
    long Id, long ServerId, int SqlDatabaseId, string Name, string? CollationName,
    string StateDescription, bool IsAccessible, long? LastSuccessfulScanId);

public sealed record SchemaObjectRecord(
    long Id, long DatabaseId, int SqlObjectId, string SchemaName, string Name,
    string TypeDescription, long? ParentObjectKey, bool IsEncrypted,
    DateTimeOffset? CreateDate, DateTimeOffset? ModifyDate, string? DefinitionHash,
    AnalysisStatus AnalysisStatus, bool HasDynamicSql, bool IsDeleted, long LastSeenScanId);

public sealed record DependencyOccurrence(
    int StartOffset, int Length, int StartLine, int StartColumn, string ReferenceText);

public sealed record DependencyEdgeDraft(
    int SourceSqlObjectId, int? TargetSqlObjectId, int? TargetSqlDatabaseId,
    string ReferenceKind, ReferenceClassification Classification,
    ResolutionStatus ResolutionStatus, long? TargetServerId, string? TargetLinkedAlias,
    string? TargetDatabaseName, string? TargetSchemaName, string TargetObjectName,
    string? RawServerPart, string? RawDatabasePart, string? RawSchemaPart,
    string RawObjectPart, int PartCount, EvidenceKind EvidenceKind,
    IReadOnlyList<DependencyOccurrence> Occurrences);

public sealed record DependencyEdgeRecord(
    long Id, long SourceObjectKey, long? TargetObjectKey, string ReferenceKind,
    ReferenceClassification Classification, ResolutionStatus ResolutionStatus,
    long? TargetServerId, long? TargetLinkedServerId, string? TargetDatabaseName,
    string? TargetSchemaName, string TargetObjectName, string? RawServerPart,
    string? RawDatabasePart, string? RawSchemaPart, string RawObjectPart,
    int PartCount, EvidenceKind EvidenceKind, long FirstSeenScanId, long LastSeenScanId,
    IReadOnlyList<DependencyOccurrence> Occurrences);

public sealed record ObjectSearchDto(
    long ObjectKey, long ServerId, string Server, long DatabaseId, string Database,
    string Schema, string Name, string Type, long? ParentObjectKey, string? Parent,
    bool Encrypted, AnalysisStatus AnalysisStatus, bool HasDynamicSql, bool IsDeleted);

public sealed record CoverageSummary(
    int Complete, int NoDefinition, int Encrypted, int ParseErrors, int DynamicSql,
    int Inaccessible, int UnresolvedEdges, int AmbiguousEdges);

public sealed record FreshnessSummary(
    DateTimeOffset? LastSuccessfulScanUtc, bool IsStale, int FailedDatabases,
    int InaccessibleDatabases);

public sealed record GraphFilter(
    IReadOnlySet<string>? DatabaseNames = null,
    IReadOnlySet<string>? SchemaNames = null,
    IReadOnlySet<string>? TypeDescriptions = null,
    bool IncludeUnresolved = true,
    // DRI = database-enforced constraint plumbing (foreign keys, CHECK/DEFAULT constraint
    // objects) as opposed to code-level usage (ScriptDom references, triggers, synonyms, type
    // uses). Off by default - these are usually noise next to the object relationships people
    // actually came here to see.
    bool IncludeDriDependencies = false);

public sealed record GraphNode(ObjectSearchDto Object, int Depth);
public sealed record GraphEdge(DependencyEdgeRecord Edge, long SourceObjectKey, long? TargetObjectKey);
public sealed record GraphResult(
    IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, bool Truncated,
    CoverageSummary Coverage, FreshnessSummary Freshness);
public sealed record ShortestPathResult(
    IReadOnlyList<ObjectSearchDto> Nodes, IReadOnlyList<DependencyEdgeRecord> Edges);

public sealed record ServerIdentity(
    string CanonicalKey, string DisplayName, string DataSource, string ActualServerName,
    string MachineName, string? InstanceName, int EngineEdition, string ProductVersion);

public sealed record CatalogDatabase(
    int SqlDatabaseId, string Name, string? CollationName, string StateDescription,
    bool IsAccessible);

public sealed record CatalogLinkedServer(
    string AliasName, string? Product, string? Provider, string? DataSource,
    string? Location, string? DefaultCatalog, bool IsDataAccessEnabled, bool IsRpcOutEnabled);

public sealed record CatalogObject(
    int SqlObjectId, string SchemaName, string Name, string TypeDescription,
    int? ParentSqlObjectId, bool IsEncrypted, DateTimeOffset? CreateDate,
    DateTimeOffset? ModifyDate, string? Definition);

public sealed record CatalogForeignKey(int SourceSqlObjectId, int TargetSqlObjectId, string Name);
public sealed record CatalogSynonym(int SourceSqlObjectId, string BaseObjectName);
public sealed record CatalogTypeUse(int SourceSqlObjectId, int TypeSqlObjectId, string TypeName);

public sealed record DatabaseCatalogSnapshot(
    CatalogDatabase Database, IReadOnlyList<CatalogObject> Objects,
    IReadOnlyList<CatalogForeignKey> ForeignKeys, IReadOnlyList<CatalogSynonym> Synonyms,
    IReadOnlyList<CatalogTypeUse> TypeUses, ScanOutcome Outcome, string? Error = null);

public sealed record ServerCatalogSnapshot(
    ServerIdentity Identity, IReadOnlyList<CatalogLinkedServer> LinkedServers,
    IReadOnlyList<DatabaseCatalogSnapshot> Databases);

public sealed record IndexBuildOptions(
    bool FullRebuild = false, int MaxAnalysisConcurrency = 0,
    EncryptedModuleDecryptMode EncryptedModuleDecrypt = EncryptedModuleDecryptMode.PromptIfNeeded);
public sealed record DatabaseFailureDetail(string DatabaseName, ScanOutcome Outcome, string? Error);
public sealed record IndexBuildResult(long ScanId, ScanOutcome Outcome, int DatabasesSucceeded,
    int DatabasesFailed, int ObjectsIndexed, int EdgesIndexed, long ServerId,
    IReadOnlyList<DatabaseFailureDetail> FailureDetails);
