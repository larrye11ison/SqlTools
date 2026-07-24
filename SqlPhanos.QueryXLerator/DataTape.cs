using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace SqlPhanos.QueryXLerator;

/// <summary>
/// Executes a SQL command and writes its result set(s) to an XLSX file, one worksheet per
/// result set. Ported from the original QueryXLerator.Library (EPPlus-based) to ClosedXML.
///
/// Column headers support a suffix convention parsed out of the SQL alias text itself:
/// "/sum", "/average", etc. (any ClosedXML XLTotalsRowFunction name) adds that column to the
/// Excel Table's totals row; "/$" and "/%" apply currency/percent number formats. A column
/// literally named (or containing) "__tabname__" is not written as a data column at all -
/// instead its value (evaluated per query, from the first row) becomes the worksheet's tab
/// name, letting the SQL query itself control tab naming.
/// </summary>
public static class DataTape
{
	private const string MagicTabNameFieldHeaderColumnNameString = "__tabname__";

	// internal (not private) so tests can drive WriteWorksheet directly with an in-memory
	// IDataReader fixture (e.g. DataTable.CreateDataReader()) instead of needing a live SQL
	// Server, which the public surface otherwise requires.
	internal static readonly Func<string, bool> IsColumnNameSpecialAndToBeIgnored = columnName =>
		columnName?.IndexOf(MagicTabNameFieldHeaderColumnNameString, StringComparison.InvariantCultureIgnoreCase) >= 0;

	private static readonly Regex ColumnFormatRegex = new(@"\/(?<p>[%\$a-z]+)");

	private static readonly Dictionary<string, XLTotalsRowFunction> ExcelFuncNames = Enum.GetValues<XLTotalsRowFunction>()
		.ToDictionary(x => Enum.GetName(x)!, x => x);

	private static readonly Regex InvalidTabNameRegex = new(@"[\[\]\*\/\\\?\:]");

	public static IEnumerable<string> TableStyleNames()
	{
		// XLTableTheme is a class exposing its built-in styles as public static fields of its
		// own type (not a real enum, unlike EPPlus's TableStyles) - reflect over those instead.
		return typeof(XLTableTheme)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(f => f.FieldType == typeof(XLTableTheme))
			.Select(f => f.Name)
			.Where(n => n.IndexOf("Custom", StringComparison.Ordinal) == -1)
			.OrderBy(t => t);
	}

	public static void WriteOutputFile(string outputPath, string commandText, string connectionString,
		bool skipEmptyResults = false, string? tableStyleName = "", CancellationToken? token = null)
	{
		using var cn = new SqlConnection(connectionString);
		cn.Open();
		using var cmd = cn.CreateCommand();
		cmd.CommandType = CommandType.Text;
		cmd.CommandText = commandText;
		cmd.CommandTimeout = 16000;
		WriteOutputFile(outputPath, cmd, skipEmptyResults, tableStyleName, token);
	}

	public static void WriteOutputFile(string outputPath, SqlCommand cmd,
		bool skipEmptyResults = false, string? tableStyleName = "", CancellationToken? token = null)
	{
		var cancelToken = token ?? CancellationToken.None;
		cancelToken.Register(() => cmd.Cancel());

		if (File.Exists(outputPath))
		{
			File.Delete(outputPath);
		}

		using var workbook = new XLWorkbook();
		var tabNumber = 0;
		using (var rdr = cmd.ExecuteReader())
		{
			do
			{
				var proposedWorksheetName = $"Result_{tabNumber++}";
				WriteWorksheet(workbook, proposedWorksheetName, IsColumnNameSpecialAndToBeIgnored, rdr, skipEmptyResults, tableStyleName);
			} while (rdr.NextResult());
		}

		workbook.SaveAs(outputPath);
	}

	private static ColumnMetadata GetColumnMetadata(string columnName)
	{
		if (string.IsNullOrEmpty(columnName) || columnName.Trim().Length == 0)
		{
			return new ColumnMetadata { ExcelFormatString = "", Name = " ", RowFunction = XLTotalsRowFunction.None };
		}

		var proposedColumnName = columnName.Trim();

		var formatMatches = ColumnFormatRegex.Matches(columnName).Cast<Match>()
			.Where(m => m.Groups.Count == 2)
			.Select(m => m.Groups[1].Value.Trim());

		var theFormatString = "";
		var rowFunction = XLTotalsRowFunction.None;

		// If duplicates exist, the last one wins - i.e. /sum/average would end up as average.
		foreach (var match in formatMatches)
		{
			switch (match)
			{
				case "$":
					theFormatString = ColumnFormats.CurrencyFormat;
					break;

				case "%":
					theFormatString = ColumnFormats.PercentFormat;
					break;
			}

			var matchingFunc = ExcelFuncNames.Where(x => string.Equals(x.Key, match, StringComparison.OrdinalIgnoreCase));
			if (matchingFunc.Any())
			{
				rowFunction = matchingFunc.First().Value;
			}

			proposedColumnName = ColumnFormatRegex.Replace(columnName, "");
		}

		// do a bit of cleanup - in case there are some special chars left in the field name
		proposedColumnName = proposedColumnName.Replace("#", "Num");

		return new ColumnMetadata
		{
			ExcelFormatString = theFormatString,
			Name = proposedColumnName,
			RowFunction = rowFunction
		};
	}

	private static XLTableTheme ResolveTableTheme(string? tableStyleName)
	{
		var matchingTableStyleName = TableStyleNames()
			.FirstOrDefault(ts => string.Equals(ts, tableStyleName, StringComparison.OrdinalIgnoreCase))
			?? "None";

		return (XLTableTheme)typeof(XLTableTheme)
			.GetField(matchingTableStyleName, BindingFlags.Public | BindingFlags.Static)!
			.GetValue(null)!;
	}

	private static string Uniqueify(IEnumerable<string> previousValues, string proposedValue)
	{
		var proposedReturnValue = proposedValue;
		var uniqueifier = 0;
		while (previousValues.Any(v => string.Equals(v, proposedReturnValue, StringComparison.OrdinalIgnoreCase)))
		{
			proposedReturnValue = $"{proposedValue}_{++uniqueifier}";
		}

		return proposedReturnValue;
	}

	// internal (not private) - see IsColumnNameSpecialAndToBeIgnored's comment above.
	internal static void WriteWorksheet(XLWorkbook workbook,
		string proposedWorksheetName,
		Func<string, bool> isColumnNameSpecialAndToBeIgnored,
		IDataReader rdr,
		bool skipEmptyResults,
		string? tableStyleName)
	{
		// If the reader is actually a SqlDataReader, some richer SQL-provider-type-aware
		// formatting can be enabled. Both functions "opt out" of that for any other reader
		// type (e.g. the DataTable.CreateDataReader() fixture used in tests).
		var sdr = rdr as SqlDataReader;

		bool ReaderHasRows(IDataReader idr) => sdr?.HasRows ?? true;

		Type ProviderSpecificDataType(IDataReader idr, int columnIndex) =>
			sdr?.GetProviderSpecificFieldType(columnIndex) ?? idr.GetFieldType(columnIndex);

		if (skipEmptyResults && !ReaderHasRows(rdr))
		{
			return;
		}

		var theTableStyle = ResolveTableTheme(tableStyleName);

		var previousWorksheetNames = workbook.Worksheets.Select(ws => ws.Name).ToArray();

		var columnHandlers = new Dictionary<int, ColumnHandler>();

		// A temporary name only - renamed to the real tab name once it's known. Kept short
		// (unlike the original's full-GUID temp name) since Excel worksheet names are capped
		// at 31 characters and ClosedXML enforces that (EPPlus 4.0.5 apparently didn't).
		var sheet = workbook.Worksheets.Add("_" + Guid.NewGuid().ToString("N")[..8]);

		var rawColumnMetadataFromDataReader = Enumerable.Range(0, rdr.FieldCount)
			.Select(cc => new
			{
				ReaderIndex = cc,
				ColumnMetaData = GetColumnMetadata(rdr.GetName(cc)),
				ProviderType = ProviderSpecificDataType(rdr, cc),
				Type = rdr.GetFieldType(cc)
			}).ToArray();

		var excelColumnIndex = 1;
		var realColumnsToWriteToExcel = rawColumnMetadataFromDataReader
			.Where(c => !isColumnNameSpecialAndToBeIgnored(c.ColumnMetaData.Name))
			.Select(c => new { Column = c, ExcelIndex = excelColumnIndex++ })
			.ToArray();

		var specialColumnForTabName = rawColumnMetadataFromDataReader
			.FirstOrDefault(c => isColumnNameSpecialAndToBeIgnored(c.ColumnMetaData.Name));

		// keep track of the actual headers used so we don't reuse one going forward
		var columnHeaders = new List<string>();

		//
		// Write column headers
		//
		foreach (var c in realColumnsToWriteToExcel)
		{
			var excelIndex = c.ExcelIndex;

			// make sure the column header is unique, else the Excel table will blow chunks
			var proposedColumnName = Uniqueify(columnHeaders, c.Column.ColumnMetaData.Name);
			columnHeaders.Add(proposedColumnName);

			sheet.Cell(1, excelIndex).Value = proposedColumnName;

			var columnFormat = ColumnFormats.MapTypeToColumnHandler(c.Column.Type, c.Column.ProviderType);
			columnHandlers[excelIndex] = columnFormat;

			var column = sheet.Column(excelIndex);
			column.Style.NumberFormat.Format = rawColumnMetadataFromDataReader[c.Column.ReaderIndex].ColumnMetaData.ExcelFormatString == ""
				? columnFormat.ExcelFormatName()
				: rawColumnMetadataFromDataReader[c.Column.ReaderIndex].ColumnMetaData.ExcelFormatString;
			column.Width = 20;
		}

		var excelRowNumber = 2;

		var tabNameSet = false;
		if (specialColumnForTabName != null && specialColumnForTabName.ColumnMetaData.Name.Replace(MagicTabNameFieldHeaderColumnNameString, string.Empty) != string.Empty)
		{
			proposedWorksheetName = specialColumnForTabName.ColumnMetaData.Name.Replace(MagicTabNameFieldHeaderColumnNameString, string.Empty);
			tabNameSet = true;
		}

		//
		// Write out the actual rows of data
		//
		while (rdr.Read())
		{
			// if the query specified the magic column name, use its value as the tab name
			if (!tabNameSet && specialColumnForTabName != null)
			{
				proposedWorksheetName = rdr.GetString(specialColumnForTabName.ReaderIndex);
				tabNameSet = true;
			}

			foreach (var c in realColumnsToWriteToExcel)
			{
				var columnIndex = c.Column.ReaderIndex;
				if (!rdr.IsDBNull(columnIndex) && !string.IsNullOrEmpty(rdr[columnIndex].ToString()?.Trim()))
				{
					var formattedValue = columnHandlers[c.ExcelIndex].Formatter(rdr.GetValue(columnIndex));
					sheet.Cell(excelRowNumber, c.ExcelIndex).Value = XLCellValue.FromObject(formattedValue);
				}
			}

			excelRowNumber++;
		}

		//
		// Make sure the tab name is unique and doesn't contain any of: [ ] * / \ ? :
		//
		var tempTabName = InvalidTabNameRegex.Replace(proposedWorksheetName, "");
		tempTabName = Uniqueify(previousWorksheetNames, tempTabName);
		sheet.Name = tempTabName;

		//
		// Set up the Excel Table over the written range.
		//
		var tableRange = sheet.Range(1, 1, excelRowNumber - 1, realColumnsToWriteToExcel.Length);
		var newTableName = $"Table_{tempTabName}";
		var newExcelTable = tableRange.CreateTable(newTableName);
		newExcelTable.Theme = theTableStyle;

		//
		// Set up the functions in the Totals Row (if any were specified via the /sum,
		// /average, etc. alias suffix - if none were, the totals row is left off entirely).
		//
		// Unlike EPPlus's ExcelTable, ClosedXML's XLTableField.TotalsRowFunction setter
		// requires ShowTotalsRow to already be true - it can't be flipped on as a side effect
		// while iterating fields the way the original code did it, so whether any column
		// needs a total is determined first.
		//
		var columnsNeedingTotals = rawColumnMetadataFromDataReader
			.Where(c => c.ColumnMetaData.RowFunction != XLTotalsRowFunction.None)
			.ToArray();

		if (columnsNeedingTotals.Length > 0)
		{
			newExcelTable.ShowTotalsRow = true;
			foreach (var field in newExcelTable.Fields)
			{
				var colMeta = columnsNeedingTotals.FirstOrDefault(f => f.ColumnMetaData.Name == field.Name);
				if (colMeta != null)
				{
					field.TotalsRowFunction = colMeta.ColumnMetaData.RowFunction;
				}
			}
		}

		sheet.Columns().AdjustToContents();
	}

	private sealed class ColumnMetadata
	{
		public string ExcelFormatString { get; set; } = "";

		public string Name { get; set; } = "";

		public XLTotalsRowFunction RowFunction { get; set; }
	}
}
