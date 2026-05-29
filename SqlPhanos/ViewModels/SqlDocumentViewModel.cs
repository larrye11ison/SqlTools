using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using SqlPhanos.CodeFormatting;

namespace SqlPhanos.ViewModels;

/// <summary>
/// View model for a single SQL script document
/// </summary>
public partial class SqlDocumentViewModel : Document
{
    private readonly SqlCanonicalizationService _sqlCanonicalizationService = new();
    private string _currentSqlText = "";
    private string _filePath = "";
    private string _formattedSqlText = "";
    private bool _isShowingFormatted;
    private string _originalSqlText = "";

    public string CurrentSqlText
    {
        get => _currentSqlText;
        private set => SetProperty(ref _currentSqlText, value);
    }

    public string FilePath
    {
        get => _filePath;
        private set => SetProperty(ref _filePath, value);
    }

    public string FormattedSqlText
    {
        get => _formattedSqlText;
        private set => SetProperty(ref _formattedSqlText, value);
    }

    public bool IsShowingFormatted
    {
        get => _isShowingFormatted;
        private set
        {
            if (SetProperty(ref _isShowingFormatted, value))
            {
                OnPropertyChanged(nameof(DisplayModeLabel));
            }
        }
    }

    public string OriginalSqlText
    {
        get => _originalSqlText;
        private set => SetProperty(ref _originalSqlText, value);
    }

    public string DisplayModeLabel => IsShowingFormatted ? "Formatted SQL" : "Original SQL";

    public string SyntaxScopeName => "source.sql";

    public SqlDocumentViewModel()
    {
        Title = "SQL Script";
    }

    public SqlDocumentViewModel(string filePath, string content, string title)
    {
        FilePath = filePath;
        OriginalSqlText = content;
        FormattedSqlText = _sqlCanonicalizationService.FormatForDisplay(content);
        CurrentSqlText = OriginalSqlText;
        IsShowingFormatted = false;
        Title = title;
    }

    [RelayCommand]
    private void ShowFormatted()
    {
        IsShowingFormatted = true;
        CurrentSqlText = FormattedSqlText;
    }

    [RelayCommand]
    private void ShowOriginal()
    {
        IsShowingFormatted = false;
        CurrentSqlText = OriginalSqlText;
    }
}
