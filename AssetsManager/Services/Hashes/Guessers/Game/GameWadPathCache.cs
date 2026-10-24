using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AssetsManager.Services.Hashes.Guessers.Game;

// Owned by one Custom run. Cache raw membership only: pending hashes and learned names change between methods.
internal sealed class GameWadPathCache
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _byteBudget;
    private long _retainedBytes;

    internal GameWadPathCache(long byteBudget = 16 * 1024 * 1024) => _byteBudget = Math.Max(0, byteBudget);

    internal IReadOnlyList<ulong> Read(string wadPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string key = Path.GetFullPath(wadPath);
        var stamp = Stamp(key);
        if (_entries.TryGetValue(key, out var cached))
        {
            if (cached.Stamp == stamp) return cached.Hashes;
            _retainedBytes -= cached.Hashes.LongLength * sizeof(ulong);
            _entries.Remove(key);
        }

        ulong[] hashes = ReadUncached(key).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        long bytes = hashes.LongLength * sizeof(ulong);
        // Failed, changing or over-budget files are retried by the next method.
        if (bytes <= _byteBudget - _retainedBytes && IsUnchanged(key, stamp))
        {
            _entries.Add(key, new Entry(stamp, hashes));
            _retainedBytes += bytes;
        }
        return hashes;
    }

    internal static IEnumerable<ulong> ReadUncached(string wadPath)
    {
        using var wad = HashGuesser.OpenWad(wadPath);
        foreach (ulong hash in wad.Chunks.Keys) yield return hash;
    }

    private static (long Length, DateTime LastWrite) Stamp(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static bool IsUnchanged(string path, (long Length, DateTime LastWrite) stamp)
    {
        try { return Stamp(path) == stamp; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record Entry((long Length, DateTime LastWrite) Stamp, ulong[] Hashes);
}
