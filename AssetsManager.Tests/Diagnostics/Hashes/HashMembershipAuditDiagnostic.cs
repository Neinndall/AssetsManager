using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Services.Hashes.Guessers.Lcu;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Tests.Diagnostics.Hashes;

internal static class HashMembershipAuditDiagnostic
{
    internal static void Run(string[] args)
    {
        string Option(string name) => args.First(a => a.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];
        string state = Option("--state");
        string root = Option("--root");
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        foreach (string domain in new[] { "game", "lcu" })
            File.Copy(Path.Combine(state, "hashes", $"hashes.{domain}.txt"),
                Path.Combine(bridge.Directories.HashesPath, $"hashes.{domain}.txt"));
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        resolver.LoadHashes();
        using var game = new BinaryHashCache(Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"), bridge.LogService);
        using var lcu = new BinaryHashCache(Path.Combine(bridge.Directories.HashesPath, "hashes.lcu.txt"), bridge.LogService);
        game.Load();
        lcu.Load();
        bool Legacy(ulong hash) => game.Resolve(hash) != null || lcu.Resolve(hash) != null;
        bool Optimized(ulong hash) => resolver.IsKnownHash(hash);
        foreach (var cache in new[] { game, lcu })
        {
            for (int i = 0; i < cache.Count; i++)
            {
                ulong hash = cache.GetHash(i);
                if (Legacy(hash) != Optimized(hash)) throw new InvalidDataException($"Catalog mismatch: {hash:x16}");
            }
        }
        Console.WriteLine($"Catalog equivalence: GAME={game.Count}, LCU={lcu.Count}, mismatches=0");
        foreach (HashGuessDomain domain in new[] { HashGuessDomain.Game, HashGuessDomain.Lcu })
        {
            HashGuesser guesser = domain == HashGuessDomain.Game
                ? new GameHashGuesser(new HashFile(domain, Array.Empty<string>()))
                : new LcuHashGuesser(new HashFile(domain, Array.Empty<string>()), bridge.LogService);
            string[] wads = guesser.FindWads(root);
            if (wads.Length == 0) throw new InvalidDataException($"No WADs for {domain}");
            var readWatch = Stopwatch.StartNew();
            var inventory = guesser.FromWads(wads, CancellationToken.None,
                (path, error) => throw new IOException($"Unreadable WAD: {path}", error));
            double inventoryMs = readWatch.Elapsed.TotalMilliseconds;
            ulong[] hashes = inventory.Hashes.ToArray();
            var oldUnknowns = hashes.Where(hash => !Legacy(hash)).ToHashSet();
            var newUnknowns = hashes.Where(hash => !Optimized(hash)).ToHashSet();
            if (!oldUnknowns.SetEquals(newUnknowns)) throw new InvalidDataException($"Unknown inventory mismatch for {domain}");
            // Include the historical pending inventory in the same equivalence check.
            string pending = Path.Combine(state, "hash_lab", $"unknowns.{domain.ToString().ToLowerInvariant()}.txt");
            if (File.Exists(pending))
                foreach (string line in File.ReadLines(pending).Where(line => !string.IsNullOrWhiteSpace(line)))
                {
                    ulong hash = Convert.ToUInt64(line.Trim(), 16);
                    if (Legacy(hash) != Optimized(hash)) throw new InvalidDataException($"Pending mismatch: {hash:x16}");
                }
            var oldTimes = new List<double>();
            var newTimes = new List<double>();
            var oldBytes = new List<long>();
            var newBytes = new List<long>();
            for (int round = 0; round < 7; round++)
            {
                if (round % 2 == 0) { Measure(Legacy, oldTimes, oldBytes); Measure(Optimized, newTimes, newBytes); }
                else { Measure(Optimized, newTimes, newBytes); Measure(Legacy, oldTimes, oldBytes); }
            }
            Console.WriteLine($"{domain}: WADs={wads.Length}, distinct hashes={hashes.Length}, old unknowns={oldUnknowns.Count}, new unknowns={newUnknowns.Count}, differences=0, inventory read={inventoryMs:F2}ms");
            Console.WriteLine($"{domain}: median filter legacy={oldTimes.Order().ElementAt(3):F2}ms / {oldBytes.Order().ElementAt(3)}B, optimized={newTimes.Order().ElementAt(3):F2}ms / {newBytes.Order().ElementAt(3)}B; 7 alternating rounds, warm catalogs");

            void Measure(Func<ulong, bool> known, List<double> times, List<long> bytes)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                int count = 0;
                foreach (ulong hash in hashes) if (!known(hash)) count++;
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (count != oldUnknowns.Count) throw new InvalidDataException("Measured result changed");
                times.Add(elapsed);
                bytes.Add(allocated);
            }
        }
    }
}
