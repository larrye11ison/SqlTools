using CommunityToolkit.Mvvm.ComponentModel;

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
    private string _parentFqName = "";

    [ObservableProperty]
    private string _schemaName = "";

    [ObservableProperty]
    private string _serverName = "";

    [ObservableProperty]
    private string _typeDesc = "";

    public bool CanScriptObject => true; // Placeholder logic
}