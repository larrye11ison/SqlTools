namespace SqlPhanos.ViewModels;

public sealed class SqlDocumentReference
{
    public SqlDocumentReference(
        int offset,
        int length,
        string displayName,
        SearchResultViewModel? target,
        bool isLinkedServer,
        string? resolutionWarning = null,
        bool isRemoteDataSource = false)
    {
        Offset = offset;
        Length = length;
        DisplayName = displayName;
        Target = target;
        IsLinkedServer = isLinkedServer;
        ResolutionWarning = resolutionWarning;
        IsRemoteDataSource = isRemoteDataSource;
    }

    public int Offset { get; }

    public int Length { get; }

    public string DisplayName { get; }

    public SearchResultViewModel? Target { get; }

    public bool IsLinkedServer { get; }

    public string? ResolutionWarning { get; }

    public bool IsRemoteDataSource { get; }

    public bool IsExternalReference => IsLinkedServer || IsRemoteDataSource;

    public bool IsClickable => Target is not null && !IsExternalReference;

    public bool IsUnresolved => !IsExternalReference && Target is null;

    public bool Contains(int offset) => offset >= Offset && offset < Offset + Length;

    public string ToolTipText => IsLinkedServer
        ? $"{DisplayName}\nLinked-server reference; opening is not supported."
        : IsRemoteDataSource
            ? $"{DisplayName}\nRemote data-source reference; opening is not supported."
        : Target is not null
            ? $"{Target.TypeDisplay}: {Target.DbName}.{Target.SchemaName}.{Target.ObjectName}\nClick to script in a new tab."
            : $"{DisplayName}\n{ResolutionWarning ?? "Referenced object could not be resolved. The script may fail until the reference is corrected."}";
}
