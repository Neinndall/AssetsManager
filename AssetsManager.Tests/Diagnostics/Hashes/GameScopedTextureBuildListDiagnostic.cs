using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Services.Hashes.Guessers.Lcu;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes;

// Measures WAD-scoped texture vocabulary against the real inventory without persisting matches.
internal static class GameScopedTextureBuildListDiagnostic
{
    internal static void Run(string[] args)
    {
        string Option(string key) => args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        string root = Option("--root") ?? @"C:\Riot Games\League of Legends (PBE)";
        string filter = Option("--wad");
        int seconds = int.TryParse(Option("--seconds"), out int value) ? value : 30;
        long budget = long.TryParse(Option("--budget"), out long count) ? count : 50_000_000;
        long perWadBudget = long.TryParse(Option("--per-wad-budget"), out long wadCount) ? wadCount : 2_000_000;
        if (seconds <= 0 || budget <= 0 || perWadBudget <= 0)
            throw new ArgumentException("Time and candidate budgets must be positive.");
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
        var known = new HashFile(HashGuessDomain.Game, Path.Combine(local, "hashes", "hashes.game.txt")).Load();
        var pending = File.ReadLines(Path.Combine(local, "hash_lab", "unknowns.game.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => ulong.Parse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
        pending.ExceptWith(known.Keys);
        string comparison = Option("--compare-context");
        if (comparison != null)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(comparison));
            var targets = json.RootElement.EnumerateArray().SelectMany(record => record.GetProperty("matches").EnumerateArray())
                .Select(record => (Hash: ulong.Parse(record.GetProperty("hash").GetString(), NumberStyles.HexNumber),
                    Path: record.GetProperty("path").GetString())).ToArray();
            var existing = new HashGuessEngine(HashGuessDomain.Game, targets.Select(t => t.Hash).Append(42UL).ToHashSet(),
                match => Console.WriteLine($"EXISTING {match.Hash:x16} {match.Path}"));
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, known.Values));
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, Path.Combine(local, "hashes", "hashes.lcu.txt")), null);
            Measure("Production GAME from LCU", token => game.GuessFromLcuHashes(existing, lcu, token));
            Measure("Production token removal, full catalog", token => game.RemoveBasenameTokens(existing, token));
            var directories = targets.Where(t => t.Path.EndsWith(".anm", StringComparison.Ordinal))
                .Select(t => t.Path[..(t.Path.LastIndexOf('/') + 1)]).ToHashSet(StringComparer.Ordinal);
            var seeds = known.Values.Where(path => directories.Contains(path[..(path.LastIndexOf('/') + 1)])).ToArray();
            var focused = new GameHashGuesser(new HashFile(HashGuessDomain.Game, seeds));
            Console.WriteLine($"Numeric comparison uses {seeds.Length:N0} real catalog seeds from target animation directories");
            Measure("Production numbers, target directories", token => focused.SubstituteNumbers(existing, token, maximum: 200));
            Measure("Production padded numbers, target directories", token => focused.SubstituteBasicPaddedNumbers(existing, token));
            foreach (var target in targets.Where(t => !existing.Matches.ContainsKey(t.Hash)))
                Console.WriteLine($"ADDITIONAL_TO_MEASURED {target.Hash:x16} {target.Path}");
            Console.WriteLine("Read-only comparison. Other Basic/Extended/Grep methods were not exhaustively tested.");
            return;

            void Measure(string name, Action<CancellationToken> action)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var watch = Stopwatch.StartNew();
                long before = existing.CheckedCandidates;
                int matches = existing.Matches.Count;
                bool complete = true;
                try { action(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { complete = false; }
                Console.WriteLine($"{name}: {existing.Matches.Count - matches} matches; {existing.CheckedCandidates - before:N0} checks; {watch.Elapsed.TotalSeconds:F2}s; complete={complete}");
            }
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var timer = Stopwatch.StartNew();
        var contexts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var active = new HashSet<ulong>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Game"), "*.wad.client", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            using var wad = new WadFile(file);
            bool textures = false;
            foreach (ulong hash in wad.Chunks.Keys.Where(pending.Contains))
            {
                var chunk = wad.Chunks[hash];
                if (chunk.Compression == WadChunkCompression.Satellite) continue;
                using var data = wad.LoadChunkDecompressed(chunk);
                string extension = HashGuessingService.InferChunkExtension(data.DangerousGetArray(), false);
                if (extension is not ("tex" or "dds" or "jpg")) continue;
                active.Add(hash);
                textures = true;
            }
            if (!textures) continue;
            var paths = wad.Chunks.Keys.Where(known.ContainsKey).Select(hash => known[hash])
                .Where(Texture).ToHashSet(StringComparer.Ordinal);
            contexts[file] = paths;
        }
        var engine = new HashGuessEngine(HashGuessDomain.Game, active,
            match => Console.WriteLine($"MATCH {match.Hash:x16} {match.Path} [{match.SourceWadPath}] at {timer.Elapsed.TotalSeconds:F2}s"));
        Console.WriteLine($"Scoped build-list: {pending.Count} persisted unknowns, {active.Count} current texture targets, {contexts.Count} WADs; no persistence");
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string path in known.Values.Where(Texture))
        {
            cancellation.Token.ThrowIfCancellationRequested();
            int slash = path.LastIndexOf('/');
            if (path.AsSpan(slash + 1, path.Length - slash - 5).Contains('.')) continue;
            int end = path.Length - 4;
            for (int i = 0; i < 3; i++)
            {
                int separator = path.LastIndexOf('_', end - 1);
                if (separator <= slash) break;
                string suffix = path[(separator + 1)..];
                frequencies[suffix] = frequencies.GetValueOrDefault(suffix) + 1;
                end = separator;
            }
        }
        var suffixes = frequencies.Where(pair => pair.Value >= 2).OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(25_000).Select(pair => pair.Key).ToArray();
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (wad, paths) in contexts)
        foreach (string path in paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (args.Contains("--exclude-particles", StringComparer.Ordinal) && path.Contains("/particles/", StringComparison.Ordinal)) continue;
            int slash = path.LastIndexOf('/');
            int end = path.Length - 4;
            for (int i = 0; i < 4; i++)
            {
                int separator = path.LastIndexOf('_', end - 1);
                if (separator <= slash) break;
                prefixes.TryAdd(path[..(separator + 1)], wad);
                end = separator;
            }
        }
        Console.WriteLine($"Index: {suffixes.Length:N0} suffixes, {prefixes.Count:N0} active prefixes; preparation {timer.Elapsed.TotalSeconds:F2}s");
        if (args.Contains("--local", StringComparer.Ordinal))
        Run("WAD-local suffix vocabulary", () =>
        {
            foreach (var (wad, paths) in contexts)
            {
                var localSuffixes = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string path in paths)
                {
                    int slash = path.LastIndexOf('/');
                    int end = path.Length - 4;
                    for (int i = 0; i < 3; i++)
                    {
                        int at = path.LastIndexOf('_', end - 1);
                        if (at <= slash) break;
                        string suffix = path[(at + 1)..];
                        localSuffixes[suffix] = localSuffixes.GetValueOrDefault(suffix) + 1;
                        end = at;
                    }
                }
                var localPrefixes = prefixes.Where(pair => pair.Value == wad).Select(pair => pair.Key).ToArray();
                long start = engine.CheckedCandidates;
                foreach (string suffix in localSuffixes.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key))
                {
                    foreach (string prefix in localPrefixes)
                    {
                        if (!Check(prefix, suffix, wad)) return;
                        if (engine.CheckedCandidates - start >= perWadBudget) break;
                    }
                    if (engine.CheckedCandidates - start >= perWadBudget) break;
                }
            }
        });
        if (!args.Contains("--local-only", StringComparer.Ordinal))
        Run("Scoped global suffixes", () =>
        {
            foreach (string suffix in suffixes)
            foreach (var (prefix, wad) in prefixes)
                if (!Check(prefix, suffix, wad)) return;
        });
        if (args.Contains("--relocate", StringComparer.Ordinal))
        Run("WAD-local basename relocation", () =>
        {
            foreach (var (wad, paths) in contexts)
            {
                var directories = paths.Select(path => path[..(path.LastIndexOf('/') + 1)]).Distinct(StringComparer.Ordinal).ToArray();
                var names = paths.Select(path => path[(path.LastIndexOf('/') + 1)..]).Distinct(StringComparer.Ordinal).ToArray();
                foreach (string name in names)
                foreach (string directory in directories)
                    if (!Check(directory, name, wad)) return;
            }
        });
        Console.WriteLine($"TOTAL: {engine.Matches.Count} matches; {engine.CheckedCandidates:N0} candidates; {timer.Elapsed.TotalSeconds:F2}s including preparation. Nothing persisted.");
        if (args.Contains("--compare-basics", StringComparer.Ordinal))
        {
            var existing = new HashGuessEngine(HashGuessDomain.Game, engine.Matches.Keys.Append(42UL).ToHashSet(),
                match => Console.WriteLine($"BASIC_EXISTING {match.Hash:x16} {match.Path}"));
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, known.Values));
            Compare("Token removal", token => guesser.RemoveBasenameTokens(existing, token));
            Compare("Numbers", token => guesser.SubstituteNumbers(existing, token, maximum: 200));
            foreach (var match in engine.Matches.Values.Where(match => !existing.Matches.ContainsKey(match.Hash)))
                Console.WriteLine($"ADDITIONAL_TO_MEASURED_BASIC {match.Hash:x16} {match.Path}");

            void Compare(string name, Action<CancellationToken> run)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                long start = Stopwatch.GetTimestamp();
                long before = existing.CheckedCandidates;
                bool completed = true;
                try { run(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { completed = false; }
                Console.WriteLine($"Basic {name}: {existing.CheckedCandidates - before:N0} candidates; {Stopwatch.GetElapsedTime(start).TotalSeconds:F2}s; {(completed ? "complete" : "time-limited")}");
            }
        }

        bool Check(string prefix, string suffix, string wad)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (engine.CheckedCandidates >= budget || engine.RemainingUnknownCount == 0) return false;
            engine.CheckPrefixSuffix(prefix, suffix, HashGuessStrategy.WordlistVariant, wad);
            return true;
        }
        void Run(string name, Action run)
        {
            long before = engine.CheckedCandidates;
            int hits = engine.Matches.Count;
            long start = Stopwatch.GetTimestamp();
            bool complete = true;
            try
            {
                run();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { complete = false; }
            string coverage = !complete ? "time-limited" : engine.CheckedCandidates >= budget ? "budget-limited" :
                name == "WAD-local suffix vocabulary" ? $"bounded at {perWadBudget:N0} candidates per WAD" : "complete";
            Console.WriteLine($"{name}: {engine.Matches.Count - hits} matches; {engine.CheckedCandidates - before:N0} candidates; {Stopwatch.GetElapsedTime(start).TotalSeconds:F2}s; {coverage}");
        }
        static bool Texture(string path) => path.EndsWith(".tex", StringComparison.Ordinal) ||
            path.EndsWith(".dds", StringComparison.Ordinal) || path.EndsWith(".jpg", StringComparison.Ordinal);
    }
}
