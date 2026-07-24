using CommunityToolkit.Mvvm.ComponentModel;
using SqlPhanos.Services;
using System.Collections.Generic;

namespace SqlPhanos.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
	[ObservableProperty]
	private string? _selectedFont;

	public List<string> AvailableFonts { get; }

	public SettingsViewModel()
	{
		AvailableFonts = FontResolutionService.GetAvailableFontNames();
		SelectedFont = FontSettingsService.CurrentFontFamily;
	}
}
