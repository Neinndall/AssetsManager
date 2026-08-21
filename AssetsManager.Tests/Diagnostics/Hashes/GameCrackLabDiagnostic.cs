using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Hashing;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class GameCrackLabDiagnostic
    {
        private const string HashesFileName = "hashes.game.txt";
        private static readonly string[] Roots = { "assets", "data" };
        private static readonly string[] AnimationPrefixes = { "", "recallin_", "recall_", "respawn_", "death_", "idle_", "run_", "attack_" };
        private static readonly string[] AnimationSuffixes = { "", "_stage", "_homeguard", "_hookup", "_loop", "_in", "_out", "_channel", "_dash", "_impact" };

        private static readonly Regex PathLikeTokenRegex = new(
            @"(?:data|assets|maps|plugins|patches|gameplay|characters|shared|levels|shaders|ux)/[a-z0-9_\-\.#/]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static void Run(string[] args)
        {
            string pbeRoot = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends (PBE)";

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");
            string hashesPath = Path.Combine(localAppData, "AssetsManager", "hashes", HashesFileName);
            if (!File.Exists(unknownsPath) || !File.Exists(hashesPath))
            {
                Console.WriteLine("Required inputs missing (unknowns.game.txt or hashes.game.txt).");
                return;
            }

            var unknownHashes = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    unknownHashes.Add(hash);

            string gameDir = Directory.Exists(Path.Combine(pbeRoot, "Game")) ? Path.Combine(pbeRoot, "Game") : pbeRoot;
            var wads = Directory.EnumerateFiles(gameDir, "*.wad.client", SearchOption.AllDirectories).ToList();

            Console.WriteLine("==================================================");
            Console.WriteLine($"    GAME CRACK LAB ({unknownHashes.Count} unknowns)");
            Console.WriteLine("==================================================");

            var contextHashes = new Dictionary<string, HashSet<ulong>>(StringComparer.OrdinalIgnoreCase);
            var chunkContext = new Dictionary<ulong, string>();
            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (!unknownHashes.Contains(pair.Key) || chunkContext.ContainsKey(pair.Key))
                            continue;
                        string context = ExtractWadContext(Path.GetFileName(wadPath));
                        chunkContext[pair.Key] = context;
                        if (!contextHashes.TryGetValue(context, out var set))
                            contextHashes[context] = set = new HashSet<ulong>();
                        set.Add(pair.Key);
                    }
                }
                catch { }
            }

            foreach (var group in contextHashes.OrderByDescending(item => item.Value.Count))
                Console.WriteLine($"  context '{group.Key}': {group.Value.Count} unknowns");

            var champions = contextHashes.Keys
                .Where(context => !IsSystemContext(context))
                .Select(context => context.ToLowerInvariant())
                .ToList();

            Console.WriteLine("\nHarvesting vocabulary from known catalog...");
            var stopwatch = Stopwatch.StartNew();
            var harvest = HarvestVocabulary(hashesPath, champions);
            List<string> suffixes = LoadSuffixVocabulary(hashesPath);
            stopwatch.Stop();
            foreach (var entry in harvest)
                Console.WriteLine($"  [{entry.Key}] animStems={entry.Value.AnimationStems.Count}, texBasenames={entry.Value.TextureBasenames.Count}, paths={entry.Value.KnownPaths.Count}, maxSkin={entry.Value.MaxKnownSkin}");
            Console.WriteLine($"  dotted suffix vocabulary: {suffixes.Count}");

            var solved = new Dictionary<ulong, string>();
            var stats = new List<(string Technique, int Candidates, int Cracked, TimeSpan Elapsed)>();

            CrackWithTechnique(
                "Scoped animation stem recombination",
                GenerateAnimationCandidates(harvest, champions),
                unknownHashes, solved, stats);

            CrackWithTechnique(
                "Scoped texture base->skin substitution",
                GenerateTextureCandidates(harvest, champions),
                unknownHashes, solved, stats);

            CrackWithTechnique(
                "Scoped suffix cloning (champions)",
                GenerateSuffixClones(harvest, champions, suffixes),
                unknownHashes, solved, stats);

            CrackWithTechnique(
                "Scoped BIN string harvest (champion wads)",
                HarvestReferencedPaths(gameDir, champions, unknownHashes, solved),
                unknownHashes, solved, stats);

            CrackContentBridge(gameDir, contextHashes.Keys.ToList(), unknownHashes, solved, suffixes, stats);

            CrackWithRealGrepWad(gameDir, contextHashes.Keys.ToList(), unknownHashes, solved, stats);

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine($"    RESULT: {solved.Count}/{unknownHashes.Count} cracked");
            Console.WriteLine("==================================================");
            foreach (var row in stats)
                Console.WriteLine($"  {row.Technique,-42} {row.Candidates,12:N0} cand  {row.Elapsed:hh\\:mm\\:ss}  -> {row.Cracked}");
            if (solved.Count > 0)
            {
                Console.WriteLine();
                foreach (var pair in solved.OrderBy(item => item.Key))
                    Console.WriteLine($"  [CRACKED] {pair.Key:x16} = {pair.Value}");
            }
        }

        private static void CrackWithTechnique(
            string name,
            IEnumerable<string> candidates,
            HashSet<ulong> unknownHashes,
            Dictionary<ulong, string> solved,
            List<(string Technique, int Candidates, int Cracked, TimeSpan Elapsed)> stats)
        {
            Console.Write($"\nRunning '{name}'... ");
            var stopwatch = Stopwatch.StartNew();
            int count = 0;
            int before = solved.Count;
            foreach (string candidate in candidates)
            {
                count++;
                ulong hash = XxHash64Ext.Hash(candidate.ToLowerInvariant());
                if (unknownHashes.Contains(hash) && !solved.ContainsKey(hash))
                {
                    solved[hash] = candidate;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write($"\n  [CRACKED] {hash:x16} = {candidate}");
                    Console.ResetColor();
                }
            }
            stopwatch.Stop();
            int cracked = solved.Count - before;
            stats.Add((name, count, cracked, stopwatch.Elapsed));
            Console.WriteLine($"\n  done: {count:N0} candidates, {cracked} cracked.");
        }

        private static IEnumerable<string> GenerateAnimationCandidates(
            Dictionary<string, ChampionVocabulary> harvest,
            List<string> champions)
        {
            foreach (string champion in champions)
            {
                if (!harvest.TryGetValue(champion, out var vocab) || vocab.AnimationStems.Count == 0)
                    continue;

                var skinTokens = Enumerable.Range(0, vocab.MaxKnownSkin + 10)
                    .Select(index => $"skin{index}")
                    .Concat(Enumerable.Range(0, 10).Select(index => $"skin0{index}"))
                    .Distinct()
                    .ToList();

                foreach (string stem in vocab.AnimationStems)
                foreach (string prefix in AnimationPrefixes)
                foreach (string suffix in AnimationSuffixes)
                {
                    string composed = prefix + stem + suffix;
                    foreach (string root in Roots)
                        yield return $"{root}/characters/{champion}/animations/{composed}.anm";

                    foreach (string skinToken in skinTokens)
                    {
                        string padded = skinToken.Length == 6 && char.IsDigit(skinToken[5])
                            ? "skin0" + skinToken[5]
                            : skinToken;
                        foreach (string root in Roots)
                        {
                            string baseDir = $"{root}/characters/{champion}/skins/{padded}/animations";
                            yield return $"{baseDir}/{composed}.anm";
                            yield return $"{baseDir}/{champion}_{composed}.anm";
                            yield return $"{baseDir}/{champion}_{padded}_{composed}.anm";
                            yield return $"{baseDir}/{padded}_{composed}.anm";
                        }
                    }
                }
            }
        }

        private static void CrackWithRealGrepWad(
            string gameDir,
            List<string> contexts,
            HashSet<ulong> unknownHashes,
            Dictionary<ulong, string> solved,
            List<(string Technique, int Candidates, int Cracked, TimeSpan Elapsed)> stats)
        {
            Console.Write("\nRunning 'Real GrepWad pipeline (no size ceiling)'... ");
            var stopwatch = Stopwatch.StartNew();
            var contextSet = new HashSet<string>(contexts, StringComparer.OrdinalIgnoreCase);
            var wads = Directory.EnumerateFiles(gameDir, "*.wad.client", SearchOption.AllDirectories)
                .Where(path => contextSet.Contains(ExtractWadContext(Path.GetFileNameWithoutExtension(path))))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var guesser = new GameHashGuesser();
            var engine = new HashGuessEngine(HashGuessDomain.Game, unknownHashes.ToHashSet(), null);
            int before = engine.Matches.Count;
            long chunks = 0;

            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    string wadName = Path.GetFileName(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (pair.Value.UncompressedSize < 8) continue;
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> seg = owner.DangerousGetArray();
                            chunks++;
                            guesser.GrepWad(engine, seg, $"grep:{wadName}", wadPath, pair.Key);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            stopwatch.Stop();
            foreach (var match in engine.Matches.Values.OrderBy(match => match.Hash))
            {
                if (solved.TryAdd(match.Hash, match.Path))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write($"\n  [CRACKED] {match.Hash:x16} = {match.Path} ({match.Strategy})");
                    Console.ResetColor();
                }
            }
            int cracked = solved.Count - before;
            stats.Add(("Real GrepWad pipeline (no size ceiling)", (int)chunks, cracked, stopwatch.Elapsed));
            Console.WriteLine($"\n  done: {chunks:N0} chunks grepped, {cracked} cracked.");
        }

        private static void CrackContentBridge(
            string gameDir,
            List<string> contexts,
            HashSet<ulong> unknownHashes,
            Dictionary<ulong, string> solved,
            List<string> suffixes,
            List<(string Technique, int Candidates, int Cracked, TimeSpan Elapsed)> stats)
        {
            Console.Write("\nRunning 'Content-identical twin bridging'... ");
            var stopwatch = Stopwatch.StartNew();
            var contextSet = new HashSet<string>(contexts, StringComparer.OrdinalIgnoreCase);
            var wads = Directory.EnumerateFiles(gameDir, "*.wad.client", SearchOption.AllDirectories)
                .Where(path => contextSet.Contains(ExtractWadContext(Path.GetFileNameWithoutExtension(path))))
                .ToList();

            var contentIndex = new Dictionary<ulong, List<ulong>>();
            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (pair.Value.UncompressedSize is < 8 or > 64 * 1024 * 1024) continue;
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> seg = owner.DangerousGetArray();
                            ulong contentHash = XxHash64.HashToUInt64(seg.Array.AsSpan(seg.Offset, seg.Count));
                            if (!contentIndex.TryGetValue(contentHash, out var list))
                                contentIndex[contentHash] = list = new List<ulong>();
                            list.Add(pair.Key);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            var twins = new Dictionary<ulong, List<ulong>>();
            foreach (ulong unknown in unknownHashes)
            {
                if (solved.ContainsKey(unknown)) continue;
                if (contentIndex.TryGetValue(unknown, out var list))
                {
                    var knownTwins = list.Where(hash => !unknownHashes.Contains(hash)).ToList();
                    if (knownTwins.Count > 0) twins[unknown] = knownTwins;
                }
            }
            Console.WriteLine($"\n  content twins found for {twins.Count} unknowns.");

            string hashesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetsManager", "hashes", HashesFileName);
            var twinPathHashes = twins.Values.SelectMany(list => list).ToHashSet();
            var twinPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(hashesPath))
            {
                int space = line.IndexOf(' ');
                if (space < 1) continue;
                if (!ulong.TryParse(line[..space], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash)) continue;
                if (twinPathHashes.Contains(hash)) twinPaths[hash] = line[(space + 1)..];
            }
            Console.WriteLine($"  resolved names recovered for {twinPaths.Count}/{twinPathHashes.Count} twins.");

            int candidates = 0, before = solved.Count;
            foreach (var pair in twins)
            {
                foreach (ulong twinHash in pair.Value)
                {
                    if (!twinPaths.TryGetValue(twinHash, out string path)) continue;
                    string directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
                    string basename = Path.GetFileNameWithoutExtension(path);
                    string extension = Path.GetExtension(path);
                    if (string.IsNullOrEmpty(directory)) continue;
                    foreach (string suffix in suffixes)
                    {
                        string candidate = $"{directory}/{basename}{suffix}{extension}";
                        candidates++;
                        ulong hash = XxHash64Ext.Hash(candidate.ToLowerInvariant());
                        if (unknownHashes.Contains(hash) && !solved.ContainsKey(hash))
                        {
                            solved[hash] = candidate;
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.Write($"\n  [CRACKED] {hash:x16} = {candidate}");
                            Console.ResetColor();
                        }
                    }
                }
            }
            stopwatch.Stop();
            int cracked = solved.Count - before;
            stats.Add(("Content-identical twin bridging", candidates, cracked, stopwatch.Elapsed));
            Console.WriteLine($"\n  done: {candidates:N0} candidates, {cracked} cracked.");
        }

        private static List<string> LoadSuffixVocabulary(string hashesPath)
        {
            var vocabulary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(hashesPath))
            {
                int space = line.IndexOf(' ');
                if (space < 1) continue;
                string path = line[(space + 1)..];
                string extension = Path.GetExtension(path);
                bool relevant = extension is ".anm" or ".tex" or ".prop" or ".bin" or ".skl" or ".skn" or ".dds" or ".png";
                if (!relevant || !path.Contains('/')) continue;
                string basename = Path.GetFileNameWithoutExtension(path);
                int dot = basename.IndexOf('.');
                if (dot <= 0) continue;
                string suffix = basename[dot..];
                if (suffix.Length >= 3 && suffix.Length <= 48 && !suffix.Contains(' ')) vocabulary.Add(suffix);
            }
            return vocabulary.OrderByDescending(suffix => suffix.Length).ToList();
        }

        private static IEnumerable<string> HarvestReferencedPaths(
            string gameDir,
            List<string> champions,
            HashSet<ulong> unknownHashes,
            Dictionary<ulong, string> solved)
        {
            var championSet = new HashSet<string>(champions, StringComparer.OrdinalIgnoreCase);
            var wads = Directory.EnumerateFiles(gameDir, "*.wad.client", SearchOption.AllDirectories)
                .Where(path => championSet.Contains(ExtractWadContext(Path.GetFileNameWithoutExtension(path))))
                .ToList();
            Console.WriteLine($"\n  harvesting {wads.Count} champion WADs (no size limit)...");

            long totalChunks = 0, binChunks = 0, failures = 0, tokenCount = 0;
            var magicHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string wadPath in wads)
            {
                using var wad = new WadFile(wadPath);
                foreach (var pair in wad.Chunks)
                {
                    if (pair.Value.UncompressedSize < 8) continue;
                    totalChunks++;
                    byte[] data;
                    try
                    {
                        using var owner = wad.LoadChunkDecompressed(pair.Value);
                        ArraySegment<byte> seg = owner.DangerousGetArray();
                        data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                    }
                    catch { failures++; continue; }

                    if (data[0] != (byte)'r' || data[1] != (byte)'3' || data[2] != (byte)'d' || data[3] != (byte)'2')
                    {
                        string key = Convert.ToHexString(data, 0, 4);
                        magicHistogram[key] = magicHistogram.TryGetValue(key, out int value) ? value + 1 : 1;
                        continue;
                    }
                    binChunks++;

                    foreach (Match match in PathLikeTokenRegex.Matches(Encoding.ASCII.GetString(data)))
                    {
                        string token = match.Value.TrimEnd('.', '/', '#');
                        if (token.Split('/').Length >= 3)
                        {
                            tokenCount++;
                            yield return token;
                        }
                    }
                }
            }

            Console.WriteLine($"  chunks={totalChunks:N0}, r3d2={binChunks:N0}, decompressFailures={failures:N0}, tokens={tokenCount:N0}");
            foreach (var entry in magicHistogram.OrderByDescending(item => item.Value).Take(6))
                Console.WriteLine($"    non-bin magic {entry.Key}: x{entry.Value}");
        }

        private static IEnumerable<string> GenerateTextureCandidates(
            Dictionary<string, ChampionVocabulary> harvest,
            List<string> champions)
        {
            foreach (string champion in champions)
            {
                if (!harvest.TryGetValue(champion, out var vocab) || vocab.TextureBasenames.Count == 0)
                    continue;

                foreach (string basename in vocab.TextureBasenames)
                {
                    if (!basename.Contains("base", StringComparison.Ordinal)) continue;
                    for (int skin = 0; skin < vocab.MaxKnownSkin + 10; skin++)
                    {
                        yield return $"assets/characters/{champion}/skins/skin{skin}/"
                            + basename.Replace("base", $"skin{skin}", StringComparison.Ordinal) + ".tex";
                        yield return "assets/characters/" + basename.Replace("base", $"skin{skin}", StringComparison.Ordinal) + ".tex";
                    }
                }
            }
        }

        private static IEnumerable<string> GenerateSuffixClones(
            Dictionary<string, ChampionVocabulary> harvest,
            List<string> champions,
            List<string> suffixes)
        {
            foreach (string champion in champions)
            {
                if (!harvest.TryGetValue(champion, out var vocab)) continue;
                foreach (string path in vocab.KnownPaths)
                {
                    string directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
                    string basename = Path.GetFileNameWithoutExtension(path);
                    string extension = Path.GetExtension(path);
                    if (string.IsNullOrEmpty(directory) || basename.Contains('.')) continue;
                    foreach (string suffix in suffixes)
                        yield return $"{directory}/{basename}{suffix}{extension}";
                }
            }
        }

        private sealed class ChampionVocabulary
        {
            public HashSet<string> AnimationStems { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> TextureBasenames { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> KnownPaths { get; } = new();
            public int MaxKnownSkin { get; set; }
        }

        private static Dictionary<string, ChampionVocabulary> HarvestVocabulary(string hashesPath, List<string> champions)
        {
            var result = champions.ToDictionary(
                champion => champion,
                _ => new ChampionVocabulary(),
                StringComparer.OrdinalIgnoreCase);

            using var reader = new StreamReader(hashesPath);
            while (reader.ReadLine() is { } line)
            {
                int space = line.IndexOf(' ');
                if (space < 0) continue;
                string path = line[(space + 1)..];
                if (!path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
                    !path.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                    continue;

                int charsIndex = path.IndexOf("/characters/", StringComparison.OrdinalIgnoreCase);
                if (charsIndex < 0) continue;
                int champStart = charsIndex + "/characters/".Length;
                int champEnd = path.IndexOf('/', champStart);
                if (champEnd < 0) continue;
                string champion = path[champStart..champEnd];
                if (!result.TryGetValue(champion, out var vocab)) continue;

                string remainder = path[(champEnd + 1)..].ToLowerInvariant();
                vocab.KnownPaths.Add(path.ToLowerInvariant());
                if (remainder.Contains("skins/skin", StringComparison.Ordinal))
                {
                    Match skinMatch = Regex.Match(remainder, @"skins/skin(\d+)");
                    if (skinMatch.Success)
                    {
                        int skin = int.Parse(skinMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (skin > vocab.MaxKnownSkin) vocab.MaxKnownSkin = skin;
                    }
                }
                if (remainder.EndsWith(".anm", StringComparison.Ordinal))
                {
                    string fileName = Path.GetFileNameWithoutExtension(remainder);
                    vocab.AnimationStems.Add(fileName);
                    vocab.AnimationStems.Add(StripKnownPrefixes(fileName, champion));
                }
                else if (remainder.EndsWith(".tex", StringComparison.Ordinal))
                {
                    vocab.TextureBasenames.Add(StripKnownPrefixes(Path.GetFileNameWithoutExtension(remainder), champion));
                }
            }
            return result;
        }

        private static string StripKnownPrefixes(string fileName, string champion)
        {
            string stem = fileName.ToLowerInvariant();
            int index = stem.IndexOf(champion + "_", StringComparison.Ordinal);
            if (index >= 0) stem = stem[(index + champion.Length + 1)..];
            if (stem.StartsWith("skin", StringComparison.OrdinalIgnoreCase))
            {
                int underscore = stem.IndexOf('_');
                if (underscore > 4) stem = stem[(underscore + 1)..];
            }
            return stem;
        }

        private static string ExtractWadContext(string wadFileName)
        {
            string name = Path.GetFileNameWithoutExtension(wadFileName);
            const string suffix = ".wad";
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
            return name;
        }

        private static bool IsSystemContext(string context) =>
            context.Equals("Global", StringComparison.OrdinalIgnoreCase) ||
            context.Equals("UI", StringComparison.OrdinalIgnoreCase) ||
            context.Equals("Bootstrap.windows", StringComparison.OrdinalIgnoreCase) ||
            context.StartsWith("Map", StringComparison.OrdinalIgnoreCase) ||
            context.StartsWith("Shaders", StringComparison.OrdinalIgnoreCase);
    }
}
