using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SqlPhanos.Services;

namespace SqlPhanos.Views;

public partial class AboutView : Window
{
	public AboutView()
	{
		InitializeComponent();

		var versionText = this.FindControl<TextBlock>("VersionText");
		if (versionText is not null)
		{
			versionText.Text = $"Version {AppVersionService.GetDisplayVersion()}";
		}
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}

	private void OnOkClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}
}
