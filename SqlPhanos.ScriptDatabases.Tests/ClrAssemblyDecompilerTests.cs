using System.IO;
using System.Reflection;
using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class ClrAssemblyDecompilerTests
{
	[Fact]
	public void DecompileProducesReadableSourceContainingKnownMemberNames()
	{
		// Decompiles this test project's own already-built assembly - a real, non-trivial .NET
		// assembly, avoiding the need to emit or compile a throwaway fixture just for this test.
		var assemblyPath = Assembly.GetExecutingAssembly().Location;
		var bytes = File.ReadAllBytes(assemblyPath);

		var source = ClrAssemblyDecompiler.Decompile(bytes, "SqlPhanos.ScriptDatabases.Tests");

		Assert.False(string.IsNullOrWhiteSpace(source));
		Assert.Contains("class ClrAssemblyDecompilerTests", source);
		Assert.Contains("namespace SqlPhanos.ScriptDatabases.Tests", source);
	}
}
