using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SqlPhanos.Services;

namespace SqlPhanos.Views;

public partial class UpdateAvailableView : Window
{
	public UpdateAvailableView()
	{
		InitializeComponent();

		var update = UpdateCheckService.AvailableUpdate;
		if (update is null)
		{
			return;
		}

		if (this.FindControl<TextBlock>("StatusText") is { } statusText)
		{
			statusText.Text = $"Version {update.Version} is available (you're on {AppVersionService.GetDisplayVersion()}).";
		}

		if (this.FindControl<TextBlock>("NotesText") is { } notesText)
		{
			notesText.Text = update.ReleaseNotes;
		}
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}

	private void OnUpdateClick(object? sender, RoutedEventArgs e)
	{
		Close(true);
	}

	private void OnLaterClick(object? sender, RoutedEventArgs e)
	{
		Close(false);
	}
}
