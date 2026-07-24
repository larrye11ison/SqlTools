using System;

namespace SqlPhanos.Services;

/// <summary>
/// Ambient holder for the app's current editor font, following the same static-service
/// pattern already used for WeakReferenceMessenger/PaneFocusTracker in this app (no DI
/// container exists here). Already-open document tabs subscribe to FontFamilyChanged so a
/// font change in Settings applies live, not just to newly opened tabs.
/// </summary>
public static class FontSettingsService
{
	private static readonly ConnectionProfileStoreService StoreService = new();
	private static string _currentFontFamily = "Consolas";

	public static string CurrentFontFamily
	{
		get => _currentFontFamily;
		private set
		{
			if (_currentFontFamily != value)
			{
				_currentFontFamily = value;
				FontFamilyChanged?.Invoke(null, EventArgs.Empty);
			}
		}
	}

	public static event EventHandler? FontFamilyChanged;

	public static void Initialize()
	{
		var saved = StoreService.LoadFontFamily();
		CurrentFontFamily = FontResolutionService.ResolveFont(saved);
	}

	public static void ApplyAndSave(string fontFamily)
	{
		StoreService.SaveFontFamily(fontFamily);
		CurrentFontFamily = fontFamily;
	}
}
