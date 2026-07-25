using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SqlPhanos.ScriptDatabases;

public static class FileNameUtil
{
    private static readonly HashSet<char> InvalidFileNameCharacters = [..Path.GetInvalidFileNameChars()];

    /// <summary>
    /// Visually similar Unicode substitutes for common invalid filename characters.
    /// </summary>
    private static readonly Dictionary<char, char> LookalikeReplacements = new()
    {
        ['\\'] = '⧵', // ⧵ REVERSE SOLIDUS OPERATOR
        ['/'] = '∕',  // ∕ DIVISION SLASH
        [':'] = '꞉',  // ꞉ MODIFIER LETTER COLON
        ['*'] = '∗',  // ∗ ASTERISK OPERATOR
        ['?'] = '？',  // ？ FULLWIDTH QUESTION MARK
        ['"'] = '″',  // ″ DOUBLE PRIME
        ['<'] = '＜',  // ＜ FULLWIDTH LESS-THAN SIGN
        ['>'] = '＞',  // ＞ FULLWIDTH GREATER-THAN SIGN
        ['|'] = '│',  // │ BOX DRAWINGS LIGHT VERTICAL
    };

    public static void InjectTextAtTopOfFile(string fileName, string textToInject)
    {
        string? directoryName = Path.GetDirectoryName(fileName);
        if (string.IsNullOrEmpty(directoryName))
        {
            throw new InvalidOperationException($"Unable to resolve directory for '{fileName}'.");
        }

        string tempPath = Path.Combine(directoryName, Guid.NewGuid().ToString("N"));
        using (StreamWriter writer = new(tempPath, append: false, Encoding.UTF8))
        using (StreamReader reader = new(fileName))
        {
            writer.WriteLine(textToInject);
            writer.Write(reader.ReadToEnd());
        }

        File.Delete(fileName);
        File.Move(tempPath, fileName);
    }

    public static string SanitiseFileName(string inputVal)
    {
        if (string.IsNullOrEmpty(inputVal))
        {
            return inputVal ?? string.Empty;
        }

        StringBuilder builder = new(inputVal);
        for (int index = 0; index < builder.Length; index++)
        {
            char current = builder[index];
            if (!InvalidFileNameCharacters.Contains(current))
            {
                continue;
            }

            builder[index] = ResolveLookalike(current);
        }

        return builder.ToString();
    }

    private static char ResolveLookalike(char invalidCharacter)
    {
        if (LookalikeReplacements.TryGetValue(invalidCharacter, out char lookalike))
        {
            return lookalike;
        }

        if (invalidCharacter < ' ')
        {
            // Control Pictures block (␀ for NUL, ␁ for SOH, …).
            return (char)('␀' + invalidCharacter);
        }

        if (invalidCharacter is >= '!' and <= '~')
        {
            // Fullwidth Forms: ASCII printable → visually similar wide glyph.
            return (char)(invalidCharacter + 0xFEE0);
        }

        return '�'; // � REPLACEMENT CHARACTER
    }
}
