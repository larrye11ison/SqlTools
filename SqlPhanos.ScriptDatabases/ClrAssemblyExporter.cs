using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Smo;

namespace SqlPhanos.ScriptDatabases;

/// <summary>
/// Reads a CLR assembly's raw bytes directly from sys.assembly_files - SMO's SqlAssemblyFile only
/// exposes Name/ID/Parent/etc., no Content/Bytes property, so the bytes have to come from a plain
/// query instead, the same "go around SMO" pattern ObjectPermissionScriptBuilder already uses for
/// permissions. No DAC needed here - the content column is readable over an ordinary connection
/// given the usual VIEW DEFINITION-level rights.
/// </summary>
public static class ClrAssemblyExporter
{
	/// <summary>
	/// A CLR-backed stored procedure/function/trigger is the same SMO class as its T-SQL
	/// counterpart, just with ImplementationType == SqlClr and AssemblyName/ClassName/MethodName
	/// set instead of a TextBody - returns the assembly name if <paramref name="smoObject"/> is one
	/// of those, or null for anything else (including a T-SQL-implemented one of the same types).
	/// </summary>
	public static string? TryGetClrAssemblyName(SqlSmoObject smoObject) => smoObject switch
	{
		StoredProcedure { ImplementationType: ImplementationType.SqlClr } sp => sp.AssemblyName,
		UserDefinedFunction { ImplementationType: ImplementationType.SqlClr } fn => fn.AssemblyName,
		Trigger { ImplementationType: ImplementationType.SqlClr } tr => tr.AssemblyName,
		_ => null
	};

	/// <summary>
	/// Returns the primary assembly file's raw bytes, or null if the assembly has no rows in
	/// sys.assembly_files. <paramref name="fileName"/> is the file's name as SQL Server has it -
	/// this is the .NET module name embedded in the assembly's own PE metadata (what the compiler
	/// stamped as its output file name), not the original disk path passed to CREATE ASSEMBLY ...
	/// FROM, which SQL Server discards immediately after import - falls back to
	/// "&lt;assemblyName&gt;.dll" if that column is ever null/empty.
	/// </summary>
	public static byte[]? GetPrimaryAssemblyBytes(
		string connectionString,
		string databaseName,
		string assemblyName,
		out string fileName)
	{
		var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = databaseName };
		using var connection = new SqlConnection(builder.ConnectionString);
		connection.Open();

		using var command = connection.CreateCommand();
		command.CommandText =
			"""
			SELECT af.name AS FileName, af.content AS Content
			FROM sys.assembly_files af
			INNER JOIN sys.assemblies a ON a.assembly_id = af.assembly_id
			WHERE a.name = @assemblyName
			ORDER BY af.file_id;
			""";
		command.Parameters.AddWithValue("@assemblyName", assemblyName);

		using var reader = command.ExecuteReader(CommandBehavior.SingleRow);
		if (!reader.Read())
		{
			fileName = assemblyName + ".dll";
			return null;
		}

		var rawFileName = reader["FileName"] as string;
		fileName = string.IsNullOrEmpty(rawFileName) ? assemblyName + ".dll" : rawFileName;
		return (byte[])reader["Content"];
	}

	// The SQL-facing attributes SQLCLR entry points are marked with - a public class carrying
	// several of these methods is what actually defines the assembly's public API, as opposed to
	// the assembly's own SQL name (often unrelated to any type name) or an individual object's own
	// name (arbitrary - whichever proc/function happened to be scripted first).
	private static readonly HashSet<string> SqlClrEntryPointAttributeNames =
	[
		"SqlProcedureAttribute",
		"SqlFunctionAttribute",
		"SqlTriggerAttribute",
		"SqlUserDefinedTypeAttribute",
		"SqlUserDefinedAggregateAttribute"
	];

	/// <summary>
	/// Picks a name for the assembly's exported .dll/.cs files based on whichever public class
	/// has the most SQLCLR entry-point methods (SqlProcedure/SqlFunction/SqlTrigger/...) -
	/// "biggest" measured as attributed-method count, the simplest proxy for "the class that
	/// actually defines this assembly's public API." Returns null (caller falls back to the
	/// assembly's own SQL name) if no public class has any such methods, which shouldn't happen
	/// for an assembly this method is only ever called for after already confirming it backs at
	/// least one CLR object, but a raw metadata read has no guarantee of finding it structured the
	/// way that implies.
	/// </summary>
	public static string? TryGetPrimarySqlClrTypeName(byte[] assemblyBytes)
	{
		using var stream = new MemoryStream(assemblyBytes);
		using var peReader = new PEReader(stream);
		var metadata = peReader.GetMetadataReader();

		string? bestName = null;
		var bestScore = 0;

		foreach (var typeHandle in metadata.TypeDefinitions)
		{
			var type = metadata.GetTypeDefinition(typeHandle);
			if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
			{
				continue;
			}

			var score = type.GetMethods()
				.Select(metadata.GetMethodDefinition)
				.Sum(method => method.GetCustomAttributes()
					.Select(metadata.GetCustomAttribute)
					.Count(attribute => IsSqlClrEntryPointAttribute(metadata, attribute)));

			if (score > bestScore)
			{
				bestScore = score;
				var ns = metadata.GetString(type.Namespace);
				var name = metadata.GetString(type.Name);
				bestName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
			}
		}

		return bestName;
	}

	private static bool IsSqlClrEntryPointAttribute(MetadataReader metadata, CustomAttribute attribute)
	{
		// SqlProcedureAttribute et al. live in Microsoft.SqlServer.Server (System.Data.dll on
		// .NET Framework) - an assembly external to the one being inspected, so the attribute's
		// constructor is a MemberReference whose Parent is a TypeReference, not a
		// TypeDefinition/MethodDefinition local to this module.
		var ctorHandle = attribute.Constructor;
		if (ctorHandle.Kind == HandleKind.MemberReference)
		{
			var memberRef = metadata.GetMemberReference((MemberReferenceHandle)ctorHandle);
			if (memberRef.Parent.Kind == HandleKind.TypeReference)
			{
				var typeRef = metadata.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
				return SqlClrEntryPointAttributeNames.Contains(metadata.GetString(typeRef.Name));
			}
		}
		else if (ctorHandle.Kind == HandleKind.MethodDefinition)
		{
			var methodDef = metadata.GetMethodDefinition((MethodDefinitionHandle)ctorHandle);
			var typeDef = metadata.GetTypeDefinition(methodDef.GetDeclaringType());
			return SqlClrEntryPointAttributeNames.Contains(metadata.GetString(typeDef.Name));
		}

		return false;
	}
}
