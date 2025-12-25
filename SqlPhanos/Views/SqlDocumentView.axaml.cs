using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using SqlPhanos.ViewModels;
using System;
using System.Diagnostics;

namespace SqlPhanos.Views;

public partial class SqlDocumentView : UserControl
{
    private TextEditor? _editor;

    public SqlDocumentView()
    {
        Debug.WriteLine("=== SqlDocumentView constructor ===");
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        
        _editor = this.FindControl<TextEditor>("SqlEditor");
        Debug.WriteLine($"Editor found: {_editor is not null}");
        
        if (_editor is not null)
        {
            SetupTextMate();
        }

        this.DataContextChanged += OnDataContextChanged;
    }

    private void SetupTextMate()
    {
        try
        {
            var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
            var textMateInstallation = _editor!.InstallTextMate(registryOptions);
            
            var language = registryOptions.GetLanguageByExtension(".sql");
            var scope = registryOptions.GetScopeByLanguageId(language.Id);
            
            textMateInstallation.SetGrammar(scope);
            Debug.WriteLine("TextMate configured for SQL");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TextMate setup failed: {ex}");
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_editor is not null && DataContext is SqlDocumentViewModel viewModel)
        {
            _editor.Text = viewModel.SqlText ?? string.Empty;
            Debug.WriteLine($"✓ SQL loaded: {viewModel.SqlText?.Length ?? 0} characters");
        }
    }
}