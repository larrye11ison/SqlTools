using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlPhanos.ViewModels;

public partial class SearchResultViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullyQualifiedName))]
    private string _dbName = "";

    [ObservableProperty]
    private bool _isEncrypted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullyQualifiedName))]
    private string _objectName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParent))]
    private string _parentFqName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullyQualifiedName))]
    private string _schemaName = "";

    [ObservableProperty]
    private string _serverName = "";

    [ObservableProperty]
    private string _typeDesc = "";

    public string FullyQualifiedName => $"{DbName}.{SchemaName}.{ObjectName}";

    public bool HasParent => !string.IsNullOrWhiteSpace(ParentFqName);
}
