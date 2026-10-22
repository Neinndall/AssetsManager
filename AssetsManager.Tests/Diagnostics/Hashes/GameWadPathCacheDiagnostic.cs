using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AssetsManager.Services.Hashes.Guessers.Game;

namespace AssetsManager.Tests.Diagnostics.Hashes;

internal static class GameWadPathCacheDiagnostic
{
    internal static void Run(string[] args)
    {
        string root = args.Single(a => a.StartsWith("--root=", StringComparison.Ordinal))[7..];
        string[] paths = new GameHashGuesser().FindWads(root);
        var cache = new GameWadPathCache();
        var baseline = Read("uncached", path => GameWadPathCache.ReadUncached(path).ToArray());
        var cold = Read("cache-cold", path => cache.Read(path, CancellationToken.None));
        Verify(cold);
        var warm = Read("cache-reused", path => cache.Read(path, CancellationToken.None));
        Verify(warm);

        void Verify(IReadOnlyList<ulong>[] actual)
        {
            for (int i = 0; i < paths.Length; i++)
                if (!baseline[i].SequenceEqual(actual[i])) throw new InvalidOperationException($"Header sequence changed: {paths[i]}");
        }

        IReadOnlyList<ulong>[] Read(string mode, Func<string, IReadOnlyList<ulong>> read)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var data = paths.Select(read).ToArray();
            watch.Stop();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine(JsonSerializer.Serialize(new { Mode = mode, Wads = paths.Length, Hashes = data.Sum(h => (long)h.Count),
                Milliseconds = watch.Elapsed.TotalMilliseconds, AllocatedBytes = bytes }));
            return data;
        }
    }
}
