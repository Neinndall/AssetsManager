using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using AssetsManager.Services.Hashes.Guessers.Game;

namespace AssetsManager.Tests.Diagnostics.Hashes;

internal static class GameContainerIndexDiagnostic
{
    internal static void Run(string[] args)
    {
        string source = args.Single(a => a.StartsWith("--hashes=", StringComparison.Ordinal))[9..];
        var folders = File.ReadLines(source).Where(line => line.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(line.IndexOf(' ') + 1)..].ToLowerInvariant())
            .Select(path => path[..path.LastIndexOf('/')].Split('/')).ToArray();
        var containers = folders.Where(parts => parts.Length >= 5 && parts[1] == "characters")
            .Select(parts => (Character: parts[2], Container: parts[4])).Distinct().Take(256).ToHashSet();
        bool Legacy(string[] parts) => parts.Length >= 5 && parts[1] == "characters" &&
            containers.Any(container => container.Character == parts[2] &&
                (parts.Skip(3).Contains(container.Container) ||
                 container.Container == "base" && parts.Skip(3).Contains("skin0")));
        var index = new GameAnimationContainerIndex(containers);
        foreach (string[] parts in folders)
            if (Legacy(parts) != index.Contains(parts)) throw new InvalidDataException("Container selection changed.");
        for (int round = 0; round < 3; round++)
        {
            Measure("legacy", Legacy);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            index = new GameAnimationContainerIndex(containers);
            int matches = folders.Count(index.Contains);
            watch.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new { Round = round, Mode = "indexed-including-build", Paths = folders.Length,
                Containers = containers.Count, Matches = matches, Milliseconds = watch.Elapsed.TotalMilliseconds,
                AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before }));

            void Measure(string mode, Func<string[], bool> predicate)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                int count = folders.Count(predicate);
                timer.Stop();
                Console.WriteLine(JsonSerializer.Serialize(new { Round = round, Mode = mode, Paths = folders.Length,
                    Containers = containers.Count, Matches = count, Milliseconds = timer.Elapsed.TotalMilliseconds,
                    AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated }));
            }
        }
    }
}
