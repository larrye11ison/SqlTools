using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlPhanos.CodeFormatting;

/// <summary>
/// One parsed token's position in both the source text that was formatted and the formatted
/// text that was produced from it - lets a caller map a caret position from one rendering of a
/// script to the other (e.g. keeping the caret "in the same place" when toggling between the
/// original and formatted views of the same object).
/// </summary>
public readonly struct SqlTokenPosition
{
	public SqlTokenPosition(TSqlTokenType tokenType, int sourceOffset, int sourceLength, int formattedOffset)
	{
		TokenType = tokenType;
		SourceOffset = sourceOffset;
		SourceLength = sourceLength;
		FormattedOffset = formattedOffset;
	}

	public TSqlTokenType TokenType { get; }

	public int SourceOffset { get; }

	public int SourceLength { get; }

	public int FormattedOffset { get; }
}

/// <summary>
/// The result of formatting a script, optionally including per-token position data.
/// </summary>
public sealed class SqlFormatResult
{
	public SqlFormatResult(string text, IReadOnlyList<SqlTokenPosition>? tokenPositions)
	{
		Text = text;
		TokenPositions = tokenPositions;
	}

	public string Text { get; }

	/// <summary>
	/// Null when this result came from one of the fallback formatting paths (parse failure, or a
	/// shape handled without going through the main tokenizer loop) that have no token-level
	/// position data available - callers should treat that as "no mapping possible" rather than
	/// an empty-but-usable list.
	/// </summary>
	public IReadOnlyList<SqlTokenPosition>? TokenPositions { get; }
}
