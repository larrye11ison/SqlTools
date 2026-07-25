using System;

namespace SqlPhanos.Services;

/// <summary>
/// Ambient holder for the app's current editor font family/size, following the same
/// static-service pattern already used for WeakReferenceMessenger/PaneFocusTracker in this
/// app (no DI container exists here). Already-open document tabs subscribe to
/// SettingsChanged so a change in Settings applies live, not just to newly opened tabs.
/// </summary>
public static class FontSettingsService
{
	private const double DefaultFontSize = 16;

	private static readonly ConnectionProfileStoreService StoreService = new();
	private static string _currentFontFamily = "Consolas";
	private static double _currentFontSize = DefaultFontSize;

	public static string CurrentFontFamily
	{
		get => _currentFontFamily;
		private set
		{
			if (_currentFontFamily != value)
			{
				_currentFontFamily = value;
				SettingsChanged?.Invoke(null, EventArgs.Empty);
			}
		}
	}

	public static double CurrentFontSize
	{
		get => _currentFontSize;
		private set
		{
			if (_currentFontSize != value)
			{
				_currentFontSize = value;
				SettingsChanged?.Invoke(null, EventArgs.Empty);
			}
		}
	}

	public static event EventHandler? SettingsChanged;

	public static void Initialize()
	{
		var savedFamily = StoreService.LoadFontFamily();
		CurrentFontFamily = FontResolutionService.ResolveFont(savedFamily);

		var savedSize = StoreService.LoadFontSize();
		CurrentFontSize = savedSize is > 0 ? savedSize.Value : DefaultFontSize;
	}

	public static void ApplyAndSave(string fontFamily, double fontSize)
	{
		StoreService.SaveFontFamily(fontFamily);
		StoreService.SaveFontSize(fontSize);
		CurrentFontFamily = fontFamily;
		CurrentFontSize = fontSize;
	}
}
