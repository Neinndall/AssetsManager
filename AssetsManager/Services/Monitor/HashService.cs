using System;
using System.Security.Cryptography;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor;

public class HashService
{
    public bool VerifyChunk(ReadOnlySpan<byte> data, ulong expectedChunkId, HashType type)
    {
        ulong actualHash = type switch
        {
            HashType.Sha256 => HashSha256(data),
            HashType.Blake3 => throw new NotSupportedException("Blake3 hash verification is no longer supported."),
            HashType.Hkdf => HashHkdf(data),
            _ => throw new NotSupportedException($"Hash type {type} is not supported.")
        };

        return actualHash == expectedChunkId;
    }

    private ulong HashSha256(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return BitConverter.ToUInt64(hash);
    }

    private ulong HashHkdf(ReadOnlySpan<byte> data)
    {
        Span<byte> key = stackalloc byte[32];
        SHA256.HashData(data, key);
        
        // Use stackalloc for fixed-size buffers to avoid Heap allocation (very fast)
        Span<byte> ipad = stackalloc byte[64];
        Span<byte> opad = stackalloc byte[64];
        ipad.Fill(0x36);
        opad.Fill(0x5C);

        for (int i = 0; i < key.Length; i++)
        {
            ipad[i] ^= key[i];
            opad[i] ^= key[i];
        }

        Span<byte> buffer = stackalloc byte[32];
        Span<byte> step1 = stackalloc byte[64 + 4];
        ipad.CopyTo(step1);
        step1[67] = 1;
        SHA256.HashData(step1, buffer);

        Span<byte> step2 = stackalloc byte[64 + 32];
        opad.CopyTo(step2);
        buffer.CopyTo(step2[64..]);
        SHA256.HashData(step2, buffer);

        Span<byte> result = stackalloc byte[8];
        buffer[..8].CopyTo(result);

        // Pre-allocate loop buffers on stack
        Span<byte> iter1 = stackalloc byte[64 + 32];
        ipad.CopyTo(iter1);
        
        Span<byte> iter2 = stackalloc byte[64 + 32];
        opad.CopyTo(iter2);

        for (int i = 0; i < 31; i++)
        {
            buffer.CopyTo(iter1[64..]);
            SHA256.HashData(iter1, buffer);

            buffer.CopyTo(iter2[64..]);
            SHA256.HashData(iter2, buffer);

            for (int j = 0; j < 8; j++) result[j] ^= buffer[j];
        }

        return BitConverter.ToUInt64(result);
    }
}
