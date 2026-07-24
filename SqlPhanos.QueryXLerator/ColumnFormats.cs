using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SqlPhanos.QueryXLerator;

internal class ColumnFormats
{
	public static readonly string CurrencyFormat = "$#,##0.00_);($#,##0.00)";
	public static readonly string GeneralNumericFormat = "#,##0.000_);(#,##0.000)";
	public static readonly string PercentFormat = "0.00%";

	private static readonly Func<object, object> byteArrayFormatter =
		(i) =>
		{
			var bytez = (byte[])i;
			var stud = bytez
				.Take(1900)
				.Aggregate(new StringBuilder("0x"), (x, y) => x.AppendFormat("{0:X2}", y));

			// truncate it if it's too long
			if (bytez.Length > 2000)
			{
				stud.Length = 1900;
				stud.Append("... !!! truncated !!!");
			}
			return stud.ToString();
		};

	private static readonly Dictionary<Type, string> formatMappings = new();

	static ColumnFormats()
	{
		formatMappings.Add(typeof(System.Data.SqlTypes.SqlDateTime), "m/d/yyyy");
		formatMappings.Add(typeof(DateTime), "m/d/yyyy");
		formatMappings.Add(typeof(System.Data.SqlTypes.SqlDouble), GeneralNumericFormat);
		formatMappings.Add(typeof(System.Data.SqlTypes.SqlDecimal), GeneralNumericFormat);
		formatMappings.Add(typeof(System.Data.SqlTypes.SqlMoney), CurrencyFormat);
	}

	public static ColumnHandler MapTypeToColumnHandler(Type type, Type providerType)
	{
		var rv = new ColumnHandler();

		if (formatMappings.TryGetValue(providerType, out var formatString))
		{
			rv.ExcelFormatName = () => formatString;
		}

		if (providerType == typeof(System.Data.SqlTypes.SqlMoney) ||
			providerType == typeof(System.Data.SqlTypes.SqlDecimal) ||
			providerType == typeof(System.Data.SqlTypes.SqlSingle) ||
			providerType == typeof(System.Data.SqlTypes.SqlInt16) ||
			providerType == typeof(System.Data.SqlTypes.SqlInt32) ||
			providerType == typeof(System.Data.SqlTypes.SqlInt64) ||
			providerType == typeof(System.Data.SqlTypes.SqlDouble))
		{
			rv.RowFunction = () => XLTotalsRowFunction.Sum;
		}

		if (type == typeof(byte[]))
		{
			rv.Formatter = byteArrayFormatter;
		}

		return rv;
	}
}
