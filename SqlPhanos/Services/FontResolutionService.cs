using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace SqlPhanos.Services;

/// <summary>
/// Resolves a usable monospaced editor font: the user's saved choice if still installed,
/// otherwise the first available font in a fixed fallback chain, otherwise whatever
/// monospaced font can be found on the system.
/// </summary>
public static class FontResolutionService
{
	private static readonly string[] FallbackChain = { "Aporetic Sans Mono", "Cascadia Code", "Lucida Console" };

	public static string ResolveFont(string? preferredFontFamily)
	{
		if (!string.IsNullOrWhiteSpace(preferredFontFamily) && IsFontAvailable(preferredFontFamily))
		{
			return preferredFontFamily;
		}

		foreach (var candidate in FallbackChain)
		{
			if (IsFontAvailable(candidate))
			{
				return candidate;
			}
		}

		return FindFirstMonospaceSystemFont() ?? FallbackChain[^1];
	}

	public static bool IsFontAvailable(string fontFamilyName)
	{
		foreach (var family in FontManager.Current.SystemFonts)
		{
			if (string.Equals(family.Name, fontFamilyName, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	public static List<string> GetAvailableFontNames()
	{
		var names = new List<string>();
		foreach (var family in FontManager.Current.SystemFonts)
		{
			names.Add(family.Name);
		}

		names.Sort(StringComparer.OrdinalIgnoreCase);
		return names;
	}

	private static string? FindFirstMonospaceSystemFont()
	{
		var fontManager = FontManager.Current;
		foreach (var family in fontManager.SystemFonts)
		{
			if (IsMonospace(fontManager, family.Name))
			{
				return family.Name;
			}
		}

		return null;
	}

	// Avalonia's font APIs don't expose an "is monospace" flag directly, so this compares
	// the rendered advance width of a narrow ('i') and a wide ('m') glyph - equal widths is
	// the standard heuristic for detecting a fixed-pitch font. Verified empirically: Consolas
	// and Cascadia Code both report equal i/m advances, while Arial and Calibri don't.
	private static bool IsMonospace(FontManager fontManager, string familyName)
	{
		if (!fontManager.TryGetGlyphTypeface(new Typeface(familyName), out var glyphTypeface))
		{
			return false;
		}

		var map = glyphTypeface.CharacterToGlyphMap;
		var narrowGlyph = map['i'];
		var wideGlyph = map['m'];
		if (narrowGlyph == 0 || wideGlyph == 0)
		{
			return false;
		}

		return glyphTypeface.TryGetHorizontalGlyphAdvance(narrowGlyph, out var narrowAdvance)
			&& glyphTypeface.TryGetHorizontalGlyphAdvance(wideGlyph, out var wideAdvance)
			&& narrowAdvance == wideAdvance;
	}
}
