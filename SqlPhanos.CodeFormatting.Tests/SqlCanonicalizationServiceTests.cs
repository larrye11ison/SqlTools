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
	public void BasicJoinFormattedCorrectly()
	{
		var expected = """
			SELECT
				a,
				b
			FROM foo f
			INNER JOIN bar b ON f.id = b.foo_id
				AND f.status = 'active'
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
			END
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
			END
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
			END
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
			END
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
	public void LeftJoinFormattedCorrectly()
	{
		string sql = """
			SELECT e.FirstName
			FROM Employees e
			LEFT JOIN Departments d ON e.DepartmentID = d.DepartmentID
				AND d.DepartmentName = 'Sales'
			WHERE e.EmployeeID IN (1, 2, 3)
			""";

		RunFactTest(sql);
	}



	[Fact]
	public void IfElseWithBeginBlocksFormatCorrectly()
	{
		var expected = """
			IF(1 = 1)
			BEGIN
				PRINT 'hi';
			END
			ELSE
			BEGIN
				PRINT 'bye';
			END
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
	public void LongerNestedFunctionsAreBrokenApart()
	{
		var expected = """
			SELECT
				COALESCE(
					ISNULL(CAST(a AS VARCHAR(10)), 'N/A'),
					FORMAT(a.DateSold, 'yyyy-MM-dd'),
					MAX(CAST(a AS VARCHAR(20)))
				)
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void MultiColumnSelectFormattedCorrectly()
	{
		var expected = """
			SELECT
				a,
				b,
				c
			FROM foo
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
				and b = 420
			""";
		RunFactTest(expected);
	}

	[Fact]
	public void NestedFunkHellWithAlias()
	{
		// read sql text from file "SELECT.sql" in CodeSamples dir
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
				) AS ComplexStringExpression
			""";

		RunFactTest(expected);
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
	public void SingleColumnSelectFormattedCorrectly()
	{
		var expected = """
			SELECT a
			FROM foo
			""";

		RunFactTest(expected);
	}

	[Fact]
	public void SimpleAssignmentSelectFormattedCorrectly()
	{
		var expected = """
			SELECT @foo = 3
			FROM foo
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
			WHERE a = 3
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
			)
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
			FROM dbo.Blerg a
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
		var normalizedInput = input.Replace("\r\n", "\n").Replace('\r', '\n');
		var lines = normalizedInput.Split('\n');
		var sb = new StringBuilder();
		var preserveLineBreak = false;

		for (var i = 0; i < lines.Length; i++)
		{
			var normalizedLine = System.Text.RegularExpressions.Regex.Replace(lines[i], @"\s+", " ").Trim();
			if (normalizedLine.Length == 0)
			{
				continue;
			}

			if (sb.Length > 0)
			{
				if (preserveLineBreak)
				{
					sb.AppendLine();
				}
				else
				{
					sb.Append(' ');
				}
			}

			sb.Append(normalizedLine);
			preserveLineBreak = ContainsSqlSingleLineComment(normalizedLine);
		}

		return sb.ToString().Trim();
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

	private void WriteStringDiff(string expected, string actual)
	{
		_output.WriteLine($"Expected length: {expected.Length}, Actual length: {actual.Length}");

		var expectedLines = expected.Replace("\r\n", "\n").Split('\n');
		var actualLines = actual.Replace("\r\n", "\n").Split('\n');
		var maxLines = Math.Max(expectedLines.Length, actualLines.Length);

		for (var i = 0; i < maxLines; i++)
		{
			var expectedLine = i < expectedLines.Length ? expectedLines[i] : string.Empty;
			var actualLine = i < actualLines.Length ? actualLines[i] : string.Empty;

			if (expectedLine == actualLine)
			{
				continue;
			}

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
	}
}