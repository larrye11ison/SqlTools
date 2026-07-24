using System;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlPhanos.Services;

/// <summary>
/// Recovers the plaintext of a WITH ENCRYPTION stored procedure or view using a publicly
/// documented known-plaintext XOR technique. SQL Server encrypts the *entire submitted
/// statement text* (UTF-16LE, verbatim) with a length-preserving stream cipher keyed off
/// the database, so re-issuing (and immediately rolling back) an ALTER whose statement text
/// is padded to exactly the same byte length as the original produces a second ciphertext
/// for a KNOWN plaintext at the same byte positions. XORing the two ciphertexts cancels the
/// keystream out; XORing that recovered keystream against the original ciphertext recovers
/// the original plaintext.
///
/// This is a best-effort, unofficial recovery - not a supported SQL Server feature. It
/// requires sysadmin rights, the Dedicated Admin Connection (DAC) enabled on the target
/// server, and ALTER permission on the object. Scope is deliberately limited to stored
/// procedures and views, whose ALTER syntax needs no additional signature information
/// beyond the schema-qualified name; functions and triggers need their full parameter/
/// event signature reconstructed from metadata to ALTER correctly, which isn't implemented
/// here. This has not been validated against a live SQL Server as part of this change -
/// verify recovered output before relying on it.
/// </summary>
public static class EncryptedObjectDecryptionService
{
	private static readonly string[] SupportedTypeDescs = { "SQL_STORED_PROCEDURE", "VIEW" };

	public static bool IsSupportedType(string typeDesc)
	{
		return Array.IndexOf(SupportedTypeDescs, typeDesc) >= 0;
	}

	public static string? TryDecrypt(string connectionString, string typeDesc, string schemaName, string objectName)
	{
		if (!IsSupportedType(typeDesc))
		{
			return null;
		}

		var objectTypeKeyword = typeDesc == "VIEW" ? "VIEW" : "PROCEDURE";

		var builder = new SqlConnectionStringBuilder(connectionString);
		var dacBuilder = new SqlConnectionStringBuilder(connectionString)
		{
			DataSource = "ADMIN:" + builder.DataSource,
			Pooling = false
		};

		var quotedSchema = QuoteIdentifier(schemaName);
		var quotedName = QuoteIdentifier(objectName);
		var fqName = $"{quotedSchema}.{quotedName}";

		try
		{
			byte[]? originalCipher;
			using (var dac = new SqlConnection(dacBuilder.ConnectionString))
			{
				dac.Open();
				originalCipher = ReadEncryptedImage(dac, fqName);
			}

			// Ciphertext is UTF-16LE and length-preserving, so it must be a whole number of
			// characters for a dummy statement to be constructible at all.
			if (originalCipher is null || originalCipher.Length == 0 || originalCipher.Length % 2 != 0)
			{
				return null;
			}

			var dummyStatement = BuildDummyStatement(objectTypeKeyword, quotedSchema, quotedName, originalCipher.Length / 2);
			var dummyPlaintextBytes = Encoding.Unicode.GetBytes(dummyStatement);
			if (dummyPlaintextBytes.Length != originalCipher.Length)
			{
				return null;
			}

			byte[]? dummyCipher;
			using (var admin = new SqlConnection(builder.ConnectionString))
			{
				admin.Open();
				using var transaction = admin.BeginTransaction();
				try
				{
					using (var alter = new SqlCommand(dummyStatement, admin, transaction))
					{
						alter.ExecuteNonQuery();
					}

					using (var dac = new SqlConnection(dacBuilder.ConnectionString))
					{
						dac.Open();
						dummyCipher = ReadEncryptedImage(dac, fqName);
					}
				}
				finally
				{
					// The dummy statement must never actually replace the real object,
					// whether or not the recovery attempt below succeeds.
					transaction.Rollback();
				}
			}

			if (dummyCipher is null || dummyCipher.Length != originalCipher.Length)
			{
				return null;
			}

			var recovered = new byte[originalCipher.Length];
			for (var i = 0; i < recovered.Length; i++)
			{
				var keystreamByte = (byte)(dummyCipher[i] ^ dummyPlaintextBytes[i]);
				recovered[i] = (byte)(originalCipher[i] ^ keystreamByte);
			}

			return Encoding.Unicode.GetString(recovered);
		}
		catch
		{
			// Any failure here (no DAC access, insufficient permissions, an unexpected
			// sysobjvalues shape on this SQL Server version, etc.) just means recovery isn't
			// possible - the caller falls back to a plain "encrypted, could not decrypt"
			// message rather than this propagating and breaking the whole scripting call.
			return null;
		}
	}

	private static byte[]? ReadEncryptedImage(SqlConnection dacConnection, string fqName)
	{
		const string query = @"
			SELECT TOP 1 v.imageval
			FROM sys.sysobjvalues v
			WHERE v.objid = OBJECT_ID(@objectName)
				AND v.imageval IS NOT NULL
			ORDER BY DATALENGTH(v.imageval) DESC";

		using var command = new SqlCommand(query, dacConnection);
		command.Parameters.AddWithValue("@objectName", fqName);

		var result = command.ExecuteScalar();
		return result as byte[];
	}

	private static string BuildDummyStatement(string objectTypeKeyword, string quotedSchema, string quotedName, int totalCharCount)
	{
		// A trailing line comment is used as the padding zone: it's valid at the end of both
		// a PROCEDURE body (any batch content) and a VIEW body (a VIEW must be a single SELECT
		// statement, so no BEGIN/END wrapper is usable there - a comment after the SELECT is).
		var header = $"ALTER {objectTypeKeyword} {quotedSchema}.{quotedName}\r\nWITH ENCRYPTION\r\nAS\r\nSELECT 1 AS X -- ";

		var paddingCharsNeeded = totalCharCount - header.Length;
		if (paddingCharsNeeded < 0)
		{
			throw new InvalidOperationException("Original encrypted definition is too short to build a length-matched dummy statement.");
		}

		return header + new string('X', paddingCharsNeeded);
	}

	private static string QuoteIdentifier(string identifier)
	{
		return "[" + identifier.Replace("]", "]]") + "]";
	}
}
