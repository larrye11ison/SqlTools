using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SqlPhanos.ViewModels;
using System.Linq;

namespace SqlPhanos.Views;

public partial class ScriptDatabasesDocumentView : UserControl
{
	public ScriptDatabasesDocumentView()
	{
		InitializeComponent();
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}

	public void FocusDefault()
	{
		this.FindControl<TextBox>("OutputDirectoryBox")?.Focus();
	}

	private async void OnBrowseClick(object? sender, RoutedEventArgs e)
	{
		if (DataContext is not ScriptDatabasesDocumentViewModel viewModel)
		{
			return;
		}

		var topLevel = TopLevel.GetTopLevel(this);
		if (topLevel is null)
		{
			return;
		}

		var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
		{
			Title = "Choose an output folder",
			AllowMultiple = false
		});

		var folder = folders.FirstOrDefault();
		if (folder?.TryGetLocalPath() is { } path)
		{
			viewModel.BaseOutputDirectory = path;
		}
	}
}
