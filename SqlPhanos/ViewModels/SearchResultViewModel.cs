using CommunityToolkit.Mvvm.ComponentModel;
using SqlPhanos.Services;

namespace SqlPhanos.ViewModels;

public partial class SearchResultViewModel : ObservableObject
{
    [ObservableProperty]
    private string _dbName = "";

    [ObservableProperty]
    private bool _isEncrypted;

    [ObservableProperty]
    private string _objectName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParent))]
    private string _parentFqName = "";

    [ObservableProperty]
    private string _schemaName = "";

    [ObservableProperty]
    private string _serverName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeDisplay))]
    private string _typeDesc = "";

    public string TypeDisplay => SqlObjectTypeDisplayNames.GetFriendlyName(TypeDesc);

    public bool HasParent => !string.IsNullOrWhiteSpace(ParentFqName);
}
