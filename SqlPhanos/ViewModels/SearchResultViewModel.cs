using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlPhanos.ViewModels;

public partial class SearchResultViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullyQualifiedName))]
    private string _dbName = "";

    [ObservableProperty]
    private bool _isEncrypted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScriptButtonLabel))]
    [NotifyPropertyChangedFor(nameof(CanScriptObject))]
    [NotifyCanExecuteChangedFor(nameof(CancelScriptingCommand))]
    private bool _isScripting;

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

    public CancellationTokenSource? ScriptingCts { get; set; }

    public string ScriptButtonLabel => IsScripting ? "Scripting..." : "Script";

    public bool CanScriptObject => !IsScripting;

    public string FullyQualifiedName => $"{DbName}.{SchemaName}.{ObjectName}";

    public bool HasParent => !string.IsNullOrWhiteSpace(ParentFqName);

    [RelayCommand(CanExecute = nameof(IsScripting))]
    private void CancelScripting()
    {
        ScriptingCts?.Cancel();
    }
}