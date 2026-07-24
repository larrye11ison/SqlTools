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
		var sql = "INSERT INTO #dateRangeLastTwelve\nSELECT CAST(dt.date_id AS DATE) AS DATA_DATE\nFROM OperationalDatamart.dbo.D_Time dt";
		var expected = """
			INSERT INTO #dateRangeLastTwelve
			SELECT CAST(dt.date_id AS DATE) AS DATA_DATE
			FROM OperationalDatamart.dbo.D_Time dt;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void InsertIntoSelectWithRowNumberOverDoesNotBreakEmptyParens()
	{
		// Same pendingInsertColumnList leak as InsertIntoSelectWithoutColumnListDoesNotCorruptLaterParentheses,
		// but for ROW_NUMBER()'s empty argument list specifically: the stray "(" of ROW_NUMBER()
		// was mistaken for the insert column list opener, splitting "()" across four lines.
		var sql = "INSERT INTO #dilDates\nSELECT\n\t[l].[LoanID],\n\tROW_NUMBER() OVER (PARTITION BY l.LoanID ORDER BY [lmd].[DeedInLieuDate] DESC) AS RowNum\nFROM LossMitDatesBase lmd";
		var expected = """
			INSERT INTO #dilDates
			SELECT
				[l].[LoanID],
				ROW_NUMBER()
				OVER (
					PARTITION BY l.LoanID
					ORDER BY [lmd].[DeedInLieuDate] DESC
				) AS RowNum
			FROM LossMitDatesBase lmd;
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void CreateTableAfterPrecedingInsertIntoSelectKeepsDatatypeAndPrimaryKeyGlued()
	{
		// Same pendingInsertColumnList leak: a stuck flag from an earlier INSERT INTO ... SELECT
		// (no column list) survived across statement boundaries and corrupted an unrelated,
		// later CREATE TABLE's VARCHAR(10) column-length parens.
		var sql = "INSERT INTO #dateRangeLastTwelve\nSELECT CAST(dt.date_id AS DATE) AS DATA_DATE\nFROM OperationalDatamart.dbo.D_Time dt\n\nCREATE TABLE #closedLoans (\n\t[LoanID] VARCHAR(10) PRIMARY KEY CLUSTERED,\n\t[LoanClosedDate] DATETIME\n)";
		var expected = """
			INSERT INTO #dateRangeLastTwelve
			SELECT CAST(dt.date_id AS DATE) AS DATA_DATE
			FROM OperationalDatamart.dbo.D_Time dt;

			CREATE TABLE #closedLoans (
				[LoanID] VARCHAR(10) PRIMARY KEY CLUSTERED,
				[LoanClosedDate] DATETIME
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
		var sql = "INSERT INTO #dateRangeLastTwelve\nSELECT CAST(dt.date_id AS DATE) AS DATA_DATE\nFROM OperationalDatamart.dbo.D_Time dt\n\nSELECT\n\tfb.LoanID\nFROM Core.dbo.LossMitigationForbearanceWorkflow fb\nOUTER APPLY (\n\tSELECT 1 AS X\n) fmt";
		var expected = """
			INSERT INTO #dateRangeLastTwelve
			SELECT CAST(dt.date_id AS DATE) AS DATA_DATE
			FROM OperationalDatamart.dbo.D_Time dt;

			SELECT fb.LoanID
			FROM Core.dbo.LossMitigationForbearanceWorkflow fb
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
		var sql = "ALTER TABLE #currentLastSix ADD CONSTRAINT [pk_currentLastSix_LoanID] PRIMARY KEY CLUSTERED (LoanID)";
		var expected = """
			ALTER TABLE #currentLastSix
			ADD CONSTRAINT [pk_currentLastSix_LoanID]
			PRIMARY KEY CLUSTERED (LoanID);
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void AlterTableAddConstraintPrimaryKeyMultiColumnExpandsColumnList()
	{
		var sql = "ALTER TABLE #delinquencies ADD CONSTRAINT [pk_delinquencies_LoanID_DATA_DATE] PRIMARY KEY CLUSTERED ( LoanID, DATA_DATE)";
		var expected = """
			ALTER TABLE #delinquencies
			ADD CONSTRAINT [pk_delinquencies_LoanID_DATA_DATE]
			PRIMARY KEY CLUSTERED (
				LoanID,
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
		var sql = "ALTER TABLE #curMba ADD PRIMARY KEY CLUSTERED ([LoanID])";
		var expected = """
			ALTER TABLE #curMba
			ADD PRIMARY KEY CLUSTERED ([LoanID]);
			""";

		RunFactTest(sql, expected);
	}

	[Fact]
	public void AlterTableAddPrimaryKeyWithoutConstraintNameMultiColumnExpandsColumnList()
	{
		var sql = "ALTER TABLE #prevDeferBal ADD PRIMARY KEY CLUSTERED ( LoanID, RowId, SubCode)";
		var expected = """
			ALTER TABLE #prevDeferBal
			ADD PRIMARY KEY CLUSTERED (
				LoanID,
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
		var sql = "SELECT\n\tad.LoanID,\n\tROW_NUMBER() OVER (PARTITION BY ad.LoanID ORDER BY ad.DATA_DATE DESC) AS RowNum\nFROM Miser.dbo.ASSET_DETAIL_DAILY ad";
		var expected = """
			SELECT
				ad.LoanID,
				ROW_NUMBER()
				OVER (
					PARTITION BY ad.LoanID
					ORDER BY ad.DATA_DATE DESC
				) AS RowNum
			FROM Miser.dbo.ASSET_DETAIL_DAILY ad;
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
	public void ShortCastAndBuiltInFunctionsStayOnOneLineAndAreCapitalized()
	{
		// CAST(...) - and other built-in functions like DATEADD - must stay on one line when
		// short, and must be capitalized regardless of how they were typed in the source, since
		// they tokenize as plain identifiers (not reserved keywords) and were previously left
		// exactly as typed, including being broken across multiple lines if the source happened
		// to put the argument list on its own line.
		var expected = """
			SELECT CAST(dt.date_id AS DATE) AS DATA_DATE
			FROM OperationalDatamart.dbo.D_Time dt
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
			UPDATE Asset_Detail
			SET
				INGOMAR_MARK_DESC = 'Paid',
				INGOMAR_MARK = 4
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
			UPDATE Asset_Detail
			SET
				MARK = 1,
				MARK_CODE = 'REMIC'
			FROM Asset_Detail AD
			INNER JOIN ParticipationMasterHist PMH (NOLOCK) ON PMH.LoanID = AD.LoanId
				AND PMH.DATA_DATE = AD.DATA_DATE
			WHERE PMH.RecordTypeID = '6'
				AND AD.InvestorId NOT IN
				(
					SELECT INVESTOR_ID
					FROM SNMLT_INVESTOR(NOLOCK)
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
			WITH histWithUpb AS (
				SELECT
					[h].[LoanID],
					MAX([TransactionDate]) AS [LastActiveDate]
				FROM Service.dbo.History h
				INNER JOIN Service.dbo.Loan l ON l.LoanID = h.LoanID
				GROUP BY [h].[LoanID]
				HAVING MAX([TransactionDate]) <= @lastOfPreviousMonth
				), histWithoutUpb AS (
				SELECT
					[h].[LoanID],
					MIN([TransactionDate]) AS [FirstNotActiveDate]
				FROM Service.dbo.History h
				INNER JOIN histWithUpb u ON u.LoanID = h.LoanID
				GROUP BY [h].[LoanID]
				)
			INSERT INTO #closedLoans
			SELECT
				[LoanID],
				[FirstNotActiveDate]
			FROM histWithoutUpb;
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
				[lfb].[LoanID],
				[RowId] = ROW_NUMBER()
				OVER (
					PARTITION BY [lfb].[LoanID],
					[lfb].[SubCode]
					ORDER BY [lfb].[DataDate] DESC
				)
			FROM Miser.dbo.LoanFeeBalancesHistory lfb;
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
				CASE ad.OccupancyStatus
					WHEN 'Owner Occupied' THEN 'Owner Occupied'
					WHEN 'Vacant' THEN 'Vacant'
					ELSE 'Unknown'
				END AS OccupancyStatus
			FROM Asset_Detail ad;
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
			CREATE TABLE #gseLoans (
				[LoanID] VARCHAR(12) PRIMARY KEY CLUSTERED,
				[MonthEndPrinAmount] MONEY,
				[UPBShortFall] MONEY
				-- Extra data (from AD2.. but StepRate is not captured historically)
				,
				[StepRate1Date] DATE NULL,
				[StepRate2Date] DATE NULL
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
			CREATE TABLE #gseLoans (
				[LoanID] VARCHAR(12) PRIMARY KEY CLUSTERED,
				[MonthEndPrinAmount] MONEY,
				[MonthStartPrinAmount] MONEY,
				[AquiredUPB] MONEY,
				[UPBShortFall] MONEY,
				[StepRate1Date] DATE NULL,
				[StepRate2Date] DATE NULL,
				[StepRate3Date] DATE NULL,
				[StepRate4Date] DATE NULL,
				[StepRate5Date] DATE NULL,
				[StepRate6Date] DATE NULL
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
				fb.LoanID,
				fb.ReviewPmtPlanSubStatus,
			FROM Core.dbo.LossMitigationForbearanceWorkflow fb
			INNER JOIN #gseLoans l ON l.LoanID = fb.LoanID
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
	public void FullSuiteMBADelinquency()
	{
		string sql;
		using (var reader = new StreamReader("CodeSamples/FullSuiteMBADelinquency.sql"))
		{
			sql = reader.ReadToEnd();
		}

		var formatted = service.FormatForDisplay(sql);
		Assert.False(string.IsNullOrWhiteSpace(formatted));
		_output.WriteLine("Here is the formatted output for FullSuiteMBADelinquency.sql:");
		_output.WriteLine(formatted);
	}

	[Fact]
	public void ArithmeticExpressionWithNestedCaseIndentsLogically()
	{
		// Regression test for the MBADaysDelinquent expression in FullSuiteMBADelinquency.sql:
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
				) + 1 AS MBADaysDelinquent,
				NULL AS DelinquencyStringCode
			FROM ASSET_DETAIL_DAILY ad;
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
			INSERT INTO foo
			(
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
	public void InsertWithSelectFormattedCorrectly()
	{
		var expected = """
			INSERT INTO foo
			(
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
	public void ChainedJoinsWithBetweenClauseFormatCorrectly()
	{
		// Regression test for the #LoanListDataTape join chain in FullSuiteMBADelinquency.sql:
		// every JOIN (bare or modified) starts its own line with the joined table and first ON
		// condition kept together, and a short BETWEEN ... AND ... stays on one line rather than
		// being split just because a JOIN clause follows it.
		var expected = """
			SELECT
				*
			FROM #LoanListDataTape ll
			JOIN Miser.dbo.ASSET_DETAIL_DAILY ad ON ll.LoanID = ad.LoanID
				AND ad.Data_Date BETWEEN @backTo AND @thru
			JOIN OperationalDatamart.dbo.D_Time dt ON dt.date_id = ad.DATA_DATE
				AND dt.IsMonthEnd = 1;
			""";

		RunFactTest(expected);
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
				SET @chvErrMessage = 'ERROR: Stored Procedure up_AIFu_SecondaryUpdateAssetDetail ' +
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