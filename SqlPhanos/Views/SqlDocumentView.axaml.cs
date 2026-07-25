using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlPhanos.Services;
using SqlPhanos.ViewModels;
using System.ComponentModel;
using TextMateSharp.Grammars;
using TextMateHost = AvaloniaEdit.TextMate.TextMate;

namespace SqlPhanos.Views;

public partial class SqlDocumentView : UserControl
{
    private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
    private TextEditor? _editor;
    private TextMateHost.Installation? _textMateInstallation;
    private SearchPanel? _searchPanel;
    private SqlDocumentViewModel? _trackedViewModel;

    public SqlDocumentView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) => EnsureTextMateInstalled();
        DetachedFromVisualTree += (_, _) => DisposeTextMate();
        DataContextChanged += (_, _) => SyncFromViewModel();
        ActualThemeVariantChanged += (_, _) => ApplyTheme();

        ApplyFont();
        FontSettingsService.SettingsChanged += OnFontSettingsChanged;
        DetachedFromVisualTree += (_, _) => FontSettingsService.SettingsChanged -= OnFontSettingsChanged;
    }

    public void FocusEditor()
    {
        _editor?.Focus();
    }

    public void OpenFind()
    {
        if (_searchPanel is null)
        {
            return;
        }

        // Reactivate (rather than Open again) when the panel is already open so that pressing
        // Ctrl+F a second time refocuses the search box and re-selects its current text for
        // immediate retyping, instead of being a no-op.
        if (_searchPanel.IsClosed)
        {
            _searchPanel.Open();
        }
        else
        {
            _searchPanel.Reactivate();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _editor = this.FindControl<TextEditor>("Editor");
    }

    private void OnFontSettingsChanged(object? sender, System.EventArgs e)
    {
        ApplyFont();
    }

    private void ApplyFont()
    {
        if (_editor is not null)
        {
            _editor.FontFamily = new FontFamily(FontSettingsService.CurrentFontFamily);
            _editor.FontSize = FontSettingsService.CurrentFontSize;
        }
    }

    private void EnsureTextMateInstalled()
    {
        if (_editor is null)
        {
            return;
        }

        _searchPanel ??= SearchPanel.Install(_editor);

        if (_textMateInstallation is not null)
        {
            return;
        }

        _textMateInstallation = TextMateHost.InstallTextMate(
            _editor,
            _registryOptions,
            true,
            ex => System.Diagnostics.Debug.WriteLine($"TextMate initialization error: {ex.Message}"));

        ApplyTheme();
        SyncFromViewModel();
    }

    private void SyncFromViewModel()
    {
        if (_editor is null)
        {
            return;
        }

        if (_trackedViewModel is not null)
        {
            _trackedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _trackedViewModel = null;
        }

        if (DataContext is SqlDocumentViewModel viewModel)
        {
            _trackedViewModel = viewModel;
            _trackedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateEditorDocument(viewModel);
            ApplyGrammar(viewModel.SyntaxScopeName);
        }
        else
        {
            _editor.Document = new TextDocument();
            ApplyGrammar("source.sql");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SqlDocumentViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(SqlDocumentViewModel.CurrentSqlText))
        {
            UpdateEditorDocument(viewModel);
        }
    }

    private void UpdateEditorDocument(SqlDocumentViewModel viewModel)
    {
        if (_editor is null)
        {
            return;
        }

        _editor.Document = new TextDocument(viewModel.CurrentSqlText ?? string.Empty);
    }

    private void ApplyGrammar(string? scopeName)
    {
        if (_textMateInstallation is null)
        {
            return;
        }

        _textMateInstallation.SetGrammar(string.IsNullOrWhiteSpace(scopeName)
            ? "source.sql"
            : scopeName);
    }

    private void ApplyTheme()
    {
        if (_textMateInstallation is null)
        {
            return;
        }

        var themeName = ActualThemeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;

        _textMateInstallation.SetTheme(_registryOptions.LoadTheme(themeName));
    }

    private void DisposeTextMate()
    {
        if (_trackedViewModel is not null)
        {
            _trackedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _trackedViewModel = null;
        }

        _textMateInstallation?.Dispose();
        _textMateInstallation = null;

        _searchPanel?.Uninstall();
        _searchPanel = null;
    }
}
