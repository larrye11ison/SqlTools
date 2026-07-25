using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SqlPhanos.ScriptDatabases;

/// <summary>
/// RC4 used by SQL Server for module WITH ENCRYPTION (including SQL Server 2019).
/// Key material: SHA1(family_guid || object_id_le || subobjid_le).
/// </summary>
public static class SqlModuleRc4
{
    private const int KeyMaterialLength = 22;
    private const int StateSize = 256;

    public static byte[] DeriveInitializationKey(Guid familyGuid, int objectId, short subObjectId)
    {
        byte[] material = new byte[KeyMaterialLength];
        familyGuid.ToByteArray().AsSpan().CopyTo(material);
        BinaryPrimitives.WriteInt32LittleEndian(material.AsSpan(16), objectId);
        BinaryPrimitives.WriteInt16LittleEndian(material.AsSpan(20), subObjectId);
        return SHA1.HashData(material);
    }

    public static byte[] Transform(byte[] key, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);

        byte[] state = CreateKeySchedule(key);
        byte[] output = new byte[data.Length];
        int i = 0;
        int j = 0;
        for (int index = 0; index < data.Length; index++)
        {
            i = (i + 1) % StateSize;
            j = (j + state[i]) % StateSize;
            Swap(state, i, j);
            int keyStreamByte = state[(state[i] + state[j]) % StateSize];
            output[index] = (byte)(data[index] ^ keyStreamByte);
        }

        return output;
    }

    private static byte[] CreateKeySchedule(byte[] key)
    {
        if (key.Length == 0)
        {
            throw new ArgumentException("RC4 key must not be empty.", nameof(key));
        }

        byte[] state = new byte[StateSize];
        for (int index = 0; index < StateSize; index++)
        {
            state[index] = (byte)index;
        }

        int j = 0;
        for (int i = 0; i < StateSize; i++)
        {
            j = (j + state[i] + key[i % key.Length]) % StateSize;
            Swap(state, i, j);
        }

        return state;
    }

    private static void Swap(byte[] state, int left, int right)
    {
        (state[left], state[right]) = (state[right], state[left]);
    }
}
