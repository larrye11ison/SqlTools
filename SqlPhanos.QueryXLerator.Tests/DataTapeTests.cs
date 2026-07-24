using ClosedXML.Excel;
using System;
using System.Data;
using System.IO;
using System.Linq;
using Xunit;

namespace SqlPhanos.QueryXLerator.Tests;

public class DataTapeTests
{
	// DataTable.CreateDataReader() returns a DataTableReader, not a SqlDataReader, so the
	// provider-specific-type auto-formatting path (which needs a real SqlDataReader) isn't
	// exercised here - only the SQL-column-alias-suffix-driven behavior is, which is
	// independent of provider type and is the more deliberately-designed feature anyway.

	[Fact]
	public void WritesHeadersAndValuesForSimpleResultSet()
	{
		using var table = new DataTable();
		table.Columns.Add("Id", typeof(int));
		table.Columns.Add("Name", typeof(string));
		table.Rows.Add(1, "Alice");
		table.Rows.Add(2, "Bob");

		using var workbook = new XLWorkbook();
		using var reader = table.CreateDataReader();
		DataTape.WriteWorksheet(workbook, "Result_0", DataTape.IsColumnNameSpecialAndToBeIgnored, reader, skipEmptyResults: false, tableStyleName: null);

		var sheet = workbook.Worksheets.Single();
		Assert.Equal("Id", sheet.Cell(1, 1).GetString());
		Assert.Equal("Name", sheet.Cell(1, 2).GetString());
		Assert.Equal("1", sheet.Cell(2, 1).GetString());
		Assert.Equal("Alice", sheet.Cell(2, 2).GetString());
		Assert.Equal("2", sheet.Cell(3, 1).GetString());
		Assert.Equal("Bob", sheet.Cell(3, 2).GetString());

		var xlTable = sheet.Tables.Single();
		Assert.False(xlTable.ShowTotalsRow);
	}

	[Fact]
	public void AppliesTotalsRowFunctionFromAliasSuffix()
	{
		using var table = new DataTable();
		table.Columns.Add("Id", typeof(int));
		table.Columns.Add("[Total of amount/sum]", typeof(int));
		table.Rows.Add(1, 10);
		table.Rows.Add(2, 20);

		using var workbook = new XLWorkbook();
		using var reader = table.CreateDataReader();
		DataTape.WriteWorksheet(workbook, "Result_0", DataTape.IsColumnNameSpecialAndToBeIgnored, reader, skipEmptyResults: false, tableStyleName: null);

		var sheet = workbook.Worksheets.Single();
		// Only the "/sum" substring is stripped from the header text - the rest is untouched.
		Assert.Equal("[Total of amount]", sheet.Cell(1, 2).GetString());

		var xlTable = sheet.Tables.Single();
		Assert.True(xlTable.ShowTotalsRow);
		var field = xlTable.Fields.Single(f => f.Name == "[Total of amount]");
		Assert.Equal(XLTotalsRowFunction.Sum, field.TotalsRowFunction);
	}

	[Fact]
	public void AppliesCurrencyFormatFromDollarSuffix()
	{
		using var table = new DataTable();
		table.Columns.Add("Price/$", typeof(decimal));
		table.Rows.Add(19.99m);

		using var workbook = new XLWorkbook();
		using var reader = table.CreateDataReader();
		DataTape.WriteWorksheet(workbook, "Result_0", DataTape.IsColumnNameSpecialAndToBeIgnored, reader, skipEmptyResults: false, tableStyleName: null);

		var sheet = workbook.Worksheets.Single();
		Assert.Equal("Price", sheet.Cell(1, 1).GetString());
		Assert.Equal("$#,##0.00_);($#,##0.00)", sheet.Column(1).Style.NumberFormat.Format);
	}

	[Fact]
	public void MagicTabNameColumnSetsWorksheetNameAndIsExcludedFromTable()
	{
		using var table = new DataTable();
		table.Columns.Add("__tabname__", typeof(string));
		table.Columns.Add("Name", typeof(string));
		table.Rows.Add("MyCustomTab", "Alice");
		table.Rows.Add("MyCustomTab", "Bob");

		using var workbook = new XLWorkbook();
		using var reader = table.CreateDataReader();
		DataTape.WriteWorksheet(workbook, "Result_0", DataTape.IsColumnNameSpecialAndToBeIgnored, reader, skipEmptyResults: false, tableStyleName: null);

		var sheet = workbook.Worksheets.Single();
		Assert.Equal("MyCustomTab", sheet.Name);

		// Only "Name" should have been written as a data column - the tabname column itself
		// is consumed as metadata, not written out.
		Assert.Equal("Name", sheet.Cell(1, 1).GetString());
		Assert.Equal("Alice", sheet.Cell(2, 1).GetString());
		var xlTable = sheet.Tables.Single();
		Assert.Single(xlTable.Fields);
	}

	[Fact]
	public void UniqueifiesDuplicateColumnHeaders()
	{
		using var table = new DataTable();
		// Two distinct SQL aliases that both clean up to the same Excel header text.
		table.Columns.Add("Total/sum", typeof(int));
		table.Columns.Add("Total/average", typeof(int));
		table.Rows.Add(5, 5);

		using var workbook = new XLWorkbook();
		using var reader = table.CreateDataReader();
		DataTape.WriteWorksheet(workbook, "Result_0", DataTape.IsColumnNameSpecialAndToBeIgnored, reader, skipEmptyResults: false, tableStyleName: null);

		var sheet = workbook.Worksheets.Single();
		Assert.Equal("Total", sheet.Cell(1, 1).GetString());
		Assert.Equal("Total_1", sheet.Cell(1, 2).GetString());
	}

	[Fact]
	public void TableStyleNamesIncludesNoneAndExcludesCustom()
	{
		var names = DataTape.TableStyleNames().ToArray();

		Assert.Contains("None", names);
		Assert.DoesNotContain(names, n => n.Contains("Custom", StringComparison.Ordinal));
	}

	[Fact]
	public void SavedWorkbookRoundTripsThroughDisk()
	{
		// The rest of the tests inspect the in-memory XLWorkbook directly; this one confirms
		// the same content survives an actual SaveAs-to-disk-and-reload cycle, which is what
		// the public WriteOutputFile(SqlCommand) entry point does (untestable end-to-end here
		// since it requires a live SQL Server connection, not available in this environment).
		var path = Path.Combine(Path.GetTempPath(), $"queryxlerator-test-{Guid.NewGuid():N}.xlsx");
		try
		{
			using var table = new DataTable();
			table.Columns.Add("Id", typeof(int));
			table.Rows.Add(1);

			using (var workbook = new XLWorkbook())
			{
				using var reader = table.CreateDataReader();
				DataTape.WriteWorksheet(workbook, "Result_0", DataTape.IsColumnNameSpecialAndToBeIgnored, reader, skipEmptyResults: false, tableStyleName: null);
				workbook.SaveAs(path);
			}

			Assert.True(File.Exists(path));
			using var reloaded = new XLWorkbook(path);
			Assert.Equal("1", reloaded.Worksheets.Single().Cell(2, 1).GetString());
		}
		finally
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
	}
}
