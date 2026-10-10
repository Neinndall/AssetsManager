using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes;

// Candidate experiments use installed WAD families and the real pending inventory; nothing is persisted.
internal static class GamePatternLabDiagnostic
{
    internal static void Run(string[] args)
    {
        string Option(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        string root = Option("--root") ?? @"C:\Riot Games\League of Legends (PBE)";
        string filter = Option("--wad");
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
        var known = new HashFile(HashGuessDomain.Game, Path.Combine(local, "hashes", "hashes.game.txt")).Load();
        var unknown = File.ReadLines(Path.Combine(local, "hash_lab", "unknowns.game.txt"))
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => ulong.Parse(l.Trim(), NumberStyles.HexNumber)).ToHashSet();
        using var cancellation = new CancellationTokenSource();
        if (int.TryParse(Option("--seconds"), out int seconds) && seconds > 0) cancellation.CancelAfter(TimeSpan.FromSeconds(seconds));
        var engine = new HashGuessEngine(HashGuessDomain.Game, unknown,
            m => Console.WriteLine($"MATCH {m.Hash:x16} {m.Path} [{m.SourceWadPath}]"));
        var timer = Stopwatch.StartNew();
        Console.WriteLine($"Pattern lab: {unknown.Count} unknowns, persistence disabled");
        if (args.Contains("--custom-local", StringComparer.Ordinal))
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, known.Values));
            foreach (bool animationPatterns in new[] { true, false })
            {
                var run = new HashGuessEngine(HashGuessDomain.Game, unknown.ToHashSet(),
                    match => Console.WriteLine($"MATCH {match.Hash:x16} {match.Path}"));
                timer.Restart();
                long count = guesser.GuessLocalNamePatterns(run, root, animationPatterns, CancellationToken.None, long.MaxValue, null);
                Console.WriteLine($"Custom {(animationPatterns ? "Animation" : "Texture")} local patterns: {run.Matches.Count} hits; {count:N0} checks; {timer.Elapsed.TotalSeconds:F2}s; complete");
            }
            return;
        }
        if (Option("--compare-edits") is string report)
        {
            var targets = File.ReadLines(report).Where(l => l.StartsWith("MATCH ", StringComparison.Ordinal))
                .Select(l => ulong.Parse(l.AsSpan(6, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
            var baseline = new HashGuessEngine(HashGuessDomain.Game, targets.Append(42UL).ToHashSet(),
                m => Console.WriteLine($"EXISTING {m.Hash:x16} {m.Path}"));
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, known.Values));
            Measure("Token removal", token => guesser.RemoveBasenameTokens(baseline, token));
            Measure("BIN-referenced animations", token => guesser.GuessBinReferencedAnimations(baseline, root, token,
                candidateBudget: 100_000_000));
            Measure("Custom animations", token => guesser.SubstituteAnimationBuildListWords(baseline, token,
                candidateBudget: 100_000_000, rootDirectory: root));
            Measure("Custom textures", token => guesser.SubstituteTextureBuildListWords(baseline, token,
                candidateBudget: 100_000_000, rootDirectory: root));
            foreach (ulong hash in targets.Except(baseline.Matches.Keys))
                Console.WriteLine($"ADDITIONAL_TO_MEASURED {hash:x16}");
            var located = new HashSet<ulong>();
            foreach (string file in guesser.FindWads(root))
            {
                using var wad = new WadFile(file);
                located.UnionWith(wad.Chunks.Keys.Where(targets.Contains));
            }
            Console.WriteLine($"Located {located.Count}/{targets.Count} targets in installed GAME. No persistence.");
            Console.WriteLine("Comparisons use bounded candidate budgets; they do not establish exhaustive coverage.");
            return;

            void Measure(string name, Action<CancellationToken> action)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var watch = Stopwatch.StartNew();
                long before = baseline.CheckedCandidates;
                int hits = baseline.Matches.Count;
                bool complete = true;
                try { action(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { complete = false; }
                Console.WriteLine($"{name}: {baseline.Matches.Count - hits} matches; {baseline.CheckedCandidates - before:N0} checks; {watch.Elapsed.TotalSeconds:F2}s; completedWithinBudget={complete}");
            }
        }
        if (args.Contains("--exe", StringComparer.Ordinal))
        {
            string binary = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(root, "Game", "League of Legends.exe")));
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(binary,
                         @"(?:assets|data|maps|characters|clientstates|loadouts|ux|shaders)/[a-z0-9_./\\-]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string path = PathUtils.NormalizePath(match.Value);
                if (!paths.Add(path)) continue;
                Check(path, "executable path");
                foreach (string extension in new[] { ".bin", ".inibin", ".tex", ".dds", ".skn", ".skl", ".anm" })
                    Check(path + extension, "executable path extension");
                if (path.Contains('.')) Console.WriteLine($"EXE PATH {path}");
            }
            Console.WriteLine($"Executable: {paths.Count} paths; {engine.Matches.Count} hits; {engine.CheckedCandidates} candidates; nothing persisted");
            return;
        }
        if (args.Contains("--characters", StringComparer.Ordinal))
        {
            var characters = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in known.Values)
            {
                int start = path.StartsWith("assets/characters/", StringComparison.Ordinal) ? 18
                    : path.StartsWith("data/characters/", StringComparison.Ordinal) ? 16 : -1;
                if (start < 0) continue;
                int stop = path.IndexOf('/', start);
                if (stop > start) characters.Add(path[start..stop]);
            }
            try
            {
                foreach (string character in characters.OrderBy(c => c, StringComparer.Ordinal))
                {
                    foreach (string ability in new[] { "", "p", "q", "w", "e", "r" })
                    foreach (string number in new[] { "", "1", "2", "3", "4" })
                        Check($"assets/characters/{character}/hud/icons2d/{character}_{ability}{number}.tex", "ability icon TEX");
                    for (int skin = 0; skin < 400; skin++)
                    foreach (string folder in new[] { $"skin{skin}", $"skin{skin:D2}" }.Distinct())
                    foreach (string stemSkin in new[] { $"skin{skin}", $"skin{skin:D2}" }.Distinct())
                    foreach (string role in new[] { "tx_gm", "tx_nm", "tx_m", "tx_mask", "body_tx_cm", "body_tx_gm", "matcap", "matcap_tx", "matcap_tx_cm", "matcap_tex" })
                        Check($"assets/characters/{character}/skins/{folder}/{character}_{stemSkin}_{role}.tex", "fixed character texture");
                }
                Console.WriteLine("Complete");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Console.WriteLine("Time limit reached; coverage is partial");
            }
            Console.WriteLine($"Characters: {characters.Count}; {engine.Matches.Count} hits; {engine.CheckedCandidates:N0} candidates; {timer.Elapsed.TotalSeconds:F1}s; nothing persisted");
            return;
        }
        bool links = args.Contains("--links", StringComparer.Ordinal);
        bool animations = args.Contains("--animations", StringComparer.Ordinal);
        using var resolver = links || animations ? new HashResolverService(new DirectoriesCreator(),
            new LogService(new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger())) : null;
        resolver?.LoadAllHashesAsync().GetAwaiter().GetResult();
        var templates = links ? new GameBinLinkTemplateIndex() : null;
        try
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Game"), "*.wad.client", SearchOption.AllDirectories))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                using var wad = new WadFile(file);
                int pending = wad.Chunks.Keys.Count(unknown.Contains);
                if (pending == 0) continue;
                Console.WriteLine($"WAD {Path.GetFileName(file)}: {pending} pending");
                if (links || animations)
                {
                    foreach (var (hash, chunk) in wad.Chunks)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (chunk.Compression == WadChunkCompression.Satellite) continue;
                        if (known.TryGetValue(hash, out string path) && !path.EndsWith(".bin", StringComparison.Ordinal) && !path.EndsWith(".inibin", StringComparison.Ordinal)) continue;
                        try
                        {
                            using var data = wad.LoadChunkDecompressed(chunk);
                            if (!FileTypeDetector.IsPropertyBin(data.Span)) continue;
                            using var stream = new MemoryStream(data.Span.ToArray(), false);
                            var tree = new BinTree(stream);
                            if (links) templates.Guess(engine, tree, known, resolver.ResolveBinHashGeneral, resolver.ResolveBinXxh3, file, hash, cancellation.Token);
                            else ProbeAnimation(tree);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Console.WriteLine($"BIN scan skipped {hash:x16}: {ex.Message}");
                        }
                    }
                    continue;
                }
                var families = wad.Chunks.Keys.Where(known.ContainsKey).Select(h => known[h])
                    .Where(p => p.Contains('/') && (p.EndsWith(".tex", StringComparison.Ordinal) || p.EndsWith(".dds", StringComparison.Ordinal) || p.EndsWith(".anm", StringComparison.Ordinal)))
                    .GroupBy(p => p[..p.LastIndexOf('/')], StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).ToArray();
                foreach (var family in families.Take(12)) Console.WriteLine($"  FAMILY {family.Count()} {family.Key}");
                if (args.Contains("--edit-tokens", StringComparer.Ordinal))
                {
                    GameHashGuesser.SubstituteLocalNameWords(engine, families.SelectMany(family => family), cancellation.Token);
                    Console.WriteLine($"Local name patterns: {engine.CheckedCandidates:N0} candidates, {engine.Matches.Count} hits");
                    continue;
                }
                if (args.Contains("--swap", StringComparer.Ordinal))
                {
                    var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (string path in families.SelectMany(f => f))
                    foreach (string word in Path.GetFileNameWithoutExtension(path).Split('_', '.'))
                        if (word.Length is >= 3 and <= 40) frequencies[word] = frequencies.GetValueOrDefault(word) + 1;
                    foreach (string line in File.ReadLines(Path.Combine(local, "hashes", "hashes.bintypes.txt")))
                    {
                        string type = line[(line.IndexOf(' ') + 1)..];
                        string[] words = System.Text.RegularExpressions.Regex.Replace(type, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant().Split('_');
                        for (int first = 0; first < words.Length; first++)
                        for (int count = 1; count <= 3 && first + count <= words.Length; count++)
                        {
                            string word = string.Concat(words.Skip(first).Take(count));
                            if (word.Length is >= 3 and <= 40) frequencies[word] = frequencies.GetValueOrDefault(word) + 10;
                        }
                    }
                    string[] vocabulary = frequencies.OrderByDescending(p => p.Value).Take(5_000).Select(p => p.Key).ToArray();
                    Console.WriteLine($"Token swap vocabulary: {vocabulary.Length}");
                    foreach (string path in families.SelectMany(f => f))
                    {
                        int start = path.LastIndexOf('/') + 1;
                        int end = path.IndexOf('.', start);
                        if (end < 0) continue;
                        for (int at = start; at < end;)
                        {
                            int stop = path.IndexOf('_', at);
                            if (stop < 0 || stop > end) stop = end;
                            if (stop - at >= 3)
                            foreach (string word in vocabulary)
                            {
                                cancellation.Token.ThrowIfCancellationRequested();
                                engine.CheckNormalizedParts(path.AsSpan(0, at), word, path.AsSpan(stop), HashGuessStrategy.WordlistVariant, "local token substitution");
                            }
                            at = stop + 1;
                        }
                    }
                    continue;
                }
                long before = engine.CheckedCandidates;
                int hits = engine.Matches.Count;
                foreach (var family in families)
                foreach (string path in family)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    int slash = path.LastIndexOf('/');
                    string folder = path[..(slash + 1)], name = path[(slash + 1)..];
                    string extension = path[^4..], stem = name[..^4];
                    if (stem.Contains('.')) Check(folder + stem[..stem.IndexOf('.')] + extension, "remove decoration");
                    string[] tokens = stem.Split('_');
                    for (int i = 0; i < tokens.Length && tokens.Length > 1; i++)
                        Check(folder + string.Join('_', tokens.Where((_, j) => j != i)) + extension, "remove basename token");
                    if (extension == ".anm")
                        foreach (string suffix in new[] { "_in", "_out", "_loop", "_start", "_end", "_intro", "_outro", "_transition", "_idle", "_run", "_walk", "_layer", "_additive" })
                            Check(folder + stem + suffix + extension, "animation suffix");
                }
                Console.WriteLine($"Simple rules: {engine.CheckedCandidates - before:N0} candidates, {engine.Matches.Count - hits} hits");
                before = engine.CheckedCandidates; hits = engine.Matches.Count;
                int cappedFamilies = 0;
                foreach (var family in families)
                {
                    var prefixes = new HashSet<string>(StringComparer.Ordinal);
                    var suffixes = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (string path in family)
                    {
                        int slash = path.LastIndexOf('/');
                        if (path.AsSpan(slash + 1, path.Length - slash - 5).Contains('.')) continue;
                        int end = path.Length - 4;
                        for (int i = 0; i < 3; i++)
                        {
                            int at = path.LastIndexOf('_', end - 1);
                            if (at <= slash) break;
                            prefixes.Add(path[..(at + 1)]);
                            string suffix = path[(at + 1)..];
                            suffixes[suffix] = suffixes.GetValueOrDefault(suffix) + 1;
                            end = at;
                        }
                    }
                    int probes = 0;
                    foreach (string suffix in suffixes.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key))
                    {
                        foreach (string prefix in prefixes)
                        {
                            Check(prefix + suffix, "local filename recombination");
                            if (++probes >= 2_000_000) break;
                        }
                        if (probes >= 2_000_000) break;
                    }
                    if (probes >= 2_000_000) cappedFamilies++;
                }
                Console.WriteLine($"Local recombination: {engine.CheckedCandidates - before:N0} candidates, {engine.Matches.Count - hits} hits; {cappedFamilies} families reached the candidate limit");
            }
            Console.WriteLine(args.Contains("--edit-tokens", StringComparer.Ordinal)
                ? "Local name patterns complete"
                : "Configured rules complete (local recombination is capped at 2,000,000 candidates per family)");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine("Time limit reached; coverage is partial");
        }
        Console.WriteLine($"Total {engine.Matches.Count} hits; {engine.CheckedCandidates:N0} candidates; {timer.Elapsed.TotalSeconds:F1}s; nothing persisted");

        void Check(string path, string source)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            engine.CheckNormalizedPath(path, HashGuessStrategy.WordlistVariant, source);
        }

        void ProbeAnimation(BinTree tree)
        {
            foreach (var (hash, obj) in tree.Objects)
            {
                if (obj.ClassHash != Fnv1a.HashLower("AnimationGraphData")) continue;
                string entry = resolver.ResolveBinEntry(hash).ToLowerInvariant();
                string[] parts = entry.Split('/');
                if (parts.Length != 4 || parts[0] != "characters" || parts[2] != "animations") continue;
                if (!obj.Properties.TryGetValue(Fnv1a.HashLower("mClipDataMap"), out var prop) || prop is not BinTreeMap clips) continue;
                string character = parts[1], skin = parts[3];
                string padded = skin.Length == 5 && skin.StartsWith("skin") ? "skin0" + skin[4..] : skin;
                var directories = new[] { $"assets/characters/{character}/skins/{skin}/animations/", $"assets/characters/{character}/skins/{padded}/animations/" }.Distinct().ToArray();
                foreach (var pair in clips)
                {
                    if (pair.Key is not BinTreeHash key || pair.Value is not BinTreeStruct clip) continue;
                    if (!Descendants(clip).OfType<BinTreeWadChunkLink>().Any(l => unknown.Contains(l.Value))) continue;
                    string name = resolver.ResolveBinHashGeneral(key.Value);
                    if (name == key.Value.ToString("x8") || name.Contains('/')) continue;
                    var names = new HashSet<string>(StringComparer.Ordinal) { name.ToLowerInvariant() };
                    if (args.Contains("--camel", StringComparer.Ordinal))
                        names.Add(System.Text.RegularExpressions.Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
                    foreach (string candidateName in names)
                    foreach (string directory in directories)
                    foreach (string stemPrefix in new[] { "", character + "_", skin + "_", padded + "_", character + "_" + skin + "_", character + "_" + padded + "_" }.Distinct())
                    foreach (string prefix in new[] { "", "in_", "out_", "loop_", "intro_", "outro_", "start_", "end_", "idle_", "run_", "attack_", "spell_", "emote_", "win_", "lose_" })
                    foreach (string suffix in new[] { "", "_in", "_out", "_loop", "_start", "_end", "_intro", "_outro", "_idle", "_run", "_win", "_lose" })
                        Check(directory + stemPrefix + prefix + candidateName + suffix + ".anm", "animation layout");
                }
            }
        }

        static IEnumerable<BinTreeProperty> Descendants(BinTreeProperty property)
        {
            yield return property;
            IEnumerable<BinTreeProperty> children = property switch
            {
                BinTreeStruct s => s.Properties.Values,
                BinTreeContainer c => c.Elements,
                BinTreeOptional { Value: not null } o => new[] { o.Value },
                BinTreeMap m => m.SelectMany(p => new[] { p.Key, p.Value }),
                _ => Array.Empty<BinTreeProperty>()
            };
            foreach (var child in children)
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
