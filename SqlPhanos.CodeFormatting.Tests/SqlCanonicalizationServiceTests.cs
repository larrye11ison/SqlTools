using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SqlPhanos.CodeFormatting.Tests;

public sealed class SqlCanonicalizationServiceTests
{
	private static readonly SqlCanonicalizationService service = new();
	private readonly ITestOutputHelper _output;

	public SqlCanonicalizationServiceTests(ITestOutputHelper output) => _output = output;

	[Fact]
	public void NestedJoinIndentsInnerJoinAndDedentsOuterOn()
	{
		// A "nested join" (T-SQL's own term - see "Using Nested Joins" in the FROM clause docs):
		// the INNER JOIN's table source is folded into the LEFT OUTER JOIN's composite table
		// source before the outer join's own ON appears, so T-SQL resolves each ON against the
		// most recently opened unresolved JOIN (LIFO) rather than the textually-nearest one. The
		// inner join is rendered as a single deeper-indented unit (JOIN + its own ON together),
		// while the outer join's ON drops to its own, one-level-dedented line - making which ON
		// belongs to which JOIN unambiguous without requiring explicit parentheses.
		var sql = "select *\nfrom b\nleft outer join l\ninner join i on i.id = l.id\non l.b_id = b.b_id";
		var expected = """
			SELECT
				*
			FROM b
			LEFT OUTER JOIN l
					INNER JOIN i ON i.id = l.id
				ON l.b_id = b.b_id;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void MultiLevelNestedJoinsFormAStaircase()
	{
		// Three joins nested inside one another (a JOIN b JOIN c JOIN d, none resolved by an ON
		// until all three table sources are in place) - each additional nesting level indents one
		// step deeper, and the three closing ONs unwind back down one level at a time in reverse
		// order, mirroring the LIFO ON-to-JOIN matching that makes this valid T-SQL in the first
		// place.
		var sql = "select *\nfrom a\njoin b\njoin c\njoin d on d.x = c.x\non c.y = b.y\non b.z = a.z";
		var expected = """
			SELECT
				*
			FROM a
			JOIN b
					JOIN c
						JOIN d ON d.x = c.x
					ON c.y = b.y
				ON b.z = a.z;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CreateTableAndInsertColumnListParenAreConsistentByDefault()
	{
		// Regression test: CREATE TABLE's column-list paren stayed glued to the same line
		// while INSERT INTO's column-list paren was always forced onto its own line -
		// inconsistent regardless of how the source SQL was formatted. Both must now agree
		// on the default (same-line) placement, even when (as here) the INSERT source itself
		// already had the paren on its own line - the formatter normalizes it.
		var sql = """
			CREATE TABLE #ItemList (
				TheIDNumber INT PRIMARY KEY CLUSTERED,
				MasterIDNumber VARCHAR(10)
			);

			INSERT INTO #ItemList
			(
				TheIDNumber,
				MasterIDNumber
			)
			SELECT
				x.TheIDNumber,
				x.MasterIDNumber
			FROM @ItemList x;
			""";
		var expected = """
			CREATE TABLE #ItemList (
				TheIDNumber INT PRIMARY KEY CLUSTERED,
				MasterIDNumber VARCHAR(10)
			);

			INSERT INTO #ItemList (
				TheIDNumber,
				MasterIDNumber
			)
			SELECT
				x.TheIDNumber,
				x.MasterIDNumber
			FROM @ItemList x;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void OpeningParenOnNewLineOptionAppliesToCreateTableAndInsertConsistently()
	{
		var sql = """
			CREATE TABLE #ItemList (
				TheIDNumber INT PRIMARY KEY CLUSTERED,
				MasterIDNumber VARCHAR(10)
			);

			INSERT INTO #ItemList (
				TheIDNumber,
				MasterIDNumber
			)
			SELECT
				x.TheIDNumber,
				x.MasterIDNumber
			FROM @ItemList x;
			""";
		var expected = """
			CREATE TABLE #ItemList
			(
				TheIDNumber INT PRIMARY KEY CLUSTERED,
				MasterIDNumber VARCHAR(10)
			);

			INSERT INTO #ItemList
			(
				TheIDNumber,
				MasterIDNumber
			)
			SELECT
				x.TheIDNumber,
				x.MasterIDNumber
			FROM @ItemList x;
			""";

		RunFactTest(sql, expected, openingParenOnNewLine: true);
	}

	[Fact]
	public void ExecWithNamedParametersStartsOwnLineWithOneParamPerLine()
	{
		// EXEC/EXECUTE previously fell through to the default token handling entirely (no
		// dedicated case), so it never started its own line and its parameters were never
		// broken one-per-line like CREATE PROCEDURE parameter lists are.
		var expected = """
			SET @x = 1;

			EXEC sp_Foo
				@a = 1,
				@b = 'x';
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void ExecuteWithReturnCaptureAndSchemaQualifiedNameFormattedCorrectly()
	{
		var expected = """
			SET @x = 1;

			EXECUTE @ret = dbo.sp_Foo
				@a = 1,
				@b = 'x';
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void ExecWithNoParametersStaysOnOneLine()
	{
		var expected = """
			SET @x = 1;

			EXEC sp_Foo;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void ExecuteDynamicSqlStringFormattedCorrectly()
	{
		var expected = """
			SET @x = 1;

			EXECUTE (@sql);
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void ExecWithBareLiteralParametersGetsSpaceAndIndentedContinuationLines()
	{
		// Regression test: EXEC sp_proc 'literal', 'literal2' (no named @param = value pairs)
		// was gluing the proc name directly to the first literal with no space at all
		// ("sp_MSdroptemptable'#temp'"), and the continuation parameters after the first
		// comma were not indented one level deeper like the named-parameter form is.
		var sql = "EXEC sp_MSdroptemptable '#CurrentLastTwelve',\n'two',\n3,\n'more'";
		var expected = """
			EXEC sp_MSdroptemptable '#CurrentLastTwelve',
				'two',
				3,
				'more';
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void InsertIntoSelectWithoutColumnListDoesNotCorruptLaterParentheses()
	{
		// Regression test: INSERT INTO tbl SELECT ... (no explicit "(col1, col2)" column list)
		// left the pendingInsertColumnList flag stuck true, since it is normally only cleared
		// by the "(" of that column list or by VALUES. With no column list and no VALUES, the
		// flag stayed true until the *next* parenthesis anywhere in the SELECT - e.g. a
		// CAST(...) or ROW_NUMBER() call - which then got mistaken for the insert's own column
		// list and mangled (broken across lines it should never have broken across, sometimes
		// bleeding into unrelated statements much further down the same batch).
		var sql = "INSERT INTO #dateRangeLastTwelve\nSELECT CAST(dt.date_id AS DATE) AS DATA_DATE\nFROM DB2.dbo.TimeTable dt";
		var expected = """
			INSERT INTO #dateRangeLastTwelve
			SELECT CAST(dt.date_id AS DATE) AS DATA_DATE
			FROM DB2.dbo.TimeTable dt;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void InsertIntoSelectWithRowNumberOverDoesNotBreakEmptyParens()
	{
		// Same pendingInsertColumnList leak as InsertIntoSelectWithoutColumnListDoesNotCorruptLaterParentheses,
		// but for ROW_NUMBER()'s empty argument list specifically: the stray "(" of ROW_NUMBER()
		// was mistaken for the insert column list opener, splitting "()" across four lines.
		var sql = "INSERT INTO #dilDates\nSELECT\n\t[l].[MasterIDNumber],\n\tROW_NUMBER() OVER (PARTITION BY l.MasterIDNumber ORDER BY [lmd].[SomeOtherDate] DESC) AS RowNum\nFROM SourceTableX lmd";
		var expected = """
			INSERT INTO #dilDates
			SELECT
				[l].[MasterIDNumber],
				ROW_NUMBER()
				OVER (
					PARTITION BY l.MasterIDNumber
					ORDER BY [lmd].[SomeOtherDate] DESC
				) AS RowNum
			FROM SourceTableX lmd;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CreateTableAfterPrecedingInsertIntoSelectKeepsDatatypeAndPrimaryKeyGlued()
	{
		// Same pendingInsertColumnList leak: a stuck flag from an earlier INSERT INTO ... SELECT
		// (no column list) survived across statement boundaries and corrupted an unrelated,
		// later CREATE TABLE's VARCHAR(10) column-length parens.
		var sql = "INSERT INTO #dateRangeLastTwelve\nSELECT CAST(dt.date_id AS DATE) AS DATA_DATE\nFROM DB2.dbo.TimeTable dt\n\nCREATE TABLE #TempB (\n\t[MasterIDNumber] VARCHAR(10) PRIMARY KEY CLUSTERED,\n\t[TheMainThingClosedDate] DATETIME\n)";
		var expected = """
			INSERT INTO #dateRangeLastTwelve
			SELECT CAST(dt.date_id AS DATE) AS DATA_DATE
			FROM DB2.dbo.TimeTable dt;

			CREATE TABLE #TempB (
				[MasterIDNumber] VARCHAR(10) PRIMARY KEY CLUSTERED,
				[TheMainThingClosedDate] DATETIME
			);
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void OuterApplyAliasStaysOnClosingParenLineAfterPrecedingInsertIntoSelect()
	{
		// Same pendingInsertColumnList leak: the "fmt" alias immediately following the closing
		// paren of an OUTER APPLY subquery was dropped to its own line instead of staying glued
		// to ")", once a preceding INSERT INTO ... SELECT (no column list) left the flag stuck.
		var sql = "INSERT INTO #dateRangeLastTwelve\nSELECT CAST(dt.date_id AS DATE) AS DATA_DATE\nFROM DB2.dbo.TimeTable dt\n\nSELECT\n\tfb.MasterIDNumber\nFROM DB3.dbo.LegalThingTable fb\nOUTER APPLY (\n\tSELECT 1 AS X\n) fmt";
		var expected = """
			INSERT INTO #dateRangeLastTwelve
			SELECT CAST(dt.date_id AS DATE) AS DATA_DATE
			FROM DB2.dbo.TimeTable dt;

			SELECT fb.MasterIDNumber
			FROM DB3.dbo.LegalThingTable fb
			OUTER APPLY
			(
				SELECT
					1 AS X
			) fmt;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void AlterTableAddConstraintPrimaryKeySingleColumnStaysCompactOnThirdLine()
	{
		// ALTER TABLE / ADD CONSTRAINT [name] / PRIMARY KEY CLUSTERED (...) always break onto
		// three separate lines regardless of column count, but the column list itself only
		// expands across multiple lines when there is more than one column.
		var sql = "ALTER TABLE #currentLastSix ADD CONSTRAINT [pk_currentLastSix_MasterIDNumber] PRIMARY KEY CLUSTERED (MasterIDNumber)";
		var expected = """
			ALTER TABLE #currentLastSix
			ADD CONSTRAINT [pk_currentLastSix_MasterIDNumber]
			PRIMARY KEY CLUSTERED (MasterIDNumber);
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void AlterTableAddConstraintPrimaryKeyMultiColumnExpandsColumnList()
	{
		var sql = "ALTER TABLE #delinquencies ADD CONSTRAINT [pk_delinquencies_MasterIDNumber_DATA_DATE] PRIMARY KEY CLUSTERED ( MasterIDNumber, DATA_DATE)";
		var expected = """
			ALTER TABLE #delinquencies
			ADD CONSTRAINT [pk_delinquencies_MasterIDNumber_DATA_DATE]
			PRIMARY KEY CLUSTERED (
				MasterIDNumber,
				DATA_DATE
			);
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void AlterTableAddPrimaryKeyWithoutConstraintNameKeepsAddAndPrimaryOnSameLine()
	{
		// With no CONSTRAINT [name] clause, ADD and PRIMARY KEY CLUSTERED share a line - the
		// same way ADD and CONSTRAINT do when a name is given - since there is no separate
		// constraint-name clause to justify a break between them.
		var sql = "ALTER TABLE #curTable ADD PRIMARY KEY CLUSTERED ([MasterIDNumber])";
		var expected = """
			ALTER TABLE #curTable
			ADD PRIMARY KEY CLUSTERED ([MasterIDNumber]);
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void AlterTableAddPrimaryKeyWithoutConstraintNameMultiColumnExpandsColumnList()
	{
		var sql = "ALTER TABLE #prevDeferBal ADD PRIMARY KEY CLUSTERED ( MasterIDNumber, RowId, SubCode)";
		var expected = """
			ALTER TABLE #prevDeferBal
			ADD PRIMARY KEY CLUSTERED (
				MasterIDNumber,
				RowId,
				SubCode
			);
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CreateOrAlterProcedureKeepsAlterOnTheCreateLine()
	{
		// Regression test: adding dedicated ALTER TABLE handling must not affect the common
		// "CREATE OR ALTER PROCEDURE ..." idiom, where ALTER continues the CREATE line rather
		// than starting a standalone ALTER statement.
		var sql = "CREATE OR ALTER PROCEDURE dbo.MyProc\nAS\nBEGIN\n\tSELECT 1\nEND";
		var expected = """
			CREATE OR ALTER PROCEDURE dbo.MyProc
			AS
			BEGIN
				SELECT 1;

			END;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void RowNumberOverWithTrailingAliasBreaksOverOntoOwnLine()
	{
		var sql = "SELECT\n\tad.MasterIDNumber,\n\tROW_NUMBER() OVER (PARTITION BY ad.MasterIDNumber ORDER BY ad.DATA_DATE DESC) AS RowNum\nFROM DB1.dbo.ThingJustLikeTheOtherThingDetailDaily ad";
		var expected = """
			SELECT
				ad.MasterIDNumber,
				ROW_NUMBER()
				OVER (
					PARTITION BY ad.MasterIDNumber
					ORDER BY ad.DATA_DATE DESC
				) AS RowNum
			FROM DB1.dbo.ThingJustLikeTheOtherThingDetailDaily ad;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void MissingSemicolonIsAddedWithNoTrailingBlankLineAtEndOfDocument()
	{
		var input = "SELECT 1";
		var formatted = service.FormatForDisplay(input);
		Assert.Equal("SELECT\r\n\t1;", formatted);
	}

	[Fact]
	public void TwoStatementsGetExactlyOneBlankLineBetweenThemAndNoneAfterTheLastOne()
	{
		var input = "SELECT 1\nSELECT 2";
		var formatted = service.FormatForDisplay(input);
		Assert.Equal("SELECT\r\n\t1;\r\n\r\nSELECT\r\n\t2;", formatted);
	}

	[Fact]
	public void AlreadyTerminatedStatementIsNotDoubleTerminated()
	{
		var input = "SELECT 1;";
		var formatted = service.FormatForDisplay(input);
		Assert.Equal("SELECT 1;", formatted);
	}

	[Fact]
	public void CompoundIfStatementGetsSemicolonAndBlankLineAfterEndIncludingNestedBody()
	{
		var input = "IF @x = 1\nBEGIN\n\tSET @y = 2\nEND\nSELECT 1";
		var formatted = service.FormatForDisplay(input);
		Assert.Equal("IF @x = 1\r\nBEGIN\r\n\tSET @y = 2;\r\n\r\nEND;\r\n\r\nSELECT\r\n\t1;", formatted);
	}

	[Fact]
	public void SubqueryInsideWhereClauseIsNotIndependentlyTerminated()
	{
		// Only the outer statement's closing paren gets the terminator; the subquery SELECT
		// is a QuerySpecification, not a TSqlStatement, so the AST-based boundary collector
		// never sees it as a candidate statement boundary.
		var input = "SELECT 1 WHERE x IN (SELECT y FROM t)";
		var formatted = service.FormatForDisplay(input);
		Assert.Equal("SELECT\r\n\t1\r\nWHERE x IN\r\n\t(\r\n\t\tSELECT y\r\n\t\tFROM t\r\n\t);", formatted);
	}

	[Fact]
	public void GoSeparatedBatchesEachGetTerminatedWithoutDoublingBlankLines()
	{
		var input = "SELECT 1\nGO\nSELECT 2";
		var formatted = service.FormatForDisplay(input);
		Assert.Equal("SELECT 1;\r\n\r\nGO\r\nSELECT\r\n\t2;", formatted);
	}

	[Fact]
	public void NestedCaseInsideWhenThenKeepsElseAlignedWithSiblingWhen()
	{
		// Regression test: IsInsideCaseBlock scanned backward token-by-token and stopped at the
		// first END it saw, assuming that meant "not inside a CASE". When a nested CASE...END
		// sits entirely inside an earlier WHEN...THEN branch, the nested END is not a statement
		// boundary - it's already-closed content the scan must skip past to find the *true*
		// enclosing CASE. Without that, the outer ELSE was mistaken for an IF-statement ELSE
		// (dedented to column 0, with its value dropped to a new line) instead of a CASE ELSE.
		var sql = "SELECT\n\tCASE\n\t\tWHEN a = 1 THEN\n\t\tCASE\n\t\t\tWHEN b = 1 THEN 'x'\n\t\t\tELSE 'y'\n\t\tEND\n\t\tELSE 'z'\n\tEND AS Result\nFROM t";
		var expected = """
			SELECT
				CASE
					WHEN a = 1 THEN
					CASE
						WHEN b = 1 THEN 'x'
						ELSE 'y'
					END
					ELSE 'z'
				END AS Result
			FROM t;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CountStarAndTableQualifiedStarGetNoSurroundingSpaces()
	{
		// Regression test: '*' shared the same case as the arithmetic operators (+, -, /), which
		// always pads it with a space on each side. COUNT(*) and x.* are wildcard "all columns"
		// markers, not multiplication, and must render with no space around the '*' at all.
		var sql = "SELECT COUNT(*) AS TheCount, x.* FROM t x";
		var expected = """
			SELECT
				COUNT(*) AS TheCount,
				x.*
			FROM t x;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void UnaryMinusImmediatelyAfterOpenParenHugsTheParen()
	{
		// Regression test: '-' shares its case with the binary arithmetic operators, which
		// always get leading/trailing spaces. A unary minus (negative literal) instead hugs
		// whatever number it negates on both sides - the '(' it directly follows here (e.g.
		// CAST(-1.00 * ...), matching every other CAST(...) in practice) and the literal itself,
		// so it never picks up the binary operators' usual spacing.
		var sql = "SELECT CAST(-1.00 * SUM(amt) AS MONEY) FROM t";
		var expected = """
			SELECT CAST(-1.00 * SUM(amt) AS MONEY)
			FROM t;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void UnaryMinusAfterCommaAndComparisonOperatorsHugsTheLiteral()
	{
		// A unary minus can only ever follow '(', ',', or another operator token (=, >, < - also
		// how >=, <=, <> tokenize) - never an identifier/number/closing paren, which would make it
		// binary subtraction instead. "a - 1" (identifier before '-') must keep its normal spacing
		// on both sides; every unary case must hug the literal it negates with no trailing space.
		var sql = "SELECT DATEADD(DAY, -30, GETDATE()), a - 1, c = -5, d >= -1, e <> -1, f < -1, h > -1, i <= -1";
		var expected = """
			SELECT
				DATEADD(DAY, -30, GETDATE()),
				a - 1,
				c = -5,
				d >= -1,
				e <> -1,
				f < -1,
				h > -1,
				i <= -1
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CaseExpressionInWhereClauseIndentsRelativeToWhere()
	{
		// Regression test: a CASE expression used directly as a WHERE/HAVING/ON condition (not
		// inside a SELECT column list, not wrapped in an extra expression paren) used to collapse
		// to column 0 regardless of how deep the enclosing clause actually was, since its indent
		// was computed from raw indentLevel/paren-depth alone with no awareness of where the
		// WHERE clause itself landed. It must now align one level under WHERE, matching how a
		// plain AND/OR condition already does.
		var sql = "SELECT *\nFROM t\nWHERE CASE WHEN a = 1 THEN 1 ELSE 0 END = 1\nAND CASE WHEN b = 1 THEN 1 ELSE 0 END = 1";
		var expected = """
			SELECT
				*
			FROM t
			WHERE
				CASE
					WHEN a = 1 THEN 1
					ELSE 0
				END = 1
				AND
				CASE
					WHEN b = 1 THEN 1
					ELSE 0
				END = 1;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CaseExpressionInNestedJoinOnIndentsRelativeToThatOn()
	{
		// The same indentation-context bug as CaseExpressionInWhereClauseIndentsRelativeToWhere,
		// but for a nested join's ON - the case that originally exposed it. CASE is a peer
		// continuation of the condition alongside AND, so it lands at the same indent AND does -
		// one level under the INNER JOIN's own (already deeper, nested-join) ON - not under the
		// outer statement's base indent.
		var sql = "select *\nfrom a\nleft outer join b\ninner join c on c.id = b.id\nAND CASE WHEN c.flag = 1 THEN 1 ELSE 0 END = 1\non b.a_id = a.id";
		var expected = """
			SELECT
				*
			FROM a
			LEFT OUTER JOIN b
					INNER JOIN c ON c.id = b.id
						AND
						CASE
							WHEN c.flag = 1 THEN 1
							ELSE 0
						END = 1
				ON b.a_id = a.id;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void IfConditionAndIndentsRelativeToCurrentBlockNesting()
	{
		// Regression test: fixing AND/OR's indent to track a WHERE/ON clause's actual depth (for
		// nested joins) must not affect an IF/WHILE condition's AND, which has nothing to do with
		// WHERE/ON tracking and must keep indenting one level under the IF itself, wherever that
		// IF sits in the current BEGIN/END nesting.
		var sql = "IF @a IS NOT NULL\nAND @a > 0\nBEGIN\n\tIF @b IS NOT NULL\n\tAND @b > 0\n\tBEGIN\n\t\tPRINT 'hi';\n\tEND;\nEND;";
		var expected = """
			IF @a IS NOT NULL
				AND @a > 0
			BEGIN
				IF @b IS NOT NULL
					AND @b > 0
				BEGIN
					PRINT 'hi';

				END;

			END;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void ValuesTableConstructorInsideCrossApplyIndentsUnderTheOuterParen()
	{
		// Regression test: the VALUES keyword and its tuple's parens always indented using the
		// bare indentLevel, ignoring any already-active outer expanded paren scope. That's fine
		// for the plain "INSERT INTO t VALUES (...)" form (nothing else is on the parenthesis
		// stack at that point), but a VALUES table constructor nested inside another expanded
		// paren - e.g. CROSS/OUTER APPLY (VALUES (...)) - collapsed to column 0 instead of
		// nesting one level under the APPLY's own paren.
		var sql = "SELECT\n\tv.col1\nFROM t\nCROSS APPLY (VALUES (t.a, t.b)) AS v(col1, col2)";
		var expected = """
			SELECT v.col1
			FROM t
			CROSS APPLY
			(
				VALUES
				(
					t.a,
					t.b
				)
			) AS v(col1, col2);
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void ShortCastAndBuiltInFunctionsStayOnOneLineAndAreCapitalized()
	{
		// CAST(...) - and other built-in functions like DATEADD - must stay on one line when
		// short, and must be capitalized regardless of how they were typed in the source, since
		// they tokenize as plain identifiers (not reserved keywords) and were previously left
		// exactly as typed, including being broken across multiple lines if the source happened
		// to put the argument list on its own line.
		var expected = """
			SELECT CAST(dt.date_id AS DATE) AS DATA_DATE
			FROM DB2.dbo.TimeTable dt
			WHERE dt.date_id > DATEADD(month, - 12, @lastOfPreviousMonth)
				AND dt.date_id <= @lastOfPreviousMonth
				AND dt.IsMonthEnd = 1;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void LongCastExpandsFollowingEstablishedParenthesisPattern()
	{
		// The one exception to "CAST always stays on one line": when the expression exceeds the
		// 75-character threshold, it follows the same vertical-expansion pattern as any other
		// function call (e.g. FormatForDisplay_Expands_Parentheses_Vertically).
		var expected = """
			SELECT
				a,
				CAST(
					dt.SomeVeryLongColumnNameThatPushesThisOverTheLineLengthThreshold AS VARCHAR(200)
				) AS Foo
			FROM t;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void CreateProcedureWithBracketedNameAndNoBeginEndBody()
	{
		// CREATE PROCEDURE with a bracketed multi-part name and no parameters, whose body is a
		// flat sequence of statements directly after AS (no BEGIN/END wrapper) - a common, valid,
		// but distinct style from the BEGIN/END-wrapped procedures covered elsewhere.
		var expected = """
			CREATE OR ALTER PROCEDURE [dbo].[MyProc]
			AS
			SELECT 1;

			SELECT 2;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void BasicMultiColumnUpdateFormattedCorrectly()
	{
		// UPDATE ... SET col1 = v1, col2 = v2 WHERE ... was not covered by any existing test.
		var expected = """
			UPDATE ThingJustLikeTheOtherThingTable
			SET
				STATUS_DESC = 'Paid',
				STATUS_CODE = 4
			WHERE ISNULL(PrincipalBal, 0) = 0;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void UpdateFromJoinWithNolockHintAndNotInSubqueryFormattedCorrectly()
	{
		// UPDATE ... SET ... FROM ... INNER JOIN with an old-style (no WITH) table hint, plus a
		// WHERE ... NOT IN (subquery) whose own FROM also carries a hint glued directly to the
		// table name (no space) - all distinct from the simpler literal-list IN clause tests.
		var expected = """
			UPDATE ThingJustLikeTheOtherThingTable
			SET
				MARK = 1,
				MARK_CODE = 'REMIC'
			FROM ThingJustLikeTheOtherThingTable AD
			INNER JOIN PastStuffJoinTable PMH (NOLOCK) ON PMH.MasterIDNumber = AD.MasterIDNumber
				AND PMH.DATA_DATE = AD.DATA_DATE
			WHERE PMH.RecordTypeID = '6'
				AND AD.InterestedPartyId NOT IN
				(
					SELECT InterestedParty_ID
					FROM InterestedPartyLookup(NOLOCK)
				);
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void MultiCteWithInsertIntoFormattedCorrectly()
	{
		// A multi-CTE WITH clause (two named subqueries chained by comma), each containing its
		// own JOIN/GROUP BY/HAVING, followed by INSERT INTO ... SELECT FROM the second CTE.
		// Regression coverage: a JOIN nested inside a CTE's parenthesized body was losing its
		// indentation entirely (landing at column 0 instead of matching FROM).
		var expected = """
			WITH cteWithBalance AS (
				SELECT
					[h].[MasterIDNumber],
					MAX([TransactionDate]) AS [LastActiveDate]
				FROM DB4.dbo.PastStuffTable h
				INNER JOIN DB4.dbo.TheMainThingTable l ON l.MasterIDNumber = h.MasterIDNumber
				GROUP BY [h].[MasterIDNumber]
				HAVING MAX([TransactionDate]) <= @lastOfPreviousMonth
				), cteWithoutBalance AS (
				SELECT
					[h].[MasterIDNumber],
					MIN([TransactionDate]) AS [FirstNotActiveDate]
				FROM DB4.dbo.PastStuffTable h
				INNER JOIN cteWithBalance u ON u.MasterIDNumber = h.MasterIDNumber
				GROUP BY [h].[MasterIDNumber]
				)
			INSERT INTO #TempB
			SELECT
				[MasterIDNumber],
				[FirstNotActiveDate]
			FROM cteWithoutBalance;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void RowNumberOverPartitionByWithBracketedAliasAssignmentFormattedCorrectly()
	{
		// ROW_NUMBER() OVER (PARTITION BY ... ORDER BY ...) using the "[alias] = expr" column
		// syntax instead of "expr AS [alias]". Regression coverage: ORDER BY inside an OVER(...)
		// window clause was being treated as the enclosing statement's own ORDER BY - closing the
		// select column list early and losing a level of indentation relative to PARTITION BY.
		// ROW_NUMBER() and OVER always break onto separate lines at the same indent, with the
		// window spec's parens forced to expand regardless of length.
		var expected = """
			SELECT
				[lfb].[MasterIDNumber],
				[RowId] = ROW_NUMBER()
				OVER (
					PARTITION BY [lfb].[MasterIDNumber],
					[lfb].[SubCode]
					ORDER BY [lfb].[DataDate] DESC
				)
			FROM DB1.dbo.TheMainThingFeePastStuff lfb;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void SimpleSwitchStyleCaseAsSoleSelectColumnFormattedCorrectly()
	{
		// The "simple"/switch-style CASE (CASE input_expr WHEN value THEN ...), as distinct from
		// the searched CASE (CASE WHEN predicate THEN ...) covered elsewhere. Regression coverage:
		// when this CASE was the sole/first SELECT column, its own END was mistaken for a
		// statement-terminating boundary, which suppressed the select-column-list indent and left
		// CASE (and END) sitting at column 0 instead of matching sibling columns.
		var expected = """
			SELECT
				CASE ad.ThingThatMayOrMayNotBeTrueStatus
					WHEN 'Blerg' THEN 'IsBlerg'
					WHEN 'NotBlerg' THEN 'NotBlerg'
					ELSE 'Unknown'
				END AS ThingThatMayOrMayNotBeTrueStatus
			FROM ThingJustLikeTheOtherThingTable ad;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void ReturnLabelAndRaiserrorAreSeparatedCorrectly()
	{
		// Regression test: RETURN, a GOTO label, and RAISERROR had no dedicated line-break
		// handling, so a newline in the source between them (rather than a space) vanished
		// entirely, gluing tokens together with zero separation (e.g. "OFFRETURN",
		// "@intErrErrorHandler:", ")WITH").
		var expected = """
			SET NOCOUNT OFF;

			RETURN @intErr;

			ErrorHandler:;

			SET NOCOUNT OFF;

			RAISERROR ('%s. Error Number = %d', 11, 1, @chvErrMessage, @intErr) WITH SETERROR;

			GO
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void LeadingCommaAfterCommentStaysIndented()
	{
		// Regression test: in leading-comma style, a single-line comment between a column
		// definition and its following ", nextColumn" left the comma stranded at column 0
		// instead of indented at the column-list level.
		var expected = """
			CREATE TABLE #TempA (
				[MasterIDNumber] VARCHAR(12) PRIMARY KEY CLUSTERED,
				[MonthEndAmountOfMony] MONEY,
				[SomeMoneyThingShortFall] MONEY
				-- Extra data (from AD2.. but ChangeThing is not captured historically)
				,
				[ChangeThing1Date] DATE NULL,
				[ChangeThing2Date] DATE NULL
			);
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void CreateTableColumnsEachOnOwnLine()
	{
		// CREATE TABLE column lists always break one column per line, like CREATE PROCEDURE
		// parameter lists do, rather than packing multiple short columns onto one line.
		var expected = """
			CREATE TABLE #TempA (
				[MasterIDNumber] VARCHAR(12) PRIMARY KEY CLUSTERED,
				[MonthEndAmountOfMony] MONEY,
				[MonthStartAmountOfMony] MONEY,
				[AquiredSomeMoneyThing] MONEY,
				[SomeMoneyThingShortFall] MONEY,
				[ChangeThing1Date] DATE NULL,
				[ChangeThing2Date] DATE NULL,
				[ChangeThing3Date] DATE NULL,
				[ChangeThing4Date] DATE NULL,
				[ChangeThing5Date] DATE NULL,
				[ChangeThing6Date] DATE NULL
			);
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void BasicJoinFormattedCorrectly()
	{
		var expected = """
			SELECT
				a,
				b
			FROM foo f
			INNER JOIN bar b ON f.id = b.foo_id
				AND f.status = 'active';
			""";

		RunFactTest(expected);
	}



	[Fact]
	public void OuterApplyFormattedCorrectly()
	{
		var expected = """
			SELECT
				fb.MasterIDNumber,
				fb.ReviewPmtPlanSubStatus,
			FROM DB3.dbo.LegalThingTable fb
			INNER JOIN #TempA l ON l.MasterIDNumber = fb.MasterIDNumber
			OUTER APPLY
			(
				SELECT
					'A' AS Stud,
					'B' AS TurdBurglar
			) fmt;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void BeginEndBlocksFormatCorrectly()
	{
		var expected = """
			IF(1 = 1)
			BEGIN
				PRINT 'hi';

			END;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void CaseWhenElseEndFormattedCorrectly()
	{
		var expected = """
			CASE
				WHEN a = 1 THEN 'One'
				WHEN a = 2 THEN 'Two'
				ELSE 'Other'
			END AS NumberText
			""";
		RunFactTest(expected);
	}

	[Fact]
	public void CreateObjectIsFormattedCorrectly()
	{
		var expected = """
			CREATE OR ALTER PROCEDURE dbo.MyProcedure
			AS
			BEGIN
				SELECT 1;

			END;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void CreateObjectWithParamsIsFormattedCorrectly()
	{
		var expected = """
			CREATE OR ALTER PROCEDURE dbo.MyProcedure
				@Param1 INT,
				@Param2 NVARCHAR(50) = 'default'
			AS
			BEGIN
				SELECT 1;

			END;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void CreateObjectWithParamsUsingParensIsFormattedCorrectly()
	{
		var expected = """
			CREATE OR ALTER PROCEDURE dbo.MyProcedure(
				@Param1 INT,
				@Param2 NVARCHAR(50) = 'default'
			)
			AS
			BEGIN
				SELECT 1;

			END;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void DeclarationsAreFormattedCorrectly()
	{
		var expected = """
			DECLARE @CurrentStep NVARCHAR(50) = 'INIT';

			DECLARE @CalculatedThreshold DECIMAL(18,4);

			DECLARE @Bastard int = 420,
			    @Fart as VARCHAR(69);
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void FormatForDisplay_Expands_Parentheses_Vertically()
	{
		// read sql text from file "SELECT.sql" in CodeSamples dir
		string expected = """
			SELECT
				@CalculatedThreshold = CONVERT(
					DECIMAL(18, 4),
					COALESCE(
						NULLIF(
							ISNULL(
								TRY_CAST(JSON_VALUE(@JsonPayload, '$.config.threshold') AS NUMERIC(10,2)),
								TYPEPROPERTY(RTRIM(LTRIM('  decimal  ')), 'Precision')
							),
							0
						),
						ABS(CHECKSUM(NEWID()) % 100) * 1.5, FORMAT(GETDATE(), 'yyyyMMdd')
					)
				);
			""";
		RunFactTest(expected);
	}

	[Fact]
	public void FullSuiteRealWorldSample()
	{
		string sql;
		using (var reader = new StreamReader("CodeSamples/FullSuiteRealWorldSample.sql"))
		{
			sql = reader.ReadToEnd();
		}

		var formatted = service.FormatForDisplay(sql);
		Assert.False(string.IsNullOrWhiteSpace(formatted));
		_output.WriteLine("Here is the formatted output for FullSuiteRealWorldSample.sql:");
		_output.WriteLine(formatted);
	}

	[Fact]
	public void ArithmeticExpressionWithNestedCaseIndentsLogically()
	{
		// Regression test for the DaysPastDue expression in FullSuiteRealWorldSample.sql:
		// operator-joined terms that fit within the line-length threshold stay on their own
		// line at the same indent, and each nested paren (including CASE...END, which behaves
		// like an implicit paren for indentation purposes) indents its content one level deeper
		// than the line that opened it, with the matching close aligned back to that opening line.
		var expected = """
			SELECT
				ad.DueDate,
				(
					((DATEPART(YYYY, ad.DATA_DATE) - DATEPART(YYYY, ad.DueDate)) * 360) +
					((DATEPART(MM, ad.DATA_DATE) - DATEPART(MM, ad.DueDate)) * 30) +
					(
						(
							CASE
								WHEN (
									(DATEPART(MM, ad.DATA_DATE) = 2)
									AND (DATEPART(DD, ad.DATA_DATE) IN (28, 29))
								) THEN 30
								WHEN DATEPART(DD, ad.DATA_DATE) >= 30 THEN 30
								ELSE DATEPART(DD, ad.DATA_DATE)
							END
						) -
						(
							CASE
								WHEN DATEPART(DD, ad.DueDate) >= 30 THEN 30
								ELSE DATEPART(DD, ad.DueDate)
							END
						)
					)
				) + 1 AS DaysPastDue,
				NULL AS StatusCode
			FROM ThingJustLikeTheOtherThingDetailDaily ad;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void FullSuiteSynthetic()
	{
		string expected;
		using (var reader = new StreamReader("CodeSamples/FullSuiteSynthetic.sql"))
		{
			expected = reader.ReadToEnd();
		}

		var formatted = RunFactTest(expected);
		_output.WriteLine(formatted);
	}

	[Fact]
	public void IfElseWithBeginBlocksFormatCorrectly()
	{
		var expected = """
			IF(1 = 1)
			BEGIN
				PRINT 'hi';

			END;

			ELSE
			BEGIN
				PRINT 'bye';

			END;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void InsertBasicFormattedCorrectly()
	{
		var expected = """
			INSERT INTO foo (
				a,
				b,
				c
			)
			VALUES
			(
				'a',
				'b',
				'c'
			);
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void InsertValuesListGivesVariablesAndPlainExpressionsTheSameIndent()
	{
		// Regression test: '@'-prefixed variable tokens at the start of a line always added a
		// flat "+1 level" baseline on top of whatever indent the VALUES tuple's own parenthesis
		// scope already contributed, double-counting that level and over-indenting variables one
		// tab deeper than plain expressions (function calls, NULL, literals) in the exact same
		// list. Mirrors a real-world INSERT with a mix of @variables and expressions.
		var sql = "INSERT INTO RECORD_LOG\n(\n\tENTITY_ID,\n\tRESULT_ID,\n\tRECORD_SUBJECT,\n\tMODIFIED_DATE,\n\tMODIFIED_BY_ID\n)\nVALUES\n(\n\t@ENTITY_ID,\n\t@RESULT_ID,\n\tCOALESCE(@RECORD_SUBJECT, ''),\n\tGETDATE(),\n\t@MODIFIED_BY_ID\n)";
		var expected = """
			INSERT INTO RECORD_LOG (
				ENTITY_ID,
				RESULT_ID,
				RECORD_SUBJECT,
				MODIFIED_DATE,
				MODIFIED_BY_ID
			)
			VALUES
			(
				@ENTITY_ID,
				@RESULT_ID,
				COALESCE(@RECORD_SUBJECT, ''),
				GETDATE(),
				@MODIFIED_BY_ID
			);
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void InsertWithSelectFormattedCorrectly()
	{
		var expected = """
			INSERT INTO foo (
				a,
				b,
				c
			)
			SELECT
				a.One,
				a.Two,
				a.Three
			FROM dbo.Blerg a;
			""";
		RunFactTest(expected);
	}

	[Fact]
	public void LeftJoinFormattedCorrectly()
	{
		string sql = """
			SELECT e.FirstName
			FROM Employees e
			LEFT JOIN Departments d ON e.DepartmentID = d.DepartmentID
				AND d.DepartmentName = 'Sales'
			WHERE e.EmployeeID IN (1, 2, 3);
			""";

		RunFactTest(sql);
	}


	[Fact]
	public void LeftOuterJoinFormattedCorrectly()
	{
		string sql = """
			SELECT e.FirstName
			FROM Employees e
			LEFT OUTER JOIN Departments d ON e.DepartmentID = d.DepartmentID
				AND d.DepartmentName = 'Sales'
			WHERE e.EmployeeID IN (1, 2, 3);
			""";

		RunFactTest(sql);
	}

	[Fact]
	public void BareJoinStartsOnOwnLine()
	{
		// A bare JOIN (no INNER/LEFT/RIGHT/OUTER/CROSS/FULL modifier) must start its own
		// line, just like every other join variant already does.
		string sql = """
			SELECT e.FirstName
			FROM Employees e
			JOIN Departments d ON e.DepartmentID = d.DepartmentID
				AND d.DepartmentName = 'Sales'
			WHERE e.EmployeeID IN (1, 2, 3);
			""";

		RunFactTest(sql);
	}

	[Fact]
	public void LongInClauseWrapsWithTrailingCommasAndCorrectIndent()
	{
		// A long IN-list (many constant values) wraps once a line would exceed the length
		// threshold. Two bugs previously affected this: the comma that belongs between the
		// last value on one wrapped line and the first value on the next was dropped entirely
		// (values.Foreach only appended ", " *between* items already accumulated on the current
		// line, never onto the line being flushed), and the values' indent was a flat offset
		// from a counter (indentLevel) that doesn't track CASE/WHEN/nested-parenthesis context -
		// it now derives from the "IN (" line's own actual indent instead (see
		// LongInClauseInsideNestedCaseWhenIndentsRelativeToItsOwnLine for the deeply-nested
		// case that flat offset got wrong).
		//
		// Built with explicit \t/\n escapes rather than this file's usual triple-quoted raw
		// string, since this test specifically asserts on exact tab placement and a raw string
		// literal makes stray-space-vs-tab mistakes invisible.
		var sql = "SELECT * FROM PastStuff WHERE 1 = 1 AND PastStuff.SubCode IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 15, 18, 20, 21, 96, 97, 98, 99, 100, 120, 121, 123, 101, 106, 109, 110, 111, 192, 251, 252, 253, 249, 254, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 235, 236, 237, 238, 239, 240, 241, 242, 243, 17, 25, 244, 245, 246, 247, 213, 214, 215, 216, 217, 218, 219, 220, 211, 212, 189, 210, 248, 209, 208, 207, 206, 205, 204, 203, 185, 183, 182, 181, 180, 178, 177, 176, 174, 173, 172, 171, 170);";

		var expected =
			"SELECT\n" +
			"\t*\n" +
			"FROM PastStuff\n" +
			"WHERE 1 = 1\n" +
			"\tAND PastStuff.SubCode IN (\n" +
			"\t\t0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 15, 18, 20, 21, 96, 97, 98, 99, 100, 120, 121, 123, 101, 106, 109, 110, 111, 192,\n" +
			"\t\t251, 252, 253, 249, 254, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 235, 236, 237, 238, 239, 240,\n" +
			"\t\t241, 242, 243, 17, 25, 244, 245, 246, 247, 213, 214, 215, 216, 217, 218, 219, 220, 211, 212, 189, 210, 248, 209, 208,\n" +
			"\t\t207, 206, 205, 204, 203, 185, 183, 182, 181, 180, 178, 177, 176, 174, 173, 172, 171, 170\n" +
			"\t);";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void LongInClauseKeepsLineCommentsOnTheirOwnLine()
	{
		// A line comment (-- ...) embedded between values in a long IN-list must never be
		// merged with an adjacent value - the previous plain Split(',') treated a comment plus
		// everything up to the next comma as a single "value", silently swallowing the real
		// value's own comma and gluing unrelated text together.
		var sql = "SELECT * FROM PastStuff WHERE 1 = 1 AND PastStuff.SubCode IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 15, 18, 20, 21, 96, 97, 98, 99, 100, 120, 121, 123, 101, 106, 109, 110, 111, 192, 251, 252, 253, 249, 254, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 235, 236, 237, 238, 239, 240, 241, 242, 243, 17, 25, 244, 245, 246, 247, 213, 214, 215, 216, 217, 218, 219, 220, 211, 212, 189, 210, 248, 209, 208, 207, 206, 205, 204, 203, 185, 183, 182, 181, 180, 178, 177, 176, 174, 173, 172\n" +
			"-- Added 172 mapping here 2020-08-05 Jdudejp TASC127392 (per Matt..)\n" +
			", 171\n" +
			"-- Added 171 mapping here 2021-05-28 Jdudejp TASC133635\n" +
			", 170);";

		var expected =
			"SELECT\n" +
			"\t*\n" +
			"FROM PastStuff\n" +
			"WHERE 1 = 1\n" +
			"\tAND PastStuff.SubCode IN (\n" +
			"\t\t0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 15, 18, 20, 21, 96, 97, 98, 99, 100, 120, 121, 123, 101, 106, 109, 110, 111, 192,\n" +
			"\t\t251, 252, 253, 249, 254, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 235, 236, 237, 238, 239, 240,\n" +
			"\t\t241, 242, 243, 17, 25, 244, 245, 246, 247, 213, 214, 215, 216, 217, 218, 219, 220, 211, 212, 189, 210, 248, 209, 208,\n" +
			"\t\t207, 206, 205, 204, 203, 185, 183, 182, 181, 180, 178, 177, 176, 174, 173, 172,\n" +
			"\t\t-- Added 172 mapping here 2020-08-05 Jdudejp TASC127392 (per Matt..)\n" +
			"\t\t171,\n" +
			"\t\t-- Added 171 mapping here 2021-05-28 Jdudejp TASC133635\n" +
			"\t\t170\n" +
			"\t);";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void LongInClauseInsideNestedCaseWhenIndentsRelativeToItsOwnLine()
	{
		// The regression this guards against: with a flat "indentLevel + N" offset, an IN clause
		// buried inside a CASE/WHEN's boolean expression (AND/OR, nested parens) came out
		// under-indented relative to its own "IN (" line, because indentLevel doesn't track that
		// nesting the same way the text actually on the page does. The fix reads the indent
		// directly off the "IN (" line itself, so values always land one tab deeper than
		// wherever that line actually ended up - 3 tabs here, one more than "AND PastStuff.SubCode
		// IN ("'s own 2.
		var sql = "SELECT FEES_AMT = CASE WHEN (PastStuff.SomeCode IN (250, 251)) OR (PastStuff.SomeCode = 330 AND PastStuff.SubCode IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 15, 18, 20, 21, 96, 97, 98, 99, 100, 120, 121, 123, 101, 106, 109, 110, 111, 192, 251, 252, 253, 249, 254, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 235, 236, 237, 238, 239, 240, 241, 242, 243, 17, 25, 244, 245, 246, 247, 213, 214, 215, 216, 217, 218, 219, 220, 211, 212, 189, 210, 248, 209, 208, 207, 206, 205, 204, 203, 185, 183, 182, 181, 180, 178, 177, 176, 174, 173, 172, 171, 170)) THEN 1 END FROM PastStuff;";

		var expected =
			"SELECT FEES_AMT =\n" +
			"CASE\n" +
			"\tWHEN (PastStuff.SomeCode IN (250, 251))\n" +
			"\t\tOR (\n" +
			"\t\tPastStuff.SomeCode = 330\n" +
			"\t\tAND PastStuff.SubCode IN (\n" +
			"\t\t\t0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 15, 18, 20, 21, 96, 97, 98, 99, 100, 120, 121, 123, 101, 106, 109, 110, 111, 192,\n" +
			"\t\t\t251, 252, 253, 249, 254, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 235, 236, 237, 238, 239, 240,\n" +
			"\t\t\t241, 242, 243, 17, 25, 244, 245, 246, 247, 213, 214, 215, 216, 217, 218, 219, 220, 211, 212, 189, 210, 248, 209, 208,\n" +
			"\t\t\t207, 206, 205, 204, 203, 185, 183, 182, 181, 180, 178, 177, 176, 174, 173, 172, 171, 170\n" +
			"\t\t)\n" +
			"\t\t) THEN 1\n" +
			"END\n" +
			"FROM PastStuff;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void TokenAfterInClauseEndingInACommentStartsItsOwnLine()
	{
		// The regression this guards against: when the last thing before a long IN clause's
		// closing paren is a comment, the main loop's lineStart was left true from processing
		// that comment - true because a comment always ends its own line. But
		// FormatInClauseMultiline rebuilds the whole clause from scratch and always ends by
		// appending ")" with no trailing newline, and nothing resynced lineStart to match. Every
		// token handler afterward trusted that stale true and indented on top of the same line
		// instead of starting a new one, so the OR right after the wrapping parenthesis's own
		// close ended up glued to the same line with a pile of stray tabs between them instead
		// of on its own line.
		var sql = "SELECT FEES_AMT = CASE WHEN (PastStuff.SomeCode = 1) OR (PastStuff.SomeCode = 330 AND PastStuff.SubCode IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 15, 18, 20, 21, 96, 97, 98, 99, 100, 120, 121, 123, 101, 106, 109, 110, 111, 192, 251, 252, 253, 249, 254, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 235, 236, 237, 238, 239, 240, 241, 242, 243, 17, 25, 244, 245, 246, 247, 213, 214, 215, 216, 217, 218, 219, 220, 211, 212, 189, 210, 248, 209, 208, 207, 206, 205, 204, 203, 185, 183, 182, 181, 180, 178, 177, 176, 174, 173, 172\n" +
			"-- trailing comment before close\n" +
			")) OR (PastStuff.SomeCode = 2) THEN 1 END FROM PastStuff;";

		var expected =
			"SELECT FEES_AMT =\n" +
			"CASE\n" +
			"\tWHEN (PastStuff.SomeCode = 1)\n" +
			"\t\tOR (\n" +
			"\t\tPastStuff.SomeCode = 330\n" +
			"\t\tAND PastStuff.SubCode IN (\n" +
			"\t\t\t0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 15, 18, 20, 21, 96, 97, 98, 99, 100, 120, 121, 123, 101, 106, 109, 110, 111, 192,\n" +
			"\t\t\t251, 252, 253, 249, 254, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 235, 236, 237, 238, 239, 240,\n" +
			"\t\t\t241, 242, 243, 17, 25, 244, 245, 246, 247, 213, 214, 215, 216, 217, 218, 219, 220, 211, 212, 189, 210, 248, 209, 208,\n" +
			"\t\t\t207, 206, 205, 204, 203, 185, 183, 182, 181, 180, 178, 177, 176, 174, 173, 172\n" +
			"\t\t\t-- trailing comment before close\n" +
			"\t\t)\n" +
			"\t\t)\n" +
			"\t\tOR (PastStuff.SomeCode = 2) THEN 1\n" +
			"END\n" +
			"FROM PastStuff;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CommentBetweenInAndItsOpeningParenIsNotTornApartAsFakeValues()
	{
		// Regression test: inClauseStartIndex used to snapshot result.Length right at the IN
		// token, before a comment sitting between IN and its real "(" was even rendered - so
		// FormatInClauseMultiline's inClauseContent captured the comment's own rendered text too.
		// Its IndexOf('(') then found the "(" INSIDE the comment (this comment's text itself looks
		// like an old IN list) instead of the real one, and its comma-splitter tore the rest of the
		// comment apart as if it were real values - fabricating string literal, comma, and paren
		// tokens that were never in the source and failing the round-trip safety check outright.
		var sql =
			"SELECT LoanID\n" +
			"FROM SampleTable\n" +
			"WHERE Department  in -- ('OldGroup1', 'OldGroup2')\n" +
			"\t\t('Group1', 'Group2', 'Group3');";

		var expected =
			"SELECT LoanID\n" +
			"FROM SampleTable\n" +
			"WHERE Department IN\n" +
			"-- ('OldGroup1', 'OldGroup2')\n" +
			"('Group1', 'Group2', 'Group3');";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void LeadingCommaSeparatedFromValueByMultipleCommentsSkipsMultilineFormatting()
	{
		// Regression test: the round-trip check's own tolerance (see the IsReorderableConnector
		// branch in SignificantTokenSequencesMatch) only accepts moving a comma past exactly one
		// adjacent comment - the normal amount of reordering FormatInClauseMultiline's
		// leading-comma-to-trailing-comma normalization produces. This value list deliberately
		// keeps each removed candidate value grouped with the comment(s) explaining why it was
		// removed, so the real comma for the next value arrives after several comment-only
		// lines - moving it up next to the previous value would require reordering past more
		// comments than that tolerance covers, and previously failed the round-trip check
		// outright (SafetyCheckPassed = false, whole object fell back to unformatted text).
		// HasCommentBetweenValueAndItsComma now detects this shape up front and skips the
		// special-cased multiline renderer for just this clause, falling through to the generic
		// per-token path, which preserves the source's own leading-comma layout and can't reorder
		// anything - safe, if less polished, output instead of no formatting at all.
		var sql =
			"SELECT LoanID\n" +
			"FROM SampleTable\n" +
			"WHERE CATEGORY_TYPE_ID IN\n" +
			"(\n" +
			"\t-- ** The last two are the only ones they want, although the others were requested\n" +
			"\t-- ** at various times.\n" +
			"\t11\t  -- BANKRUPTCY  PAYMENT (readded TASC105548)\n" +
			"\t--113\tBANKRUPTCY\n" +
			"\t--1945\tBankruptcy Reconciliation\n" +
			"\t--1988\tBK Proof of Claim\n" +
			"\t--\n" +
			"\t, 1988 -- bk proof of claim\n" +
			"\t, 1945 -- BK Recon\n" +
			"\t);";

		var expected =
			"SELECT LoanID\n" +
			"FROM SampleTable\n" +
			"WHERE CATEGORY_TYPE_ID IN (\n" +
			"\t-- ** The last two are the only ones they want, although the others were requested\n" +
			"\t-- ** at various times.\n" +
			"\t11\n" +
			"\t-- BANKRUPTCY  PAYMENT (readded TASC105548)\n" +
			"\t--113\tBANKRUPTCY\n" +
			"\t--1945\tBankruptcy Reconciliation\n" +
			"\t--1988\tBK Proof of Claim\n" +
			"\t--\n" +
			"\t, 1988\n" +
			"\t-- bk proof of claim\n" +
			"\t, 1945\n" +
			"\t-- BK Recon\n" +
			");";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void BracketedInClauseValueContainingACommaIsNotSplitApart()
	{
		// Regression test: SplitInClauseSegments (used for both a regular WHERE ... IN (...) and
		// a PIVOT's "FOR x IN (...)" column list, since both go through the same IN-token
		// detection) originally re-scanned already-rendered text character by character, tracking
		// string literals and nested parens to avoid splitting on a comma inside either - but not
		// bracket-quoted identifiers, which, unlike a regular identifier, can legally contain
		// almost any character, including a literal comma (a real PIVOT column name here). It has
		// since been rewritten to split on the real token stream's TSqlTokenType.Comma tokens
		// instead (see SplitInClauseSegments), which sidesteps this whole class of bug - a comma
		// inside a QuotedIdentifier or StringLiteral token's own Text was never a separate Comma
		// token to begin with, regardless of what quoting character surrounds it.
		var sql =
			"SELECT *\n" +
			"FROM data\n" +
			"PIVOT\n" +
			"(\n" +
			"\tMIN(Value)\n" +
			"\tFOR TypeName IN\n" +
			"\t(\n" +
			"\t\t[Group A:First Category], [Group B:Second Category, With Comma Inside], [Group C:Third]\n" +
			"\t)\n" +
			") AS pvt;";

		var expected =
			"SELECT\n" +
			"\t*\n" +
			"FROM data PIVOT (\n" +
			"\tMIN(Value) FOR TypeName IN (\n" +
			"\t\t[Group A:First Category], [Group B:Second Category, With Comma Inside], [Group C:Third])) AS pvt;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void DoubleQuotedInClauseValueContainingACommaIsNotSplitApart()
	{
		// Companion to BracketedInClauseValueContainingACommaIsNotSplitApart: under
		// QUOTED_IDENTIFIER ON (which both real objects this was found against explicitly set),
		// a double-quoted identifier is just as legal a PIVOT column name as a bracketed one, and
		// can just as legally contain a literal comma. The old text-scanning splitter tracked
		// '\'' string literals and (after the bracket fix) '[' identifiers but never '"' -
		// splitting this the same way brackets used to. The token-based rewrite fixes this too,
		// for free, since it never inspects a QuotedIdentifier token's characters at all.
		var sql =
			"SELECT *\n" +
			"FROM data\n" +
			"PIVOT\n" +
			"(\n" +
			"\tMIN(Value)\n" +
			"\tFOR TypeName IN\n" +
			"\t(\n" +
			"\t\t\"Group A:First Category\", \"Group B:Second Category, With Comma Inside\", \"Group C:Third\"\n" +
			"\t)\n" +
			") AS pvt;";

		var expected =
			"SELECT\n" +
			"\t*\n" +
			"FROM data PIVOT (\n" +
			"\tMIN(Value) FOR TypeName IN (\n" +
			"\t\t\"Group A:First Category\", \"Group B:Second Category, With Comma Inside\", \"Group C:Third\")) AS pvt;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void ThenIndentsCorrectlyWhenPrecededByACommentAfterAMultilineWhen()
	{
		// THEN normally continues on the same line as its WHEN condition. When the WHEN
		// condition is long enough to wrap onto multiple lines and a comment sits right before
		// THEN (a comment always ends with a newline), THEN previously landed at column 0 with
		// no indent at all, and the token right after it wrongly absorbed the indent that should
		// have belonged to THEN instead - because AppendSpaceIfNeeded, unlike
		// AppendIndentIfNeeded, is a no-op at the start of a line and never clears lineStart.
		var sql = "SELECT FEES_AMT = CASE WHEN PastStuff.SomeCode = 330 AND (ISNULL(InterestedPartytypename, '') <> '3rd Party' OR ParentInterestedPartyID IN ('7074', 'F00043')) -- Prior Servicer, LegalThing, and Arrearage Late Charges for TPS only.  - mdudemd 8/16/2017\nTHEN CONVERT(VARCHAR(13), PastStuff.TransactionAmt) END FROM PastStuff;";

		var expected =
			"SELECT FEES_AMT =\n" +
			"CASE\n" +
			"\tWHEN PastStuff.SomeCode = 330\n" +
			"\t\tAND (\n" +
			"\t\tISNULL(InterestedPartytypename, '') <> '3rd Party'\n" +
			"\t\tOR ParentInterestedPartyID IN ('7074', 'F00043')\n" +
			"\t\t)\n" +
			"\t-- Prior Servicer, LegalThing, and Arrearage Late Charges for TPS only.  - mdudemd 8/16/2017\n" +
			"\tTHEN CONVERT(VARCHAR(13), PastStuff.TransactionAmt)\n" +
			"END\n" +
			"FROM PastStuff;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void LeadingCommentsBeforeUpdateDoNotCollapseWholeStatement()
	{
		// Regression test: comment-blind text heuristics (ShouldUseExpressionFallback and the
		// naive FormatExpressionFallback renderer it can route into) used to decide whether to
		// use a fast-path fallback by regex-collapsing all whitespace - including the newline
		// that terminates a leading "--" comment - before checking whether the result started
		// with a disallowed keyword like UPDATE. A statement preceded by "--" comment lines
		// collapsed to text starting with "--" instead of "UPDATE", slipped past that check, and
		// got rendered by a renderer with zero comment handling - turning the ENTIRE statement
		// into one dead, fully-commented-out line.
		var sql =
			"        --INNER JOIN #TableA [l] ON l.[RecordID] = res.[RecordID]; -- join is slow (quicker to dump whole view into tempdb)\n" +
			"        --Update the closed reason...\n" +
			"        UPDATE c\n" +
			"        SET\n" +
			"            [c].[ClosedReason] = [po].[Reason1]\n" +
			"        FROM #TableB [c]\n" +
			"                        INNER JOIN #TableA [l] ON [l].[RecordID] = [c].[RecordID]\n" +
			"        -- changed to DB1.dbo.TableC not ThingJustLikeTheOtherThing_detail (shouldn't this be as of data_date?)\n" +
			"                        INNER JOIN DB1.dbo.TableC [ad] ON [ad].[RecordID] = [c].[RecordID]\n" +
			"                            AND ad.DATA_DATE = @lastOfPreviousMonth\n" +
			"                        INNER JOIN db1.dbo.TableD sh ON sh.RecordID = ad.RecordID\n" +
			"                            AND sh.DATA_DATE = ad.DATA_DATE\n" +
			"                        LEFT JOIN [DB1]..[TableE] [M] ON [M].[RecordID] = [ad].[RecordID]\n" +
			"                        LEFT JOIN #TableF [res] ON [res].[RecordID] = [c].[RecordID]\n" +
			"                        LEFT JOIN #TableG rms ON rms.RecordId = c.RecordID\n" +
			"                            AND rms.RowNum = 1\n" +
			"                        LEFT JOIN DB2.dbo.TableH attr1 ON attr1.EntityId = c.RecordID\n" +
			"                            AND attr1.AttributeTypeID = @attributeTypeId\n" +
			"                        OUTER APPLY\n" +
			"        (\n" +
			"            SELECT [Reason1] = 12\n" +
			"        )";

		var expected =
			"--INNER JOIN #TableA [l] ON l.[RecordID] = res.[RecordID]; -- join is slow (quicker to dump whole view into tempdb)\n" +
			"--Update the closed reason...\n" +
			"UPDATE c\n" +
			"SET\n" +
			"\t[c].[ClosedReason] = [po].[Reason1]\n" +
			"FROM #TableB [c]\n" +
			"INNER JOIN #TableA [l] ON [l].[RecordID] = [c].[RecordID]\n" +
			"-- changed to DB1.dbo.TableC not ThingJustLikeTheOtherThing_detail (shouldn't this be as of data_date?)\n" +
			"INNER JOIN DB1.dbo.TableC [ad] ON [ad].[RecordID] = [c].[RecordID]\n" +
			"\tAND ad.DATA_DATE = @lastOfPreviousMonth\n" +
			"INNER JOIN db1.dbo.TableD sh ON sh.RecordID = ad.RecordID\n" +
			"\tAND sh.DATA_DATE = ad.DATA_DATE\n" +
			"LEFT JOIN [DB1]..[TableE] [M] ON [M].[RecordID] = [ad].[RecordID]\n" +
			"LEFT JOIN #TableF [res] ON [res].[RecordID] = [c].[RecordID]\n" +
			"LEFT JOIN #TableG rms ON rms.RecordId = c.RecordID\n" +
			"\tAND rms.RowNum = 1\n" +
			"LEFT JOIN DB2.dbo.TableH attr1 ON attr1.EntityId = c.RecordID\n" +
			"\tAND attr1.AttributeTypeID = @attributeTypeId\n" +
			"OUTER APPLY\n" +
			"(\n" +
			"\tSELECT\n" +
			"\t\t[Reason1] = 12\n" +
			")";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CommentBetweenJoinModifierAndJoinKeywordKeepsNormalIndentation()
	{
		// Regression test: NextNonWhitespaceIndex/PreviousNonWhitespaceIndex used to skip only
		// whitespace tokens, not comments, so a comment sitting between a JOIN modifier (LEFT,
		// INNER, etc.) and the JOIN keyword itself defeated the "is this JOIN preceded by a
		// modifier" adjacency check on both sides - the modifier was rendered as if it weren't a
		// join at all, and JOIN was then misclassified as an independent bare join, which (when
		// another join frame was still open) got spuriously treated as nested and over-indented.
		var sql =
			"SELECT *\n" +
			"FROM A a\n" +
			"LEFT\n" +
			"-- note\n" +
			"JOIN B b ON b.Id = a.Id\n" +
			"INNER JOIN C c ON c.Id = a.Id;";

		var expected =
			"SELECT\n" +
			"\t*\n" +
			"FROM A a\n" +
			"LEFT\n" +
			"-- note\n" +
			"JOIN B b ON b.Id = a.Id\n" +
			"INNER JOIN C c ON c.Id = a.Id;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CommentBetweenCompleteJoinClausesKeepsIndentationFlat()
	{
		// Regression test mirroring the reported repro: a chain of INNER JOIN/LEFT JOIN clauses
		// with a "--" comment sitting between two complete JOIN...ON clauses must not throw off
		// indentation for the JOINs that follow the comment.
		var sql =
			"SELECT *\n" +
			"FROM A a\n" +
			"INNER JOIN B b ON b.Id = a.Id\n" +
			"-- switched to C per ticket 123\n" +
			"INNER JOIN C c ON c.Id = a.Id\n" +
			"LEFT JOIN D d ON d.Id = a.Id;";

		var expected =
			"SELECT\n" +
			"\t*\n" +
			"FROM A a\n" +
			"INNER JOIN B b ON b.Id = a.Id\n" +
			"-- switched to C per ticket 123\n" +
			"INNER JOIN C c ON c.Id = a.Id\n" +
			"LEFT JOIN D d ON d.Id = a.Id;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void TriggerOnClauseGetsSpaceWhenSourceHasNone()
	{
		// ScriptDom accepts "ON[dbo].[Table]" with zero source whitespace - ON is a reserved
		// word, so the brackets alone are enough to lex it as a separate token - but there's no
		// WhiteSpace token there for the normal whitespace-handling logic to turn into a space.
		var sql =
			"CREATE OR ALTER TRIGGER [dbo].[test_trigger]\n" +
			"ON[dbo].[TestTable]\n" +
			"INSTEAD OF UPDATE\n" +
			"AS\n" +
			"PRINT 1;";

		var expected =
			"CREATE OR ALTER TRIGGER [dbo].[test_trigger]\n" +
			"ON [dbo].[TestTable]\n" +
			"INSTEAD OF UPDATE\n" +
			"AS\n" +
			"PRINT 1;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void AlterTableDisableTriggerAllDoesNotCorruptLaterStatements()
	{
		// Regression test: TRIGGER isn't exclusive to CREATE/ALTER TRIGGER - it also appears in
		// "ALTER TABLE ... {ENABLE|DISABLE} TRIGGER ALL/<name>", where it doesn't name a
		// create-able object. The TRIGGER case used to unconditionally set afterCreateObjectName,
		// which then stayed stuck true for the rest of the batch (it only clears on a matching "("
		// or "AS") - corrupting spacing in unrelated statements far downstream. Here it glued
		// DECLARE directly onto its @variable and TOP's row count directly onto the following
		// @variable, in a statement with no direct connection to the ALTER TABLE line at all.
		var sql =
			"CREATE OR ALTER PROCEDURE dbo.usp_SampleProc\n" +
			"\t@fromLoanID varchar(10)\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tALTER TABLE SampleAudit disable TRIGGER ALL;\n\n" +
			"\tdeclare @srelDate datetime;\n" +
			"\tSELECT top 1 @srelDate = ISNULL(a.ActiveDate, '9999-12-31')\n" +
			"\tFROM dbo.SampleTable a\n" +
			"\tWHERE a.StatusID = 58\n" +
			"END";

		var expected =
			"CREATE OR ALTER PROCEDURE dbo.usp_SampleProc\n" +
			"\t@fromLoanID varchar(10)\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tALTER TABLE SampleAudit disable TRIGGER ALL;\n\n" +
			"\tDECLARE @srelDate datetime;\n\n" +
			"\tSELECT\n" +
			"\t\tTOP 1 @srelDate = ISNULL(a.ActiveDate, '9999-12-31')\n" +
			"\tFROM dbo.SampleTable a\n" +
			"\tWHERE a.StatusID = 58;\n\n" +
			"END;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void MultiStatementTvfReturnsTableClauseFormatsLikeCreateTable()
	{
		// Regression test: RETURNS has no TSqlTokenType of its own (lexed as a plain Identifier,
		// like TRY/CATCH/INSTEAD elsewhere in this file) and TABLE in "RETURNS @var TABLE (...)"
		// reuses the same Table token type as CREATE TABLE. With no dedicated handling, RETURNS
		// fell through to the generic identifier path and inherited the real parameter list's
		// still-true (deliberately, for AS's sake) inCreateStatementParams flag as if it were a
		// continuation of that list - over-indenting RETURNS one level and, since "@" is a valid
		// non-leading identifier character in T-SQL, gluing "RETURNS" directly onto "@RETVAL"
		// with zero separator merged them into a single token, changing the significant token
		// sequence and failing the round-trip safety check outright (not just a display quirk).
		var sql =
			"CREATE OR ALTER FUNCTION [dbo].[udt_SampleAssetStatuses](\n" +
			"\t@dtTargetDate  datetime\n" +
			")\n" +
			"RETURNS  \t@RETVAL TABLE\n" +
			"\t( \t[ASSET_ID] [int] NOT NULL\n" +
			"\t\t,[STATUS_ID] [integer] NOT NULL\n" +
			"\t)\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tINSERT @RETVAL\n" +
			"\tSELECT ASSET_ID, STATUS_ID\n" +
			"\tFROM SampleAssetTable\n" +
			"\tWHERE TargetDate = @dtTargetDate;\n" +
			"\tRETURN;\n" +
			"END";

		var expected =
			"CREATE OR ALTER FUNCTION [dbo].[udt_SampleAssetStatuses](\n" +
			"\t@dtTargetDate datetime\n" +
			")\n" +
			"RETURNS @RETVAL TABLE (\n" +
			"\t[ASSET_ID] [int] NOT NULL,\n" +
			"\t[STATUS_ID] [integer] NOT NULL\n" +
			")\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tINSERT @RETVAL\n" +
			"\tSELECT\n" +
			"\t\tASSET_ID,\n" +
			"\t\tSTATUS_ID\n" +
			"\tFROM SampleAssetTable\n" +
			"\tWHERE TargetDate = @dtTargetDate;\n\n" +
			"\tRETURN;\n\n" +
			"END;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void DeclareTableVariableColumnListFormatsLikeCreateTable()
	{
		// Companion to MultiStatementTvfReturnsTableClauseFormatsLikeCreateTable: a local
		// "DECLARE @x TABLE (...)" variable also has "@x" immediately before the Table token, so
		// the fix that recognizes "RETURNS @var TABLE (...)" must not be specific to RETURNS -
		// it needs to key off the preceding Variable token generally.
		var sql =
			"CREATE OR ALTER PROCEDURE dbo.usp_SampleProc\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tDECLARE @MyTableVar TABLE (Col1 int, Col2 varchar(10));\n" +
			"\tINSERT @MyTableVar SELECT 1, 'x';\n" +
			"END";

		var expected =
			"CREATE OR ALTER PROCEDURE dbo.usp_SampleProc\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tDECLARE @MyTableVar TABLE (\n" +
			"\t\tCol1 int,\n" +
			"\t\tCol2 varchar(10)\n" +
			"\t);\n\n" +
			"\tINSERT @MyTableVar\n" +
			"\tSELECT\n" +
			"\t\t1,\n" +
			"\t\t'x';\n\n" +
			"END;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void EmptyCreateObjectParameterListStaysOnOneLine()
	{
		// Regression test: a zero-parameter CREATE FUNCTION/PROC's "()" used to always get split
		// onto two lines ("(\n)") because the opening paren's afterCreateObjectName handling
		// unconditionally appended a newline after "(", with no check for whether there was
		// actually anything to list before the matching ")".
		var sql =
			"CREATE FUNCTION dbo.fSampleFunc ()\n" +
			"RETURNS int\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tRETURN 1;\n" +
			"END";

		var expected =
			"CREATE FUNCTION dbo.fSampleFunc ()\n" +
			"RETURNS int AS\n" +
			"BEGIN\n" +
			"\tRETURN 1;\n\n" +
			"END;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void OuterApplyDoesNotLeakStuckIndentIntoLaterStatements()
	{
		// Regression test: CROSS JOIN/CROSS APPLY and OUTER APPLY are the only JOIN-modifier
		// shapes that are never followed by an ON clause - only OUTER JOIN has one. The
		// OUTER-modifier branch passed expectsOnClause: true for OUTER APPLY too (as if it were
		// OUTER JOIN), which pushes a join frame - normally popped when its ON is seen. Since
		// OUTER APPLY has no ON, that frame was never popped: BeginJoinClause's frame stack (and
		// therefore every later JOIN's indent, since it factors in how many frames are still
		// open) stayed one level deeper than it should for the rest of the batch, well past the
		// statement the OUTER APPLY was even in.
		var sql =
			"CREATE OR ALTER PROCEDURE dbo.usp_SampleProc\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tSELECT ad.LoanID\n" +
			"\tFROM TableA ad\n" +
			"\tOUTER APPLY\n" +
			"\t(\n" +
			"\t\tSELECT 1 AS X\n" +
			"\t) dataChk;\n\n" +
			"\tUPDATE c\n" +
			"\tSET c.Col1 = po.Col2\n" +
			"\tFROM TableB c\n" +
			"\t\t\tINNER JOIN TableC l ON l.LoanID = c.LoanID\n" +
			"\t\t\tOUTER APPLY\n" +
			"\t(\n" +
			"\t\tSELECT 1 AS Col2\n" +
			"\t) AS po;\n" +
			"END";

		var expected =
			"CREATE OR ALTER PROCEDURE dbo.usp_SampleProc\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tSELECT ad.LoanID\n" +
			"\tFROM TableA ad\n" +
			"\tOUTER APPLY\n" +
			"\t(\n" +
			"\t\tSELECT 1 AS X\n" +
			"\t) dataChk;\n\n" +
			"\tUPDATE c\n" +
			"\tSET\n" +
			"\t\tc.Col1 = po.Col2\n" +
			"\tFROM TableB c\n" +
			"\tINNER JOIN TableC l ON l.LoanID = c.LoanID\n" +
			"\tOUTER APPLY\n" +
			"\t(\n" +
			"\t\tSELECT 1 AS Col2\n" +
			"\t) AS po;\n\n" +
			"END;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void OnClauseKeepsSpaceBeforeLeftUsedAsFunctionCall()
	{
		// Regression test: LEFT/RIGHT/INNER/OUTER/CROSS/FULL are reserved words regardless of
		// context, so ScriptDom hands back the same TSqlTokenType whether LEFT is a JOIN modifier
		// or the LEFT() string function. StartsOnNewLine(Left) makes the WhiteSpace case defer to
		// LEFT's own case for spacing (correct for the "LEFT JOIN" case, where that case starts a
		// fresh line and indents) - but when LEFT appears as a function call mid-condition (here,
		// right after "ON "), lineStart is already false and the non-modifier fallthrough used to
		// call only AppendIndentIfNeeded, a no-op once already mid-line, gluing "ON" directly onto
		// "LEFT(" with zero separator.
		var sql =
			"UPDATE TableA\n" +
			"SET TableA.ModifiedDate = SYSDATETIMEOFFSET()\n" +
			"FROM TableA (nolock)\n" +
			"Left Outer Join TableB (nolock)\n" +
			"On Left(TableA.Code,5) = TableB.Code\n" +
			"Where TableA.Code Is Not Null;";

		var expected =
			"UPDATE TableA\n" +
			"SET\n" +
			"\tTableA.ModifiedDate = SYSDATETIMEOFFSET()\n" +
			"FROM TableA (nolock)\n" +
			"LEFT OUTER JOIN TableB (nolock) ON LEFT(TableA.Code, 5) = TableB.Code\n" +
			"WHERE TableA.Code IS NOT NULL;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void FunctionCallImmediatelyInsideAnotherFunctionCallStaysGlued()
	{
		// Companion to OnClauseKeepsSpaceBeforeLeftUsedAsFunctionCall above: the fix there must
		// not add a space whenever lineStart happens to be false - only when the source actually
		// had whitespace before LEFT/RIGHT/etc. "UPPER(LEFT(..." has none (a function call's
		// argument list always hugs the opening paren), so no space belongs there either.
		var sql = "SELECT UPPER(LEFT(x, 3)) FROM t;";

		var expected = "SELECT UPPER(LEFT(x, 3))\nFROM t;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void InsteadOfTriggerClauseIsUppercasedAndJoinedOntoOneLine()
	{
		// INSTEAD has no TSqlTokenType of its own (lexed as a plain Identifier, like TRY/CATCH/
		// FINALLY), and OF is easy to lose track of case-wise since it's not in the formatter's
		// general keyword list - both need explicit handling, not just IsKeyword. The whole
		// clause must land on one line regardless of how the source broke it up.
		var sql =
			"CREATE OR ALTER TRIGGER [dbo].[test_trigger]\n" +
			"ON [dbo].[TestTable]\n" +
			"instead\n" +
			"of\n" +
			"UPDATE\n" +
			"AS\n" +
			"PRINT 1;";

		var expected =
			"CREATE OR ALTER TRIGGER [dbo].[test_trigger]\n" +
			"ON [dbo].[TestTable]\n" +
			"INSTEAD OF UPDATE\n" +
			"AS\n" +
			"PRINT 1;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void InsteadOfTriggerClauseHandlesMultipleOperationsRegardlessOfSourceLayout()
	{
		// Covers the multi-operation shapes explicitly called out when this was reported: comma
		// placement, casing, and line breaks in the source must not affect the single-line,
		// comma-space-separated output.
		var sql =
			"CREATE OR ALTER TRIGGER [dbo].[test_trigger]\n" +
			"ON [dbo].[TestTable]\n" +
			"instead\n" +
			"of\n" +
			"UPDATE,\n" +
			"delete\n" +
			",INSERT\n" +
			"AS\n" +
			"PRINT 1;";

		var expected =
			"CREATE OR ALTER TRIGGER [dbo].[test_trigger]\n" +
			"ON [dbo].[TestTable]\n" +
			"INSTEAD OF UPDATE, DELETE, INSERT\n" +
			"AS\n" +
			"PRINT 1;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void ChainedJoinsWithBetweenClauseFormatCorrectly()
	{
		// Regression test for the #ItemList join chain in FullSuiteRealWorldSample.sql:
		// every JOIN (bare or modified) starts its own line with the joined table and first ON
		// condition kept together, and a short BETWEEN ... AND ... stays on one line rather than
		// being split just because a JOIN clause follows it.
		var expected = """
			SELECT
				*
			FROM #ItemList ll
			JOIN DB1.dbo.ThingJustLikeTheOtherThingDetailDaily ad ON ll.MasterIDNumber = ad.MasterIDNumber
				AND ad.Data_Date BETWEEN @backTo AND @thru
			JOIN DB2.dbo.TimeTable dt ON dt.date_id = ad.DATA_DATE
				AND dt.IsMonthEnd = 1;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void ShortBetweenInsideCaseWhenDoesNotWrapBeforeThen()
	{
		// The regression this guards against: deciding whether a BETWEEN's upper bound is "too
		// long to keep on this line" measured forward from that value until IsClauseBoundaryToken
		// (AND/OR/FROM/WHERE/JOIN/etc.) - which doesn't include THEN, WHEN, ELSE, END, CASE, or
		// commas. Inside a CASE WHEN, that ran straight through ") THEN 1" and beyond, inflating
		// the measured "length" of a two-character constant enough to wrongly trigger a line
		// break immediately before it - "79" would end up alone on its own line, indented, for
		// no reason a reader could see.
		var sql = "SELECT FEES_AMT = CASE WHEN (PastStuff.SomeCode = 330 AND PastStuff.SubCode BETWEEN 66 AND 79) THEN 1 ELSE 2 END FROM PastStuff;";

		var expected =
			"SELECT FEES_AMT =\n" +
			"CASE\n" +
			"\tWHEN (PastStuff.SomeCode = 330\n" +
			"\t\tAND PastStuff.SubCode BETWEEN 66 AND 79) THEN 1\n" +
			"\tELSE 2\n" +
			"END\n" +
			"FROM PastStuff;";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void LongBetweenSplitLines()
	{
		var expected = """
			SELECT
				1
			WHERE a BETWEEN 10 AND
				CONVERT(
					DECIMAL(18, 4),
					COALESCE(
						NULLIF(
							ISNULL(
								TRY_CAST(JSON_VALUE(@JsonPayload, '$.config.threshold') AS NUMERIC(10,2)),
								TYPEPROPERTY(RTRIM(LTRIM('  decimal  ')), 'Precision')
							),
							0
						),
						ABS(CHECKSUM(NEWID()) % 100) * 1.5, FORMAT(GETDATE(), 'yyyyMMdd')
					)
				);
			""";
		RunFactTest(expected);
	}

	[Fact]
	public void LongerNestedFunctionsAreBrokenApart()
	{
		// The input is deliberately left without a trailing semicolon: ShouldKeepSelectInline
		// treats any comma-free top-level SELECT projection immediately followed by a semicolon
		// as eligible to stay on one line (a pre-existing, narrower-than-intended heuristic meant
		// for trivial single-value selects like "SELECT 1;"), so an already-terminated version of
		// this multi-line nested-function expression would not round-trip through this same
		// method - it would collapse onto one line instead of staying broken apart.
		var sql = """
			SELECT
				COALESCE(
					ISNULL(CAST(a AS VARCHAR(10)), 'N/A'), FORMAT(a.DateSold, 'yyyy-MM-dd'),
					MAX(CAST(a AS VARCHAR(20)))
				)
			""";
		var expected = """
			SELECT
				COALESCE(
					ISNULL(CAST(a AS VARCHAR(10)), 'N/A'), FORMAT(a.DateSold, 'yyyy-MM-dd'),
					MAX(CAST(a AS VARCHAR(20)))
				);
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void MultiColumnSelectFormattedCorrectly()
	{
		var expected = """
			SELECT
				a,
				b,
				c
			FROM foo;
			""";
		RunFactTest(expected);
	}

	[Fact]
	public void MultiPartWhereClauseSplitsLines()
	{
		var expected = """
			SELECT
				1
			FROM sometable
			WHERE a = 69
				and b = 420;
			""";
		RunFactTest(expected);
	}

	[Fact]
	public void NestedFunkHellWithAlias()
	{
		// See the comment on LongerNestedFunctionsAreBrokenApart: the input is deliberately left
		// without a trailing semicolon so ShouldKeepSelectInline's "single comma-free projection
		// immediately followed by ;" heuristic doesn't collapse this back onto one line.
		string sql = """
			SELECT
				COALESCE(
					NULLIF(
						RTRIM(
							LTRIM(
								ISNULL(UPPER(FORMAT(b.UpdatedDate, 'yyyy-MM-dd HH:mm:ss')), 'NOT_MODIFIED')
							)
						),
						''
					),
					UPPER(LEFT(ISNULL(f.FooName, 'UNKNOWN_FOO'), 3)), 'DEFAULT_FALLBACK'
				) AS ComplexStringExpression
			""";
		string expected = """
			SELECT
				COALESCE(
					NULLIF(
						RTRIM(
							LTRIM(
								ISNULL(UPPER(FORMAT(b.UpdatedDate, 'yyyy-MM-dd HH:mm:ss')), 'NOT_MODIFIED')
							)
						),
						''
					),
					UPPER(LEFT(ISNULL(f.FooName, 'UNKNOWN_FOO'), 3)), 'DEFAULT_FALLBACK'
				) AS ComplexStringExpression;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void ShortBetweenKeptOnSameLine()
	{
		var expected = """
			SELECT
				1
			WHERE a BETWEEN 10 AND 20
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void ShortNestedFunctionsRemainOnOneLine()
	{
		var expected = """
			IF(ISNULL(CAST(a AS VARCHAR(10)), 'N/A') = '')
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void SimpleAssignmentSelectFormattedCorrectly()
	{
		var expected = """
			SELECT @foo = 3
			FROM foo;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void SingleColumnSelectFormattedCorrectly()
	{
		var expected = """
			SELECT a
			FROM foo;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void GotoAfterMultiLineStringConcatStaysOnOwnLine()
	{
		var expected = """
			IF @intErr <> 0
			BEGIN
				SET @chvErrMessage = 'ERROR: Stored Procedure usp_SampleGenericProcedureNameXX ' +
					'failed at: Update. Correct the problem and Rerun.';

				GOTO ErrorHandler;

			END;
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void TryCatchFinallyBlocksFormatCorrectly()
	{
		var expected = """
			BEGIN TRY
				SELECT 1 / 0; -- This will cause a divide by zero error
			END TRY
			BEGIN CATCH
				RAISERROR('An error occurred: %s', 1, 1, ERROR_MESSAGE());
			END CATCH
			BEGIN FINALLY
				PRINT 'Execution completed.';
			END
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void WhereClauseFormattedCorrectly()
	{
		var expected = """
			SELECT
				1
			FROM sometable
			WHERE a = 3;
			""";

		RunFactTest(expected);
	}

	private static bool ContainsSqlSingleLineComment(string line)
	{
		var inStringLiteral = false;
		for (var i = 0; i < line.Length - 1; i++)
		{
			if (line[i] == '\'')
			{
				if (inStringLiteral && i + 1 < line.Length && line[i + 1] == '\'')
				{
					i++;
					continue;
				}

				inStringLiteral = !inStringLiteral;
				continue;
			}

			if (!inStringLiteral && line[i] == '-' && line[i + 1] == '-')
			{
				return true;
			}
		}

		return false;
	}

	private static string DescribeChar(string value, int index)
	{
		if (index < 0)
		{
			return "<none>";
		}

		if (index >= value.Length)
		{
			return "<end of line>";
		}

		var c = value[index];
		return c switch
		{
			' ' => "' ' (space)",
			'\t' => "'\\t' (tab)",
			'\r' => "'\\r' (carriage return)",
			'\n' => "'\\n' (line feed)",
			_ => $"'{c}' (U+{(int)c:X4})"
		};
	}

	private static int FirstDiffIndex(string left, string right)
	{
		var max = Math.Max(left.Length, right.Length);
		for (var i = 0; i < max; i++)
		{
			var leftChar = i < left.Length ? left[i] : '\0';
			var rightChar = i < right.Length ? right[i] : '\0';
			if (leftChar != rightChar)
			{
				return i;
			}
		}

		return -1;
	}

	private static string NormalizeExpectedForComparison(string expected)
	{
		var normalizedLines = expected
			.Replace("\r\n", "\n")
			.Split('\n')
			.Select(line => line.TrimEnd());

		return string.Join(Environment.NewLine, normalizedLines);
	}

	private static string VisualizeWhitespace(string value)
	{
		var sb = new StringBuilder(value.Length);
		foreach (var c in value)
		{
			sb.Append(c switch
			{
				' ' => '·',
				'\t' => '⇥',
				'\r' => '␍',
				'\n' => '␊',
				_ => c
			});
		}

		return sb.ToString();
	}

	// IsRoundTripSafe is the backstop that keeps a future rendering bug from silently handing
	// back corrupted SQL (see the whole-query-collapse and JOIN-gluing bugs this was written
	// after) - these tests pin both halves of that contract: legitimate formatter behavior
	// (case normalization, an inserted terminator, comma/comment repositioning) must never trip
	// it, and real content loss/mangling must always trip it.

	[Theory]
	[InlineData("SELECT 1", "SELECT\n\t1", "whitespace-only reformatting")]
	[InlineData("SELECT 1", "SELECT 1;", "a missing statement terminator gets added")]
	[InlineData("select a from t where 1 = 1", "SELECT a\nFROM t\nWHERE 1 = 1", "keywords get uppercased")]
	[InlineData("select isnull(x, 0)", "SELECT\n\tISNULL(x, 0)", "a recognized built-in function name gets uppercased")]
	public void RoundTripSafeForLegitimateFormatterChanges(string original, string formatted, string because)
	{
		Assert.True(SqlCanonicalizationService.IsRoundTripSafe(original, formatted), because);
	}

	[Fact]
	public void RoundTripSafeWhenLeadingCommaBecomesTrailingCommaAcrossAComment()
	{
		// The exact transformation LongInClauseKeepsLineCommentsOnTheirOwnLine relies on: the
		// comma and the comment next to it swap textual order, but no value or comment is lost.
		var original = "SELECT * FROM t WHERE x IN (1\n-- note\n, 2)";
		var formatted = "SELECT\n\t*\nFROM t\nWHERE x IN (\n\t1,\n\t-- note\n\t2\n)";

		Assert.True(SqlCanonicalizationService.IsRoundTripSafe(original, formatted));
	}

	[Theory]
	[InlineData("SELECT 1 -- keep this comment", "SELECT 1", "a trailing comment is dropped")]
	[InlineData("SELECT a, b FROM t", "SELECT a FROM t", "a real column is dropped")]
	[InlineData("SELECT a FROM t", "SELECT b FROM t", "an identifier's text is changed")]
	[InlineData("SELECT * FROM A LEFT\n-- note\nJOIN B ON B.Id = A.Id", "SELECT * FROM A LEFT\n-- note\nJOINB ON B.Id = A.Id", "two tokens get glued into one (JOIN + B -> JOINB)")]
	[InlineData("SELECT 1; SELECT 2", "SELECT 1", "a whole trailing statement is dropped")]
	public void RoundTripUnsafeWhenContentIsLostOrChanged(string original, string formatted, string because)
	{
		Assert.False(SqlCanonicalizationService.IsRoundTripSafe(original, formatted), because);
	}

	[Fact]
	public void RoundTripMismatchOffsetPointsAtTheActualDivergence()
	{
		// The whole reason to report offsets at all: a caller (ScriptDatabasesDocumentViewModel)
		// needs to show just the relevant few lines, not a whole (possibly huge) object script -
		// so the offset must land at the real point of divergence, not just "somewhere".
		var original = "SELECT a FROM t WHERE x = 1";
		var formatted = "SELECT b FROM t WHERE x = 1";

		Assert.True(SqlCanonicalizationService.TryFindRoundTripMismatch(original, formatted, out var originalOffset, out var formattedOffset));
		Assert.Equal("a", original.Substring(originalOffset, 1));
		Assert.Equal("b", formatted.Substring(formattedOffset, 1));
	}

	[Fact]
	public void ExtractContextSnippetReturnsOnlyNearbyLinesNotTheWholeText()
	{
		// This is the actual point of the snippet mechanism: a warning must show a handful of
		// lines around the problem, not the entire (possibly hundreds-of-lines) object script.
		var lines = Enumerable.Range(1, 200).Select(n => $"line{n}").ToArray();
		var text = string.Join('\n', lines);
		var offsetOfLine100 = string.Join('\n', lines[..99]).Length + 1;

		var snippet = SqlCanonicalizationService.ExtractContextSnippet(text, offsetOfLine100, contextLines: 5);

		Assert.Contains("line100", snippet);
		Assert.Contains("line95", snippet);
		Assert.Contains("line105", snippet);
		Assert.DoesNotContain("line1\n", snippet);
		Assert.DoesNotContain("line200", snippet);
		Assert.True(snippet.Length < text.Length / 4, "snippet should be a small fraction of the full text");
	}

	[Fact]
	public void ExtractContextSnippetReturnsWholeTextWhenItAlreadyFitsInTheWindow()
	{
		var text = "line1\nline2\nline3";

		var snippet = SqlCanonicalizationService.ExtractContextSnippet(text, offset: 6, contextLines: 5);

		Assert.Equal(text, snippet);
	}

	[Fact]
	public void RoundTripSafeWhenMultilineCommentUsesPlainLfInternally()
	{
		// Regression test for a real false positive: TrimTrailingWhitespaceTrackingOffsets
		// normalizes every line ending in the *output* to Environment.NewLine (\r\n on Windows).
		// Whitespace *between* tokens is excluded from the round-trip comparison so that's
		// invisible there, but a multi-line "/* ... */" comment's own internal line breaks are
		// part of its token text, not separate whitespace tokens - so a comment authored with
		// plain \n internally (as this input is, deliberately, regardless of the environment
		// running the test) used to look like changed content and reject an otherwise-correct
		// reformat.
		var sql = "/*----\n     Object: Foo\n----*/\nCREATE OR ALTER PROCEDURE [dbo].[Foo]\nAS\nSELECT 1";

		var result = service.FormatForDisplayWithPositions(sql);

		Assert.True(result.SafetyCheckPassed);
	}

	[Fact]
	public void RoundTripSafeWhenCommentLinesHaveTrailingWhitespace()
	{
		// Regression test for a second real false positive found on the same object as the \n
		// one above: TrimTrailingWhitespaceTrackingOffsets also strips trailing whitespace from
		// every line of the *output*, which - same as the line-ending case - is invisible for
		// whitespace *between* tokens but is a real change to a multi-line comment's own text
		// (each of its internal lines is part of the token). A comment authored with trailing
		// spaces on some lines (e.g. copy-pasted from an editor that didn't strip them) used to
		// look like changed content and reject an otherwise-correct reformat.
		var sql = "/*****************\n  Auth: AB \n  Description: foo. \n*****************/\nCREATE OR ALTER PROCEDURE [dbo].[Foo]\nAS\nSELECT 1";

		var result = service.FormatForDisplayWithPositions(sql);

		Assert.True(result.SafetyCheckPassed);
	}

	[Fact]
	public void RoundTripUnsafeWhenMultilineStringLiteralLosesTrailingWhitespace()
	{
		// The trailing-whitespace leniency above is deliberately NOT extended to string literals:
		// trailing whitespace inside a multi-line string constant is part of its actual value, so
		// losing it would be a real correctness bug in the formatter, not a cosmetic one - this
		// must still be caught. Simulated directly, exercising the comparison rule on its own -
		// see RendererPreservesTrailingWhitespaceInsideMultilineStringLiteral below for proof the
		// renderer itself no longer produces this in the first place.
		var withTrailingSpace = "SELECT 'line one \nline two'";
		var withoutTrailingSpace = "SELECT 'line one\nline two'";

		Assert.False(SqlCanonicalizationService.IsRoundTripSafe(withTrailingSpace, withoutTrailingSpace));
	}

	[Fact]
	public void RendererPreservesTrailingWhitespaceInsideMultilineStringLiteral()
	{
		// The actual fix: TrimTrailingWhitespaceTrackingOffsets used to be a blind pass over the
		// whole rendered output with no token awareness, so trailing whitespace before an embedded
		// newline inside a multi-line string literal got silently stripped - changing the
		// literal's actual value, not just its formatting. BuildProtectedTokenMask now re-tokenizes
		// the output and protects comment/string-literal/quoted-identifier spans from that pass
		// entirely. Asserting the exact text (not just SafetyCheckPassed) is the point here - this
		// proves the renderer no longer produces the bad output, rather than just proving the
		// safety net would have caught it if it had.
		var sql = "SELECT 'first line   \nsecond line' AS x";

		var result = service.FormatForDisplayWithPositions(sql);

		Assert.True(result.SafetyCheckPassed);
		Assert.Contains("'first line   \nsecond line'", result.Text);
	}

	[Fact]
	public void RendererPreservesTrailingWhitespaceInsideMultilineComment()
	{
		// Same fix, comment case - previously handled by making the round-trip check tolerant of
		// this specific difference (see RoundTripSafeWhenCommentLinesHaveTrailingWhitespace); now
		// the renderer itself never produces the difference in the first place, so the comment's
		// original trailing whitespace survives byte-for-byte.
		var sql = "/*****\n  Auth: AB \n*****/\nCREATE OR ALTER PROCEDURE [dbo].[Foo]\nAS\nSELECT 1";

		var result = service.FormatForDisplayWithPositions(sql);

		Assert.True(result.SafetyCheckPassed);
		Assert.Contains("  Auth: AB \n", result.Text);
	}

	[Fact]
	public void ExistingSemicolonAfterTrailingCommentStaysOnItsOwnLine()
	{
		// Regression test: the Semicolon case deliberately glues onto the preceding content (via
		// TrimTrailingLineEndings) so e.g. a closing paren ending a subquery gets ");" instead of
		// ")\n;" - but doing that unconditionally, when the preceding content was actually a "--"
		// comment, put the semicolon inside the comment's own extent instead, silently commenting
		// the terminator itself out (e.g. "--OPTION (MAXDOP 8)\n;" became "--OPTION (MAXDOP 8);").
		var sql =
			"CREATE OR ALTER PROCEDURE dbo.Foo\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tSELECT 1\n" +
			"\t-- removed this hint: found it was faster without it\n" +
			"\t--OPTION (MAXDOP 8)\n" +
			"\t;\n" +
			"END\n" +
			"GO";

		var result = service.FormatForDisplayWithPositions(sql);

		Assert.True(result.SafetyCheckPassed);
		Assert.DoesNotContain("--OPTION (MAXDOP 8);", result.Text);
	}

	[Fact]
	public void InjectedSemicolonAfterTrailingCommentStaysOnItsOwnLine()
	{
		// Same fix, the other call site: statementEndIndices (from ScriptDom's own
		// TSqlStatement.LastTokenIndex) can point AT a trailing comment when no semicolon exists
		// in the source at all - the injection path has the identical "glue via
		// TrimTrailingLineEndings" bug for the same reason.
		var sql =
			"CREATE OR ALTER PROCEDURE dbo.Foo\n" +
			"AS\n" +
			"BEGIN\n" +
			"\tSELECT 1\n" +
			"\t-- trailing note, no semicolon in source at all\n" +
			"END\n" +
			"GO";

		var result = service.FormatForDisplayWithPositions(sql);

		Assert.True(result.SafetyCheckPassed);
		Assert.DoesNotContain("in source at all;", result.Text);
	}

	[Fact]
	public void SubqueryInsideInClauseIsNotTreatedAsAValueList()
	{
		// Regression test: ShouldFormatInClauseMultiline/FormatInClauseMultiline are built only
		// for a comma-separated value list ("IN (1, 2, 3)") - they don't know anything about
		// "IN (SELECT ...)". A long/multi-line subquery (this one has embedded "--" comments,
		// which push it well past the value-list length threshold) used to get its WHERE/FROM
		// clauses run through the value-list splitter anyway, whenever the closing paren's own
		// length-based check fired before the already-correct dedicated subquery handling got a
		// chance to run - fabricating commas like "DB1..TableB c (NOLOCK)," and "WHERE,"
		// that don't exist anywhere in the source.
		var sql =
			"SELECT t.SomeColumn\n" +
			"FROM #TableA t\n" +
			"WHERE RecordID IN (SELECT c.RecordID\n" +
			"FROM DB1..TableB c (NOLOCK)\n" +
			"-- JOIN DB2..TableC dc ON c.RecordID = dc.RecordID\n" +
			"WHERE\n" +
			"-- dc.SomeFlag = 1 and\n" +
			"c.SomeOtherFlag = 1);";

		var result = service.FormatForDisplayWithPositions(sql);

		Assert.True(result.SafetyCheckPassed);
		Assert.DoesNotContain("(NOLOCK),", result.Text);
		Assert.DoesNotContain("WHERE,", result.Text);
	}

	[Fact]
	public void GrantWithMultiplePermissionsFormatsAsTwoLines()
	{
		// Regression test: GRANT's permission list reuses the exact same keyword tokens
		// (SELECT/INSERT/UPDATE) that real DML statements use, and those tokens' own cases used to
		// force query-shaped newlines and comma-splitting onto it - producing "GRANT\nINSERT,\n
		// SELECT\n    ,\nUPDATE ON ..." instead of the two clean lines below.
		var sql = "GRANT INSERT, SELECT, UPDATE ON [dbo].[A] TO [public];";
		var expected = """
			GRANT INSERT, SELECT, UPDATE
			ON [dbo].[A] TO [public];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void DenyWithSinglePermissionFormatsAsTwoLines()
	{
		var sql = "DENY SELECT ON [dbo].[A] TO [public];";
		var expected = """
			DENY SELECT
			ON [dbo].[A] TO [public];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void GrantWithNoOnClauseBreaksBeforeTo()
	{
		// A database/server-level permission (e.g. CREATE TABLE) has no securable object, so there
		// is no ON clause - TO itself starts the second line instead.
		var sql = "GRANT CREATE TABLE TO [user1];";
		var expected = """
			GRANT CREATE TABLE
			TO [user1];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void GrantWithColumnListKeepsColumnsInlineOnFirstLine()
	{
		var sql = "GRANT SELECT (Col1, Col2) ON [dbo].[A] TO [public];";
		var expected = """
			GRANT SELECT (Col1, Col2)
			ON [dbo].[A] TO [public];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void GrantWithMultiplePrincipalsStaysInlineOnSecondLine()
	{
		var sql = "GRANT SELECT ON [dbo].[A] TO [user1], [user2], [role1];";
		var expected = """
			GRANT SELECT
			ON [dbo].[A] TO [user1], [user2], [role1];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void GrantWithGrantOptionAndGrantorStaysInlineOnSecondLine()
	{
		var sql = "GRANT SELECT ON [dbo].[A] TO [public] WITH GRANT OPTION AS [dbo];";
		var expected = """
			GRANT SELECT
			ON [dbo].[A] TO [public] WITH GRANT OPTION AS [dbo];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void RevokeWithGrantOptionForAndCascadeFormatsAsTwoLines()
	{
		// REVOKE's own extra syntax: a "GRANT OPTION FOR" prefix (still part of the first line, not
		// a mid-statement WITH GRANT OPTION) and a trailing CASCADE/AS grantor on the second line,
		// using FROM instead of TO to introduce the principal.
		var sql = "REVOKE GRANT OPTION FOR SELECT ON [dbo].[A] FROM [public] CASCADE AS [dbo];";
		var expected = """
			REVOKE GRANT OPTION FOR SELECT
			ON [dbo].[A] FROM [public] CASCADE AS [dbo];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void RevokeWithNoOnClauseBreaksBeforeFrom()
	{
		var sql = "REVOKE CREATE TABLE FROM [user1];";
		var expected = """
			REVOKE CREATE TABLE
			FROM [user1];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void GrantExecuteOnProcedureFormatsAsTwoLines()
	{
		// EXECUTE has its own case elsewhere (for real EXEC calls, setting inExecParams) that must
		// not fire for "GRANT EXECUTE ON ...".
		var sql = "GRANT EXECUTE ON [dbo].[Proc1] TO [public];";
		var expected = """
			GRANT EXECUTE
			ON [dbo].[Proc1] TO [public];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void GrantAllPrivilegesFormatsAsTwoLines()
	{
		var sql = "GRANT ALL PRIVILEGES ON [dbo].[A] TO [public];";
		var expected = """
			GRANT ALL PRIVILEGES
			ON [dbo].[A] TO [public];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void GrantLowercaseKeywordsAreCanonicalizedToUppercase()
	{
		var sql = "grant insert, select on [dbo].[a] to [public];";
		var expected = """
			GRANT INSERT, SELECT
			ON [dbo].[a] TO [public];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void GrantWithLongPermissionListWrapsOnePerLine()
	{
		// Long enough to cross LongExpressionLineBreakThreshold - each permission after the first
		// gets its own line instead of running past the width other long lists wrap at.
		var sql = "GRANT SELECT, INSERT, UPDATE, DELETE, REFERENCES, EXECUTE, CONTROL, VIEW DEFINITION ON [dbo].[SomeVeryLongObjectNameHere] TO [SomeVeryLongPrincipalNameHere];";
		var expected = """
			GRANT SELECT, INSERT, UPDATE, DELETE, REFERENCES, EXECUTE, CONTROL,
				VIEW DEFINITION
			ON [dbo].[SomeVeryLongObjectNameHere] TO [SomeVeryLongPrincipalNameHere];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void MultipleGrantStatementsInOneBatchEachFormatIndependently()
	{
		var sql = "GRANT INSERT ON [dbo].[A] TO [public]; GRANT SELECT ON [dbo].[B] TO [user1];";
		var expected = """
			GRANT INSERT
			ON [dbo].[A] TO [public];

			GRANT SELECT
			ON [dbo].[B] TO [user1];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void AlterTableSwitchToIsUnaffectedByGrantToHandling()
	{
		// TO has no dedicated case outside a GRANT/DENY/REVOKE statement - this must keep working
		// exactly as before (single line, no forced break) once TO gets its own case for GRANT's sake.
		var sql = "ALTER TABLE dbo.t1 SWITCH TO dbo.t2;";
		var expected = """
			ALTER TABLE dbo.t1 SWITCH TO dbo.t2;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CreateProcedureWithExecuteAsAndExternalNameFormatsAsDistinctClauses()
	{
		// Regression test: EXECUTE inside "WITH EXECUTE AS ..." was being treated like a real EXEC
		// statement call (forcing its own line, flipping inExecParams), and the formatter had no
		// way to tell "EXECUTE AS <caller-spec>"'s own AS apart from the procedure's real
		// body-starting AS - the first AS it saw after the parameter list always won, silently
		// consuming the wrong one and leaving the second AS (and everything after it) to fall
		// through generic keyword handling. Produced "@a nvarchar(4000) WITH\nEXECUTE\nAS\nCALLER
		// AS \tEXTERNAL NAME ..." - WITH glued to the parameter list, and the two AS/EXTERNAL NAME
		// pieces scrambled across lines with a stray injected tab.
		var sql = "CREATE OR ALTER PROCEDURE [dbo].[InsertA] @a nvarchar(4000) WITH EXECUTE AS CALLER AS EXTERNAL NAME [SqlClrTest].[SqlClrTest.StoredProcedures].[InsertA];";
		var expected = """
			CREATE OR ALTER PROCEDURE [dbo].[InsertA]
				@a nvarchar(4000)
			WITH EXECUTE
				AS CALLER
			AS
			EXTERNAL NAME [SqlClrTest].[SqlClrTest.StoredProcedures].[InsertA];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CreateProcedureWithParenthesizedParamsAndExecuteAsFormatsSameAsBareParams()
	{
		var sql = "CREATE OR ALTER PROCEDURE [dbo].[InsertA] (@a nvarchar(4000)) WITH EXECUTE AS CALLER AS EXTERNAL NAME [SqlClrTest].[SqlClrTest.StoredProcedures].[InsertA];";
		var expected = """
			CREATE OR ALTER PROCEDURE [dbo].[InsertA] (
				@a nvarchar(4000)
			)
			WITH EXECUTE
				AS CALLER
			AS
			EXTERNAL NAME [SqlClrTest].[SqlClrTest.StoredProcedures].[InsertA];
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CreateProcedureWithMultipleWithOptionsKeepsExecuteAsOnSameLine()
	{
		var sql = "CREATE PROCEDURE dbo.P1 @a int WITH RECOMPILE, EXECUTE AS CALLER AS BEGIN SELECT 1; END;";
		var expected = """
			CREATE PROCEDURE dbo.P1
				@a int
			WITH RECOMPILE, EXECUTE
				AS CALLER
			AS
			BEGIN
				SELECT 1;

			END;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CreateProcedureWithPlainWithOptionNoExecuteAsBreaksBeforeAs()
	{
		// No EXECUTE AS clause at all - only WITH's own gluing-to-the-parameter-list needed
		// fixing here; the (already-correct) plain AS/BEGIN relationship is unchanged.
		var sql = "CREATE PROCEDURE dbo.P1 @a int WITH ENCRYPTION AS BEGIN SELECT 1; END;";
		var expected = """
			CREATE PROCEDURE dbo.P1
				@a int
			WITH ENCRYPTION
			AS
			BEGIN
				SELECT 1;

			END;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CreateFunctionWithReturnsAndExecuteAsFormatsCorrectly()
	{
		// RETURNS's own handling clears inCreateStatementParams for unrelated reasons (see its own
		// comment) - WITH must still recognize the options clause after that happens.
		var sql = "CREATE FUNCTION dbo.F1(@a int) RETURNS int WITH EXECUTE AS CALLER AS EXTERNAL NAME asm.cls.Method;";
		var expected = """
			CREATE FUNCTION dbo.F1(
				@a int
			)
			RETURNS int
			WITH EXECUTE
				AS CALLER
			AS
			EXTERNAL NAME asm.cls.Method;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CreateFunctionWithReturnsAndNoWithClauseKeepsAsGluedToReturns()
	{
		// Regression test: this must NOT be affected by the WITH/EXECUTE AS fix above - a bare
		// "RETURNS <type> AS" with no WITH clause at all has always kept AS glued to RETURNS's
		// type rather than breaking onto its own line.
		var sql = "CREATE FUNCTION dbo.fSampleFunc () RETURNS int AS BEGIN RETURN 1; END";
		var expected = """
			CREATE FUNCTION dbo.fSampleFunc ()
			RETURNS int AS
			BEGIN
				RETURN 1;

			END;
			""";

		RunFactTest(sql, expected);
	}

	private string NormalizeWhitespace(string input)
	{
		// Preserve comments and original SQL structure exactly; only normalize line endings.
		return input.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
	}

	private string RunFactTest(string expected)
	{
		var normalizedExpected = NormalizeExpectedForComparison(expected);
		var sql = NormalizeWhitespace(expected);
		var formatted = service.FormatForDisplay(sql);
		WriteStringDiff(normalizedExpected, formatted);
		Assert.Equal(normalizedExpected, formatted);
		return formatted;
	}

	private string RunFactTest(string sqlInput, string expected)
	{
		var normalizedExpected = NormalizeExpectedForComparison(expected);
		var sql = NormalizeWhitespace(sqlInput);
		var formatted = service.FormatForDisplay(sql);
		WriteStringDiff(normalizedExpected, formatted);
		Assert.Equal(normalizedExpected, formatted);
		return formatted;
	}

	private string RunFactTest(string sqlInput, string expected, bool openingParenOnNewLine)
	{
		var normalizedExpected = NormalizeExpectedForComparison(expected);
		var sql = NormalizeWhitespace(sqlInput);
		var formatted = service.FormatForDisplay(sql, openingParenOnNewLine);
		WriteStringDiff(normalizedExpected, formatted);
		Assert.Equal(normalizedExpected, formatted);
		return formatted;
	}

	private void WriteStringDiff(string expected, string actual)
	{
		_output.WriteLine($"Expected length: {expected.Length}, Actual length: {actual.Length}");

		var expectedLines = expected.Replace("\r\n", "\n").Split('\n');
		var actualLines = actual.Replace("\r\n", "\n").Split('\n');
		var maxLines = Math.Max(expectedLines.Length, actualLines.Length);

		var hasDifferences = false;

		for (var i = 0; i < maxLines; i++)
		{
			var expectedLine = i < expectedLines.Length ? expectedLines[i] : string.Empty;
			var actualLine = i < actualLines.Length ? actualLines[i] : string.Empty;

			if (expectedLine == actualLine)
			{
				continue;
			}

			hasDifferences = true;

			_output.WriteLine($"Line {i + 1} differs:");
			_output.WriteLine($"  Expected({expectedLine.Length}): |{VisualizeWhitespace(expectedLine)}|");
			_output.WriteLine($"  Actual  ({actualLine.Length}): |{VisualizeWhitespace(actualLine)}|");

			var firstDiffIndex = FirstDiffIndex(expectedLine, actualLine);
			_output.WriteLine($"  First difference at column {firstDiffIndex + 1}: expected {DescribeChar(expectedLine, firstDiffIndex)}, actual {DescribeChar(actualLine, firstDiffIndex)}");
		}

		var firstGlobalDiff = FirstDiffIndex(expected, actual);
		if (firstGlobalDiff >= 0)
		{
			_output.WriteLine($"First global difference at character {firstGlobalDiff + 1}: expected {DescribeChar(expected, firstGlobalDiff)}, actual {DescribeChar(actual, firstGlobalDiff)}");
		}
		else
		{
			_output.WriteLine("No character-level differences detected.");
		}

		if(hasDifferences)
		{
			_output.WriteLine("*** Expected:");
			_output.WriteLine(expected);
			_output.WriteLine("*** Actual:");
			_output.WriteLine(actual);
		}
	}
}
