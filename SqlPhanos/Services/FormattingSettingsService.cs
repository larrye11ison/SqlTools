using System;

namespace SqlPhanos.Services;

/// <summary>
/// Ambient holder for the user's SQL formatting preferences, following the same
/// static-service pattern as FontSettingsService (no DI container exists in this app).
/// Read once by SqlDocumentViewModel/QueryXLeratorDocumentViewModel at the point they
/// actually invoke SqlCanonicalizationService.FormatForDisplay - unlike font settings, a
/// change here doesn't retroactively re-render text already displayed in open tabs.
/// </summary>
public static class FormattingSettingsService
{
	private static readonly ConnectionProfileStoreService StoreService = new();
	private static bool _openingParenOnNewLine;

	public static bool OpeningParenOnNewLine
	{
		get => _openingParenOnNewLine;
		private set
		{
			if (_openingParenOnNewLine != value)
			{
				_openingParenOnNewLine = value;
				SettingsChanged?.Invoke(null, EventArgs.Empty);
			}
		}
	}

	public static event EventHandler? SettingsChanged;

	public static void Initialize()
	{
		OpeningParenOnNewLine = StoreService.LoadOpeningParenOnNewLine() ?? false;
	}

	public static void ApplyAndSave(bool openingParenOnNewLine)
	{
		StoreService.SaveOpeningParenOnNewLine(openingParenOnNewLine);
		OpeningParenOnNewLine = openingParenOnNewLine;
	}
}
