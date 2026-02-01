using System;
using System.Security.Cryptography;
using AssetsManager.Views.Models.Monitor;
using Blake3;

namespace AssetsManager.Services.Riot;

public class HashService
{
    public bool VerifyChunk(byte[] data, ulong expectedChunkId, HashType type)
    {
        ulong actualHash = type switch
        {
            HashType.Sha256 => HashSha256(data),
            HashType.Blake3 => HashBlake3(data),
            HashType.Hkdf => HashHkdf(data),
            _ => throw new NotSupportedException($"Hash type {type} is not supported.")
        };

        return actualHash == expectedChunkId;
    }

    private ulong HashSha256(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return BitConverter.ToUInt64(hash, 0);
    }

    private ulong HashBlake3(byte[] data)
    {
        using var hasher = Hasher.New();
        hasher.Update(data);
        var hash = hasher.Finalize();
        return BitConverter.ToUInt64(hash.AsSpan()[..8]);
    }

    private ulong HashHkdf(byte[] data)
    {
        byte[] key = SHA256.HashData(data);
        byte[] ipad = new byte[64];
        byte[] opad = new byte[64];
        Array.Fill(ipad, (byte)0x36);
        Array.Fill(opad, (byte)0x5C);

        for (int i = 0; i < key.Length; i++)
        {
            ipad[i] ^= key[i];
            opad[i] ^= key[i];
        }

        byte[] buffer = new byte[32];
        byte[] step1 = new byte[64 + 4];
        Array.Copy(ipad, step1, 64);
        step1[67] = 1;
        buffer = SHA256.HashData(step1);

        byte[] step2 = new byte[64 + 32];
        Array.Copy(opad, step2, 64);
        Array.Copy(buffer, 0, step2, 64, 32);
        buffer = SHA256.HashData(step2);

        byte[] result = new byte[8];
        Array.Copy(buffer, result, 8);

        for (int i = 0; i < 31; i++)
        {
            byte[] iter1 = new byte[64 + 32];
            Array.Copy(ipad, iter1, 64);
            Array.Copy(buffer, 0, iter1, 64, 32);
            buffer = SHA256.HashData(iter1);

            byte[] iter2 = new byte[64 + 32];
            Array.Copy(opad, iter2, 64);
            Array.Copy(buffer, 0, iter2, 64, 32);
            buffer = SHA256.HashData(iter2);

            for (int j = 0; j < 8; j++) result[j] ^= buffer[j];
        }

        return BitConverter.ToUInt64(result, 0);
    }
}
