using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SqlPhanos.Services;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Views;

public partial class SettingsView : Window
{
	public SettingsView()
	{
		InitializeComponent();
		DataContext = new SettingsViewModel();
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}

	private void OnSaveClick(object? sender, RoutedEventArgs e)
	{
		if (DataContext is SettingsViewModel viewModel)
		{
			if (!string.IsNullOrWhiteSpace(viewModel.SelectedFont))
			{
				FontSettingsService.ApplyAndSave(viewModel.SelectedFont, viewModel.SelectedFontSize);
			}

			FormattingSettingsService.ApplyAndSave(viewModel.SelectedOpeningParenOnNewLine);
		}

		Close();
	}

	private void OnCancelClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}
}
