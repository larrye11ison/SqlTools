using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using SqlPhanos.ViewModels;
using TextMateSharp.Grammars;
using TextMateHost = AvaloniaEdit.TextMate.TextMate;

namespace SqlPhanos.Views;

public partial class SqlDocumentView : UserControl
{
    private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
    private TextEditor? _editor;
    private TextMateHost.Installation? _textMateInstallation;

    public SqlDocumentView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) => EnsureTextMateInstalled();
        DetachedFromVisualTree += (_, _) => DisposeTextMate();
        DataContextChanged += (_, _) => SyncFromViewModel();
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _editor = this.FindControl<TextEditor>("Editor");
    }

    private void EnsureTextMateInstalled()
    {
        if (_editor is null || _textMateInstallation is not null)
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

        if (DataContext is SqlDocumentViewModel viewModel)
        {
            System.Diagnostics.Debug.WriteLine($"SqlDocumentView SyncFromViewModel DataContext={viewModel.GetType().Name} Title={viewModel.Title} SqlLength={viewModel.SqlText?.Length ?? 0}");
            _editor.Document = new TextDocument(viewModel.SqlText ?? string.Empty);
            ApplyGrammar(viewModel.SyntaxScopeName);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"SqlDocumentView SyncFromViewModel DataContext={(DataContext?.GetType().Name ?? "null")}");
            _editor.Document = new TextDocument();
            ApplyGrammar("source.sql");
        }
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
        _textMateInstallation?.Dispose();
        _textMateInstallation = null;
    }
}