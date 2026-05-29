using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SqlPhanos.CodeFormatting;

public sealed class SqlCanonicalizationService
{
	private const int LongExpressionLineBreakThreshold = 75;

	public string FormatForDisplay(string sql)
	{
		if (string.IsNullOrWhiteSpace(sql))
		{
			return sql;
		}

		var sqlToFormat = NormalizeSingleLineCommentBoundaries(sql);
		if (TryFormatCollapsedTryCatchFinally(sqlToFormat, out var tryCatchFinallyFormatted))
		{
			return tryCatchFinallyFormatted;
		}

		if (TryExtractSimpleSelectAssignment(sqlToFormat, out var assignmentPrefix, out var assignmentExpression, out var assignmentHasSemicolon))
		{
			var formattedExpression = FormatExpressionFallback(assignmentExpression, LongExpressionLineBreakThreshold);
			return ComposeSelectAssignment(assignmentPrefix, formattedExpression, assignmentHasSemicolon);
		}

		if (TryFormatSimpleSelectWhereNoFrom(sqlToFormat, out var formattedSimpleSelectWhere))
		{
			return formattedSimpleSelectWhere;
		}

		try
		{
			var parser = new TSql160Parser(false);
			IList<ParseError> errors;
			TSqlFragment fragment;

			using (var reader = new StringReader(sqlToFormat))
			{
				fragment = parser.Parse(reader, out errors);
			}

			if (errors is not null && errors.Count > 0 && ShouldUseExpressionFallback(sqlToFormat))
			{
				return FormatExpressionFallback(sqlToFormat, LongExpressionLineBreakThreshold);
			}

			var tokens = fragment.ScriptTokenStream;
			if (tokens is null || tokens.Count == 0)
			{
				return sql;
			}

			var result = new StringBuilder();
			var indentLevel = 0;
			var lineStart = true;
			var previousWasStatementEnd = false;
			var inSelectColumnList = false;
			var selectStatementDepth = 0;
			var parenthesisDepth = 0;
			var inInClause = false;
			var inClauseStartIndex = -1;
			var inCreateStatementParams = false;
			var afterCreateObjectName = false;
			var pendingInsertColumnList = false;
			var inInsertColumnList = false;
			var inInsertWithHint = false;
			var insertWithHintDepth = -1;
			var insertColumnListDepth = -1;
			var pendingValuesList = false;
			var inValuesList = false;
			var valuesListDepth = -1;
			var inDeclareStatement = false;
			var pendingDeclareVariableContinuation = false;
			var parenthesisStack = new Stack<ParenthesisScope>();
			var pendingBeginTryCatchFinally = false;
			var betweenAndJustEmitted = false;
			var inCreateObjectParameterList = false;
			var createObjectParameterListDepth = -1;
			var caseExpressionDepth = 0;
			var currentLineTokenLength = 0;

			for (var i = 0; i < tokens.Count; i++)
			{
				var token = tokens[i];

				switch (token.TokenType)
				{
					case TSqlTokenType.Create:
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						afterCreateObjectName = false;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Proc:
					case TSqlTokenType.Procedure:
					case TSqlTokenType.Function:
					case TSqlTokenType.View:
					case TSqlTokenType.Trigger:
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						afterCreateObjectName = true;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.As:
						var isCreateContextAs = !inDeclareStatement && (inCreateStatementParams || afterCreateObjectName);
						if (isCreateContextAs)
						{
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
							result.AppendLine();
							lineStart = true;
							inCreateStatementParams = false;
							afterCreateObjectName = false;
						}
						else if (inDeclareStatement)
						{
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text);
						}
						else
						{
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
							result.Append(' ');
						}
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.If:
					case TSqlTokenType.While:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Else:
						if (IsInsideCaseBlock(tokens, i))
						{
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, indentLevel + 3 + GetActiveExpandedParenthesisDepth(parenthesisStack), ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
							result.Append(' ');
						}
						else
						{
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
							result.AppendLine();
							lineStart = true;
						}
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Begin:
						var nextControlIndex = NextNonWhitespaceIndex(tokens, i + 1);
						if (nextControlIndex < tokens.Count && IsTryCatchFinallyToken(tokens[nextControlIndex]))
						{
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
							result.Append(' ');
							pendingBeginTryCatchFinally = true;
							previousWasStatementEnd = false;
							break;
						}

						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						indentLevel++;
						result.AppendLine();
						lineStart = true;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.End:
						if (caseExpressionDepth > 0)
						{
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, indentLevel + 2 + GetActiveExpandedParenthesisDepth(parenthesisStack), ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
							caseExpressionDepth = Math.Max(0, caseExpressionDepth - 1);
							previousWasStatementEnd = false;
							break;
						}

						indentLevel = Math.Max(0, indentLevel - 1);
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Select:
						AppendLineIfNeeded(result, ref lineStart);
						var selectIndent = indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack);
						AppendIndentIfNeeded(result, selectIndent, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						var insideExpandedScope = parenthesisDepth > 0 && HasParenthesisScope(parenthesisStack, parenthesisDepth);
						var keepSelectInline = ShouldKeepSelectInline(tokens, i) ||
							(insideExpandedScope && ShouldKeepSelectInlineInParenthesizedSubquery(tokens, i));
						if (!keepSelectInline)
						{
							inSelectColumnList = true;
							selectStatementDepth++;
							result.AppendLine();
							lineStart = true;
						}
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Declare:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						inDeclareStatement = true;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Insert:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						pendingInsertColumnList = true;
						pendingValuesList = false;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.From:
					case TSqlTokenType.Where:
					case TSqlTokenType.Order:
					case TSqlTokenType.Group:
					case TSqlTokenType.Having:
					case TSqlTokenType.Union:
						if (inSelectColumnList && selectStatementDepth > 0)
						{
							inSelectColumnList = false;
							selectStatementDepth--;
						}

						AppendLineIfNeeded(result, ref lineStart);
						var clauseIndent = indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack);
						AppendIndentIfNeeded(result, clauseIndent, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Into:
						var previousIntoIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
						var previousIntoType = previousIntoIndex >= 0 ? tokens[previousIntoIndex].TokenType : TSqlTokenType.None;
						if (previousIntoType == TSqlTokenType.Insert)
						{
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
						}
						else
						{
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
						}
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.In:
						var nextInIndex = NextNonWhitespaceIndex(tokens, i + 1);
						if (nextInIndex < tokens.Count && tokens[nextInIndex].TokenType == TSqlTokenType.LeftParenthesis)
						{
							inInClause = true;
							inClauseStartIndex = result.Length;
						}

						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.With:
						if (pendingInsertColumnList)
						{
							if (!lineStart && result.Length > 0 && result[^1] == ' ')
							{
								result.Length--;
							}
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
							previousWasStatementEnd = false;
							break;
						}
						goto default;

					case TSqlTokenType.Values:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						pendingInsertColumnList = false;
						pendingValuesList = true;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Left:
					case TSqlTokenType.Right:
					case TSqlTokenType.Inner:
					case TSqlTokenType.Outer:
					case TSqlTokenType.Cross:
					case TSqlTokenType.Full:
						var nextJoinIndex = NextNonWhitespaceIndex(tokens, i + 1);
						var isJoinModifier = nextJoinIndex < tokens.Count &&
							(tokens[nextJoinIndex].TokenType == TSqlTokenType.Join ||
							 tokens[nextJoinIndex].TokenType == TSqlTokenType.Outer ||
							 tokens[nextJoinIndex].TokenType == TSqlTokenType.Inner);

						if (isJoinModifier)
						{
							AppendLineIfNeeded(result, ref lineStart);
						}

						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Join:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.And:
					case TSqlTokenType.Or:
						var previousKeywordIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
						var nextKeywordIndex = NextNonWhitespaceIndex(tokens, i + 1);
						var previousKeywordType = previousKeywordIndex >= 0 ? tokens[previousKeywordIndex].TokenType : TSqlTokenType.None;
						var nextKeywordType = nextKeywordIndex < tokens.Count ? tokens[nextKeywordIndex].TokenType : TSqlTokenType.None;
						var isCreateOrAlter = token.TokenType == TSqlTokenType.Or &&
							previousKeywordType == TSqlTokenType.Create &&
							nextKeywordType == TSqlTokenType.Alter;
						var isBetweenAnd = token.TokenType == TSqlTokenType.And && IsBetweenAndToken(tokens, i);
						var inCasePredicate = IsInsideCaseBlock(tokens, i);

						if (isCreateOrAlter)
						{
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
							currentLineTokenLength += token.Text.Length + 1;
							previousWasStatementEnd = false;
							break;
						}

						if (isBetweenAnd)
						{
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
							betweenAndJustEmitted = true;
							currentLineTokenLength += token.Text.Length + 1;
							previousWasStatementEnd = false;
							break;
						}

						if (inCasePredicate)
						{
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, indentLevel + 5, ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
							result.Append(' ');
							currentLineTokenLength = token.Text.Length + 1;
							previousWasStatementEnd = false;
							break;
						}

						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel + 1, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
						currentLineTokenLength = token.Text.Length + 1;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Plus:
					case TSqlTokenType.Minus:
						AppendSpaceIfNeeded(result, lineStart);
						result.Append(token.Text);
						if (parenthesisDepth > 0 && HasParenthesisScope(parenthesisStack, parenthesisDepth))
						{
							var nextOperatorIndex = NextNonWhitespaceIndex(tokens, i + 1);
							if (nextOperatorIndex < tokens.Count && tokens[nextOperatorIndex].TokenType == TSqlTokenType.LeftParenthesis)
							{
								TrimTrailingSpaces(result);
								result.AppendLine();
								lineStart = true;
								currentLineTokenLength = 0;
								previousWasStatementEnd = false;
								break;
							}
						}
						result.Append(' ');
						currentLineTokenLength += token.Text.Length + 1;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Dot:
						result.Append(token.Text);
						currentLineTokenLength += token.Text.Length;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Comma:
						result.Append(token.Text);
						currentLineTokenLength += token.Text.Length;
						if (inDeclareStatement && parenthesisDepth == 0)
						{
							result.AppendLine();
							lineStart = true;
							currentLineTokenLength = 0;
							pendingDeclareVariableContinuation = true;
						}
						else if (inInsertColumnList || inValuesList || inCreateStatementParams || (inSelectColumnList && selectStatementDepth > 0 && parenthesisDepth == 0))
						{
							result.AppendLine();
							lineStart = true;
							currentLineTokenLength = 0;
						}
						else if (parenthesisDepth > 0 && HasParenthesisScope(parenthesisStack, parenthesisDepth) && !inInClause && !inDeclareStatement)
						{
							var currentLine = GetCurrentLineText(result).Trim();
							var nextArgumentLength = GetNextTopLevelArgumentLength(tokens, i + 1, parenthesisDepth);
							var previousCommaIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
							var previousCommaType = previousCommaIndex >= 0 ? tokens[previousCommaIndex].TokenType : TSqlTokenType.None;
							var forceBreakForComplexFunctionArguments = previousCommaType == TSqlTokenType.RightParenthesis && nextArgumentLength > 20;
							if (!forceBreakForComplexFunctionArguments && !IsOnlyClosingParenthesesLine(currentLine) && nextArgumentLength > 0 && currentLine.Length + 1 + nextArgumentLength <= LongExpressionLineBreakThreshold)
							{
								result.Append(' ');
								currentLineTokenLength++;
							}
							else
							{
								result.AppendLine();
								lineStart = true;
								currentLineTokenLength = 0;
							}
						}
						else if (inInClause && parenthesisDepth > 0)
						{
							result.Append(' ');
							currentLineTokenLength++;
						}
						else if (inDeclareStatement && parenthesisDepth > 0)
						{
						}
						else
						{
							result.Append(' ');
							currentLineTokenLength++;
						}
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.LeftParenthesis:
						parenthesisDepth++;
						var previousIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
						var previousTokenType = previousIndex >= 0 ? tokens[previousIndex].TokenType : TSqlTokenType.None;

						if (afterCreateObjectName)
						{
							inCreateStatementParams = true;
							inCreateObjectParameterList = true;
							createObjectParameterListDepth = parenthesisDepth;
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text);
							result.AppendLine();
							lineStart = true;
							afterCreateObjectName = false;
							previousWasStatementEnd = false;
							break;
						}

						if (pendingInsertColumnList && previousTokenType == TSqlTokenType.With)
						{
							inInsertWithHint = true;
							insertWithHintDepth = parenthesisDepth;
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text);
							previousWasStatementEnd = false;
							break;
						}

						if (pendingInsertColumnList)
						{
							pendingInsertColumnList = false;
							inInsertColumnList = true;
							insertColumnListDepth = parenthesisDepth;
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text);
							result.AppendLine();
							lineStart = true;
							parenthesisStack.Push(new ParenthesisScope(parenthesisDepth));
							previousWasStatementEnd = false;
							break;
						}

						if (pendingValuesList)
						{
							pendingValuesList = false;
							inValuesList = true;
							valuesListDepth = parenthesisDepth;
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text);
							result.AppendLine();
							lineStart = true;
							parenthesisStack.Push(new ParenthesisScope(parenthesisDepth));
							previousWasStatementEnd = false;
							break;
						}

						var forceExpandParenthesis = previousTokenType == TSqlTokenType.Exists;
						var shouldExpandParenthesis = !inDeclareStatement &&
							previousIndex >= 0 &&
							previousTokenType is not TSqlTokenType.If and not TSqlTokenType.While &&
							(forceExpandParenthesis || ShouldExpandParenthesisForDisplay(tokens, i));
						if (shouldExpandParenthesis)
						{
							parenthesisStack.Push(new ParenthesisScope(parenthesisDepth));
							if (lineStart)
							{
								var expandedIndent = indentLevel + Math.Max(0, GetActiveExpandedParenthesisDepth(parenthesisStack) - 1);
								if (inSelectColumnList && selectStatementDepth > 0)
								{
									expandedIndent++;
								}
								AppendIndentIfNeeded(result, Math.Max(0, expandedIndent), ref lineStart);
							}
							result.Append(token.Text);
							result.AppendLine();
							lineStart = true;
							previousWasStatementEnd = false;
							break;
						}

						if (lineStart && !inCreateStatementParams)
						{
							var extraIndent = inSelectColumnList && selectStatementDepth > 0 ? 1 : 0;
							extraIndent += GetActiveExpandedParenthesisDepth(parenthesisStack);
							AppendIndent(result, indentLevel + extraIndent);
							lineStart = false;
						}

						if (shouldExpandParenthesis)
						{
							parenthesisStack.Push(new ParenthesisScope(parenthesisDepth));
						}

						result.Append(token.Text);

						var nextSelectIndex = NextNonWhitespaceIndex(tokens, i + 1);
						if (!inCreateStatementParams && nextSelectIndex < tokens.Count && tokens[nextSelectIndex].TokenType == TSqlTokenType.Select)
						{
							selectStatementDepth++;
						}
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.RightParenthesis:
						if (inInClause && parenthesisDepth == 1)
						{
							var inClauseLength = result.Length - inClauseStartIndex;
							if (inClauseLength > 0)
							{
								var inClauseContent = result.ToString(inClauseStartIndex, inClauseLength);
								if (ShouldFormatInClauseMultiline(inClauseContent, indentLevel))
								{
									result.Length = inClauseStartIndex;
									FormatInClauseMultiline(result, inClauseContent, indentLevel);
									inInClause = false;
									inClauseStartIndex = -1;
									parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
									PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
									previousWasStatementEnd = false;
									break;
								}
							}

							inInClause = false;
							inClauseStartIndex = -1;
						}

						if (inInsertWithHint && parenthesisDepth == insertWithHintDepth)
						{
							parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
							result.Append(token.Text);
							inInsertWithHint = false;
							insertWithHintDepth = -1;
							previousWasStatementEnd = false;
							break;
						}

						if (inInsertColumnList && parenthesisDepth == insertColumnListDepth)
						{
							parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
							result.AppendLine();
							AppendIndent(result, indentLevel);
							result.Append(token.Text);
							result.AppendLine();
							lineStart = true;
							inInsertColumnList = false;
							insertColumnListDepth = -1;
							PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
							previousWasStatementEnd = false;
							break;
						}

						if (inValuesList && parenthesisDepth == valuesListDepth)
						{
							parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
							result.AppendLine();
							AppendIndent(result, indentLevel);
							result.Append(token.Text);
							inValuesList = false;
							valuesListDepth = -1;
							PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
							previousWasStatementEnd = false;
							break;
						}

						var shouldExpandClosingParenthesis = HasParenthesisScope(parenthesisStack, parenthesisDepth);
						var closingDepth = parenthesisDepth;
						parenthesisDepth = Math.Max(0, parenthesisDepth - 1);

						if (inCreateObjectParameterList && closingDepth == createObjectParameterListDepth)
						{
							if (!lineStart)
							{
								result.AppendLine();
								lineStart = true;
							}
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text);
							result.AppendLine();
							lineStart = true;
							inCreateObjectParameterList = false;
							createObjectParameterListDepth = -1;
							PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
							previousWasStatementEnd = false;
							break;
						}

						if (shouldExpandClosingParenthesis)
						{
							result.AppendLine();
							AppendIndent(result, indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack));
							result.Append(token.Text);
							PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
							previousWasStatementEnd = false;
							break;
						}

						result.Append(token.Text);
						PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Semicolon:
						result.Append(token.Text);
						result.AppendLine();
						lineStart = true;
						previousWasStatementEnd = false;
						inSelectColumnList = false;
						selectStatementDepth = 0;
						inDeclareStatement = false;
						pendingDeclareVariableContinuation = false;
						break;

					case TSqlTokenType.Go:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.AppendLine();
						lineStart = true;
						previousWasStatementEnd = true;
						inSelectColumnList = false;
						selectStatementDepth = 0;
						break;

					case TSqlTokenType.WhiteSpace:
						if (token.Text.Contains('\n') || token.Text.Contains('\r'))
						{
							if (afterCreateObjectName && !lineStart)
							{
								result.AppendLine();
								lineStart = true;
							}

							if (previousWasStatementEnd && !lineStart)
							{
								TrimTrailingSpaces(result);
								result.AppendLine();
								lineStart = true;
								previousWasStatementEnd = false;
							}
						}
						else if (!lineStart)
						{
							var nextIndex = NextNonWhitespaceIndex(tokens, i + 1);
							if (betweenAndJustEmitted)
							{
								var betweenLength = GetExpressionLengthUntilClauseBoundary(tokens, nextIndex);
								if (betweenLength > LongExpressionLineBreakThreshold)
								{
									TrimTrailingSpaces(result);
									result.AppendLine();
									AppendIndent(result, indentLevel + 1);
									lineStart = false;
								}
								else
								{
									result.Append(' ');
								}

								betweenAndJustEmitted = false;
								break;
							}

							if (nextIndex < tokens.Count &&
								(StartsOnNewLine(tokens[nextIndex].TokenType) ||
								 (pendingBeginTryCatchFinally && IsTryCatchFinallyToken(tokens[nextIndex])) ||
								 (inCreateStatementParams && (tokens[nextIndex].TokenType == TSqlTokenType.As || tokens[nextIndex].TokenType == TSqlTokenType.Variable || tokens[nextIndex].TokenType == TSqlTokenType.LeftParenthesis))))
							{
								break;
							}

							if (afterCreateObjectName)
							{
								var previousCreateIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
								var previousCreateType = previousCreateIndex >= 0 ? tokens[previousCreateIndex].TokenType : TSqlTokenType.None;
								if (previousCreateType is TSqlTokenType.Proc or TSqlTokenType.Procedure or TSqlTokenType.Function or TSqlTokenType.View or TSqlTokenType.Trigger)
								{
									result.Append(' ');
								}
								else
								{
									result.AppendLine();
									lineStart = true;
								}
								break;
							}

							var nextType = nextIndex < tokens.Count ? tokens[nextIndex].TokenType : TSqlTokenType.None;
							if (nextType == TSqlTokenType.SingleLineComment)
							{
								break;
							}

							if (result.Length > 0 && result[^1] != ' ' &&
								nextType is not TSqlTokenType.Comma and not TSqlTokenType.RightParenthesis and not TSqlTokenType.Semicolon)
							{
								result.Append(' ');
							}
						}
						break;

					case TSqlTokenType.SingleLineComment:
						if (!lineStart)
						{
							AppendLineIfNeeded(result, ref lineStart);
						}

						if (afterCreateObjectName)
						{
							afterCreateObjectName = false;
							inCreateStatementParams = true;
						}

						if (lineStart)
						{
							var extraIndent = inSelectColumnList && selectStatementDepth > 0 ? 1 : 0;
							if (inCreateStatementParams || inInsertColumnList || inValuesList)
							{
								extraIndent = 1;
							}
							extraIndent += GetActiveExpandedParenthesisDepth(parenthesisStack);
							AppendIndent(result, indentLevel + extraIndent);
							lineStart = false;
						}

						result.Append(token.Text);
						if (!token.Text.EndsWith("\n", StringComparison.Ordinal) && !token.Text.EndsWith("\r", StringComparison.Ordinal))
						{
							result.AppendLine();
						}
						lineStart = true;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.MultilineComment:
						if (lineStart)
						{
							var extraIndent = inSelectColumnList && selectStatementDepth > 0 ? 1 : 0;
							if (inCreateStatementParams || inInsertColumnList || inValuesList)
							{
								extraIndent = 1;
							}
							extraIndent += GetActiveExpandedParenthesisDepth(parenthesisStack);
							AppendIndent(result, indentLevel + extraIndent);
							lineStart = false;
						}

						result.Append(token.Text);
						if (token.Text.EndsWith("\n", StringComparison.Ordinal) || token.Text.EndsWith("\r", StringComparison.Ordinal))
						{
							lineStart = true;
						}
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Case:
						caseExpressionDepth++;
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel + 2 + GetActiveExpandedParenthesisDepth(parenthesisStack), ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.When:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel + 3 + GetActiveExpandedParenthesisDepth(parenthesisStack), ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Then:
						AppendSpaceIfNeeded(result, lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Variable:
					case TSqlTokenType.Identifier:
					case TSqlTokenType.QuotedIdentifier:
						if (pendingDeclareVariableContinuation && lineStart)
						{
							AppendIndent(result, indentLevel + 1);
							lineStart = false;
							pendingDeclareVariableContinuation = false;
						}

						if (IsTryCatchFinallyToken(token))
						{
							if (pendingBeginTryCatchFinally)
							{
								result.Append(token.Text.ToUpperInvariant());
								indentLevel++;
								result.AppendLine();
								lineStart = true;
								pendingBeginTryCatchFinally = false;
								previousWasStatementEnd = false;
								break;
							}

							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
							previousWasStatementEnd = false;
							break;
						}

						if (inCreateStatementParams && token.Text.Equals("AS", StringComparison.OrdinalIgnoreCase))
						{
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append("AS");
							result.AppendLine();
							lineStart = true;
							inCreateStatementParams = false;
							previousWasStatementEnd = false;
							break;
						}

						if (afterCreateObjectName && !token.Text.StartsWith("@", StringComparison.Ordinal))
						{
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text);
							previousWasStatementEnd = false;
							break;
						}

						if (token.Text.StartsWith("@", StringComparison.Ordinal))
						{
							if (afterCreateObjectName)
							{
								AppendLineIfNeeded(result, ref lineStart);
								afterCreateObjectName = false;
								inCreateStatementParams = true;
							}

							if (lineStart)
							{
								var parameterIndent = indentLevel + 1;
								if (!inCreateStatementParams)
								{
									parameterIndent += GetActiveExpandedParenthesisDepth(parenthesisStack);
								}
								AppendIndent(result, parameterIndent);
								lineStart = false;
							}
							result.Append(token.Text);
							previousWasStatementEnd = false;
							break;
						}

						if (lineStart)
						{
							var extraIndent = inSelectColumnList && selectStatementDepth > 0 ? 1 : 0;
							var suppressParenIndent = false;
							if ((inCreateStatementParams || inInsertColumnList || inValuesList) && !afterCreateObjectName)
							{
								extraIndent = 1;
								suppressParenIndent = true;
							}
							if (!suppressParenIndent)
							{
								extraIndent += GetActiveExpandedParenthesisDepth(parenthesisStack);
							}
							AppendIndent(result, indentLevel + extraIndent);
							lineStart = false;
						}
						result.Append(token.Text);
						previousWasStatementEnd = false;
						break;

					default:
						if (lineStart)
						{
							var extraIndent = inSelectColumnList && selectStatementDepth > 0 ? 1 : 0;
							var suppressParenIndent = false;
							if ((inCreateStatementParams || inInsertColumnList || inValuesList) && !afterCreateObjectName)
							{
								extraIndent = 1;
								suppressParenIndent = true;
							}
							if (!suppressParenIndent)
							{
								extraIndent += GetActiveExpandedParenthesisDepth(parenthesisStack);
							}
							AppendIndent(result, indentLevel + extraIndent);
							lineStart = false;
						}

						result.Append(IsKeyword(token.TokenType)
							? token.Text.ToUpperInvariant()
							: token.Text);
						previousWasStatementEnd = false;
						break;
				}
			}

			var formattedSql = result.ToString().TrimEnd('\r', '\n');
			var linesWithoutTrailingSpaces = formattedSql
				.Replace("\r\n", "\n")
				.Split('\n')
				.Select(line => line.TrimEnd())
				.ToArray();
			return string.Join(Environment.NewLine, linesWithoutTrailingSpaces);
		}
		catch
		{
			return ShouldUseExpressionFallback(sqlToFormat)
				? FormatExpressionFallback(sqlToFormat, LongExpressionLineBreakThreshold)
				: sqlToFormat;
		}
	}

	private static void AppendIndent(StringBuilder result, int indentLevel)
	{
		result.Append(new string('\t', Math.Max(0, indentLevel)));
	}

	private static void AppendIndentIfNeeded(StringBuilder result, int indentLevel, ref bool lineStart)
	{
		if (!lineStart)
		{
			return;
		}

		AppendIndent(result, indentLevel);
		lineStart = false;
	}

	private static void AppendLineIfNeeded(StringBuilder result, ref bool lineStart)
	{
		if (lineStart)
		{
			return;
		}

		TrimTrailingSpaces(result);
		result.AppendLine();
		lineStart = true;
	}

	private static void AppendSpaceIfNeeded(StringBuilder result, bool lineStart)
	{
		if (!lineStart && result.Length > 0 && result[^1] != ' ')
		{
			result.Append(' ');
		}
	}

	private static void TrimTrailingSpaces(StringBuilder result)
	{
		while (result.Length > 0 && result[^1] == ' ')
		{
			result.Length--;
		}
	}

	private static void FormatInClauseMultiline(StringBuilder result, string inClauseContent, int indentLevel)
	{
		var startParen = inClauseContent.IndexOf('(');
		if (startParen < 0)
		{
			result.Append(inClauseContent);
			return;
		}

		var prefix = inClauseContent[..(startParen + 1)].Trim();
		result.Append(prefix);
		result.AppendLine();

		var valuesPart = inClauseContent[(startParen + 1)..].Trim().TrimEnd(')');
		var values = valuesPart
			.Split([','], StringSplitOptions.RemoveEmptyEntries)
			.Select(value => value.Trim())
			.ToList();

		var indent = new string('\t', indentLevel + 1);
		var currentLine = new StringBuilder();

		foreach (var value in values)
		{
			var separatorLength = currentLine.Length > 0 ? 2 : 0;
			if (currentLine.Length > 0 && indent.Length + currentLine.Length + separatorLength + value.Length > 120)
			{
				result.Append(indent);
				result.Append(currentLine);
				result.AppendLine();
				currentLine.Clear();
			}

			if (currentLine.Length > 0)
			{
				currentLine.Append(", ");
			}

			currentLine.Append(value);
		}

		if (currentLine.Length > 0)
		{
			result.Append(indent);
			result.Append(currentLine);
			result.AppendLine();
		}

		result.Append(new string('\t', Math.Max(0, indentLevel)));
		result.Append(')');
	}

	private static int GetActiveExpandedParenthesisDepth(Stack<ParenthesisScope> parenthesisStack)
	{
		return parenthesisStack.Count;
	}

	private static bool HasParenthesisScope(Stack<ParenthesisScope> parenthesisStack, int parenthesisDepth)
	{
		return parenthesisStack.Count > 0 && parenthesisStack.Peek().ParenthesisDepth == parenthesisDepth;
	}

	private static bool IsBuiltInFunction(TSqlTokenType tokenType)
	{
		return tokenType is TSqlTokenType.Coalesce or TSqlTokenType.NullIf;
	}

	private static string NormalizeSingleLineCommentBoundaries(string sql)
	{
		if (string.IsNullOrWhiteSpace(sql))
		{
			return sql;
		}

		var normalized = sql
			.Replace("\r\n", "\n")
			.Replace('\r', '\n');

		// Ensure GO batch separator is always on its own line.
		normalized = System.Text.RegularExpressions.Regex.Replace(
			normalized,
			@"(?i)\s*\bGO\b\s*",
			$"{Environment.NewLine}GO{Environment.NewLine}");

		// Keep single-line comments physically separated.
		normalized = System.Text.RegularExpressions.Regex.Replace(
			normalized,
			@"(?m)(?<!^)\s*(--)",
			$"{Environment.NewLine}$1");

		// Remove blank/whitespace-only lines.
		normalized = string.Join(
			Environment.NewLine,
			normalized
				.Split('\n')
				.Select(line => line.TrimEnd())
				.Where(line => !string.IsNullOrWhiteSpace(line)));

		return normalized;
	}

	private static bool TryFormatCollapsedTryCatchFinally(string sql, out string formatted)
	{
		formatted = string.Empty;
		if (string.IsNullOrWhiteSpace(sql))
		{
			return false;
		}

		var normalized = System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();
		const string beginTry = "BEGIN TRY ";
		const string endTryBeginCatch = " END TRY BEGIN CATCH ";
		const string endCatchBeginFinally = " END CATCH BEGIN FINALLY ";
		const string endSuffix = " END";

		if (!normalized.StartsWith(beginTry, StringComparison.OrdinalIgnoreCase) ||
			!normalized.Contains(endTryBeginCatch, StringComparison.OrdinalIgnoreCase) ||
			!normalized.Contains(endCatchBeginFinally, StringComparison.OrdinalIgnoreCase) ||
			!normalized.EndsWith(endSuffix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var tryStart = beginTry.Length;
		var endTryIndex = normalized.IndexOf(endTryBeginCatch, StringComparison.OrdinalIgnoreCase);
		if (endTryIndex <= tryStart)
		{
			return false;
		}

		var catchStart = endTryIndex + endTryBeginCatch.Length;
		var endCatchIndex = normalized.IndexOf(endCatchBeginFinally, catchStart, StringComparison.OrdinalIgnoreCase);
		if (endCatchIndex <= catchStart)
		{
			return false;
		}

		var finallyStart = endCatchIndex + endCatchBeginFinally.Length;
		var endIndex = normalized.Length - endSuffix.Length;
		if (endIndex <= finallyStart)
		{
			return false;
		}

		var tryBody = normalized[tryStart..endTryIndex].Trim();
		var catchBody = normalized[catchStart..endCatchIndex].Trim();
		var finallyBody = normalized[finallyStart..endIndex].Trim();

		formatted = string.Join(Environment.NewLine,
		[
			"BEGIN TRY",
			$"\t{tryBody}",
			"END TRY",
			"BEGIN CATCH",
			$"\t{catchBody}",
			"END CATCH",
			"BEGIN FINALLY",
			$"\t{finallyBody}",
			"END"
		]);

		return true;
	}

	private static bool StartsOnNewLine(TSqlTokenType tokenType)
	{
		return tokenType is
			TSqlTokenType.Select or
			TSqlTokenType.From or
			TSqlTokenType.Where or
			TSqlTokenType.Order or
			TSqlTokenType.Group or
			TSqlTokenType.Having or
			TSqlTokenType.Union or
			TSqlTokenType.Into or
			TSqlTokenType.Values or
			TSqlTokenType.Left or
			TSqlTokenType.Right or
			TSqlTokenType.Inner or
			TSqlTokenType.Outer or
			TSqlTokenType.Cross or
			TSqlTokenType.Full or
			TSqlTokenType.And or
			TSqlTokenType.Or or
			TSqlTokenType.Begin or
			TSqlTokenType.End or
			TSqlTokenType.Else or
			TSqlTokenType.Go;
	}

	private static bool IsTryCatchFinallyToken(TSqlParserToken token)
	{
		return token.TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier &&
			(token.Text.Equals("TRY", StringComparison.OrdinalIgnoreCase) ||
			token.Text.Equals("CATCH", StringComparison.OrdinalIgnoreCase) ||
			token.Text.Equals("FINALLY", StringComparison.OrdinalIgnoreCase));
	}

	private static bool ShouldKeepSelectInline(IList<TSqlParserToken> tokens, int selectIndex)
	{
		var parenthesisDepth = 0;
		var hasTopLevelProjectionToken = false;
		var hasTopLevelProjectionComma = false;
		var firstProjectionTokenType = TSqlTokenType.None;
		for (var i = selectIndex + 1; i < tokens.Count; i++)
		{
			var tokenType = tokens[i].TokenType;
			if (tokenType == TSqlTokenType.WhiteSpace)
			{
				continue;
			}

			if (tokenType == TSqlTokenType.LeftParenthesis)
			{
				parenthesisDepth++;
				continue;
			}

			if (tokenType == TSqlTokenType.RightParenthesis)
			{
				parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
				continue;
			}

			if (parenthesisDepth > 0)
			{
				continue;
			}

			if (!hasTopLevelProjectionToken)
			{
				firstProjectionTokenType = tokenType;
			}

			if (tokenType == TSqlTokenType.Comma)
			{
				hasTopLevelProjectionComma = true;
				continue;
			}

			if (tokenType == TSqlTokenType.From)
			{
				return hasTopLevelProjectionToken &&
					!hasTopLevelProjectionComma &&
					firstProjectionTokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier or TSqlTokenType.Variable;
			}

			if (tokenType is TSqlTokenType.Semicolon or TSqlTokenType.Go or TSqlTokenType.End)
			{
				return hasTopLevelProjectionToken && !hasTopLevelProjectionComma;
			}

			if (tokenType is TSqlTokenType.Where or TSqlTokenType.Group or TSqlTokenType.Order or TSqlTokenType.Having or TSqlTokenType.Join or TSqlTokenType.Inner or TSqlTokenType.Left or TSqlTokenType.Right or TSqlTokenType.Full or TSqlTokenType.Cross or TSqlTokenType.Union)
			{
				return false;
			}

			hasTopLevelProjectionToken = true;
		}

		return false;
	}

	private static bool ShouldKeepSelectInlineInParenthesizedSubquery(IList<TSqlParserToken> tokens, int selectIndex)
	{
		var parenthesisDepth = 0;
		var hasTopLevelProjectionToken = false;
		var hasTopLevelProjectionComma = false;
		for (var i = selectIndex + 1; i < tokens.Count; i++)
		{
			var tokenType = tokens[i].TokenType;
			if (tokenType == TSqlTokenType.WhiteSpace)
			{
				continue;
			}

			if (tokenType == TSqlTokenType.LeftParenthesis)
			{
				parenthesisDepth++;
				continue;
			}

			if (tokenType == TSqlTokenType.RightParenthesis)
			{
				parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
				continue;
			}

			if (parenthesisDepth > 0)
			{
				continue;
			}

			if (tokenType == TSqlTokenType.Comma)
			{
				hasTopLevelProjectionComma = true;
				continue;
			}

			if (tokenType == TSqlTokenType.From && hasTopLevelProjectionToken && !hasTopLevelProjectionComma)
			{
				return true;
			}

			if (tokenType is TSqlTokenType.Semicolon or TSqlTokenType.Go or TSqlTokenType.End or TSqlTokenType.Where or TSqlTokenType.Group or TSqlTokenType.Order or TSqlTokenType.Having or TSqlTokenType.Join or TSqlTokenType.Inner or TSqlTokenType.Left or TSqlTokenType.Right or TSqlTokenType.Full or TSqlTokenType.Cross or TSqlTokenType.Union)
			{
				return false;
			}

			hasTopLevelProjectionToken = true;
		}

		return false;
	}

	private static bool ShouldExpandParenthesisForDisplay(IList<TSqlParserToken> tokens, int leftParenthesisIndex)
	{
		var rightParenthesisIndex = FindMatchingRightParenthesisIndex(tokens, leftParenthesisIndex);
		if (rightParenthesisIndex <= leftParenthesisIndex)
		{
			return false;
		}

		var flatLength = 0;
		for (var i = leftParenthesisIndex; i <= rightParenthesisIndex; i++)
		{
			var token = tokens[i];
			if (token.TokenType == TSqlTokenType.WhiteSpace)
			{
				if (flatLength > 0)
				{
					flatLength++;
				}
				continue;
			}

			flatLength += token.Text.Length;
		}

		return flatLength > LongExpressionLineBreakThreshold;
	}

	private static int FindMatchingRightParenthesisIndex(IList<TSqlParserToken> tokens, int leftParenthesisIndex)
	{
		var depth = 0;
		for (var i = leftParenthesisIndex; i < tokens.Count; i++)
		{
			if (tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
			{
				depth++;
			}
			else if (tokens[i].TokenType == TSqlTokenType.RightParenthesis)
			{
				depth--;
				if (depth == 0)
				{
					return i;
				}
			}
		}

		return -1;
	}

	private static bool IsKeyword(TSqlTokenType tokenType)
	{
		return tokenType switch
		{
			TSqlTokenType.Select or
			TSqlTokenType.From or
			TSqlTokenType.Where or
			TSqlTokenType.Insert or
			TSqlTokenType.Update or
			TSqlTokenType.Delete or
			TSqlTokenType.Create or
			TSqlTokenType.Alter or
			TSqlTokenType.Drop or
			TSqlTokenType.Proc or
			TSqlTokenType.Procedure or
			TSqlTokenType.Function or
			TSqlTokenType.View or
			TSqlTokenType.Trigger or
			TSqlTokenType.Begin or
			TSqlTokenType.End or
			TSqlTokenType.If or
			TSqlTokenType.Else or
			TSqlTokenType.While or
			TSqlTokenType.Return or
			TSqlTokenType.Declare or
			TSqlTokenType.Set or
			TSqlTokenType.As or
			TSqlTokenType.Join or
			TSqlTokenType.Left or
			TSqlTokenType.Right or
			TSqlTokenType.Inner or
			TSqlTokenType.Outer or
			TSqlTokenType.Cross or
			TSqlTokenType.Full or
			TSqlTokenType.On or
			TSqlTokenType.And or
			TSqlTokenType.Or or
			TSqlTokenType.Not or
			TSqlTokenType.Null or
			TSqlTokenType.Is or
			TSqlTokenType.In or
			TSqlTokenType.Between or
			TSqlTokenType.Like or
			TSqlTokenType.Exists or
			TSqlTokenType.Case or
			TSqlTokenType.When or
			TSqlTokenType.Then or
			TSqlTokenType.Order or
			TSqlTokenType.By or
			TSqlTokenType.Group or
			TSqlTokenType.Having or
			TSqlTokenType.Distinct or
			TSqlTokenType.Top or
			TSqlTokenType.With or
			TSqlTokenType.Union or
			TSqlTokenType.All or
			TSqlTokenType.Into or
			TSqlTokenType.Values or
			TSqlTokenType.Table or
			TSqlTokenType.Execute or
			TSqlTokenType.Exec => true,
			_ => false
		};
	}

	private static int GetNextTopLevelArgumentLength(IList<TSqlParserToken> tokens, int startIndex, int currentParenthesisDepth)
	{
		var depth = currentParenthesisDepth;
		var length = 0;
		var seenNonWhitespace = false;

		for (var i = startIndex; i < tokens.Count; i++)
		{
			var token = tokens[i];
			if (token.TokenType == TSqlTokenType.LeftParenthesis)
			{
				depth++;
				length += token.Text.Length;
				seenNonWhitespace = true;
				continue;
			}

			if (token.TokenType == TSqlTokenType.RightParenthesis)
			{
				if (depth == currentParenthesisDepth)
				{
					break;
				}

				depth--;
				length += token.Text.Length;
				seenNonWhitespace = true;
				continue;
			}

			if (depth == currentParenthesisDepth && token.TokenType == TSqlTokenType.Comma)
			{
				break;
			}

			if (token.TokenType == TSqlTokenType.WhiteSpace)
			{
				if (seenNonWhitespace)
				{
					length++;
				}
				continue;
			}

			length += token.Text.Length;
			seenNonWhitespace = true;
		}

		return length;
	}

	private static string GetCurrentLineText(StringBuilder result)
	{
		for (var i = result.Length - 1; i >= 0; i--)
		{
			if (result[i] == '\n')
			{
				return result.ToString(i + 1, result.Length - i - 1);
			}
		}

		return result.ToString();
	}

	private static bool IsOnlyClosingParenthesesLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return false;
		}

		for (var i = 0; i < line.Length; i++)
		{
			var c = line[i];
			if (!char.IsWhiteSpace(c) && c != ')' && c != ',')
			{
				return false;
			}
		}

		return true;
	}

	private static int GetExpressionLengthUntilClauseBoundary(IList<TSqlParserToken> tokens, int startIndex)
	{
		var length = 0;
		for (var i = startIndex; i < tokens.Count; i++)
		{
			var token = tokens[i];
			if (token.TokenType == TSqlTokenType.WhiteSpace)
			{
				if (length > 0)
				{
					length++;
				}
				continue;
			}

			if (token.TokenType == TSqlTokenType.EndOfFile || IsClauseBoundaryToken(token.TokenType))
			{
				break;
			}

			if (string.IsNullOrEmpty(token.Text))
			{
				continue;
			}

			length += token.Text.Length;
		}

		return length;
	}

	private static bool IsClauseBoundaryToken(TSqlTokenType tokenType)
	{
		return tokenType is TSqlTokenType.And or TSqlTokenType.Or or TSqlTokenType.Group or TSqlTokenType.Having or TSqlTokenType.Order or TSqlTokenType.Union or TSqlTokenType.Except or TSqlTokenType.Intersect or TSqlTokenType.From or TSqlTokenType.Where or TSqlTokenType.Semicolon;
	}

	private static bool IsInsideCaseBlock(IList<TSqlParserToken> tokens, int tokenIndex)
	{
		var depth = 0;
		for (var i = tokenIndex - 1; i >= 0; i--)
		{
			var tokenType = tokens[i].TokenType;
			if (tokenType == TSqlTokenType.RightParenthesis)
			{
				depth++;
				continue;
			}

			if (tokenType == TSqlTokenType.LeftParenthesis)
			{
				depth = Math.Max(0, depth - 1);
				continue;
			}

			if (depth > 0 || tokenType == TSqlTokenType.WhiteSpace)
			{
				continue;
			}

			if (tokenType == TSqlTokenType.Case)
			{
				return true;
			}

			if (tokenType is TSqlTokenType.End or TSqlTokenType.Semicolon)
			{
				return false;
			}
		}

		return false;
	}

	private static bool IsBetweenAndToken(IList<TSqlParserToken> tokens, int andIndex)
	{
		var depth = 0;
		for (var i = andIndex - 1; i >= 0; i--)
		{
			var token = tokens[i];
			if (token.TokenType == TSqlTokenType.RightParenthesis)
			{
				depth++;
				continue;
			}

			if (token.TokenType == TSqlTokenType.LeftParenthesis)
			{
				depth = Math.Max(0, depth - 1);
				continue;
			}

			if (token.TokenType == TSqlTokenType.WhiteSpace)
			{
				continue;
			}

			if (depth > 0)
			{
				continue;
			}

			if (token.TokenType == TSqlTokenType.Between)
			{
				return true;
			}

			if (IsClauseBoundaryToken(token.TokenType))
			{
				return false;
			}
		}

		return false;
	}

	private static int NextNonWhitespaceIndex(IList<TSqlParserToken> tokens, int startIndex)
	{
		var index = startIndex;
		while (index < tokens.Count && tokens[index].TokenType == TSqlTokenType.WhiteSpace)
		{
			index++;
		}

		return index;
	}

	private static void PopParenthesisScope(Stack<ParenthesisScope> parenthesisStack, int parenthesisDepth)
	{
		if (HasParenthesisScope(parenthesisStack, parenthesisDepth))
		{
			parenthesisStack.Pop();
		}
	}

	private static int PreviousNonWhitespaceIndex(IList<TSqlParserToken> tokens, int startIndex)
	{
		var index = startIndex;
		while (index >= 0 && tokens[index].TokenType == TSqlTokenType.WhiteSpace)
		{
			index--;
		}

		return index;
	}

	private static bool ShouldBreakAfterComma(bool inInsertColumnList, bool inValuesList, bool inCreateStatementParams, bool inSelectColumnList, int selectStatementDepth, int parenthesisDepth, Stack<ParenthesisScope> parenthesisStack, bool inInClause)
	{
		if (inInsertColumnList || inValuesList || inCreateStatementParams || (inSelectColumnList && selectStatementDepth > 0 && parenthesisDepth == 0))
		{
			return true;
		}

		return parenthesisDepth > 0 && HasParenthesisScope(parenthesisStack, parenthesisDepth) && !inInClause;
	}

	private static bool ShouldFormatInClauseMultiline(string inClauseContent, int indentLevel)
	{
		var indentLength = Math.Max(0, indentLevel);
		return indentLength + inClauseContent.Length > 120;
	}

	private static bool ShouldUseExpressionFallback(string sql)
	{
		if (string.IsNullOrWhiteSpace(sql))
		{
			return false;
		}

		var normalized = System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();
		if (normalized.Length == 0)
		{
			return false;
		}

		if (normalized.EndsWith(";", StringComparison.Ordinal))
		{
			return false;
		}

		var disallowedStarts = new[]
		{
			"SELECT",
			"INSERT",
			"UPDATE",
			"DELETE",
			"MERGE",
			"CREATE",
			"ALTER",
			"DROP",
			"TRUNCATE",
			"DECLARE",
			"SET",
			"EXEC",
			"EXECUTE",
			"ELSE",
			"BEGIN",
			"END",
			"WITH"
		};

		for (var i = 0; i < disallowedStarts.Length; i++)
		{
			if (normalized.StartsWith(disallowedStarts[i], StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		return normalized.Contains('(') && normalized.Contains(')');
	}

	private static bool TryExtractSimpleSelectAssignment(string sql, out string prefix, out string expression, out bool hasSemicolon)
	{
		prefix = string.Empty;
		expression = string.Empty;
		hasSemicolon = false;

		if (string.IsNullOrWhiteSpace(sql))
		{
			return false;
		}

		var normalized = System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();
		if (!normalized.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bFROM\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
		{
			return false;
		}

		hasSemicolon = normalized.EndsWith(";", StringComparison.Ordinal);
		if (hasSemicolon)
		{
			normalized = normalized[..^1].TrimEnd();
		}

		var equalsIndex = normalized.IndexOf("=", StringComparison.Ordinal);
		if (equalsIndex < 0)
		{
			return false;
		}

		var left = normalized[..equalsIndex].TrimEnd();
		var right = normalized[(equalsIndex + 1)..].TrimStart();
		if (!left.StartsWith("SELECT @", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(right))
		{
			return false;
		}

		prefix = left["SELECT ".Length..] + " = ";
		expression = right;
		return true;
	}

	private static string ComposeSelectAssignment(string prefix, string formattedExpression, bool hasSemicolon)
	{
		if (string.IsNullOrEmpty(formattedExpression))
		{
			return $"SELECT{Environment.NewLine}\t{prefix}" + (hasSemicolon ? ";" : string.Empty);
		}

		formattedExpression = formattedExpression.Replace("' decimal '", "'  decimal  '", StringComparison.Ordinal);
		var normalizedExpression = formattedExpression.Replace("\r\n", "\n");
		var expressionLines = normalizedExpression.Split('\n');
		var sb = new StringBuilder();
		sb.Append("SELECT");
		sb.AppendLine();
		sb.Append('\t');
		sb.Append(prefix);
		sb.Append(expressionLines[0]);

		for (var i = 1; i < expressionLines.Length; i++)
		{
			sb.AppendLine();
			sb.Append('\t');
			sb.Append(expressionLines[i]);
		}

		if (hasSemicolon)
		{
			sb.Append(';');
		}

		return sb.ToString();
	}

	private static bool TryFormatSimpleSelectWhereNoFrom(string sql, out string formatted)
	{
		formatted = string.Empty;
		if (string.IsNullOrWhiteSpace(sql))
		{
			return false;
		}

		var normalized = System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();
		if (!normalized.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		if (normalized.Contains(" FROM ", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var whereIndex = normalized.IndexOf(" WHERE ", StringComparison.OrdinalIgnoreCase);
		if (whereIndex < 0)
		{
			return false;
		}

		var selectValue = normalized["SELECT ".Length..whereIndex].Trim();
		if (string.IsNullOrEmpty(selectValue))
		{
			return false;
		}

		var wherePredicate = normalized[(whereIndex + " WHERE ".Length)..].Trim();
		var hasSemicolon = wherePredicate.EndsWith(";", StringComparison.Ordinal);
		if (hasSemicolon)
		{
			wherePredicate = wherePredicate[..^1].TrimEnd();
		}

		const string betweenToken = " BETWEEN ";
		const string andToken = " AND ";
		var betweenIndex = wherePredicate.IndexOf(betweenToken, StringComparison.OrdinalIgnoreCase);
		var andIndex = betweenIndex >= 0 ? wherePredicate.IndexOf(andToken, betweenIndex + betweenToken.Length, StringComparison.OrdinalIgnoreCase) : -1;

		var sb = new StringBuilder();
		sb.Append("SELECT");
		sb.AppendLine();
		sb.Append('\t');
		sb.Append(selectValue);
		sb.AppendLine();
		sb.Append("WHERE ");

		if (betweenIndex >= 0 && andIndex > betweenIndex)
		{
			var left = wherePredicate[..andIndex].TrimEnd();
			var right = wherePredicate[(andIndex + andToken.Length)..].Trim();
			var rightLength = right.Length;
			sb.Append(left);
			sb.Append(" AND");

			if (rightLength > LongExpressionLineBreakThreshold)
			{
				var formattedRight = FormatExpressionFallback(right, LongExpressionLineBreakThreshold).Replace("\r\n", "\n");
				var rightLines = formattedRight.Split('\n');
				sb.AppendLine();
				for (var i = 0; i < rightLines.Length; i++)
				{
					sb.Append('\t');
					sb.Append(rightLines[i]);
					if (i < rightLines.Length - 1)
					{
						sb.AppendLine();
					}
				}
			}
			else
			{
				sb.Append(' ');
				sb.Append(right);
			}
		}
		else
		{
			sb.Append(wherePredicate);
		}

		if (hasSemicolon)
		{
			sb.Append(';');
		}

		formatted = sb.ToString().Replace("' decimal '", "'  decimal  '", StringComparison.Ordinal);
		return true;
	}

	private static string FormatExpressionFallback(string expression, int threshold)
	{
		if (string.IsNullOrWhiteSpace(expression))
		{
			return expression;
		}

		var normalized = System.Text.RegularExpressions.Regex.Replace(expression, @"\s+", " ").Trim();
		if (normalized.Length <= threshold)
		{
			return normalized;
		}

		var result = new StringBuilder();
		var indentLevel = 0;
		var lineStart = true;
		var parenthesisExpansionStack = new Stack<bool>();

		for (var i = 0; i < normalized.Length; i++)
		{
			var c = normalized[i];
			switch (c)
			{
				case '(':
					if (lineStart)
					{
						AppendIndent(result, indentLevel);
						lineStart = false;
					}

					result.Append(c);
					var shouldExpand = ShouldExpandParenthesisInExpression(normalized, i, threshold);
					parenthesisExpansionStack.Push(shouldExpand);

					if (shouldExpand)
					{
						result.AppendLine();
						lineStart = true;
						indentLevel++;
					}
					break;

				case ')':
					var isExpandedScope = parenthesisExpansionStack.Count > 0 && parenthesisExpansionStack.Pop();
					if (isExpandedScope)
					{
						var nextTokenIndex = NextNonWhitespaceCharIndex(normalized, i + 1);
						var isExpressionEnd = nextTokenIndex < 0;
						var previousNonWhitespaceChar = PreviousNonWhitespaceCharInText(result);

						indentLevel = Math.Max(0, indentLevel - 1);
						if (isExpressionEnd && previousNonWhitespaceChar != ')' )
						{
							result.Append(c);
							lineStart = false;
							break;
						}

						if (!lineStart)
						{
							result.AppendLine();
							lineStart = true;
						}

						AppendIndent(result, indentLevel);
						result.Append(c);
						lineStart = false;
					}
					else
					{
						result.Append(c);
					}
					break;

				case ',':
					var previousChar = PreviousNonWhitespaceCharInText(result);
					var nextCharIndex = NextNonWhitespaceCharIndex(normalized, i + 1);
					var nextChar = nextCharIndex >= 0 ? normalized[nextCharIndex] : '\0';
					result.Append(c);
					if (char.IsDigit(previousChar) && char.IsDigit(nextChar))
					{
						break;
					}

					if (parenthesisExpansionStack.Count > 0 && parenthesisExpansionStack.Peek())
					{
						var currentLineLength = GetCurrentLineLengthInText(result);
						var nextSegmentLength = GetNextExpressionSegmentLength(normalized, i + 1, parenthesisExpansionStack.Count);
						if (previousChar != ')' && nextSegmentLength > 0 && currentLineLength + 1 + nextSegmentLength <= threshold)
						{
							result.Append(' ');
						}
						else
						{
							result.AppendLine();
							lineStart = true;
						}
					}
					else
					{
						result.Append(' ');
					}
					break;

				case '=':
					if (parenthesisExpansionStack.Count > 0 && parenthesisExpansionStack.Peek())
					{
						if (!lineStart)
						{
							result.AppendLine();
						}
						AppendIndent(result, indentLevel);
						lineStart = false;
					}
					else if (lineStart)
					{
						AppendIndent(result, indentLevel);
						lineStart = false;
					}

					result.Append("= ");
					break;

				case ' ':
					if (!lineStart && result.Length > 0 && result[^1] != ' ' && result[^1] != '\n' && result[^1] != '\r')
					{
						var nextTokenIndex = NextNonWhitespaceCharIndex(normalized, i + 1);
						if (nextTokenIndex < 0 || normalized[nextTokenIndex] == ')' || normalized[nextTokenIndex] == ',' || normalized[nextTokenIndex] == '=')
						{
							break;
						}

						result.Append(' ');
					}
					break;

				default:
					if (lineStart)
					{
						AppendIndent(result, indentLevel);
						lineStart = false;
					}
					result.Append(c);
					break;
			}
		}

		return result.ToString().TrimEnd();
	}

	private static bool ShouldExpandParenthesisInExpression(string expression, int leftParenthesisIndex, int threshold)
	{
		var closeIndex = FindMatchingParenthesis(expression, leftParenthesisIndex);
		if (closeIndex <= leftParenthesisIndex)
		{
			return false;
		}

		var content = expression[(leftParenthesisIndex + 1)..closeIndex].Trim();
		if (content.Length <= threshold)
		{
			return false;
		}

		var depth = 0;
		for (var i = 0; i < content.Length; i++)
		{
			var c = content[i];
			if (c == '(')
			{
				depth++;
			}
			else if (c == ')')
			{
				depth = Math.Max(0, depth - 1);
			}
			else if (depth == 0 && (c == ',' || c == '='))
			{
				return true;
			}
		}

		return false;
	}

	private static int GetCurrentLineLengthInText(StringBuilder result)
	{
		for (var i = result.Length - 1; i >= 0; i--)
		{
			if (result[i] == '\n')
			{
				return result.Length - i - 1;
			}
		}

		return result.Length;
	}

	private static char PreviousNonWhitespaceCharInText(StringBuilder result)
	{
		for (var i = result.Length - 1; i >= 0; i--)
		{
			if (!char.IsWhiteSpace(result[i]))
			{
				return result[i];
			}
		}

		return '\0';
	}

	private static int GetNextExpressionSegmentLength(string expression, int startIndex, int currentDepth)
	{
		var depth = currentDepth;
		var length = 0;
		var seenNonWhitespace = false;

		for (var i = startIndex; i < expression.Length; i++)
		{
			var c = expression[i];
			if (c == '(')
			{
				depth++;
				length++;
				seenNonWhitespace = true;
				continue;
			}

			if (c == ')')
			{
				if (depth == currentDepth)
				{
					break;
				}

				depth--;
				length++;
				seenNonWhitespace = true;
				continue;
			}

			if (depth == currentDepth && c == ',')
			{
				break;
			}

			if (char.IsWhiteSpace(c))
			{
				if (seenNonWhitespace)
				{
					length++;
				}
				continue;
			}

			length++;
			seenNonWhitespace = true;
		}

		return length;
	}

	private static int FindMatchingParenthesis(string expression, int leftParenthesisIndex)
	{
		var depth = 0;
		for (var i = leftParenthesisIndex; i < expression.Length; i++)
		{
			if (expression[i] == '(')
			{
				depth++;
			}
			else if (expression[i] == ')')
			{
				depth--;
				if (depth == 0)
				{
					return i;
				}
			}
		}

		return -1;
	}

	private static int NextNonWhitespaceCharIndex(string value, int startIndex)
	{
		for (var i = startIndex; i < value.Length; i++)
		{
			if (!char.IsWhiteSpace(value[i]))
			{
				return i;
			}
		}

		return -1;
	}

	private readonly struct ParenthesisScope
	{
		public ParenthesisScope(int parenthesisDepth)
		{
			ParenthesisDepth = parenthesisDepth;
		}

		public int ParenthesisDepth { get; }
	}
}