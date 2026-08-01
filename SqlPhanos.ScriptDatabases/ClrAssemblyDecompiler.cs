using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

namespace SqlPhanos.ScriptDatabases;

/// <summary>
/// Decompiles a SQLCLR assembly's raw bytes into a single standalone C# source string, using
/// ICSharpCode.Decompiler (the engine behind ILSpy) - lets a scripted CLR proc/function show its
/// actual implementation alongside the thin T-SQL "AS EXTERNAL NAME" wrapper, which is all SMO
/// otherwise exposes.
/// </summary>
public static class ClrAssemblyDecompiler
{
	/// <summary>
	/// Returns the whole module decompiled as one combined C# string (matching "one standalone
	/// file, compilable via dotnet build, no csproj needed" - DecompileWholeModuleAsString already
	/// produces exactly that shape). SQLCLR assemblies target .NET Framework and typically
	/// reference Microsoft.SqlServer.Server/System.Data.SqlTypes - UniversalAssemblyResolver is
	/// given the module's own detected target framework so it can find reference assemblies for
	/// those, but perfect resolution isn't guaranteed for every SQLCLR assembly found in the wild;
	/// without it, decompilation still usually produces valid, readable C#, just with some
	/// signatures a human may need to touch up by hand afterward. That's inherent to decompiling
	/// arbitrary CLR assemblies, not something to engineer around here.
	/// </summary>
	public static string Decompile(byte[] assemblyBytes, string assemblyName)
	{
		using var stream = new MemoryStream(assemblyBytes);
		var peFile = new PEFile(assemblyName, stream, PEStreamOptions.PrefetchEntireImage);
		var resolver = new UniversalAssemblyResolver(
			assemblyName,
			throwOnError: false,
			peFile.DetectTargetFrameworkId(),
			runtimePack: null,
			PEStreamOptions.PrefetchMetadata,
			MetadataReaderOptions.Default);

		var decompiler = new CSharpDecompiler(peFile, resolver, new DecompilerSettings());
		return decompiler.DecompileWholeModuleAsString();
	}
}
