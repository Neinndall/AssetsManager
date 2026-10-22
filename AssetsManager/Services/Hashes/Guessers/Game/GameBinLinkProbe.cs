using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AssetsManager.Services.Hashes.Guessers.Game;

internal static class GameBinLinkProbe
{
    // This is only a cheap prefilter. Callers must validate links through parsed BIN properties.
    internal static bool ContainsPendingLink(ReadOnlySpan<byte> data, uint fieldHash,
        IReadOnlyCollection<ulong> unknownHashes, CancellationToken cancellationToken)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(field, fieldHash);
        while (data.Length >= 13)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int offset = data.IndexOf(field);
            if (offset < 0 || data.Length - offset < 13) break;
            if (unknownHashes.Contains(BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 5, 8))))
                return true;
            data = data[(offset + 1)..];
        }
        return false;
    }
}
