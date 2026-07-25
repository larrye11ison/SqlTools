using CommunityToolkit.Mvvm.ComponentModel;
using SqlPhanos.Services;
using System.Collections.Generic;

namespace SqlPhanos.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
	[ObservableProperty]
	private string? _selectedFont;

	[ObservableProperty]
	private double _selectedFontSize;

	[ObservableProperty]
	private bool _selectedOpeningParenOnNewLine;

	public List<string> AvailableFonts { get; }

	public SettingsViewModel()
	{
		AvailableFonts = FontResolutionService.GetAvailableFontNames();
		SelectedFont = FontSettingsService.CurrentFontFamily;
		SelectedFontSize = FontSettingsService.CurrentFontSize;
		SelectedOpeningParenOnNewLine = FormattingSettingsService.OpeningParenOnNewLine;
	}
}
