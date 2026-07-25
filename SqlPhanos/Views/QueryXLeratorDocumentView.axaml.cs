using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using SqlPhanos.Services;
using SqlPhanos.ViewModels;
using System;
using System.ComponentModel;
using TextMateSharp.Grammars;
using TextMateHost = AvaloniaEdit.TextMate.TextMate;

namespace SqlPhanos.Views;

public partial class QueryXLeratorDocumentView : UserControl
{
	private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
	private TextEditor? _editor;
	private TextMateHost.Installation? _textMateInstallation;
	private QueryXLeratorDocumentViewModel? _trackedViewModel;
	private bool _syncingFromViewModel;

	public QueryXLeratorDocumentView()
	{
		InitializeComponent();

		AttachedToVisualTree += (_, _) => EnsureTextMateInstalled();
		DetachedFromVisualTree += (_, _) => DisposeTextMate();
		DataContextChanged += (_, _) => SyncFromViewModel();
		ActualThemeVariantChanged += (_, _) => ApplyTheme();

		ApplyFont();
		FontSettingsService.FontFamilyChanged += OnFontFamilyChanged;
		DetachedFromVisualTree += (_, _) => FontSettingsService.FontFamilyChanged -= OnFontFamilyChanged;
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
		_editor = this.FindControl<TextEditor>("Editor");
	}

	public void FocusEditor()
	{
		_editor?.Focus();
	}

	private async void OnBrowseClick(object? sender, RoutedEventArgs e)
	{
		if (DataContext is not QueryXLeratorDocumentViewModel viewModel)
		{
			return;
		}

		var topLevel = TopLevel.GetTopLevel(this);
		if (topLevel is null)
		{
			return;
		}

		var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Choose output file",
			SuggestedFileName = "Query.xlsx",
			DefaultExtension = "xlsx",
			FileTypeChoices = new[]
			{
				new FilePickerFileType("Excel Workbook") { Patterns = new[] { "*.xlsx" } }
			}
		});

		if (file is not null)
		{
			viewModel.OutputPath = file.Path.LocalPath;
		}
	}

	private void OnFontFamilyChanged(object? sender, EventArgs e)
	{
		ApplyFont();
	}

	private void ApplyFont()
	{
		if (_editor is not null)
		{
			_editor.FontFamily = new FontFamily(FontSettingsService.CurrentFontFamily);
		}
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

		_textMateInstallation.SetGrammar("source.sql");
		ApplyTheme();
	}

	private void SyncFromViewModel()
	{
		if (_editor is null)
		{
			return;
		}

		if (_trackedViewModel is not null)
		{
			_editor.TextChanged -= OnEditorTextChanged;
			_trackedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
			_trackedViewModel = null;
		}

		if (DataContext is QueryXLeratorDocumentViewModel viewModel)
		{
			_trackedViewModel = viewModel;
			_editor.Document = new TextDocument(viewModel.QueryText ?? string.Empty);
			_editor.TextChanged += OnEditorTextChanged;
			_trackedViewModel.PropertyChanged += OnViewModelPropertyChanged;
		}
		else
		{
			_editor.Document = new TextDocument();
		}
	}

	private void OnEditorTextChanged(object? sender, EventArgs e)
	{
		if (_syncingFromViewModel || _trackedViewModel is null || _editor is null)
		{
			return;
		}

		_trackedViewModel.QueryText = _editor.Document.Text;
	}

	// Needed for programmatic QueryText changes (e.g. ReformatCommand rewriting the text in
	// place) to actually show up in the editor - OnEditorTextChanged above only covers the
	// opposite direction (user typing). _syncingFromViewModel guards against the obvious
	// ping-pong: setting Document.Text here would otherwise re-fire TextChanged and write the
	// same value straight back into the ViewModel.
	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_editor is null || sender is not QueryXLeratorDocumentViewModel viewModel)
		{
			return;
		}

		if (e.PropertyName != nameof(QueryXLeratorDocumentViewModel.QueryText))
		{
			return;
		}

		var newText = viewModel.QueryText ?? string.Empty;
		if (_editor.Document.Text == newText)
		{
			return;
		}

		_syncingFromViewModel = true;
		try
		{
			var caretOffset = Math.Min(_editor.CaretOffset, newText.Length);
			_editor.Document.Text = newText;
			_editor.CaretOffset = caretOffset;
		}
		finally
		{
			_syncingFromViewModel = false;
		}
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
		if (_trackedViewModel is not null && _editor is not null)
		{
			_editor.TextChanged -= OnEditorTextChanged;
			_trackedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
			_trackedViewModel = null;
		}

		_textMateInstallation?.Dispose();
		_textMateInstallation = null;
	}
}
