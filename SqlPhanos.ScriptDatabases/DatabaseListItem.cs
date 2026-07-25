using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlPhanos.ScriptDatabases;

/// <summary>
/// One database's row within a Script Databases session: selection checkbox plus its own
/// scripting progress, independent of the other selected databases.
/// </summary>
public partial class DatabaseListItem : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgressBar))]
    [NotifyPropertyChangedFor(nameof(CanChangeSelection))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressStatus))]
    private string _progressStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFinalOutputDirectory))]
    private string _finalOutputDirectory = string.Empty;

    public DatabaseListItem(string name, bool isSelected)
    {
        _name = name;
        _isSelected = isSelected;
    }

    public bool ShowProgressBar => IsBusy || ProgressPercent > 0 || HasProgressStatus;

    public bool HasProgressStatus => !string.IsNullOrWhiteSpace(ProgressStatus);

    public bool HasFinalOutputDirectory => !string.IsNullOrWhiteSpace(FinalOutputDirectory);

    public bool CanChangeSelection => !IsBusy;

    public void BeginScripting()
    {
        IsBusy = true;
        ProgressPercent = 0;
        ProgressStatus = "Starting...";
    }

    public void ReportProgress(double percent, string status)
    {
        // Ignore late progress callbacks after Complete/Fail/Cancel (dispatcher race).
        if (!IsBusy)
        {
            return;
        }

        ProgressPercent = percent;
        ProgressStatus = status;
    }

    public void CompleteScripting()
    {
        IsBusy = false;
        ProgressPercent = 100;
        ProgressStatus = "Complete.";
    }

    public void FailScripting(string message)
    {
        IsBusy = false;
        ProgressStatus = message;
    }

    public void CancelScripting()
    {
        IsBusy = false;
        ProgressStatus = "Cancelled.";
    }
}
