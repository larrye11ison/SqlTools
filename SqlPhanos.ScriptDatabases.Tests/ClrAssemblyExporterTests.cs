using System.IO;
using System.Reflection;
using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class ClrAssemblyExporterTests
{
	[Fact]
	public void TryGetPrimarySqlClrTypeNameReturnsNullWhenNoSqlClrEntryPointsExist()
	{
		// This test project's own assembly has no [SqlProcedure]/[SqlFunction]/[SqlTrigger]-marked
		// methods at all - a real, non-trivial .NET assembly (same fixture-avoidance approach as
		// ClrAssemblyDecompilerTests) that exercises the "no match, don't crash" path without
		// depending on the standalone SqlClrTest fixture project (gitignored, not guaranteed to be
		// built on every machine this test runs on).
		//
		// The actual positive-match path (a real SQLCLR assembly with [SqlProcedure] methods on a
		// public class) was verified manually against SqlClrTest.dll - it correctly picked out
		// "SqlClrTest.StoredProcedures" - rather than built here as a synthetic in-memory PE image
		// just for this test.
		var assemblyPath = Assembly.GetExecutingAssembly().Location;
		var bytes = File.ReadAllBytes(assemblyPath);

		var name = ClrAssemblyExporter.TryGetPrimarySqlClrTypeName(bytes);

		Assert.Null(name);
	}
}
