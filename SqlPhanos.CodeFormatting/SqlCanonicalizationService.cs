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
	private const int CasePhantomParenthesisDepth = -1;

	/// <param name="sql">The SQL text to format.</param>
	/// <param name="openingParenOnNewLine">
	/// Controls where the opening paren of a top-level column/parameter list goes for
	/// CREATE TABLE, INSERT INTO's column list, and CREATE PROC/FUNCTION/VIEW/TRIGGER's
	/// parameter list - false (default) keeps it glued to the end of the preceding line
	/// (e.g. "CREATE TABLE Foo ("), true puts it on its own line. Does not affect
	/// expression-level or function-call parens (e.g. COALESCE(x, y)), which always stay
	/// attached regardless of this setting.
	/// </param>
	public string FormatForDisplay(string sql, bool openingParenOnNewLine = false)
		=> FormatForDisplayCore(sql, openingParenOnNewLine).Text;

	/// <summary>
	/// Same formatting as <see cref="FormatForDisplay"/>, but also reports where each source
	/// token landed in the output - see <see cref="SqlFormatResult.TokenPositions"/> for when
	/// that mapping is and isn't available.
	/// </summary>
	public SqlFormatResult FormatForDisplayWithPositions(string sql, bool openingParenOnNewLine = false)
		=> FormatForDisplayCore(sql, openingParenOnNewLine);

	// Every formatting path below - the regex-based fast paths, the main tokenizer loop, and the
	// exception-driven fallbacks - has its own way to get comment/token handling subtly wrong (we
	// found two independent examples of exactly that in one afternoon). Rather than trusting each
	// path to be individually bug-free forever, this wrapper re-tokenizes whatever came out and
	// verifies it represents the same real SQL as the input before handing it back - see
	// IsRoundTripSafe. On failure it returns the original text unchanged: a silent no-op is an
	// acceptable outcome for a formatter, silently corrupting the caller's script is not.
	// A handful of lines each side of the mismatch is enough to see the actual problem; the
	// object script it came from can run to hundreds of lines.
	private const int MismatchSnippetContextLines = 5;

	private SqlFormatResult FormatForDisplayCore(string sql, bool openingParenOnNewLine)
	{
		var result = FormatForDisplayCoreUnchecked(sql, openingParenOnNewLine);

		if (string.IsNullOrWhiteSpace(sql))
		{
			return result;
		}

		if (!TryFindRoundTripMismatch(sql, result.Text, out var originalOffset, out var formattedOffset))
		{
			return result;
		}

		var originalSnippet = ExtractContextSnippet(sql, originalOffset, MismatchSnippetContextLines);
		var rejectedSnippet = ExtractContextSnippet(result.Text, formattedOffset, MismatchSnippetContextLines);
		return new SqlFormatResult(sql, null, safetyCheckPassed: false, originalTextSnippet: originalSnippet, rejectedTextSnippet: rejectedSnippet);
	}

	private SqlFormatResult FormatForDisplayCoreUnchecked(string sql, bool openingParenOnNewLine)
	{
		if (string.IsNullOrWhiteSpace(sql))
		{
			return new SqlFormatResult(sql, null);
		}

		var sqlToFormat = NormalizeSingleLineCommentBoundaries(sql);
		if (TryFormatCollapsedTryCatchFinally(sqlToFormat, out var tryCatchFinallyFormatted))
		{
			return new SqlFormatResult(tryCatchFinallyFormatted, null);
		}

		if (TryExtractSimpleSelectAssignment(sqlToFormat, out var assignmentPrefix, out var assignmentExpression, out var assignmentHasSemicolon))
		{
			var formattedExpression = FormatExpressionFallback(assignmentExpression, LongExpressionLineBreakThreshold);
			return new SqlFormatResult(ComposeSelectAssignment(assignmentPrefix, formattedExpression, assignmentHasSemicolon), null);
		}

		if (TryFormatSimpleSelectWhereNoFrom(sqlToFormat, out var formattedSimpleSelectWhere))
		{
			return new SqlFormatResult(formattedSimpleSelectWhere, null);
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

			var tokens = fragment.ScriptTokenStream;

			if (errors is not null && errors.Count > 0 && ShouldUseExpressionFallback(sqlToFormat, tokens))
			{
				return new SqlFormatResult(FormatExpressionFallback(sqlToFormat, LongExpressionLineBreakThreshold), null);
			}

			if (tokens is null || tokens.Count == 0)
			{
				return new SqlFormatResult(sql, null);
			}

			var statementBoundaryCollector = new StatementBoundaryCollector();
			fragment.Accept(statementBoundaryCollector);
			var statementEndIndices = statementBoundaryCollector.LastTokenIndices;

			var result = new StringBuilder();
			// Captured as the very first thing each loop iteration does, before that token's own
			// indent/text is appended - approximate (a token preceded by fresh indentation reports
			// a start slightly before its actual text), which is fine for "roughly, if not
			// literally" caret repositioning and avoids instrumenting every individual case below.
			var tokenStartOffsets = new int[tokens.Count];
			var indentLevel = 0;
			var lineStart = true;
			var previousWasStatementEnd = false;
			var inSelectColumnList = false;
			var selectStatementDepth = 0;
			var parenthesisDepth = 0;
			var inInClause = false;
			var inSubqueryInClause = false;
			var inClauseStartIndex = -1;
			var inClauseDepth = -1;
			var inCreateStatementParams = false;
			var afterCreateObjectName = false;
			var createObjectRequiresAs = true;
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
			var inUpdateSetClause = false;
			var pendingUpdateSetClause = false;
			var inExecParams = false;
			var execAfterProcName = false;
			var inAlterTableStatement = false;
			var inAlterTablePrimaryKeyList = false;
			var alterTablePrimaryKeyListDepth = -1;
			var alterTablePrimaryKeyListMultiColumn = false;
			var parenthesisStack = new Stack<ParenthesisScope>();
			// Keyed by the current expanded-paren depth (GetActiveExpandedParenthesisDepth) so a
			// derived table's own JOIN/ON chain gets its own independent stack instead of being
			// confused with an outer, not-yet-resolved JOIN it happens to be nested inside.
			var joinFramesByDepth = new Dictionary<int, Stack<JoinFrame>>();
			var pendingBeginTryCatchFinally = false;
			// True from a CREATE TRIGGER's "INSTEAD" through the end of its DML operation list
			// (e.g. "INSTEAD OF UPDATE, DELETE") - keeps that whole clause glued onto one line
			// regardless of how the source SQL happened to break it up, and stops UPDATE/INSERT
			// from triggering their normal "start a new DML statement" handling (pendingUpdateSetClause
			// etc.), which would otherwise corrupt formatting of whatever follows.
			var inInsteadOfClause = false;
			var betweenAndJustEmitted = false;
			var inCreateObjectParameterList = false;
			var createObjectParameterListDepth = -1;
			var applyParenthesisDepth = -1;
			var overClauseParenDepth = -1;
			var caseExpressionDepth = 0;
			var caseWhenIndent = 0;
			var currentLineTokenLength = 0;
			// Indent of each currently-open CASE's own line, innermost on top - lets WHEN/END
			// align with wherever their CASE actually landed instead of recomputing independently
			// (which silently disagreed once CASE started consulting currentConditionIndent below).
			var caseIndentStack = new Stack<int>();
			// The indent that a boolean-clause continuation (AND/OR, or a CASE expression used as
			// a condition) should use right now - i.e. one level deeper than whatever line the
			// current WHERE/HAVING/ON clause actually landed on. Plain indentLevel + 1 works for a
			// top-level WHERE, but a nested join's ON can land on a deeper line than indentLevel
			// implies, so this is tracked explicitly instead of recomputed from indentLevel alone.
			var currentConditionIndent = indentLevel + 1;
			// True exactly while inside the token span of an active WHERE/HAVING/ON condition (set
			// at those keywords, cleared at the next clause boundary or statement end). Needed
			// because "not in a SELECT list, not inside parens" alone also matches things like a
			// bare CASE...END expression fragment with no enclosing WHERE at all, where CASE's old
			// column-0 behavior was already correct and currentConditionIndent doesn't apply.
			var inConditionClause = false;

			// CASE/END need the same "are we inside an active WHERE/ON/HAVING condition" distinction
			// that currentConditionIndent exists for for AND/OR - GetContentIndent alone has no way
			// to know about it, which is exactly why a CASE used as a WHERE/ON/HAVING condition
			// previously collapsed to column 0.
			int GetCaseAwareContentIndent()
			{
				// Only take over when the CASE sits directly in the condition, with no additional
				// expression paren wrapping it (e.g. a function-call argument) - that narrower case
				// keeps relying on GetContentIndent's existing (already-correct) paren-depth math.
				if (inConditionClause && GetActiveExpandedParenthesisDepth(parenthesisStack) == 0)
				{
					return currentConditionIndent;
				}

				return GetContentIndent(indentLevel, parenthesisStack, inSelectColumnList, selectStatementDepth);
			}

			// Computes the indent for a JOIN clause's first token and records it on the
			// join-frame stack for the current expanded-paren depth. When this JOIN starts while
			// another JOIN at the same depth is still awaiting its ON (nestingDepth > 0), it is a
			// "nested join" folded into the still-open outer JOIN's composite table source: bump
			// the indent one level deeper than the outer JOIN's own nesting, and mark that outer
			// frame so its eventual ON breaks onto its own line instead of staying glued to
			// whichever JOIN keyword happens to precede it. expectsOnClause is false for
			// CROSS JOIN/CROSS APPLY/OUTER APPLY, none of which are ever followed by an ON.
			int BeginJoinClause(bool expectsOnClause)
			{
				var depthKey = GetActiveExpandedParenthesisDepth(parenthesisStack);
				var baseIndent = indentLevel + depthKey;
				if (!joinFramesByDepth.TryGetValue(depthKey, out var frames))
				{
					frames = new Stack<JoinFrame>();
					joinFramesByDepth[depthKey] = frames;
				}

				var nestingDepth = frames.Count;
				if (nestingDepth > 0)
				{
					frames.Peek().HadNestedContent = true;
				}

				var joinLineIndent = nestingDepth > 0 ? baseIndent + nestingDepth + 1 : baseIndent;

				if (expectsOnClause)
				{
					frames.Push(new JoinFrame { Indent = joinLineIndent });
				}

				return joinLineIndent;
			}

			for (var i = 0; i < tokens.Count; i++)
			{
				var token = tokens[i];
				tokenStartOffsets[i] = result.Length;

				if (inInsteadOfClause &&
					token.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment
						or TSqlTokenType.Of or TSqlTokenType.Insert or TSqlTokenType.Update or TSqlTokenType.Delete or TSqlTokenType.Comma))
				{
					// Whatever comes after the DML operation list (AS, WITH APPEND, NOT FOR
					// REPLICATION, ...) ends the clause and renders through its own normal case.
					inInsteadOfClause = false;
				}

				switch (token.TokenType)
				{
					case TSqlTokenType.Create:
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						afterCreateObjectName = false;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Alter:
						var previousAlterIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
						var previousAlterType = previousAlterIndex >= 0 ? tokens[previousAlterIndex].TokenType : TSqlTokenType.None;
						var nextAlterIndex = NextNonWhitespaceIndex(tokens, i + 1);
						inAlterTableStatement = nextAlterIndex < tokens.Count && tokens[nextAlterIndex].TokenType == TSqlTokenType.Table;

						if (previousAlterType == TSqlTokenType.Or)
						{
							// "CREATE OR ALTER ..." - ALTER continues the CREATE line rather than
							// starting a standalone ALTER statement of its own.
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
							result.Append(' ');
							previousWasStatementEnd = false;
							break;
						}

						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Add:
						if (!inAlterTableStatement)
						{
							goto default;
						}

						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Constraint:
						if (!inAlterTableStatement)
						{
							goto default;
						}

						AppendSpaceIfNeeded(result, lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Primary:
						if (!inAlterTableStatement)
						{
							goto default;
						}

						var previousPrimaryIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
						var previousPrimaryType = previousPrimaryIndex >= 0 ? tokens[previousPrimaryIndex].TokenType : TSqlTokenType.None;
						if (previousPrimaryType == TSqlTokenType.Add)
						{
							// "ADD PRIMARY KEY ..." (no constraint name) - PRIMARY stays on ADD's
							// line, the same way CONSTRAINT does when a name is given.
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
							result.Append(' ');
							previousWasStatementEnd = false;
							break;
						}

						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Key:
						if (!inAlterTableStatement)
						{
							goto default;
						}

						AppendSpaceIfNeeded(result, lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Clustered:
					case TSqlTokenType.NonClustered:
						if (!inAlterTableStatement)
						{
							goto default;
						}

						AppendSpaceIfNeeded(result, lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
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
						createObjectRequiresAs = true;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Table:
						var previousTableIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
						var previousTableType = previousTableIndex >= 0 ? tokens[previousTableIndex].TokenType : TSqlTokenType.None;
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						if (previousTableType == TSqlTokenType.Create)
						{
							afterCreateObjectName = true;
							createObjectRequiresAs = false;
						}
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
							// Same level as this CASE's WHEN clauses - see the WHEN case for why this
							// can't be recomputed from GetContentIndent alone.
							var elseIndent = caseIndentStack.Count > 0
								? caseIndentStack.Peek() + 1
								: GetContentIndent(indentLevel, parenthesisStack, inSelectColumnList, selectStatementDepth);
							caseWhenIndent = elseIndent;
							AppendIndentIfNeeded(result, elseIndent, ref lineStart);
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
							if (parenthesisStack.Count > 0 && parenthesisStack.Peek().ParenthesisDepth == CasePhantomParenthesisDepth)
							{
								parenthesisStack.Pop();
							}
							AppendLineIfNeeded(result, ref lineStart);
							// Align with this CASE's own indent (from when it opened), not a fresh
							// recomputation - the two can disagree once currentConditionIndent has
							// moved on to a different clause by the time this END is reached.
							var endIndent = caseIndentStack.Count > 0 ? caseIndentStack.Pop() : GetCaseAwareContentIndent();
							AppendIndentIfNeeded(result, endIndent, ref lineStart);
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

					case TSqlTokenType.Over:
						// "func() OVER (...)" always puts OVER on its own line, at the same
						// indent as the function call it follows, with the window spec's parens
						// forced to expand (see forceExpandParenthesis above) regardless of length.
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, GetContentIndent(indentLevel, parenthesisStack, inSelectColumnList, selectStatementDepth), ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Select:
						AppendLineIfNeeded(result, ref lineStart);
						var previousSelectIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
						var previousSelectType = previousSelectIndex >= 0 ? tokens[previousSelectIndex].TokenType : TSqlTokenType.None;
						var selectIndent = GetSelectIndentForContext(
							indentLevel,
							parenthesisStack,
							previousSelectType == TSqlTokenType.LeftParenthesis && inInClause);
						AppendIndentIfNeeded(result, selectIndent, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						// INSERT INTO tbl SELECT ... (no explicit column list) must not leave
						// pendingInsertColumnList dangling - otherwise the next parenthesis
						// encountered anywhere in the SELECT (e.g. a CAST(...) or ROW_NUMBER()
						// call) gets mistaken for the insert column list.
						pendingInsertColumnList = false;
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
						if (inInsteadOfClause)
						{
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
							previousWasStatementEnd = false;
							break;
						}

						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						pendingInsertColumnList = true;
						pendingValuesList = false;
						pendingUpdateSetClause = false;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Update:
						if (inInsteadOfClause)
						{
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
							previousWasStatementEnd = false;
							break;
						}

						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						pendingUpdateSetClause = true;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Of:
						if (inInsteadOfClause)
						{
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
							previousWasStatementEnd = false;
							break;
						}

						if (lineStart)
						{
							AppendIndent(result, GetColumnListIndent(indentLevel, parenthesisStack, inSelectColumnList, selectStatementDepth, inCreateStatementParams, inInsertColumnList, afterCreateObjectName, inUpdateSetClause, inExecParams));
							lineStart = false;
						}

						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.From:
					case TSqlTokenType.Where:
					case TSqlTokenType.Order:
					case TSqlTokenType.Group:
					case TSqlTokenType.Having:
					case TSqlTokenType.Union:
						if (token.TokenType == TSqlTokenType.Order && parenthesisDepth == overClauseParenDepth)
						{
							// ORDER BY inside an OVER(...) window clause is a sibling of PARTITION BY,
							// not the enclosing statement's ORDER BY, so it must not close the select
							// column list and must indent like any other content inside that paren.
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, GetContentIndent(indentLevel, parenthesisStack, inSelectColumnList, selectStatementDepth), ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
							previousWasStatementEnd = false;
							break;
						}

						inUpdateSetClause = false;
						if (inSelectColumnList && selectStatementDepth > 0)
						{
							inSelectColumnList = false;
							selectStatementDepth--;
						}

						AppendLineIfNeeded(result, ref lineStart);
						var clauseIndent = GetClauseIndentForContext(
							indentLevel,
							parenthesisStack,
							inInClause && parenthesisDepth == inClauseDepth);
						inConditionClause = token.TokenType is TSqlTokenType.Where or TSqlTokenType.Having;
						if (inConditionClause)
						{
							currentConditionIndent = clauseIndent + 1;
						}
						AppendIndentIfNeeded(result, clauseIndent, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.GoTo:
					case TSqlTokenType.Return:
					case TSqlTokenType.Raiserror:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Execute:
					case TSqlTokenType.Exec:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						inExecParams = true;
						execAfterProcName = false;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Label:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						result.Append(token.Text);
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Set:
						AppendLineIfNeeded(result, ref lineStart);
						AppendIndentIfNeeded(result, indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack), ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						inUpdateSetClause = pendingUpdateSetClause;
						pendingUpdateSetClause = false;
						if (inUpdateSetClause)
						{
							result.AppendLine();
							lineStart = true;
						}
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Into:
						var previousIntoIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
						var previousIntoType = previousIntoIndex >= 0 ? tokens[previousIntoIndex].TokenType : TSqlTokenType.None;
						if (previousIntoType == TSqlTokenType.Insert)
						{
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
							pendingInsertColumnList = true;
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
							inSubqueryInClause = false;
							inClauseStartIndex = result.Length;
							inClauseDepth = parenthesisDepth + 1;
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
						// GetActiveExpandedParenthesisDepth accounts for an outer scope already
						// on the stack, e.g. CROSS APPLY (VALUES (...)) - a VALUES table
						// constructor nested inside another expanded paren, as opposed to the
						// plain "INSERT INTO t VALUES (...)" form where the stack is empty here.
						AppendIndentIfNeeded(result, indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack), ref lineStart);
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
							 tokens[nextJoinIndex].TokenType == TSqlTokenType.Inner ||
							 tokens[nextJoinIndex].TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier &&
								 tokens[nextJoinIndex].Text.Equals("APPLY", StringComparison.OrdinalIgnoreCase));
						var joinIndent = indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack);

						if (isJoinModifier)
						{
							var previousJoinIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
							var previousJoinType = previousJoinIndex >= 0 ? tokens[previousJoinIndex].TokenType : TSqlTokenType.None;
							var isCompositeOuterModifier = token.TokenType == TSqlTokenType.Outer &&
								previousJoinType is TSqlTokenType.Left or TSqlTokenType.Right or TSqlTokenType.Full;

							if (isCompositeOuterModifier)
							{
								if (lineStart)
								{
									AppendIndentIfNeeded(result, joinIndent, ref lineStart);
								}
								else
								{
									AppendSpaceIfNeeded(result, lineStart);
								}
							}
							else
							{
								// CROSS is the only modifier here that can never be followed by an
								// ON clause (CROSS JOIN / CROSS APPLY both lack one).
								var crossJoinIndent = BeginJoinClause(expectsOnClause: token.TokenType != TSqlTokenType.Cross);
								AppendLineIfNeeded(result, ref lineStart);
								AppendIndentIfNeeded(result, crossJoinIndent, ref lineStart);
							}

							result.Append(token.Text.ToUpperInvariant());
							result.Append(' ');
							previousWasStatementEnd = false;
							break;
						}

						// Not a join modifier - e.g. LEFT()/RIGHT() called as a function inside an
						// already-open condition (LEFT/RIGHT are reserved words regardless of
						// context, so ScriptDom hands back the same TSqlTokenType.Left/Right
						// either way). lineStart can already be false here (mid-line, following
						// something like "ON "), in which case AppendIndentIfNeeded alone is a
						// no-op - it only ever adds indentation on a fresh line, never a plain
						// separating space - which glued e.g. "ON" directly onto "LEFT(" with no
						// space at all. Only add that space back when the source actually had
						// whitespace here (i.e. StartsOnNewLine made the WhiteSpace case defer to
						// this case instead of adding its own space) - unconditionally adding one
						// would also wrongly separate e.g. "UPPER(LEFT(..." at the open paren,
						// where the source never had whitespace to begin with.
						if (lineStart)
						{
							AppendIndentIfNeeded(result, joinIndent, ref lineStart);
						}
						else if (i > 0 && tokens[i - 1].TokenType == TSqlTokenType.WhiteSpace)
						{
							AppendSpaceIfNeeded(result, lineStart);
						}

						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Join:
						var previousJoinKeywordIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
						var previousJoinKeywordType = previousJoinKeywordIndex >= 0 ? tokens[previousJoinKeywordIndex].TokenType : TSqlTokenType.None;
						var isAfterJoinModifier = previousJoinKeywordType is TSqlTokenType.Inner or TSqlTokenType.Left or TSqlTokenType.Right or TSqlTokenType.Outer or TSqlTokenType.Cross or TSqlTokenType.Full;
						if (isAfterJoinModifier)
						{
							if (lineStart)
							{
								// A comment between the modifier and JOIN forced a line break (a
								// comment always ends its own line) - re-indent to the modifier's
								// own line instead of leaving JOIN glued to column 0 with lineStart
								// stuck true, which would also swallow the space before the next
								// token (AppendSpaceIfNeeded is a no-op at the start of a line).
								AppendIndentIfNeeded(result, indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack), ref lineStart);
							}
							else
							{
								AppendSpaceIfNeeded(result, lineStart);
							}
						}
						else
						{
							// A bare "JOIN" (defaults to INNER) always expects an ON clause.
							var bareJoinIndent = BeginJoinClause(expectsOnClause: true);
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, bareJoinIndent, ref lineStart);
						}
						result.Append(token.Text.ToUpperInvariant());
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.On:
						// Resolve this ON against the most recently opened, still-unresolved JOIN
						// at the current paren depth (T-SQL matches ON to JOIN LIFO, like matching
						// brackets). Outside of an active JOIN chain (e.g. CREATE TABLE ... ON
						// [PRIMARY], a trigger's "ON dbo.Table", SET ... ON) the frame stack for
						// this depth is empty/absent and ON is appended inline exactly as before.
						var onDepthKey = GetActiveExpandedParenthesisDepth(parenthesisStack);
						var hasOpenJoinFrame = joinFramesByDepth.TryGetValue(onDepthKey, out var onFrames) && onFrames.Count > 0;
						if (hasOpenJoinFrame)
						{
							inConditionClause = true;
							var closingJoinFrame = onFrames!.Pop();
							if (closingJoinFrame.HadNestedContent)
							{
								// This ON closes an outer JOIN whose composite table source had
								// another (nested) JOIN folded into it - break it onto its own,
								// less-indented line so it reads as closing the outer JOIN rather
								// than continuing the nested one that was just emitted. Note this is
								// NOT always closingJoinFrame.Indent + 1: when the closing JOIN was
								// itself nested (its own line already deeper than its "logical"
								// slot), this is the formula that actually lands one level below the
								// JOIN it closes - see BeginJoinClause for the matching push-side math.
								var onLineIndent = indentLevel + onDepthKey + onFrames.Count + 1;
								currentConditionIndent = onLineIndent + 1;
								AppendLineIfNeeded(result, ref lineStart);
								AppendIndentIfNeeded(result, onLineIndent, ref lineStart);
							}
							else
							{
								// Inline with its JOIN, so AND/OR continuations of its condition (and
								// any CASE expression among them) belong one level deeper than that
								// JOIN's own line - not indentLevel + 1, which only happens to be
								// right when the JOIN this ON closes wasn't itself nested.
								currentConditionIndent = closingJoinFrame.Indent + 1;
								AppendSpaceIfNeeded(result, lineStart);
							}
						}
						else
						{
							AppendSpaceIfNeeded(result, lineStart);
						}

						result.Append(token.Text.ToUpperInvariant());

						// Neither branch above routes through AppendIndentIfNeeded (the usual
						// place lineStart gets cleared), so when ON is the first thing on a fresh
						// line - a trigger's "ON table", not a JOIN's "ON condition" - lineStart
						// was left stuck true: the WhiteSpace token right after ON then saw
						// "!lineStart" as false and skipped itself entirely (including its own
						// space-insertion logic), and the token after THAT rendered as if it were
						// starting its own fresh line at indent 0 - i.e. nothing - gluing e.g.
						// "ON" directly onto "[dbo]" with no separator at all.
						lineStart = false;

						// ON is a reserved word, so ScriptDom accepts e.g. "ON[dbo].[Table]" or
						// "ON[PRIMARY]" with zero source whitespace (the following token's own
						// delimiters, like a bracket, are enough to lex it as a separate token) -
						// but readability still needs a space there. There's no WhiteSpace token
						// in that case for the normal whitespace-handling logic to turn into one,
						// so it has to be added explicitly right here instead.
						if (i + 1 >= tokens.Count || tokens[i + 1].TokenType != TSqlTokenType.WhiteSpace)
						{
							result.Append(' ');
						}

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
							AppendIndentIfNeeded(result, caseWhenIndent + 1, ref lineStart);
							result.Append(token.Text.ToUpperInvariant());
							result.Append(' ');
							currentLineTokenLength = token.Text.Length + 1;
							previousWasStatementEnd = false;
							break;
						}

						AppendLineIfNeeded(result, ref lineStart);
						// Outside an active WHERE/HAVING/ON (e.g. an IF/WHILE condition, which
						// indents relative to the current block nesting, not a tracked clause),
						// currentConditionIndent doesn't apply - fall back to the previous behavior.
						AppendIndentIfNeeded(result, inConditionClause ? currentConditionIndent : indentLevel + 1, ref lineStart);
						var keywordText = token.TokenType == TSqlTokenType.And && !inCasePredicate ? token.Text : token.Text.ToUpperInvariant();
						result.Append(keywordText);
						result.Append(' ');
						currentLineTokenLength = token.Text.Length + 1;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Plus:
					case TSqlTokenType.Minus:
					case TSqlTokenType.Divide:
					case TSqlTokenType.Star:
						if (token.TokenType == TSqlTokenType.Star && lineStart)
						{
							// A '*' at the start of a line is a SELECT * column, not a multiplication operator.
							var starIndent = GetContentIndent(indentLevel, parenthesisStack, inSelectColumnList, selectStatementDepth);
							AppendIndentIfNeeded(result, starIndent, ref lineStart);
							result.Append(token.Text);
							previousWasStatementEnd = false;
							break;
						}

						if (token.TokenType == TSqlTokenType.Star)
						{
							var previousStarIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
							var previousStarType = previousStarIndex >= 0 ? tokens[previousStarIndex].TokenType : TSqlTokenType.None;
							if (previousStarType is TSqlTokenType.LeftParenthesis or TSqlTokenType.Dot)
							{
								// COUNT(*) / x.* - a wildcard "all columns" marker, not a
								// multiplication operator, so it gets no surrounding spaces.
								result.Append(token.Text);
								previousWasStatementEnd = false;
								break;
							}
						}

						var previousOperatorIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
						var previousOperatorType = previousOperatorIndex >= 0 ? tokens[previousOperatorIndex].TokenType : TSqlTokenType.None;
						var isUnaryMinusAfterParen = token.TokenType == TSqlTokenType.Minus &&
							previousOperatorType == TSqlTokenType.LeftParenthesis;
						// A unary minus (negative literal) never gets the binary operator's
						// trailing space either - e.g. DATEADD(DAY, -30, GETDATE()) must render as
						// "-30", not "- 30", and "x >= -1" must not become "x >= - 1". Scoped to
						// unambiguous contexts where a minus can only be unary - right after '(',
						// ',', or another operator token (=, >, < - also how >=, <=, <> tokenize,
						// each ending in one of these) - to avoid misreading an actual subtraction
						// like "a - 1" as unary.
						var isUnaryMinus = token.TokenType == TSqlTokenType.Minus &&
							previousOperatorType is TSqlTokenType.LeftParenthesis or TSqlTokenType.Comma
								or TSqlTokenType.EqualsSign or TSqlTokenType.GreaterThan or TSqlTokenType.LessThan;
						if (!isUnaryMinusAfterParen)
						{
							// A unary minus immediately after '(' - e.g. CAST(-1.00 * ...) - must
							// hug the paren rather than gaining the binary operator's usual
							// leading space.
							AppendSpaceIfNeeded(result, lineStart);
						}
						result.Append(token.Text);
						var nextOperatorIndex = NextNonWhitespaceIndex(tokens, i + 1);
						var shouldBreakAfterOperator = false;
						if (nextOperatorIndex < tokens.Count)
						{
							if (parenthesisDepth > 0 && HasParenthesisScope(parenthesisStack, parenthesisDepth) && tokens[nextOperatorIndex].TokenType == TSqlTokenType.LeftParenthesis)
							{
								shouldBreakAfterOperator = true;
							}
							else
							{
								var currentLineLength = GetCurrentLineText(result).TrimStart('\t', ' ').Length;
								if (currentLineLength > LongExpressionLineBreakThreshold)
								{
									shouldBreakAfterOperator = true;
								}
								else if (token.TokenType == TSqlTokenType.Plus && tokens[nextOperatorIndex].TokenType == TSqlTokenType.AsciiStringOrQuotedIdentifier && currentLineLength >= LongExpressionLineBreakThreshold)
								{
									shouldBreakAfterOperator = true;
								}
							}
						}

						if (shouldBreakAfterOperator)
						{
							TrimTrailingSpaces(result);
							result.AppendLine();
							lineStart = true;
							var operatorIndent = GetOperatorContinuationIndent(indentLevel, parenthesisStack, inSelectColumnList, selectStatementDepth, inInClause);
							AppendIndentIfNeeded(result, operatorIndent, ref lineStart);
							if (token.TokenType == TSqlTokenType.Plus)
							{
								TrimTrailingSpaces(result);
							}
							currentLineTokenLength = 0;
						}
						else if (isUnaryMinus)
						{
							currentLineTokenLength += token.Text.Length;
						}
						else
						{
							result.Append(' ');
							currentLineTokenLength += token.Text.Length + 1;
						}
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Dot:
						result.Append(token.Text);
						currentLineTokenLength += token.Text.Length;
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Comma:
						if (inInsteadOfClause)
						{
							result.Append(',');
							result.Append(' ');
							previousWasStatementEnd = false;
							break;
						}

						if (lineStart)
						{
							AppendIndent(result, GetColumnListIndent(indentLevel, parenthesisStack, inSelectColumnList, selectStatementDepth, inCreateStatementParams, inInsertColumnList, afterCreateObjectName, inUpdateSetClause, inExecParams));
							lineStart = false;
						}
						result.Append(token.Text);
						currentLineTokenLength += token.Text.Length;
						if (inDeclareStatement && parenthesisDepth == 0)
						{
							result.AppendLine();
							lineStart = true;
							currentLineTokenLength = 0;
							pendingDeclareVariableContinuation = true;
						}
						else if (inUpdateSetClause && parenthesisDepth == 0)
						{
							result.AppendLine();
							lineStart = true;
							currentLineTokenLength = 0;
						}
						else if (inExecParams && parenthesisDepth == 0)
						{
							result.AppendLine();
							lineStart = true;
							currentLineTokenLength = 0;
						}
						else if (inAlterTablePrimaryKeyList && parenthesisDepth == alterTablePrimaryKeyListDepth)
						{
							result.AppendLine();
							lineStart = true;
							currentLineTokenLength = 0;
						}
						else if ((inInsertColumnList && parenthesisDepth == insertColumnListDepth) || (inValuesList && parenthesisDepth == valuesListDepth) || (inCreateStatementParams && parenthesisDepth == (createObjectParameterListDepth < 0 ? 0 : createObjectParameterListDepth)) || (inSelectColumnList && selectStatementDepth > 0 && (parenthesisDepth == 0 || (applyParenthesisDepth > 0 && parenthesisDepth == applyParenthesisDepth))))
						{
							result.AppendLine();
							lineStart = true;
							currentLineTokenLength = 0;
						}
						else if (parenthesisDepth > 0 && HasParenthesisScope(parenthesisStack, parenthesisDepth) && inValuesList)
						{
							result.AppendLine();
							lineStart = true;
							currentLineTokenLength = 0;
						}
						else if (parenthesisDepth > 0 && HasParenthesisScope(parenthesisStack, parenthesisDepth) && !inInClause && !inDeclareStatement)
						{
							var currentLine = GetCurrentLineText(result).Trim();
							var nextArgumentLength = GetNextTopLevelArgumentLength(tokens, i + 1, parenthesisDepth);
							if (!IsOnlyClosingParenthesesLine(currentLine) && nextArgumentLength > 0 && currentLine.Length + 1 + nextArgumentLength <= LongExpressionLineBreakThreshold)
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
						var previousText = previousIndex >= 0 ? tokens[previousIndex].Text : string.Empty;

						if (previousTokenType == TSqlTokenType.Over)
						{
							overClauseParenDepth = parenthesisDepth;
						}

						if (inAlterTableStatement && previousTokenType is TSqlTokenType.Clustered or TSqlTokenType.NonClustered)
						{
							inAlterTablePrimaryKeyList = true;
							alterTablePrimaryKeyListDepth = parenthesisDepth;
							alterTablePrimaryKeyListMultiColumn = HasTopLevelCommaBeforeMatchingParenthesis(tokens, i);

							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text);

							if (alterTablePrimaryKeyListMultiColumn)
							{
								result.AppendLine();
								lineStart = true;
								parenthesisStack.Push(new ParenthesisScope(parenthesisDepth));
							}

							previousWasStatementEnd = false;
							break;
						}

						if (afterCreateObjectName)
						{
							inCreateStatementParams = true;
							inCreateObjectParameterList = true;
							createObjectParameterListDepth = parenthesisDepth;
							if (openingParenOnNewLine && !lineStart)
							{
								result.AppendLine();
								lineStart = true;
							}
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
							if (openingParenOnNewLine && !lineStart)
							{
								result.AppendLine();
								lineStart = true;
							}
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
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
							if (!lineStart)
							{
								result.AppendLine();
								lineStart = true;
							}
							// Same outer-scope accounting as the VALUES keyword itself, so this
							// opening paren lines up under it rather than at column 0.
							AppendIndentIfNeeded(result, indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack), ref lineStart);
							result.Append(token.Text);
							result.AppendLine();
							lineStart = true;
							parenthesisStack.Push(new ParenthesisScope(parenthesisDepth));
							previousWasStatementEnd = false;
							break;
						}

						if (inInClause && previousTokenType == TSqlTokenType.In)
						{
							var nextInElementIndex = NextNonWhitespaceIndex(tokens, i + 1);
							inSubqueryInClause = nextInElementIndex < tokens.Count && tokens[nextInElementIndex].TokenType == TSqlTokenType.Select;
							if (inSubqueryInClause)
							{
								if (!lineStart)
								{
									result.AppendLine();
									lineStart = true;
								}
								AppendIndentIfNeeded(result, indentLevel + 1 + GetActiveExpandedParenthesisDepth(parenthesisStack), ref lineStart);
								result.Append(token.Text);
								result.AppendLine();
								lineStart = true;
								previousWasStatementEnd = false;
								break;
							}
						}

						if (previousTokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier && previousText.Equals("APPLY", StringComparison.OrdinalIgnoreCase))
						{
							applyParenthesisDepth = parenthesisDepth;
							parenthesisStack.Push(new ParenthesisScope(parenthesisDepth));
							if (!lineStart)
							{
								result.AppendLine();
								lineStart = true;
							}
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text);
							result.AppendLine();
							lineStart = true;
							previousWasStatementEnd = false;
							break;
						}

						var forceExpandParenthesis = previousTokenType == TSqlTokenType.Exists || previousTokenType == TSqlTokenType.Over || (inValuesList && previousTokenType == TSqlTokenType.Identifier && previousText.Equals("CONCAT", StringComparison.OrdinalIgnoreCase));
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
							else if (inValuesList && previousTokenType == TSqlTokenType.Identifier && previousText.Equals("CONCAT", StringComparison.OrdinalIgnoreCase))
							{
								result.AppendLine();
								lineStart = true;
								var expandedIndent = indentLevel + Math.Max(0, GetActiveExpandedParenthesisDepth(parenthesisStack) - 1);
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
						if (parenthesisDepth == overClauseParenDepth)
						{
							overClauseParenDepth = -1;
						}

						if (inAlterTablePrimaryKeyList && parenthesisDepth == alterTablePrimaryKeyListDepth)
						{
							parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
							if (alterTablePrimaryKeyListMultiColumn)
							{
								result.AppendLine();
								AppendIndent(result, indentLevel);
								result.Append(token.Text);
								lineStart = false;
								PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
							}
							else
							{
								result.Append(token.Text);
							}

							inAlterTablePrimaryKeyList = false;
							alterTablePrimaryKeyListDepth = -1;
							alterTablePrimaryKeyListMultiColumn = false;
							previousWasStatementEnd = false;
							break;
						}

						if (inInClause && parenthesisDepth == inClauseDepth)
						{
							var inClauseLength = result.Length - inClauseStartIndex;
							// inSubqueryInClause must be excluded here - ShouldFormatInClauseMultiline/
							// FormatInClauseMultiline below are built only for a comma-separated
							// value list (IN (1, 2, 3)) and don't know anything about
							// "IN (SELECT ...)"; the dedicated subquery handling a few lines down
							// (if (inSubqueryInClause)) was unreachable whenever this block's own
							// length-based check happened to also fire first, so a long/multi-line
							// subquery got its WHERE/FROM clauses and any embedded comments run
							// through the value-list splitter - which inserts a trailing comma
							// after every "value" it finds, fabricating commas like
							// "DB1..TableB c (NOLOCK)," and "WHERE," out of nowhere.
							if (inClauseLength > 0 && !inSubqueryInClause)
							{
								var inClauseContent = result.ToString(inClauseStartIndex, inClauseLength);
								if (ShouldFormatInClauseMultiline(inClauseContent, indentLevel))
								{
									result.Length = inClauseStartIndex;
									FormatInClauseMultiline(result, inClauseContent, indentLevel);
									// FormatInClauseMultiline always ends by appending ")" with no
									// trailing newline, but truncating result back to
									// inClauseStartIndex doesn't touch this loop's own lineStart
									// variable - if the last thing inside the IN clause was a
									// comment (which always ends its own line), lineStart was left
									// true from processing that comment, before any of this ran.
									// Every token handler after this one trusts lineStart at face
									// value, so that stale true - result no longer actually ending
									// in a newline - made each of them indent on top of the same
									// line instead of starting a new one, stacking up extra tabs
									// between this ")" and whatever followed (e.g. an OR).
									lineStart = false;
									inInClause = false;
									inClauseStartIndex = -1;
									inClauseDepth = -1;
									parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
									PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
									previousWasStatementEnd = false;
									break;
								}
							}

							inInClause = false;
							inClauseStartIndex = -1;
							inClauseDepth = -1;
							if (inSubqueryInClause)
							{
								inSubqueryInClause = false;
								parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
								if (!lineStart)
								{
									result.AppendLine();
									lineStart = true;
								}
								AppendIndent(result, indentLevel + 1);
								result.Append(token.Text);
								result.AppendLine();
								lineStart = true;
								PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
								previousWasStatementEnd = false;
								break;
							}
							result.Append(token.Text);
							parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
							previousWasStatementEnd = false;
							break;
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
							PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
							result.AppendLine();
							// Popped above (before computing indent) so this closing paren lines
							// up with VALUES's own line - i.e. only the still-active outer scope,
							// like CROSS APPLY's own paren, counts here, not the tuple's own
							// now-closed scope.
							AppendIndent(result, indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack));
							result.Append(token.Text);
							inValuesList = false;
							valuesListDepth = -1;
							previousWasStatementEnd = false;
							break;
						}

						if (applyParenthesisDepth == parenthesisDepth)
						{
							parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
							if (!lineStart)
							{
								result.AppendLine();
								lineStart = true;
							}
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append(token.Text);
							PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
							applyParenthesisDepth = -1;
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
							inCreateObjectParameterList = false;
							createObjectParameterListDepth = -1;
							if (createObjectRequiresAs)
							{
								result.AppendLine();
								lineStart = true;
							}
							else
							{
								// TABLE has no trailing AS keyword, so ')' just stays inline (e.g. for a following ';').
								inCreateStatementParams = false;
							}
							PopParenthesisScope(parenthesisStack, parenthesisDepth + 1);
							previousWasStatementEnd = false;
							break;
						}

						if (shouldExpandClosingParenthesis)
						{
							result.AppendLine();
							var nextClosingIndex = NextNonWhitespaceIndex(tokens, i + 1);
							var nextClosingTokenType = nextClosingIndex < tokens.Count ? tokens[nextClosingIndex].TokenType : (TSqlTokenType?)null;
							var closingIndent = GetClosingParenIndentForContext(
								indentLevel,
								parenthesisStack,
								inValuesList,
								parenthesisDepth,
								valuesListDepth,
								nextClosingTokenType);
							AppendIndent(result, closingIndent);
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
						if (PrecedingRealTokenIsComment(tokens, i))
						{
							// Gluing onto the preceding content (below) would land the semicolon on
							// a "--" comment's own line, inside the comment's extent - silently
							// commenting the terminator itself out. Start a fresh line instead.
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
						}
						else
						{
							// A semicolon must always attach directly to the preceding content, even
							// if an earlier token's own line-break logic (e.g. a closing paren ending
							// an IN clause/subquery) already left the cursor at the start of a fresh
							// line.
							TrimTrailingLineEndings(result);
						}

						result.Append(token.Text);
						result.AppendLine();
						lineStart = true;
						previousWasStatementEnd = false;
						inSelectColumnList = false;
						selectStatementDepth = 0;
						inDeclareStatement = false;
						pendingDeclareVariableContinuation = false;
						inUpdateSetClause = false;
						pendingUpdateSetClause = false;
						inExecParams = false;
						execAfterProcName = false;
						inAlterTableStatement = false;
						inAlterTablePrimaryKeyList = false;
						alterTablePrimaryKeyListDepth = -1;
						alterTablePrimaryKeyListMultiColumn = false;
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
						inUpdateSetClause = false;
						pendingUpdateSetClause = false;
						inExecParams = false;
						execAfterProcName = false;
						inAlterTableStatement = false;
						inAlterTablePrimaryKeyList = false;
						alterTablePrimaryKeyListDepth = -1;
						alterTablePrimaryKeyListMultiColumn = false;
						break;

					case TSqlTokenType.WhiteSpace:
						if (inInsteadOfClause)
						{
							// Every member of this clause (INSTEAD/OF/INSERT/UPDATE/DELETE/comma)
							// already adds exactly the separator it needs - none of the newline-
							// or space-insertion logic below knows about this clause, so letting it
							// run here would reintroduce the very line breaks (or, once combined
							// with those members' own spacing, doubled-up spaces) this exists to
							// remove regardless of how the source SQL happened to space things.
							break;
						}

						if (token.Text.Contains('\n') || token.Text.Contains('\r'))
						{
							if (afterCreateObjectName && !lineStart)
							{
								var nextCreateNewlineIndex = NextNonWhitespaceIndex(tokens, i + 1);
								var nextCreateNewlineType = nextCreateNewlineIndex < tokens.Count ? tokens[nextCreateNewlineIndex].TokenType : TSqlTokenType.None;
								if (nextCreateNewlineType != TSqlTokenType.LeftParenthesis || openingParenOnNewLine)
								{
									result.AppendLine();
									lineStart = true;
								}
							}

							if (previousWasStatementEnd && !lineStart)
							{
								TrimTrailingSpaces(result);
								result.AppendLine();
								lineStart = true;
								previousWasStatementEnd = false;
							}
						}

						if (!lineStart)
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
								var nextCreateType = nextIndex < tokens.Count ? tokens[nextIndex].TokenType : TSqlTokenType.None;
								// Three independent reasons to glue onto the same line: right after
								// the CREATE TABLE/PROC/etc keyword itself (always, unrelated to
								// paren placement - that's just "CREATE TABLE" staying on one line),
								// right before the parameter/column list's opening paren (only when
								// the same-line option is in effect), or right after a trigger's own
								// "ON" (a CREATE TRIGGER's target table, not a JOIN's ON - JOIN's ON
								// carries an open join frame and never reaches this branch, since
								// afterCreateObjectName is a CREATE-statement-only flag).
								var glueAfterCreateKeyword = previousCreateType is TSqlTokenType.Proc or TSqlTokenType.Procedure or TSqlTokenType.Function or TSqlTokenType.View or TSqlTokenType.Trigger or TSqlTokenType.Table or TSqlTokenType.On;
								var glueBeforeParameterList = !openingParenOnNewLine && nextCreateType == TSqlTokenType.LeftParenthesis;
								if (glueAfterCreateKeyword || glueBeforeParameterList)
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

							var previousSpacingIndex = PreviousNonWhitespaceIndex(tokens, i - 1);
							var previousSpacingType = previousSpacingIndex >= 0 ? tokens[previousSpacingIndex].TokenType : TSqlTokenType.None;
							var isBuiltInFunctionOpenParen = nextType == TSqlTokenType.LeftParenthesis &&
								IsBuiltInFunctionCall(tokens, previousSpacingIndex);
							var isAfterBuiltInFunctionOpenParen = result.Length > 0 && result[^1] == '(' &&
								previousSpacingType == TSqlTokenType.LeftParenthesis &&
								IsBuiltInFunctionCall(tokens, PreviousNonWhitespaceIndex(tokens, previousSpacingIndex - 1));
							if (result.Length > 0 && result[^1] != ' ' && result[^1] != '\t' &&
								nextType is not TSqlTokenType.Comma and not TSqlTokenType.RightParenthesis and not TSqlTokenType.Semicolon &&
								!isBuiltInFunctionOpenParen && !isAfterBuiltInFunctionOpenParen)
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
						if (!lineStart)
						{
							AppendLineIfNeeded(result, ref lineStart);
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
						if (token.Text.EndsWith("\n", StringComparison.Ordinal) || token.Text.EndsWith("\r", StringComparison.Ordinal))
						{
							lineStart = true;
						}
						else
						{
							result.AppendLine();
							lineStart = true;
						}
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Case:
						caseExpressionDepth++;
						AppendLineIfNeeded(result, ref lineStart);
						var caseIndent = GetCaseAwareContentIndent();
						caseIndentStack.Push(caseIndent);
						AppendIndentIfNeeded(result, caseIndent, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						parenthesisStack.Push(new ParenthesisScope(CasePhantomParenthesisDepth));
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.When:
						AppendLineIfNeeded(result, ref lineStart);
						// One level deeper than this WHEN's own CASE, wherever that CASE actually
						// landed (GetContentIndent alone can't know that - see GetCaseAwareContentIndent).
						var whenIndent = caseIndentStack.Count > 0
							? caseIndentStack.Peek() + 1
							: GetContentIndent(indentLevel, parenthesisStack, inSelectColumnList, selectStatementDepth);
						caseWhenIndent = whenIndent;
						AppendIndentIfNeeded(result, whenIndent, ref lineStart);
						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Then:
						// THEN normally continues on the same line as its WHEN condition
						// (AppendSpaceIfNeeded is a no-op once already mid-line), but a long or
						// multi-line WHEN condition - or a comment right before THEN, which
						// always ends with a newline - can leave lineStart true here. Unlike
						// AppendSpaceIfNeeded, this branch actually needs to indent AND clear
						// lineStart when that happens - otherwise THEN lands at column 0 with no
						// indent, and the *next* token, still seeing lineStart true, wrongly
						// indents itself instead. caseWhenIndent (set when this THEN's own WHEN
						// was processed) aligns THEN under its WHEN, the same as ELSE/END already
						// do elsewhere in this switch.
						if (lineStart)
						{
							AppendIndentIfNeeded(result, caseWhenIndent, ref lineStart);
						}
						else
						{
							AppendSpaceIfNeeded(result, lineStart);
						}

						result.Append(token.Text.ToUpperInvariant());
						result.Append(' ');
						previousWasStatementEnd = false;
						break;

					case TSqlTokenType.Variable:
					case TSqlTokenType.Identifier:
					case TSqlTokenType.QuotedIdentifier:
						if (pendingDeclareVariableContinuation && lineStart)
						{
							var continuationIndent = GetDeclarationContinuationIndent(token.Text.StartsWith("@", StringComparison.Ordinal));
							if (continuationIndent > 0)
							{
								result.Append(new string(' ', continuationIndent));
								lineStart = false;
							}
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

						if (IsInsteadOfTriggerClauseStart(tokens, i))
						{
							AppendLineIfNeeded(result, ref lineStart);
							AppendIndentIfNeeded(result, indentLevel, ref lineStart);
							result.Append("INSTEAD");
							inInsteadOfClause = true;
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

						if (inExecParams && !execAfterProcName && !token.Text.StartsWith("@", StringComparison.Ordinal))
						{
							// First identifier (or schema-qualified continuation) of the EXEC target
							// proc name - stays on the EXEC line; the next '@' token is the first
							// parameter and must start its own line.
							execAfterProcName = true;

							var nextProcNameIndex = NextNonWhitespaceIndex(tokens, i + 1);
							var isSchemaQualifiedContinuation = nextProcNameIndex < tokens.Count && tokens[nextProcNameIndex].TokenType == TSqlTokenType.Dot;
							if (!isSchemaQualifiedContinuation)
							{
								// This is the final token of the proc name (not "schema" in
								// "schema.proc"), so a bare literal parameter immediately
								// following it (no leading '@') still needs a space, since the
								// only other place that adds one is the '@'-prefixed branch below.
								result.Append(token.Text);
								result.Append(' ');
								previousWasStatementEnd = false;
								break;
							}
						}

						if (token.Text.StartsWith("@", StringComparison.Ordinal))
						{
							if (afterCreateObjectName)
							{
								AppendLineIfNeeded(result, ref lineStart);
								afterCreateObjectName = false;
								inCreateStatementParams = true;
							}

							if (execAfterProcName)
							{
								AppendLineIfNeeded(result, ref lineStart);
								execAfterProcName = false;
							}

							if (lineStart)
							{
								// The flat "+1" baseline stands in for a parameter list that
								// manages its own indent without ever pushing a ParenthesisScope
								// (EXEC params, CREATE-statement params). inCreateStatementParams
								// and inValuesList both DO already represent "one level of list
								// nesting" on their own - inValuesList in particular pushes its
								// own scope for the VALUES tuple - so adding the active expanded
								// parenthesis depth on top of the flat baseline for those double-
								// counts that same level and over-indents relative to every other
								// (non-'@') token in the same list.
								var parameterIndent = indentLevel + 1;
								if (!inCreateStatementParams && !inValuesList)
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
							if ((inCreateStatementParams || inInsertColumnList) && !afterCreateObjectName)
							{
								extraIndent = 1;
								suppressParenIndent = true;
							}
							if (!suppressParenIndent)
							{
								extraIndent += GetActiveExpandedParenthesisDepth(parenthesisStack);
							}
							if (inUpdateSetClause)
							{
								extraIndent++;
							}
							AppendIndent(result, indentLevel + extraIndent);
							lineStart = false;
						}
						result.Append(IsBuiltInFunctionCall(tokens, i) ? token.Text.ToUpperInvariant() : token.Text);
						previousWasStatementEnd = false;
						break;

					default:
						// DELETE has no dedicated case (it's the only other DML keyword this
						// clause can list, via IsKeyword below) - glue it inline like Insert/Update
						// do rather than letting it take the normal indent-driven path.
						if (inInsteadOfClause)
						{
							AppendSpaceIfNeeded(result, lineStart);
							result.Append(token.Text.ToUpperInvariant());
							previousWasStatementEnd = false;
							break;
						}

						if (lineStart)
						{
							AppendIndent(result, GetColumnListIndent(indentLevel, parenthesisStack, inSelectColumnList, selectStatementDepth, inCreateStatementParams, inInsertColumnList, afterCreateObjectName, inUpdateSetClause, inExecParams));
							lineStart = false;
						}

						result.Append(IsKeyword(token.TokenType)
							? token.Text.ToUpperInvariant()
							: token.Text);
						previousWasStatementEnd = false;
						break;
				}

				if (statementEndIndices.Contains(i))
				{
					// ScriptDom's own AST can attribute a trailing "--" comment to a statement as
					// its LastTokenIndex (e.g. a run of comment lines right before a batch's
					// natural end) rather than pointing at the statement's actual last real
					// content. token IS that comment here - its own case above already appended
					// its text and forced a fresh line after itself, so gluing (via
					// TrimTrailingLineEndings) would put the injected terminator back on the
					// comment's own line, inside its extent.
					if (token.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
					{
						AppendIndentIfNeeded(result, indentLevel, ref lineStart);
					}
					else
					{
						TrimTrailingLineEndings(result);
					}

					if (token.TokenType != TSqlTokenType.Semicolon)
					{
						result.Append(';');
					}

					result.AppendLine();
					result.AppendLine();
					lineStart = true;
					previousWasStatementEnd = false;
					inSelectColumnList = false;
					selectStatementDepth = 0;
					inDeclareStatement = false;
					pendingDeclareVariableContinuation = false;
					inUpdateSetClause = false;
					pendingUpdateSetClause = false;
					inExecParams = false;
					execAfterProcName = false;
					inAlterTableStatement = false;
					inAlterTablePrimaryKeyList = false;
					alterTablePrimaryKeyListDepth = -1;
					alterTablePrimaryKeyListMultiColumn = false;
					inConditionClause = false;
					currentConditionIndent = indentLevel + 1;
				}
			}

			var rawOutput = result.ToString();
			var (formattedSql, offsetMap) = TrimTrailingWhitespaceTrackingOffsets(rawOutput, BuildProtectedTokenMask(rawOutput));

			var normalizedToOriginalOffset = BuildNormalizedToOriginalOffsetMap(sql, sqlToFormat);
			var tokenPositions = new List<SqlTokenPosition>(tokens.Count);
			for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
			{
				var positionToken = tokens[tokenIndex];
				// The final token in the stream is an EndOfFile sentinel with a null Text - has no
				// content to measure the length of.
				var positionTokenLength = positionToken.Text?.Length ?? 0;
				var sourceStart = normalizedToOriginalOffset[positionToken.Offset];
				var sourceEnd = normalizedToOriginalOffset[positionToken.Offset + positionTokenLength];
				var formattedStart = offsetMap[tokenStartOffsets[tokenIndex]];
				tokenPositions.Add(new SqlTokenPosition(positionToken.TokenType, sourceStart, sourceEnd - sourceStart, formattedStart));
			}

			return new SqlFormatResult(formattedSql, tokenPositions);
		}
		catch
		{
			var fallbackText = ShouldUseExpressionFallback(sqlToFormat, TryTokenize(sqlToFormat))
				? FormatExpressionFallback(sqlToFormat, LongExpressionLineBreakThreshold)
				: sqlToFormat;
			return new SqlFormatResult(fallbackText, null);
		}
	}

	// Lexer-only tokenization - cheap, and far less likely to throw than a full Parse, but still
	// guarded since callers include the round-trip safety check (which must never throw) and the
	// catch-block fallback (which already runs from inside a catch block).
	private static IList<TSqlParserToken>? TryTokenize(string sql)
	{
		try
		{
			var parser = new TSql160Parser(false);
			using var reader = new StringReader(sql);
			return parser.GetTokenStream(reader, out _);
		}
		catch
		{
			return null;
		}
	}

	// The formatter has, on separate occasions, both silently swallowed real SQL into a dead
	// comment and glued two adjacent tokens into one - each time because some rendering path got
	// clever with text instead of trusting the token stream. This is the backstop: re-tokenize
	// both sides and verify the formatted output contains the same real tokens, in the same
	// order, as the input - so a bug like either of those produces a safe no-op instead of
	// silently handing back corrupted SQL. Two adjustments account for changes the formatter
	// makes on purpose: keyword/built-in-function casing (IsKeyword/IsBuiltInFunctionCall
	// uppercase them) is compared case-insensitively, and an extra Semicolon in the formatted
	// stream with no counterpart in the input is tolerated (the formatter adds a missing
	// statement terminator - see the statementEndIndices handling above).
	internal static bool IsRoundTripSafe(string originalSql, string formattedSql)
	{
		return !TryFindRoundTripMismatch(originalSql, formattedSql, out _, out _);
	}

	// Same check as IsRoundTripSafe, but also reports where in each text the divergence starts -
	// used to show just the relevant few lines of "before/after" instead of a whole (possibly
	// huge) object's entire script. Returns true when a mismatch was found (unsafe); the two
	// offsets are only meaningful in that case.
	internal static bool TryFindRoundTripMismatch(string originalSql, string formattedSql, out int originalMismatchOffset, out int formattedMismatchOffset)
	{
		var originalTokens = TryTokenize(originalSql);
		var formattedTokens = TryTokenize(formattedSql);

		if (originalTokens is null || formattedTokens is null)
		{
			originalMismatchOffset = 0;
			formattedMismatchOffset = 0;
			return true;
		}

		return !SignificantTokenSequencesMatch(originalTokens, formattedTokens, out originalMismatchOffset, out formattedMismatchOffset);
	}

	private static bool SignificantTokenSequencesMatch(IList<TSqlParserToken> originalTokens, IList<TSqlParserToken> formattedTokens, out int originalMismatchOffset, out int formattedMismatchOffset)
	{
		var original = SignificantTokens(originalTokens);
		var formatted = SignificantTokens(formattedTokens);

		var i = 0;
		var j = 0;
		while (i < original.Count && j < formatted.Count)
		{
			if (TokensMatch(original[i], formatted[j]))
			{
				i++;
				j++;
				continue;
			}

			if (formatted[j].TokenType == TSqlTokenType.Semicolon)
			{
				j++;
				continue;
			}

			// The IN-clause formatter deliberately repositions a comma relative to an immediately
			// adjacent comment (leading-comma input becomes trailing-comma output, so the comment
			// and the comma it sat next to swap places) - that changes textual order without
			// changing meaning, so tolerate exactly this one local transposition rather than
			// treating it as dropped or corrupted content.
			if (i + 1 < original.Count && j + 1 < formatted.Count &&
				IsReorderableConnector(original[i].TokenType) && IsReorderableConnector(formatted[j].TokenType) &&
				TokensMatch(original[i], formatted[j + 1]) && TokensMatch(formatted[j], original[i + 1]))
			{
				i += 2;
				j += 2;
				continue;
			}

			originalMismatchOffset = original[i].Offset;
			formattedMismatchOffset = formatted[j].Offset;
			return false;
		}

		while (j < formatted.Count && formatted[j].TokenType == TSqlTokenType.Semicolon)
		{
			j++;
		}

		if (i == original.Count && j == formatted.Count)
		{
			originalMismatchOffset = 0;
			formattedMismatchOffset = 0;
			return true;
		}

		// One side ran out of tokens before the other (extra or missing content at the end) -
		// point at wherever the shorter side stopped, since that's where the divergence starts.
		originalMismatchOffset = i < original.Count ? original[i].Offset : EndOffset(original);
		formattedMismatchOffset = j < formatted.Count ? formatted[j].Offset : EndOffset(formatted);
		return false;

		static int EndOffset(List<TSqlParserToken> tokens) =>
			tokens.Count == 0 ? 0 : tokens[^1].Offset + (tokens[^1].Text?.Length ?? 0);
	}

	// A handful of lines of context around a mismatch offset, not the whole (possibly huge)
	// object script - the offset always falls in the middle of the window unless it's near
	// either end of the text. Leaves "..." markers when the snippet doesn't cover the full text,
	// so it's visually obvious this is a partial extract.
	internal static string ExtractContextSnippet(string text, int offset, int contextLines)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text ?? "";
		}

		var clampedOffset = Math.Clamp(offset, 0, text.Length);
		var lines = text.Split('\n');

		var lineIndex = 0;
		var runningLength = 0;
		for (var i = 0; i < lines.Length; i++)
		{
			runningLength += lines[i].Length + 1;
			if (clampedOffset < runningLength)
			{
				lineIndex = i;
				break;
			}

			lineIndex = i;
		}

		var start = Math.Max(0, lineIndex - contextLines);
		var end = Math.Min(lines.Length - 1, lineIndex + contextLines);
		var snippet = string.Join('\n', lines[start..(end + 1)]);

		if (start > 0)
		{
			snippet = "...\n" + snippet;
		}

		if (end < lines.Length - 1)
		{
			snippet += "\n...";
		}

		return snippet;
	}

	private static bool IsReorderableConnector(TSqlTokenType tokenType)
	{
		return tokenType is TSqlTokenType.Comma or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;
	}

	private static List<TSqlParserToken> SignificantTokens(IList<TSqlParserToken> tokens)
	{
		var result = new List<TSqlParserToken>(tokens.Count);
		foreach (var token in tokens)
		{
			if (token.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile))
			{
				result.Add(token);
			}
		}

		return result;
	}

	private static bool TokensMatch(TSqlParserToken original, TSqlParserToken formatted)
	{
		if (original.TokenType != formatted.TokenType)
		{
			return false;
		}

		var comparison = RequiresCaseSensitiveRoundTripComparison(original)
			? StringComparison.Ordinal
			: StringComparison.OrdinalIgnoreCase;

		var originalText = NormalizeTokenTextForComparison(original.TokenType, original.Text);
		var formattedText = NormalizeTokenTextForComparison(formatted.TokenType, formatted.Text);
		return string.Equals(originalText, formattedText, comparison);
	}

	// TrimTrailingWhitespaceTrackingOffsets is a blind pass over the *whole* output with no token
	// awareness: it normalizes every line ending to Environment.NewLine (\r\n on Windows) and
	// strips trailing whitespace from every line. Whitespace *between* tokens never sees this
	// comparison at all (SignificantTokens excludes it), but a multi-line comment's own line
	// breaks and any trailing spaces on its lines ARE part of its token text - so a comment
	// authored with plain \n internally, or with trailing spaces on some lines, used to look like
	// "changed content" here even though nothing about what the SQL means actually changed.
	// Deliberately NOT extended to string literals: trailing whitespace inside a multi-line string
	// constant is part of its actual value, and silently trimming that would be a real correctness
	// bug in the formatter, not a cosmetic one - this check must keep catching that case.
	private static string NormalizeTokenTextForComparison(TSqlTokenType tokenType, string text)
	{
		var normalized = NormalizeLineEndingsForComparison(text);

		if (tokenType is not (TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
		{
			return normalized;
		}

		var lines = normalized.Split('\n');
		for (var i = 0; i < lines.Length; i++)
		{
			lines[i] = lines[i].TrimEnd();
		}

		return string.Join('\n', lines);
	}

	private static string NormalizeLineEndingsForComparison(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

	// Words T-SQL treats as keywords in specific contexts but that ScriptDom still lexes as a
	// plain Identifier, with no TSqlTokenType of their own - see IsTryCatchFinallyToken and
	// IsInsteadOfTriggerClauseStart. The renderer intentionally uppercases these, so - like a
	// recognized built-in function name - a case difference here is expected, not corruption.
	private static readonly HashSet<string> ContextualKeywordIdentifiers = new(StringComparer.OrdinalIgnoreCase)
	{
		"TRY", "CATCH", "FINALLY", "APPLY", "INSTEAD"
	};

	// Default to case-sensitive (the stricter, safer choice) and only relax it for token types
	// the renderer is actually known to re-case on purpose: keywords (no explicit list needed -
	// every reserved word has its own TSqlTokenType, so "not one of the types below" already
	// means "keyword, punctuation, or something else with no letters to re-case") and identifiers
	// that are recognized built-in function calls or contextual keywords (BuiltInFunctionNames /
	// ContextualKeywordIdentifiers).
	private static bool RequiresCaseSensitiveRoundTripComparison(TSqlParserToken token)
	{
		return token.TokenType switch
		{
			TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier =>
				!BuiltInFunctionNames.Contains(token.Text) && !ContextualKeywordIdentifiers.Contains(token.Text),
			TSqlTokenType.Variable or
			TSqlTokenType.AsciiStringLiteral or
			TSqlTokenType.AsciiStringOrQuotedIdentifier or
			TSqlTokenType.UnicodeStringLiteral or
			TSqlTokenType.SqlCommandIdentifier or
			TSqlTokenType.SingleLineComment or
			TSqlTokenType.MultilineComment or
			TSqlTokenType.Integer or
			TSqlTokenType.Numeric or
			TSqlTokenType.Real or
			TSqlTokenType.HexLiteral or
			TSqlTokenType.Money => true,
			_ => false
		};
	}

	// NormalizeSingleLineCommentBoundaries collapses "\r\n" to "\n" (and lone "\r" to "\n") before
	// parsing, which shifts every ScriptDom token offset left of where it'd be in the caller's
	// original text whenever a "\r\n" pair was collapsed to one character. This walks the
	// original text once and records, for every offset in the normalized text, the corresponding
	// offset in the original - needed to report token positions in terms of the text the caller
	// actually passed in, not the internal normalized copy.
	private static int[] BuildNormalizedToOriginalOffsetMap(string original, string normalized)
	{
		var map = new int[normalized.Length + 1];
		var normalizedIndex = 0;
		var originalIndex = 0;

		while (normalizedIndex < normalized.Length && originalIndex < original.Length)
		{
			map[normalizedIndex] = originalIndex;

			if (original[originalIndex] == '\r' && originalIndex + 1 < original.Length && original[originalIndex + 1] == '\n')
			{
				originalIndex += 2;
			}
			else
			{
				originalIndex++;
			}

			normalizedIndex++;
		}

		for (var i = normalizedIndex; i <= normalized.Length; i++)
		{
			map[i] = originalIndex;
		}

		return map;
	}

	// Words T-SQL lexes as content that can legitimately contain embedded whitespace or line
	// breaks as part of its own meaning - a comment's wording, a string literal's actual value,
	// or (rarely, but SQL Server allows it) a bracket-quoted identifier's name. Keywords,
	// punctuation, numbers, and unquoted identifiers can never contain a line break by
	// construction, so they're never at risk from a text-level trim pass and don't need to be
	// in this set.
	private static bool IsMultilineCapableTokenType(TSqlTokenType tokenType)
	{
		return tokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment
			or TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral or TSqlTokenType.AsciiStringOrQuotedIdentifier
			or TSqlTokenType.QuotedIdentifier or TSqlTokenType.SqlCommandIdentifier;
	}

	// Marks every character belonging to a comment/string-literal/quoted-identifier in `text` as
	// protected, so TrimTrailingWhitespaceTrackingOffsets never mutates content that happens to
	// contain trailing whitespace or a line break of its own. Re-tokenizes `text` itself (the
	// near-final rendered output) with ScriptDom rather than trying to track each token's exact
	// rendered position by hand through the ~2000-line render loop above - that kind of hand-kept
	// offset bookkeeping has already been the source of multiple bugs elsewhere in this file
	// (e.g. the CREATE-statement afterCreateObjectName tracking), and re-tokenizing gives
	// ScriptDom's own authoritative offsets instead.
	private static bool[] BuildProtectedTokenMask(string text)
	{
		var mask = new bool[text.Length];
		var tokens = TryTokenize(text);
		if (tokens is null)
		{
			return mask;
		}

		foreach (var token in tokens)
		{
			if (token.Text is null || !IsMultilineCapableTokenType(token.TokenType))
			{
				continue;
			}

			var start = Math.Max(0, token.Offset);
			var end = Math.Min(text.Length, token.Offset + token.Text.Length);
			for (var p = start; p < end; p++)
			{
				mask[p] = true;
			}
		}

		return mask;
	}

	// Reimplements the trailing-whitespace cleanup FormatForDisplay has always applied (trim the
	// very end of the document, then trim trailing whitespace from every line) but also produces
	// a raw-offset -> final-offset map, so the token start offsets captured against the untrimmed
	// StringBuilder during the main loop can be translated to their position in the text actually
	// returned. protectedMask (see BuildProtectedTokenMask) marks characters that belong to a
	// comment, string literal, or quoted identifier's own text - trailing whitespace and line
	// breaks there are part of that token's actual content, not formatting whitespace the
	// renderer introduced between tokens, so they're copied through completely untouched instead
	// of being trimmed or normalized to Environment.NewLine.
	private static (string Text, int[] OffsetMap) TrimTrailingWhitespaceTrackingOffsets(string raw, bool[] protectedMask)
	{
		var keepLength = raw.Length;
		while (keepLength > 0 && char.IsWhiteSpace(raw[keepLength - 1]) && !protectedMask[keepLength - 1])
		{
			keepLength--;
		}

		var offsetMap = new int[raw.Length + 1];
		var sb = new StringBuilder(keepLength);
		var finalPos = 0;
		var rawIndex = 0;

		while (rawIndex < keepLength)
		{
			var newlineIndex = raw.IndexOf('\n', rawIndex, keepLength - rawIndex);
			var lineEndExclusive = newlineIndex >= 0 ? newlineIndex : keepLength;

			if (newlineIndex >= 0 && protectedMask[newlineIndex])
			{
				// This line break is inside a comment/string-literal/quoted-identifier's own
				// text, not one the renderer introduced between tokens - copy it (and whatever
				// precedes it on this line) through byte-for-byte rather than trimming or
				// normalizing it, so the token's actual content never changes.
				for (var p = rawIndex; p <= newlineIndex; p++)
				{
					offsetMap[p] = finalPos;
					sb.Append(raw[p]);
					finalPos++;
				}

				rawIndex = newlineIndex + 1;
				continue;
			}

			// A trailing '\r' is part of the "\r\n" pair, not this line's own content - it gets
			// replaced by Environment.NewLine below rather than copied through directly.
			var contentEnd = lineEndExclusive;
			if (newlineIndex >= 0 && contentEnd > rawIndex && raw[contentEnd - 1] == '\r')
			{
				contentEnd--;
			}

			var trimmedContentEnd = contentEnd;
			while (trimmedContentEnd > rawIndex && char.IsWhiteSpace(raw[trimmedContentEnd - 1]) && !protectedMask[trimmedContentEnd - 1])
			{
				trimmedContentEnd--;
			}

			for (var p = rawIndex; p < trimmedContentEnd; p++)
			{
				offsetMap[p] = finalPos;
				sb.Append(raw[p]);
				finalPos++;
			}

			// Everything from the trimmed tail through this line's own line-ending collapses onto
			// the position right after the kept content - a token offset that landed in stripped
			// whitespace ends up at the nearest kept character instead.
			for (var p = trimmedContentEnd; p <= lineEndExclusive; p++)
			{
				offsetMap[p] = finalPos;
			}

			if (newlineIndex >= 0)
			{
				sb.Append(Environment.NewLine);
				finalPos += Environment.NewLine.Length;
				rawIndex = newlineIndex + 1;
			}
			else
			{
				rawIndex = lineEndExclusive;
			}
		}

		for (var p = rawIndex; p <= raw.Length; p++)
		{
			offsetMap[p] = finalPos;
		}

		return (sb.ToString(), offsetMap);
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

	private static bool HasTopLevelCommaBeforeMatchingParenthesis(IList<TSqlParserToken> tokens, int leftParenthesisIndex)
	{
		var depth = 0;
		for (var i = leftParenthesisIndex; i < tokens.Count; i++)
		{
			var tokenType = tokens[i].TokenType;
			if (tokenType == TSqlTokenType.LeftParenthesis)
			{
				depth++;
			}
			else if (tokenType == TSqlTokenType.RightParenthesis)
			{
				depth--;
				if (depth == 0)
				{
					return false;
				}
			}
			else if (tokenType == TSqlTokenType.Comma && depth == 1)
			{
				return true;
			}
		}

		return false;
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
						if (isExpressionEnd && previousNonWhitespaceChar != ')')
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

	private static void FormatInClauseMultiline(StringBuilder result, string inClauseContent, int indentLevel)
	{
		var startParen = inClauseContent.IndexOf('(');
		if (startParen < 0)
		{
			result.Append(inClauseContent);
			return;
		}

		// indentLevel is a flat counter that does not track how deep "IN (" actually landed once
		// CASE/WHEN, AND-continuation, and nested-parenthesis indents (each their own, separate
		// mechanism) have all been applied to it - using it directly produced the right answer
		// only by coincidence for a shallow, top-level IN clause, and left deeply-nested ones
		// (e.g. inside a WHEN's boolean expression) badly under-indented. The line "IN (" is
		// actually sitting on, read back out of result before anything more is appended to it,
		// is the one thing that already reflects every one of those mechanisms correctly.
		var currentLineIndentTabs = GetCurrentLineText(result).TakeWhile(c => c == '\t').Count();

		var prefix = inClauseContent[..(startParen + 1)].Trim();
		result.Append(prefix);
		result.AppendLine();

		var valuesPart = inClauseContent[(startParen + 1)..].Trim().TrimEnd(')');
		var segments = SplitInClauseSegments(valuesPart);
		var lastValueIndex = segments.FindLastIndex(segment => !segment.IsComment);

		// One tab deeper than the "IN (" line itself - the same "+1 per nesting level"
		// convention this formatter already uses for ordinary parenthesis expansion.
		var indent = new string('\t', currentLineIndentTabs + 1);
		var currentLine = new StringBuilder();

		void FlushLine()
		{
			if (currentLine.Length == 0)
			{
				return;
			}

			result.Append(indent);
			result.Append(currentLine);
			result.AppendLine();
			currentLine.Clear();
		}

		for (var index = 0; index < segments.Count; index++)
		{
			var (text, isComment) = segments[index];

			if (isComment)
			{
				// A comment always gets its own line - it must never be merged with the value
				// before or after it, which is what silently swallowed values into commented-out
				// text before this rewrite.
				FlushLine();
				result.Append(indent);
				result.Append(text);
				result.AppendLine();
				continue;
			}

			// Every value except the very last one needs a trailing comma baked in before the
			// line-length check, not appended separately - appending it only to values that
			// stay on the same line (the previous approach) is what dropped the comma whenever
			// a value happened to be the last one to fit on a line.
			var isLastValue = index == lastValueIndex;
			var token = isLastValue ? text : text + ",";
			var separatorLength = currentLine.Length > 0 ? 1 : 0;

			if (currentLine.Length > 0 && indent.Length + currentLine.Length + separatorLength + token.Length > 120)
			{
				FlushLine();
			}

			if (currentLine.Length > 0)
			{
				currentLine.Append(' ');
			}

			currentLine.Append(token);
		}

		FlushLine();

		// Matches the "IN (" line's own indent, same basis as the values above (indentLevel is
		// the same disconnected counter that under-indented the values before that fix).
		result.Append(new string('\t', currentLineIndentTabs));
		result.Append(')');
	}

	/// <summary>
	/// Splits an IN-list's inner text into value/comment segments on top-level commas only -
	/// never inside a nested parenthesis (e.g. a function call), a string literal, or a
	/// -- line / block comment, all of which the previous plain Split(',') would break on.
	/// </summary>
	private static List<(string Text, bool IsComment)> SplitInClauseSegments(string valuesPart)
	{
		var segments = new List<(string Text, bool IsComment)>();
		var current = new StringBuilder();
		var parenDepth = 0;
		var i = 0;

		void FlushValue()
		{
			var text = current.ToString().Trim();
			if (text.Length > 0)
			{
				segments.Add((text, false));
			}

			current.Clear();
		}

		while (i < valuesPart.Length)
		{
			var c = valuesPart[i];

			if (c == '-' && i + 1 < valuesPart.Length && valuesPart[i + 1] == '-')
			{
				FlushValue();
				var end = valuesPart.IndexOf('\n', i);
				if (end < 0)
				{
					end = valuesPart.Length;
				}

				segments.Add((valuesPart[i..end].TrimEnd(), true));
				i = end;
				continue;
			}

			if (c == '/' && i + 1 < valuesPart.Length && valuesPart[i + 1] == '*')
			{
				FlushValue();
				var end = valuesPart.IndexOf("*/", i + 2, StringComparison.Ordinal);
				end = end < 0 ? valuesPart.Length : end + 2;
				segments.Add((valuesPart[i..end], true));
				i = end;
				continue;
			}

			if (c == '\'')
			{
				current.Append(c);
				i++;
				while (i < valuesPart.Length)
				{
					current.Append(valuesPart[i]);
					if (valuesPart[i] == '\'')
					{
						if (i + 1 < valuesPart.Length && valuesPart[i + 1] == '\'')
						{
							current.Append(valuesPart[i + 1]);
							i += 2;
							continue;
						}

						i++;
						break;
					}

					i++;
				}

				continue;
			}

			if (c == '(')
			{
				parenDepth++;
				current.Append(c);
				i++;
				continue;
			}

			if (c == ')')
			{
				parenDepth = Math.Max(0, parenDepth - 1);
				current.Append(c);
				i++;
				continue;
			}

			if (c == ',' && parenDepth == 0)
			{
				FlushValue();
				i++;
				continue;
			}

			current.Append(c);
			i++;
		}

		FlushValue();
		return segments;
	}

	private static int GetActiveExpandedParenthesisDepth(Stack<ParenthesisScope> parenthesisStack)
	{
		return parenthesisStack.Count;
	}

	private static int GetContentIndent(int indentLevel, Stack<ParenthesisScope> parenthesisStack, bool inSelectColumnList, int selectStatementDepth)
	{
		var extraIndent = inSelectColumnList && selectStatementDepth > 0 ? 1 : 0;
		extraIndent += GetActiveExpandedParenthesisDepth(parenthesisStack);
		return indentLevel + extraIndent;
	}

	private static int GetColumnListIndent(int indentLevel, Stack<ParenthesisScope> parenthesisStack, bool inSelectColumnList, int selectStatementDepth, bool inCreateStatementParams, bool inInsertColumnList, bool afterCreateObjectName, bool inUpdateSetClause, bool inExecParams = false)
	{
		var extraIndent = inSelectColumnList && selectStatementDepth > 0 ? 1 : 0;
		if ((inCreateStatementParams || inInsertColumnList || inExecParams) && !afterCreateObjectName)
		{
			extraIndent = 1;
		}
		else
		{
			extraIndent += GetActiveExpandedParenthesisDepth(parenthesisStack);
		}

		if (inUpdateSetClause)
		{
			extraIndent++;
		}

		return indentLevel + extraIndent;
	}

	private static int GetClauseIndentForContext(int indentLevel, Stack<ParenthesisScope> parenthesisStack, bool isInClauseScope)
	{
		var indent = indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack);
		return isInClauseScope ? indent + 2 : indent;
	}

	private static int GetClosingParenIndentForContext(int indentLevel, Stack<ParenthesisScope> parenthesisStack, bool inValuesList, int parenthesisDepth, int valuesListDepth, TSqlTokenType? nextTokenType)
	{
		var indent = indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack);
		if (inValuesList && parenthesisDepth > valuesListDepth)
		{
			var closingDedent = nextTokenType == TSqlTokenType.Comma ? 8 : 6;
			indent = Math.Max(0, indent - closingDedent);
		}

		return indent;
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

	private static int GetDeclarationContinuationIndent(bool isVariableToken)
	{
		return isVariableToken ? 4 : 0;
	}

	private static int GetExpressionLengthUntilClauseBoundary(IList<TSqlParserToken> tokens, int startIndex)
	{
		var length = 0;
		var depth = 0;
		for (var i = startIndex; i < tokens.Count; i++)
		{
			var token = tokens[i];

			if (token.TokenType == TSqlTokenType.LeftParenthesis)
			{
				depth++;
			}
			else if (token.TokenType == TSqlTokenType.RightParenthesis)
			{
				// A closing paren that doesn't match one opened within this span belongs to
				// whatever enclosing group the measured expression sits inside (e.g. a CASE
				// WHEN's own wrapping parenthesis) - not to the expression itself.
				if (depth == 0)
				{
					break;
				}

				depth--;
			}
			else if (depth == 0 && (IsClauseBoundaryToken(token.TokenType) || IsCaseBoundaryToken(token.TokenType) || token.TokenType == TSqlTokenType.Comma))
			{
				// THEN/WHEN/ELSE/END/CASE and commas can never legitimately be part of the
				// expression being measured either - IsClauseBoundaryToken alone doesn't know
				// about them, which is what let this run straight through a WHEN's own THEN
				// (and everything after it) when measuring a BETWEEN clause's upper bound,
				// inflating its "length" enough to wrongly trigger a line break before it.
				break;
			}

			if (token.TokenType == TSqlTokenType.WhiteSpace)
			{
				if (length > 0)
				{
					length++;
				}
				continue;
			}

			if (token.TokenType == TSqlTokenType.EndOfFile)
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

	private static bool IsCaseBoundaryToken(TSqlTokenType tokenType)
	{
		return tokenType is TSqlTokenType.Then or TSqlTokenType.When or TSqlTokenType.Else or TSqlTokenType.End or TSqlTokenType.Case;
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

	private static int GetOperatorContinuationIndent(int indentLevel, Stack<ParenthesisScope> parenthesisStack, bool inSelectColumnList, int selectStatementDepth, bool inInClause)
	{
		var activeDepth = GetActiveExpandedParenthesisDepth(parenthesisStack);
		if (activeDepth == 0)
		{
			return indentLevel + (inInClause ? 2 : 1);
		}

		var selectExtra = inSelectColumnList && selectStatementDepth > 0 ? 1 : 0;
		var baseIndent = indentLevel + selectExtra + activeDepth;
		return inInClause ? baseIndent + 1 : baseIndent;
	}

	private static int GetSelectIndentForContext(int indentLevel, Stack<ParenthesisScope> parenthesisStack, bool isLeftParenInClause)
	{
		var indent = indentLevel + GetActiveExpandedParenthesisDepth(parenthesisStack);
		return isLeftParenInClause ? indent + 2 : indent;
	}

	private static bool HasParenthesisScope(Stack<ParenthesisScope> parenthesisStack, int parenthesisDepth)
	{
		return parenthesisStack.Count > 0 && parenthesisStack.Peek().ParenthesisDepth == parenthesisDepth;
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

	private static readonly HashSet<string> BuiltInFunctionNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"CAST", "TRY_CAST", "CONVERT", "TRY_CONVERT", "PARSE", "TRY_PARSE",
		"DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATETRUNC",
		"GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "EOMONTH",
		"ISNULL", "COALESCE", "NULLIF", "IIF", "CHECKSUM", "NEWID",
		"LEN", "SUBSTRING", "STUFF", "CHARINDEX", "PATINDEX", "REPLACE",
		"LTRIM", "RTRIM", "UPPER", "LOWER", "CONCAT", "CONCAT_WS", "STR", "SPACE", "REPLICATE", "REVERSE", "QUOTENAME", "PARSENAME", "FORMAT",
		"ROUND", "CEILING", "FLOOR", "ABS", "SIGN", "POWER", "SQRT",
		"SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX", "STRING_AGG",
		"ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD",
		"OBJECT_ID", "COL_NAME", "SCHEMA_NAME",
		"ERROR_MESSAGE", "ERROR_NUMBER", "ERROR_SEVERITY", "ERROR_STATE", "ERROR_LINE", "ERROR_PROCEDURE",
		"OPENJSON", "JSON_VALUE", "JSON_QUERY",
	};

	private static bool IsBuiltInFunctionCall(IList<TSqlParserToken> tokens, int identifierIndex)
	{
		if (identifierIndex < 0 || identifierIndex >= tokens.Count)
		{
			return false;
		}

		var token = tokens[identifierIndex];
		if (token.TokenType is not (TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier))
		{
			return false;
		}

		if (!BuiltInFunctionNames.Contains(token.Text))
		{
			return false;
		}

		var nextIndex = NextNonWhitespaceIndex(tokens, identifierIndex + 1);
		return nextIndex < tokens.Count && tokens[nextIndex].TokenType == TSqlTokenType.LeftParenthesis;
	}

	private static bool IsClauseBoundaryToken(TSqlTokenType tokenType)
	{
		return tokenType is TSqlTokenType.And or TSqlTokenType.Or or TSqlTokenType.Group or TSqlTokenType.Having or TSqlTokenType.Order or TSqlTokenType.Union or TSqlTokenType.Except or TSqlTokenType.Intersect or TSqlTokenType.From or TSqlTokenType.Where or TSqlTokenType.Semicolon
			or TSqlTokenType.Join or TSqlTokenType.Inner or TSqlTokenType.Left or TSqlTokenType.Right or TSqlTokenType.Outer or TSqlTokenType.Cross or TSqlTokenType.Full;
	}

	private static bool IsInsideCaseBlock(IList<TSqlParserToken> tokens, int tokenIndex)
	{
		var parenDepth = 0;
		// Every END belongs to some earlier opener (CASE or BEGIN) that has already closed by
		// the time we reach tokenIndex - e.g. a nested CASE...END entirely inside an earlier
		// WHEN...THEN branch. Scanning backward must skip past each such already-closed pair
		// (tracked here) rather than stopping at the first END it sees, or a nested CASE/BEGIN
		// block between tokenIndex and its true enclosing CASE gets mistaken for that boundary.
		var endDepth = 0;
		for (var i = tokenIndex - 1; i >= 0; i--)
		{
			var tokenType = tokens[i].TokenType;
			if (tokenType == TSqlTokenType.RightParenthesis)
			{
				parenDepth++;
				continue;
			}

			if (tokenType == TSqlTokenType.LeftParenthesis)
			{
				parenDepth = Math.Max(0, parenDepth - 1);
				continue;
			}

			if (parenDepth > 0 || tokenType == TSqlTokenType.WhiteSpace)
			{
				continue;
			}

			if (tokenType == TSqlTokenType.End)
			{
				endDepth++;
				continue;
			}

			if (tokenType is TSqlTokenType.Case or TSqlTokenType.Begin)
			{
				if (endDepth > 0)
				{
					endDepth--;
					continue;
				}

				return tokenType == TSqlTokenType.Case;
			}

			if (endDepth == 0 && tokenType == TSqlTokenType.Semicolon)
			{
				return false;
			}
		}

		return false;
	}

	// "INSTEAD" has no TSqlTokenType of its own (ScriptDom lexes it as a plain Identifier, like
	// TRY/CATCH/FINALLY - see IsTryCatchFinallyToken) - this is the trigger that flips on
	// inInsteadOfClause, so it only fires when the next real token is actually OF, not for some
	// unrelated identifier that happens to be spelled "instead".
	private static bool IsInsteadOfTriggerClauseStart(IList<TSqlParserToken> tokens, int index)
	{
		var token = tokens[index];
		if (token.TokenType is not (TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier) ||
			!token.Text.Equals("INSTEAD", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var nextIndex = NextNonWhitespaceIndex(tokens, index + 1);
		return nextIndex < tokens.Count && tokens[nextIndex].TokenType == TSqlTokenType.Of;
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
			TSqlTokenType.Exec or
			TSqlTokenType.Coalesce or
			TSqlTokenType.NullIf => true,
			_ => false
		};
	}

	// True when the nearest real (non-whitespace) token before `index` is a comment - i.e. the
	// content currently at the end of `result` is trapped inside that comment's own extent, so
	// nothing may be glued onto the same line without silently becoming part of the comment.
	private static bool PrecedingRealTokenIsComment(IList<TSqlParserToken> tokens, int index)
	{
		var i = index - 1;
		while (i >= 0 && tokens[i].TokenType == TSqlTokenType.WhiteSpace)
		{
			i--;
		}

		return i >= 0 && tokens[i].TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;
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

	private static bool IsTryCatchFinallyToken(TSqlParserToken token)
	{
		return token.TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier &&
			(token.Text.Equals("TRY", StringComparison.OrdinalIgnoreCase) ||
			token.Text.Equals("CATCH", StringComparison.OrdinalIgnoreCase) ||
			token.Text.Equals("FINALLY", StringComparison.OrdinalIgnoreCase));
	}

	private static bool IsUpdateSetClause(IList<TSqlParserToken> tokens, int setIndex)
	{
		for (var i = setIndex - 1; i >= 0; i--)
		{
			var tokenType = tokens[i].TokenType;
			if (tokenType == TSqlTokenType.WhiteSpace)
			{
				continue;
			}

			if (tokenType == TSqlTokenType.Update)
			{
				return true;
			}

			if (tokenType is TSqlTokenType.Semicolon or TSqlTokenType.Go or TSqlTokenType.Begin or TSqlTokenType.End)
			{
				return false;
			}
		}

		return false;
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

	private static int NextNonWhitespaceIndex(IList<TSqlParserToken> tokens, int startIndex)
	{
		var index = startIndex;
		while (index < tokens.Count && IsWhitespaceOrComment(tokens[index].TokenType))
		{
			index++;
		}

		return index;
	}

	// Comments are trivia, like whitespace - a comment sitting between two real tokens (e.g.
	// between a JOIN modifier and JOIN itself) must not defeat "what's the real adjacent token"
	// adjacency checks built on NextNonWhitespaceIndex/PreviousNonWhitespaceIndex.
	private static bool IsWhitespaceOrComment(TSqlTokenType tokenType)
	{
		return tokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;
	}

	private static bool ContainsCommentToken(IList<TSqlParserToken>? tokens)
	{
		if (tokens is null)
		{
			return false;
		}

		for (var i = 0; i < tokens.Count; i++)
		{
			if (tokens[i].TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
			{
				return true;
			}
		}

		return false;
	}

	private static string NormalizeSingleLineCommentBoundaries(string sql)
	{
		if (string.IsNullOrWhiteSpace(sql))
		{
			return sql;
		}

		// Preserve comment content exactly; only normalize line endings for parser stability.
		return sql.Replace("\r\n", "\n").Replace('\r', '\n');
	}

	private static void PopParenthesisScope(Stack<ParenthesisScope> parenthesisStack, int parenthesisDepth)
	{
		if (HasParenthesisScope(parenthesisStack, parenthesisDepth))
		{
			parenthesisStack.Pop();
		}
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

	private static int PreviousNonWhitespaceIndex(IList<TSqlParserToken> tokens, int startIndex)
	{
		var index = startIndex;
		while (index >= 0 && IsWhitespaceOrComment(tokens[index].TokenType))
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

	private static bool ShouldFormatInClauseMultiline(string inClauseContent, int indentLevel)
	{
		var indentLength = Math.Max(0, indentLevel);
		return indentLength + inClauseContent.Length > 120;
	}

	private static bool ShouldKeepSelectInline(IList<TSqlParserToken> tokens, int selectIndex)
	{
		var parenthesisDepth = 0;
		var caseDepth = 0;
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

			if (tokenType == TSqlTokenType.Case)
			{
				caseDepth++;
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

			if (tokenType == TSqlTokenType.End && caseDepth > 0)
			{
				caseDepth--;
				hasTopLevelProjectionToken = true;
				continue;
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
		var caseDepth = 0;
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

			if (tokenType == TSqlTokenType.Case)
			{
				caseDepth++;
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

			if (tokenType == TSqlTokenType.End && caseDepth > 0)
			{
				caseDepth--;
				hasTopLevelProjectionToken = true;
				continue;
			}

			if (tokenType is TSqlTokenType.Semicolon or TSqlTokenType.Go or TSqlTokenType.End or TSqlTokenType.Where or TSqlTokenType.Group or TSqlTokenType.Order or TSqlTokenType.Having or TSqlTokenType.Join or TSqlTokenType.Inner or TSqlTokenType.Left or TSqlTokenType.Right or TSqlTokenType.Full or TSqlTokenType.Cross or TSqlTokenType.Union)
			{
				return false;
			}

			hasTopLevelProjectionToken = true;
		}

		return false;
	}

	// FormatExpressionFallback collapses all whitespace - including the newline that terminates a
	// "--" comment - and has no comment handling at all, so it must never run on SQL containing a
	// comment (everything after "--" would silently become part of one dead, commented-out line).
	// Trusting ScriptDom's own tokens here instead of re-scanning the string for "--" avoids a
	// second, hand-written comment parser that could disagree with ScriptDom's.
	private static bool ShouldUseExpressionFallback(string sql, IList<TSqlParserToken>? tokens)
	{
		if (string.IsNullOrWhiteSpace(sql))
		{
			return false;
		}

		if (ContainsCommentToken(tokens))
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

	private static void TrimTrailingSpaces(StringBuilder result)
	{
		while (result.Length > 0 && result[^1] == ' ')
		{
			result.Length--;
		}
	}

	private static void TrimTrailingLineEndings(StringBuilder result)
	{
		while (result.Length > 0 && (result[^1] == ' ' || result[^1] == '\t' || result[^1] == '\r' || result[^1] == '\n'))
		{
			result.Length--;
		}
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

	private readonly struct ParenthesisScope
	{
		public ParenthesisScope(int parenthesisDepth)
		{
			ParenthesisDepth = parenthesisDepth;
		}

		public int ParenthesisDepth { get; }
	}

	/// <summary>
	/// Tracks one open JOIN that is still awaiting its ON clause, so a "nested join" (a JOIN
	/// whose composite table source itself contains another JOIN before the outer JOIN's own ON
	/// appears - e.g. "a LEFT JOIN b INNER JOIN c ON c.x = b.x ON b.y = a.y") can be rendered with
	/// the inner join folded onto its own deeper-indented line while the outer JOIN's ON drops to
	/// its own, less-indented line - instead of both ON clauses colliding on one line with no
	/// visual indication of which ON belongs to which JOIN. T-SQL resolves each ON against the
	/// most recently opened unresolved JOIN (LIFO, like matching brackets), so this only needs a
	/// simple stack kept alongside the token walk - no AST/subquery rewriting required.
	/// </summary>
	private sealed class JoinFrame
	{
		public bool HadNestedContent { get; set; }

		/// <summary>
		/// The indent level of this JOIN's own line, captured when the frame is pushed - lets the
		/// matching ON (and anything, like a CASE expression, that continues that ON's condition)
		/// pick up the correct indent context even when this JOIN was itself a nested join.
		/// </summary>
		public int Indent { get; set; }
	}

	/// <summary>
	/// Collects the token-stream index of the last token of every statement in the parsed
	/// script (at any nesting depth), so the renderer can insert a missing statement-terminating
	/// semicolon. Must derive from <see cref="TSqlFragmentVisitor"/> (NOT
	/// <see cref="TSqlConcreteFragmentVisitor"/>, which sets an internal flag that silently
	/// disables the base-type dispatch this relies on) so that overriding the single
	/// <c>Visit(TSqlStatement)</c> method reaches every concrete statement type. A compound
	/// statement (e.g. an IfStatement) and its child block (its BeginEndBlockStatement) share the
	/// same LastTokenIndex, so collecting into a HashSet naturally avoids double-inserting a
	/// semicolon at that shared boundary.
	/// </summary>
	private sealed class StatementBoundaryCollector : TSqlFragmentVisitor
	{
		public HashSet<int> LastTokenIndices { get; } = new();

		public override void Visit(TSqlStatement node)
		{
			LastTokenIndices.Add(node.LastTokenIndex);
		}
	}
}