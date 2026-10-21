using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Services.Hashes.Guessers.Lcu;
using AssetsManager.Views.Models.Hashes;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes;

// Bounded experiments against an immutable snapshot; only exact XXH64 matches are reported.
internal static class GameEvidencePatternsDiagnostic
{
    internal static void Run(string[] args)
    {
        string Option(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        string state = Option("--state") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
        string root = Option("--root") ?? @"C:\Riot Games\League of Legends (PBE)";
        string stage = Option("--stage") ?? "roles";
        string filter = Option("--wad");
        int seconds = int.Parse(Option("--seconds") ?? "90", CultureInfo.InvariantCulture);
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var hashFile = new HashFile(HashGuessDomain.Game, Path.Combine(state, "hashes", "hashes.game.txt"));
        var known = hashFile.Load();
        var pending = File.ReadLines(Path.Combine(state, "hash_lab", "unknowns.game.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => ulong.Parse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
        if (Option("--exclude") is string exclusions)
            pending.ExceptWith(File.ReadLines(exclusions).Where(line => line.Length >= 16)
                .Select(line => ulong.Parse(line.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        var engine = new HashGuessEngine(HashGuessDomain.Game, pending,
            match => Console.WriteLine($"MATCH {match.Hash:x16} {match.Path} [{match.SourceWadPath}]"));
        var seeded = new HashSet<ulong>();
        if (Option("--seed") is string seedFile)
            foreach (string line in File.ReadLines(seedFile))
            {
                int separator = line.IndexOf(' ');
                if (separator <= 0) continue;
                ulong hash = ulong.Parse(line.AsSpan(0, separator), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                string path = PathUtils.NormalizePath(line[(separator + 1)..]);
                if (XxHash64Ext.Hash(path) != hash) throw new InvalidDataException("Session seed does not match its XXH64 hash.");
                if (engine.Check(path, HashGuessStrategy.WordlistVariant, "Verified session seed")) seeded.Add(hash);
            }
        if (args.Contains("--learn-seed", StringComparer.Ordinal) && seeded.Count > 0)
        {
            hashFile = new HashFile(HashGuessDomain.Game, known.Values.Concat(engine.Matches.Values.Select(match => match.Path)));
            known = hashFile.Load();
        }
        var guesser = new GameHashGuesser(hashFile);
        var watch = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        int errors = 0;
        var visited = new HashSet<(ulong, ulong)>();
        bool complete = false;
        Console.WriteLine($"Evidence patterns {stage}: {pending.Count} targets; time budget {seconds}s; no persistence");
        try
        {
            if (stage == "map-siblings")
            {
                Measure("MAP geometry companions", () => guesser.GuessMapGeometrySiblings(engine, cancellation.Token));
            }
            else if (stage == "script-families")
            {
                Measure("Character script families", () => guesser.GuessScriptFamilyAliases(engine, cancellation.Token));
            }
            else if (stage == "grep-preloads")
            {
                foreach (string file in guesser.FindWads(root).Where(path => Path.GetFileName(path).Equals("Scripts.wad.client", StringComparison.OrdinalIgnoreCase)))
                {
                    using var wad = new WadFile(file);
                    foreach (var chunk in wad.Chunks.Values)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (chunk.Compression == WadChunkCompression.Satellite) continue;
                        bool named = known.TryGetValue(chunk.PathHash, out string path);
                        if (named && !path.EndsWith(".preload", StringComparison.Ordinal)) continue;
                        using var data = wad.LoadChunkDecompressed(chunk);
                        var bytes = data.DangerousGetArray();
                        if (!named)
                        {
                            if (HashGuessingService.InferChunkExtension(bytes, false) != "preload") continue;
                            path = chunk.PathHash.ToString("x16") + ".preload";
                        }
                        guesser.GrepWad(engine, bytes, path, file, chunk.PathHash, cancellation.Token);
                    }
                }
            }
            else if (stage == "basic")
            {
                var lcu = new HashFile(HashGuessDomain.Lcu, Path.Combine(state, "hashes", "hashes.lcu.txt"));
                Measure("Cross domain", () => guesser.GuessFromLcuHashes(engine, new LcuHashGuesser(lcu, null), cancellation.Token));
                Measure("Characters", () => guesser.GuessCharactersFiles(engine, cancellation.Token));
                Measure("Regalia", () => guesser.GuessRegaliaAssets(engine, cancellation.Token));
                Measure("Shaders", () => guesser.GuessShaderVariants(engine, cancellation.Token, rootDirectory: root));
                Measure("Copies", () => guesser.GuessIdenticalCopies(engine, root, cancellation.Token));
                Measure("Locales", () => guesser.SubstituteLang(engine, cancellation.Token));
                Measure("Extensions", () => guesser.SubstituteExtensions(engine, cancellation.Token));
                Measure("Prefixes", () => guesser.CheckBasenamePrefixes(engine, cancellation.Token));
                Measure("Token removal", () => guesser.RemoveBasenameTokens(engine, cancellation.Token));
                Measure("Numbers", () => guesser.SubstituteNumbers(engine, cancellation.Token, maximum: 200));
                Measure("Padded numbers", () => guesser.SubstituteBasicPaddedNumbers(engine, cancellation.Token));
            }
            else if (stage == "grep-bins")
            {
                string binFilter = Option("--bin") ?? throw new ArgumentException("Provide --bin=<path fragment>.");
                foreach (string file in guesser.FindWads(root))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    using var wad = new WadFile(file);
                    foreach (var (hash, chunk) in wad.Chunks)
                    {
                        if (chunk.Compression == WadChunkCompression.Satellite || !known.TryGetValue(hash, out string path) ||
                            !path.EndsWith(".bin", StringComparison.Ordinal) || !path.Contains(binFilter, StringComparison.OrdinalIgnoreCase) ||
                            !visited.Add((hash, chunk.Checksum))) continue;
                        using var data = wad.LoadChunkDecompressed(chunk);
                        Measure(path, () => guesser.GrepWad(engine, data.DangerousGetArray(), path, file, hash, cancellation.Token));
                    }
                }
            }
            else if (stage == "portraits")
            {
                Measure("Learned portraits", () => guesser.GuessPortraitVariants(engine, cancellation.Token));
            }
            else if (stage == "custom")
            {
                guesser.RunCustomAttacks(engine, null, cancellation.Token,
                    Option("--method") is string method ? new HashSet<string> { method } : null, root);
            }
            else if (stage == "banners")
            {
                Measure("Esports banners", () => guesser.GuessEsportsBanners(engine, null, cancellation.Token));
            }
            else if (stage == "local-names")
            {
                Measure("Local animations", () => guesser.GuessLocalNamePatterns(engine, root, true,
                    cancellation.Token, long.MaxValue, null));
                Measure("Local textures", () => guesser.GuessLocalNamePatterns(engine, root, false,
                    cancellation.Token, long.MaxValue, null));
            }
            else if (stage == "numbers")
            {
                Measure("Numbers", () => guesser.SubstituteNumbers(engine, cancellation.Token, maximum: 200));
                Measure("Padded numbers", () => guesser.SubstituteBasicPaddedNumbers(engine, cancellation.Token));
            }
            else if (stage is "legacy-textures" or "legacy-bins" or "scoped-suffixes")
            {
                var activePaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (string file in guesser.FindWads(root))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    using var wad = new WadFile(file);
                    if (!wad.Chunks.Keys.Any(pending.Contains)) continue;
                    foreach (ulong hash in wad.Chunks.Keys)
                        if (known.TryGetValue(hash, out string path)) activePaths.Add(path);
                }
                if (stage == "scoped-suffixes")
                {
                    var expression = new Regex(@"^(.*?)(\.[^.]+)?(\.[^.]+)$");
                    var suffixes = new HashSet<string>(StringComparer.Ordinal) { "" };
                    var formats = new HashSet<(string Prefix, string Extension)>();
                    foreach (string path in known.Values)
                    {
                        Match match = expression.Match(path);
                        if (!match.Success) continue;
                        suffixes.Add(match.Groups[2].Value);
                        if (activePaths.Contains(path)) formats.Add((match.Groups[1].Value, match.Groups[3].Value));
                    }
                    Console.WriteLine($"Scoped suffixes: {formats.Count} formats; {suffixes.Count} suffixes");
                    foreach (string suffix in suffixes)
                    foreach (var format in formats)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        engine.CheckNormalizedParts(format.Prefix, suffix, format.Extension, HashGuessStrategy.WordlistVariant,
                            "WAD-scoped patch suffixes");
                    }
                }
                else foreach (string extension in stage == "legacy-bins" ? new[] { ".bin" } : new[] { ".dds", ".tex" })
                {
                    string[] paths = known.Values.Where(path => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
                        (stage == "legacy-bins" || path.StartsWith("assets/characters/", StringComparison.Ordinal))).ToArray();
                    var words = HashGuessEngine.BuildWordlist(paths.Select(Path.GetFileName)).Take(15_000).ToArray();
                    string[] scoped = paths.Where(activePaths.Contains).ToArray();
                    Console.WriteLine($"Legacy {extension}: {words.Length} original vocabulary words; {scoped.Length} paths in WADs with pending targets");
                    Measure("Legacy " + extension, () => guesser.RunFocusedWordlistSubstitution(engine, scoped, words,
                        cancellation.Token, int.MaxValue));
                }
            }
            else if (stage == "roles" && filter == null)
            {
                Measure("Material roles", () => guesser.GuessMaterialTextureRoles(engine, root, cancellation.Token));
            }
            else if (stage == "roles")
            {
                var index = new GameMaterialTextureIndex(known.Values, cancellation.Token);
                foreach (string file in guesser.FindWads(root))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    using var wad = new WadFile(file);
                    if (!wad.Chunks.Keys.Any(pending.Contains)) continue;
                    foreach (var chunk in wad.Chunks.Values)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (chunk.Compression == WadChunkCompression.Satellite || !known.TryGetValue(chunk.PathHash, out string path) ||
                            !path.EndsWith(".bin", StringComparison.Ordinal) || !visited.Add((chunk.PathHash, chunk.Checksum))) continue;
                        try
                        {
                            using var data = wad.LoadChunkDecompressed(chunk);
                            if (!FileTypeDetector.IsPropertyBin(data.Span)) continue;
                            var bytes = data.DangerousGetArray();
                            using var stream = new MemoryStream(bytes.Array, bytes.Offset, bytes.Count, false);
                            var tree = new BinTree(stream);
                            index.Guess(engine, tree, path, file, chunk.PathHash, cancellation.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            errors++;
                            if (errors <= 5) Console.WriteLine($"READ ERROR {path}: {ex.Message}");
                        }
                    }
                    Console.WriteLine($"WAD {Path.GetFileName(file)}: {engine.CheckedCandidates:N0} checks; {engine.Matches.Count} hits; {watch.Elapsed.TotalSeconds:F1}s");
                }
            }
            else if (stage == "number-pairs")
            {
                var paths = new HashSet<string>(StringComparer.Ordinal);
                string extension = Option("--extension") ?? ".anm";
                int maximum = int.Parse(Option("--maximum") ?? "64", CultureInfo.InvariantCulture);
                if (maximum is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maximum));
                foreach (string file in guesser.FindWads(root))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    using var wad = new WadFile(file);
                    if (!wad.Chunks.Keys.Any(pending.Contains)) continue;
                    foreach (ulong hash in wad.Chunks.Keys)
                        if (known.TryGetValue(hash, out string path) && path.EndsWith(extension, StringComparison.Ordinal)) paths.Add(path);
                }
                var digits = new Regex("[0-9]+", RegexOptions.CultureInvariant);
                var formats = new HashSet<(string Prefix, string Middle, string Suffix, int Width1, int Width2)>();
                foreach (string path in paths)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    int start = path.LastIndexOf('/') + 1;
                    int end = path.IndexOf('.', start);
                    if (end < 0) continue;
                    var runs = digits.Matches(path[start..end]).Where(match =>
                        !(match.Index >= 4 && path.AsSpan(start + match.Index - 4, 4).SequenceEqual("skin"))).ToArray();
                    for (int i = 0; i < runs.Length; i++)
                    for (int j = i + 1; j < runs.Length; j++)
                    {
                        Match first = runs[i], second = runs[j];
                        formats.Add((path[..(start + first.Index)], path[(start + first.Index + first.Length)..(start + second.Index)],
                            path[(start + second.Index + second.Length)..], first.Value[0] == '0' ? first.Length : 1,
                            second.Value[0] == '0' ? second.Length : 1));
                    }
                }
                Console.WriteLine($"Numeric pairs: {paths.Count:N0} local paths; {formats.Count:N0} unique templates; 0..{maximum - 1} per counter");
                var values = new Dictionary<int, string[]>();
                Span<char> middle = stackalloc char[1024];
                foreach (var format in formats.OrderBy(value => value.Prefix, StringComparer.Ordinal).ThenBy(value => value.Middle, StringComparer.Ordinal)
                    .ThenBy(value => value.Suffix, StringComparer.Ordinal).ThenBy(value => value.Width1).ThenBy(value => value.Width2))
                {
                    foreach (int width in new[] { format.Width1, format.Width2 })
                        if (!values.ContainsKey(width)) values[width] = Enumerable.Range(0, maximum)
                            .Select(value => value.ToString("D" + width, CultureInfo.InvariantCulture)).ToArray();
                    foreach (string first in values[format.Width1])
                    foreach (string second in values[format.Width2])
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        int length = first.Length + format.Middle.Length + second.Length;
                        if (length > middle.Length) throw new InvalidDataException("Numeric template exceeds the experiment buffer.");
                        first.AsSpan().CopyTo(middle);
                        format.Middle.AsSpan().CopyTo(middle[first.Length..]);
                        second.AsSpan().CopyTo(middle[(first.Length + format.Middle.Length)..]);
                        engine.CheckNormalizedParts(format.Prefix, middle[..length], format.Suffix,
                            HashGuessStrategy.NumberVariant, "GAME paired filename counters");
                    }
                }
            }
            else if (stage == "typos")
            {
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (string file in guesser.FindWads(root))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    using var wad = new WadFile(file);
                    if (!wad.Chunks.Keys.Any(pending.Contains)) continue;
                    foreach (ulong hash in wad.Chunks.Keys)
                        if (known.TryGetValue(hash, out string path) && (path.EndsWith(".tex", StringComparison.Ordinal) || path.EndsWith(".anm", StringComparison.Ordinal))) paths.Add(path);
                }
                foreach (string path in paths.OrderBy(p => p, StringComparer.Ordinal))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    int start = path.LastIndexOf('/') + 1, end = path.Length - 4;
                    for (int at = start; at < end; at++)
                    {
                        Check(path.AsSpan(0, at), "", path.AsSpan(at + 1));
                        if (at + 1 < end) Check(path.AsSpan(0, at), string.Concat(path[at + 1], path[at]), path.AsSpan(at + 2));
                        foreach (char letter in "abcdefghijklmnopqrstuvwxyz0123456789_-")
                            if (letter != path[at]) Check(path.AsSpan(0, at), letter.ToString(), path.AsSpan(at + 1));
                        foreach (char letter in "abcdefghijklmnopqrstuvwxyz0123456789_-")
                            Check(path.AsSpan(0, at), letter.ToString(), path.AsSpan(at));
                    }
                    foreach (char letter in "abcdefghijklmnopqrstuvwxyz0123456789_-") Check(path.AsSpan(0, end), letter.ToString(), path.AsSpan(end));
                }
            }
            else throw new ArgumentException("Stage must be map-siblings, script-families, grep-preloads, basic, numbers, portraits, grep-bins, custom, banners, local-names, roles, legacy-textures, legacy-bins, scoped-suffixes, number-pairs or typos.");
            complete = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Console.WriteLine($"RESULT {stage}: {engine.Matches.Count - seeded.Count} new hits; {engine.CheckedCandidates - seeded.Count:N0} checks; {engine.RemainingUnknownCount} remaining; {watch.Elapsed.TotalSeconds:F2}s; complete={complete}; errors={errors}; allocated={(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1_000_000:N0} MB; no persistence");
        if (Option("--out") is string output)
            File.WriteAllLines(output, engine.Matches.Values.Where(m => !seeded.Contains(m.Hash)).OrderBy(m => m.Path, StringComparer.Ordinal).Select(m => $"{m.Hash:x16} {m.Path}"));
        void Check(ReadOnlySpan<char> prefix, string middle, ReadOnlySpan<char> suffix)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            engine.CheckNormalizedParts(prefix, middle, suffix, HashGuessStrategy.WordlistVariant, "GAME filename edit");
        }
        void Measure(string name, Action action)
        {
            long before = engine.CheckedCandidates;
            int found = engine.Matches.Count;
            var timer = Stopwatch.StartNew();
            action();
            Console.WriteLine($"STAGE {name}: {engine.Matches.Count - found} hits; {engine.CheckedCandidates - before:N0} checks; {timer.Elapsed.TotalSeconds:F2}s");
        }
    }
}
