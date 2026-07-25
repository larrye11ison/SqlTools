using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using SqlPhanos.CodeFormatting;
using SqlPhanos.Messages;
using SqlPhanos.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SqlPhanos.ViewModels;

public enum SqlDisplayMode
{
    Original,
    Formatted,
}

/// <summary>
/// View model for a single SQL script document
/// </summary>
public partial class SqlDocumentViewModel : Document, IHasTabHeaderLines
{
    private readonly SqlCanonicalizationService _sqlCanonicalizationService = new();
    private string _currentSqlText = "";
    private SqlDisplayMode _displayMode = SqlDisplayMode.Original;
    private string _dbName = "";
    private string _schemaName = "";
    private string _objectName = "";
    private string _formattedSqlText = "";
    private ObservableCollection<SearchResultViewModel> _dependentObjects = new();
    private string _originalSqlText = "";

    public string CurrentSqlText
    {
        get => _currentSqlText;
        private set => SetProperty(ref _currentSqlText, value);
    }

    public string DbName
    {
        get => _dbName;
        private set => SetProperty(ref _dbName, value);
    }

    public string SchemaName
    {
        get => _schemaName;
        private set => SetProperty(ref _schemaName, value);
    }

    public string ObjectName
    {
        get => _objectName;
        private set => SetProperty(ref _objectName, value);
    }

    // IHasTabHeaderLines - backs the shared DocumentTabStrip.HeaderTemplate (see
    // ShellView.axaml), which replaces the object-name text that used to be repeated in this
    // document's own header bar.
    public string TabHeaderLine1 => DbName;

    public string TabHeaderLine2 => $"{SchemaName}.{ObjectName}";

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

    /// <summary>
    /// Objects that depend on the one scripted into this document (e.g. the triggers on a
    /// table). Populated asynchronously after the document opens, since discovering them
    /// requires an extra round trip to the server.
    /// </summary>
    public ObservableCollection<SearchResultViewModel> DependentObjects => _dependentObjects;

    public bool HasDependentObjects => DependentObjects.Count > 0;

    public SqlDisplayMode DisplayMode
    {
        get => _displayMode;
        private set
        {
            if (SetProperty(ref _displayMode, value))
            {
                OnPropertyChanged(nameof(IsShowingOriginal));
                OnPropertyChanged(nameof(IsShowingFormatted));
            }
        }
    }

    public bool IsShowingOriginal => DisplayMode == SqlDisplayMode.Original;

    public bool IsShowingFormatted => DisplayMode == SqlDisplayMode.Formatted;

    public string SyntaxScopeName => "source.sql";

    public SqlDocumentViewModel()
    {
        Title = "SQL Script";
    }

    public SqlDocumentViewModel(string dbName, string schemaName, string objectName, string content)
    {
        DbName = dbName;
        SchemaName = schemaName;
        ObjectName = objectName;
        OriginalSqlText = content;
        FormattedSqlText = _sqlCanonicalizationService.FormatForDisplay(content, FormattingSettingsService.OpeningParenOnNewLine);
        CurrentSqlText = OriginalSqlText;
        DisplayMode = SqlDisplayMode.Original;
        Title = $"{schemaName}.{objectName}";
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

    [RelayCommand]
    private void ToggleDisplayMode()
    {
        if (IsShowingOriginal)
        {
            ShowFormatted();
        }
        else
        {
            ShowOriginal();
        }
    }

    public void SetDependentObjects(IEnumerable<SearchResultViewModel> dependents)
    {
        _dependentObjects.Clear();
        foreach (var dependent in dependents)
        {
            _dependentObjects.Add(dependent);
        }

        OnPropertyChanged(nameof(HasDependentObjects));
    }

    [RelayCommand]
    private void ScriptDependentObject(SearchResultViewModel? dependent)
    {
        if (dependent is null)
        {
            return;
        }

        WeakReferenceMessenger.Default.Send(new ScriptObjectRequestMessage(dependent));
    }
}
