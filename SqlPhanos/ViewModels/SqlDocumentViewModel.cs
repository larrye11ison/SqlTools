using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace SqlPhanos.ViewModels;

/// <summary>
/// View model for a single SQL script document
/// </summary>
public partial class SqlDocumentViewModel : Document
{
    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private string _sqlText = "";

    public SqlDocumentViewModel()
    {
        Title = "SQL Script";
    }

    public SqlDocumentViewModel(string filePath, string content, string title)
    {
        FilePath = filePath;
        SqlText = content;
        Title = title;
    }
}