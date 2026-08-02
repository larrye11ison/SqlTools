using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SqlPhanos.CodeFormatting;

public enum SqlObjectReferenceKind
{
	Any,
	SchemaObject,
	Executable,
	TableOrView,
	Rowset,
	Procedure,
	Function,
	Sequence,
	Type,
	Trigger,
}

public enum SqlObjectReferenceClassification
{
	Local,
	LinkedServer,
	RemoteDataSource,
}

/// <summary>
/// One semantic SQL object reference and its exact location in the original SQL text.
/// Identifier parts are unquoted values supplied by ScriptDom; <see cref="Text"/> retains
/// the original delimiters, escaping, whitespace, and casing.
/// </summary>
public sealed class SqlObjectReference
{
	internal SqlObjectReference(
		SqlObjectReferenceKind kind,
		int offset,
		int length,
		string text,
		string? server,
		string? database,
		string? schema,
		string objectName,
		int partCount,
		bool isRemoteDataSource)
	{
		Kind = kind;
		Offset = offset;
		Length = length;
		Text = text;
		Server = EmptyToNull(server);
		Database = EmptyToNull(database);
		Schema = EmptyToNull(schema);
		Object = objectName;
		PartCount = partCount;
		Classification = isRemoteDataSource
			? SqlObjectReferenceClassification.RemoteDataSource
			: partCount == 4
				? SqlObjectReferenceClassification.LinkedServer
				: SqlObjectReferenceClassification.Local;
	}

	public SqlObjectReferenceKind Kind { get; }

	public SqlObjectReferenceClassification Classification { get; }

	public int Offset { get; }

	public int Length { get; }

	public string Text { get; }

	public string? Server { get; }

	public string? Database { get; }

	public string? Schema { get; }

	public string Object { get; }

	public int PartCount { get; }

	private static string? EmptyToNull(string? value)
		=> string.IsNullOrEmpty(value) ? null : value;
}

public sealed class SqlObjectReferenceParseError
{
	internal SqlObjectReferenceParseError(int number, int offset, int line, int column, string message)
	{
		Number = number;
		Offset = offset;
		Line = line;
		Column = column;
		Message = message;
	}

	public int Number { get; }

	public int Offset { get; }

	public int Line { get; }

	public int Column { get; }

	public string Message { get; }
}

/// <summary>
/// An analyzer result. ScriptDom can return a partial AST alongside parse errors; references in
/// that AST remain available so one unsupported or malformed statement does not suppress valid
/// object references elsewhere in a long module.
/// </summary>
public sealed class SqlObjectReferenceAnalysisResult
{
	internal SqlObjectReferenceAnalysisResult(
		IList<SqlObjectReference> references,
		IList<SqlObjectReferenceParseError> parseErrors)
	{
		References = new ReadOnlyCollection<SqlObjectReference>(references);
		ParseErrors = new ReadOnlyCollection<SqlObjectReferenceParseError>(parseErrors);
		ParseSucceeded = parseErrors.Count == 0;
	}

	public bool ParseSucceeded { get; }

	public IReadOnlyList<SqlObjectReference> References { get; }

	public IReadOnlyList<SqlObjectReferenceParseError> ParseErrors { get; }
}
