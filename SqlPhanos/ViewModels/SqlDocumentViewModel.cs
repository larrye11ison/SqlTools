using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using SqlPhanos.CodeFormatting;

namespace SqlPhanos.ViewModels;

public enum SqlDisplayMode
{
    Original,
    Formatted,
}

/// <summary>
/// View model for a single SQL script document
/// </summary>
public partial class SqlDocumentViewModel : Document
{
    private readonly SqlCanonicalizationService _sqlCanonicalizationService = new();
    private string _currentSqlText = "";
    private SqlDisplayMode _displayMode = SqlDisplayMode.Original;
    private string _filePath = "";
    private string _formattedSqlText = "";
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

    public string OriginalSqlText
    {
        get => _originalSqlText;
        private set => SetProperty(ref _originalSqlText, value);
    }

    public SqlDisplayMode DisplayMode
    {
        get => _displayMode;
        private set
        {
            if (SetProperty(ref _displayMode, value))
            {
                OnPropertyChanged(nameof(DisplayModeLabel));
                OnPropertyChanged(nameof(IsShowingOriginal));
                OnPropertyChanged(nameof(IsShowingFormatted));
            }
        }
    }

    public bool IsShowingOriginal => DisplayMode == SqlDisplayMode.Original;

    public bool IsShowingFormatted => DisplayMode == SqlDisplayMode.Formatted;

    public string DisplayModeLabel => DisplayMode switch
    {
        SqlDisplayMode.Formatted => "Formatted SQL",
        _ => "Original SQL",
    };

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
        DisplayMode = SqlDisplayMode.Original;
        Title = title;
    }

    [RelayCommand]
    private void ShowFormatted()
    {
        DisplayMode = SqlDisplayMode.Formatted;
        CurrentSqlText = FormattedSqlText;
    }

    [RelayCommand]
    private void ShowOriginal()
    {
        DisplayMode = SqlDisplayMode.Original;
        CurrentSqlText = OriginalSqlText;
    }
}
