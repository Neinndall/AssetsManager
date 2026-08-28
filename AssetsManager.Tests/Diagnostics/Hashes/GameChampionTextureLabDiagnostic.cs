using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    /// <summary>
    /// Read-only GAME laboratory focused on unresolved champion textures.
    /// It converts WAD-local structure, material strings, path families, and
    /// content twins into bounded candidate checks without persisting matches.
    /// </summary>
    internal static class GameChampionTextureLabDiagnostic
    {
        private const string DefaultPbeDirectory = @"C:\Riot Games\League of Legends (PBE)";
        private const int DefaultCandidateBudget = 50_000_000;
        private const int DefaultRawCandidateBudget = 1_000_000;
        private const int DefaultContentReadBudget = 300_000;
        private const int DefaultNearTwinReadBudget = 5_000;
        private const int PropMagic = 0x504F5250;
        private const int PtchMagic = 0x48435450;
        private const int NearTwinSampleSize = 32;
        private const int NearTwinGridSize = 16;
        private const int NearTwinTopK = 12;
        private const double NearTwinMaximumDistance = 0.30;
        private const int CandidateDeduplicationWindow = 5_000_000;
        private static int TraceCandidateLimit;
        private static int TracedCandidates;

        private static readonly Regex ChampionTexturePathRegex = new(
            @"^(?:assets|data)/characters/(?<champion>[^/]+)/skins/(?<skin>base|skin\d+)/(?<relative>.+\.(?:tex|dds))$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ChampionPathRegex = new(
            @"^(?:assets|data)/characters/(?<champion>[^/]+)/(?<relative>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ChampionSkinPathRegex = new(
            @"^(?:assets|data)/characters/(?<champion>[^/]+)/skins/(?<skin>base|skin\d+)(?:/|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NumericTextureNameRegex = new(
            @"^(?<prefix>.*?)(?<number>\d+)(?<suffix>(?:_[a-z0-9-]+)*(?:\.[a-z0-9_-]+)*\.(?:tex|dds))$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DecoratedTextureNameRegex = new(
            @"^(?<core>.*?)(?<decorators>(?:\.[a-z0-9_-]+)*)\.(?<extension>tex|dds)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RawTexturePathRegex = new(
            @"(?:assets|data|characters)/[a-z0-9_./-]+\.(?:tex|dds)(?:\.[a-z0-9_]+)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] TexturePrefixes =
        {
            "2x_", "4x_", "sd_", "2x_sd_", "4x_sd_", "tft_"
        };

        private static readonly HashSet<string> StructuredExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "inibin", "skn", "skl", "anm", "preload"
        };

        private static readonly HashSet<string> RawReferenceExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "inibin", "preload"
        };

        private static readonly HashSet<string> GenericWadNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "global", "shared", "bootstrap", "champions", "maps", "map11", "map22", "ui", "ux",
            "shaders", "particles", "common", "levels", "loadouts", "patching", "clientstates",
            "companions", "items", "spells", "data", "final", "shipping"
        };

        private static readonly string[][] ChampionAliasGroups =
        {
            new[] { "annie", "tibbers" },
            new[] { "heimerdinger", "heimergarrison" },
            new[] { "kalista", "kalistaspawn" },
            new[] { "malzahar", "malzaharvoidling" },
            new[] { "orianna", "oriana", "oriannaball", "oriannanoball" },
            new[] { "quinn", "quinnvalor" },
            new[] { "taliyah", "taliyahwallchunk" },
            new[] { "xinzhao", "xinzhaorework" },
            new[] { "yorick", "yorickghoul" }
        };

        private static readonly string[] ChampionAliasPrefixes =
        {
            "cherry_goh_", "strawberry_", "crepe_", "ruby_", "jade_", "tft_"
        };

        public static void Run(string[] args)
        {
            LabOptions options = ParseOptions(args);
            TraceCandidateLimit = options.TraceCandidates;
            TracedCandidates = 0;
            string gameDirectory = Directory.Exists(Path.Combine(options.PbeRoot, "Game"))
                ? Path.Combine(options.PbeRoot, "Game")
                : options.PbeRoot;

            if (!Directory.Exists(gameDirectory))
            {
                Console.WriteLine($"GAME root not found: {gameDirectory}");
                return;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string hashesDirectory = Path.Combine(localAppData, "AssetsManager", "hashes");
            string hashesPath = Path.Combine(hashesDirectory, "hashes.game.txt");
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");
            if (!File.Exists(hashesPath) || !File.Exists(unknownsPath))
            {
                Console.WriteLine("Missing hashes.game.txt or unknowns.game.txt in the local AssetsManager data.");
                return;
            }

            var hashFile = new HashFile(HashGuessDomain.Game, hashesPath);
            IReadOnlyDictionary<ulong, string> catalog = hashFile.Load();
            HashSet<ulong> unknowns = LoadUnknowns(unknownsPath);
            unknowns.ExceptWith(catalog.Keys);

            string[] wadPaths = Directory.EnumerateFiles(gameDirectory, "*.wad.client", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Console.WriteLine("==================================================");
            Console.WriteLine("    GAME CHAMPION TEXTURE RESOLUTION LAB");
            Console.WriteLine("==================================================");
            Console.WriteLine($"Root:              {options.PbeRoot}");
            Console.WriteLine($"WADs:              {wadPaths.Length:N0}");
            Console.WriteLine($"GAME catalog paths: {catalog.Count:N0}");
            Console.WriteLine($"Persisted unknowns: {unknowns.Count:N0}");
            Console.WriteLine("Persistence:        disabled");

            Dictionary<ulong, TargetTexture> targets = IndexTargetTextures(
                wadPaths,
                gameDirectory,
                unknowns,
                options.ChampionFilter);
            if (targets.Count == 0)
            {
                Console.WriteLine("No unresolved champion TEX/DDS payloads were found.");
                return;
            }

            PrintTargetSummary(targets);
            Dictionary<string, HashSet<string>> globalPathsByChampion = IndexGlobalTexturePaths(catalog);
            Dictionary<string, HashSet<string>> localPathsByWad = IndexLocalTexturePaths(
                targets,
                catalog,
                out Dictionary<string, HashSet<int>> localTextureSizes,
                out Dictionary<string, HashSet<TextureMetadata>> localTextureMetadata);
            if (options.PrintContext)
            {
                PrintTargetContexts(targets, catalog);
                PrintLocalTextureContexts(targets, localPathsByWad, localTextureSizes, localTextureMetadata);
            }
            Dictionary<string, IReadOnlyList<string>> skinsByChampion = BuildSkinTokens(
                targets,
                globalPathsByChampion,
                localPathsByWad);

            var engine = new HashGuessEngine(HashGuessDomain.Game, targets.Keys.ToHashSet());

            if (!options.SkipContent)
            {
                ContentTwinIndex twins = IndexContentTwins(
                    engine,
                    targets,
                    catalog,
                    wadPaths,
                    options.ContentReadBudget);
                PrintContentTwinSummary(twins, targets);
                StageResult result = RunContentTwinPass(
                    engine,
                    targets,
                    twins,
                    globalPathsByChampion,
                    skinsByChampion,
                    options.CandidateBudget);
                PrintStageResult(result);
            }

            if (!options.SkipReferences)
            {
                StageResult result = RunLiteralReferencePass(
                    engine,
                    targets,
                    catalog,
                    wadPaths,
                    options.CandidateBudget);
                PrintStageResult(result);

            }

            if (!options.SkipTopology)
            {
                StageResult result = RunLocalTopologyPass(
                    engine,
                    targets,
                    globalPathsByChampion,
                    localPathsByWad,
                    localTextureSizes,
                    localTextureMetadata,
                    skinsByChampion,
                    options.CandidateBudget);
                PrintStageResult(result);
            }

            if (options.RunNearTwins)
            {
                StageResult result = RunNearTwinPass(
                    engine,
                    targets,
                    catalog,
                    localTextureSizes,
                    localTextureMetadata,
                    skinsByChampion,
                    options.NearTwinReadBudget,
                    options.CandidateBudget);
                PrintStageResult(result);
            }

            if (!options.SkipReferences)
            {
                StageResult result = RunRawHashReferencePass(
                    engine,
                    targets,
                    catalog,
                    wadPaths,
                    localPathsByWad,
                    skinsByChampion,
                    Math.Min(options.CandidateBudget, options.RawCandidateBudget));
                PrintStageResult(result);
            }

            if (!options.SkipCrossChampion)
            {
                IReadOnlyList<string> crossChampionTemplates = BuildCrossChampionTemplates(globalPathsByChampion);
                Console.WriteLine();
                Console.WriteLine($"Cross-champion texture templates (support >= 2 champions): {crossChampionTemplates.Count:N0}");
                StageResult result = RunCrossChampionPass(
                    engine,
                    targets,
                    crossChampionTemplates,
                    skinsByChampion,
                    options.CandidateBudget);
                PrintStageResult(result);
            }

            PrintFinalSummary(engine, targets);
            Console.WriteLine();
            Console.WriteLine("Lab finished. No GAME catalog, unknown list, or hash store was modified.");
        }

        private static LabOptions ParseOptions(string[] args)
        {
            string root = null;
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    if (args[index].Equals("--candidate-budget", StringComparison.OrdinalIgnoreCase) ||
                        args[index].Equals("--content-read-budget", StringComparison.OrdinalIgnoreCase) ||
                        args[index].Equals("--raw-candidate-budget", StringComparison.OrdinalIgnoreCase) ||
                        args[index].Equals("--champion", StringComparison.OrdinalIgnoreCase) ||
                        args[index].Equals("--near-twin-read-budget", StringComparison.OrdinalIgnoreCase) ||
                        args[index].Equals("--trace-candidates", StringComparison.OrdinalIgnoreCase))
                    {
                        index++;
                    }
                    continue;
                }

                root = args[index];
                break;
            }
            root ??= DefaultPbeDirectory;
            return new LabOptions(
                root,
                ParseInt(args, "--candidate-budget", DefaultCandidateBudget),
                ParseInt(args, "--raw-candidate-budget", DefaultRawCandidateBudget),
                ParseInt(args, "--content-read-budget", DefaultContentReadBudget),
                GetOption(args, "--champion"),
                args.Any(value => value.Equals("--skip-references", StringComparison.OrdinalIgnoreCase)),
                args.Any(value => value.Equals("--skip-topology", StringComparison.OrdinalIgnoreCase)),
                args.Any(value => value.Equals("--skip-cross-champion", StringComparison.OrdinalIgnoreCase)),
                args.Any(value => value.Equals("--skip-content", StringComparison.OrdinalIgnoreCase)),
                args.Any(value => value.Equals("--print-context", StringComparison.OrdinalIgnoreCase)),
                args.Any(value => value.Equals("--near-twins", StringComparison.OrdinalIgnoreCase)),
                ParseInt(args, "--near-twin-read-budget", DefaultNearTwinReadBudget),
                ParseInt(args, "--trace-candidates", 0));
        }

        private static string GetOption(string[] args, string option)
        {
            int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1]
                : null;
        }

        private static int ParseInt(string[] args, string option, int fallback)
        {
            int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value)
                ? Math.Max(1, value)
                : fallback;
        }

        private static HashSet<ulong> LoadUnknowns(string path)
        {
            var values = new HashSet<ulong>();
            foreach (string line in File.ReadLines(path))
            {
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    values.Add(hash);
            }
            return values;
        }

        private static Dictionary<ulong, TargetTexture> IndexTargetTextures(
            IReadOnlyList<string> wadPaths,
            string gameDirectory,
            IReadOnlySet<ulong> unknowns,
            string championFilter)
        {
            Console.WriteLine();
            Console.WriteLine("[1] Locating unresolved champion texture payloads...");
            var targets = new Dictionary<ulong, TargetTexture>();
            int errors = 0;
            int championWads = 0;

            foreach (string wadPath in wadPaths)
            {
                string champion = ExtractChampionFromWad(wadPath);
                if (champion is null || (championFilter != null &&
                                         !champion.Equals(championFilter, StringComparison.OrdinalIgnoreCase)))
                    continue;

                championWads++;
                string relativeWad = Path.GetRelativePath(gameDirectory, wadPath).Replace('\\', '/');
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach ((ulong hash, WadChunk chunk) in wad.Chunks)
                    {
                        if (!unknowns.Contains(hash)) continue;

                        if (targets.TryGetValue(hash, out TargetTexture existing))
                        {
                            try
                            {
                                using var owner = wad.LoadChunkDecompressed(chunk);
                                ArraySegment<byte> data = owner.DangerousGetArray();
                                string extension = HashGuessingService.InferChunkExtension(data, detectJson: false);
                                if (IsTextureExtension(extension))
                                {
                                    existing.AddPayload(
                                        extension,
                                        data.Count,
                                        HashContent(data),
                                        TryReadTextureMetadata(data, extension, out TextureMetadata metadata)
                                            ? metadata
                                            : null);
                                }
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                errors++;
                                if (errors <= 10)
                                    Console.WriteLine($"  [warn] duplicate target {hash:x16} could not be compared in {relativeWad}: {exception.Message}");
                            }

                            existing.AddLocation(champion, wadPath, relativeWad, hash, chunk.UncompressedSize);
                            continue;
                        }

                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(chunk);
                            ArraySegment<byte> data = owner.DangerousGetArray();
                            string extension = HashGuessingService.InferChunkExtension(data, detectJson: false);
                            if (!IsTextureExtension(extension)) continue;

                            var target = new TargetTexture(
                                hash,
                                extension,
                                data.Count,
                                HashContent(data),
                                TryReadTextureMetadata(data, extension, out TextureMetadata metadata)
                                    ? metadata
                                    : null);
                            target.AddLocation(champion, wadPath, relativeWad, hash, chunk.UncompressedSize);
                            targets.Add(hash, target);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            errors++;
                            if (errors <= 10)
                                Console.WriteLine($"  [warn] target {hash:x16} could not be classified in {relativeWad}: {exception.Message}");
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors++;
                    if (errors <= 10)
                        Console.WriteLine($"  [warn] could not inspect {relativeWad}: {exception.Message}");
                }
            }

            Console.WriteLine($"  champion WADs inspected: {championWads:N0}");
            Console.WriteLine($"  texture targets located: {targets.Count:N0}");
            int payloadConflicts = targets.Values.Count(value => value.HasPayloadConflict);
            if (payloadConflicts > 0)
                Console.WriteLine($"  target payload conflicts across WAD occurrences: {payloadConflicts:N0}");
            if (errors > 0) Console.WriteLine($"  non-fatal inspection errors: {errors:N0}");
            return targets;
        }

        private static void PrintTargetSummary(IReadOnlyDictionary<ulong, TargetTexture> targets)
        {
            Console.WriteLine("  target payloads by champion:");
            int multiChampionTargets = targets.Values.Count(value => value.Champions.Count > 1);
            if (multiChampionTargets > 0)
                Console.WriteLine($"  target hashes occurring in multiple champion WAD contexts: {multiChampionTargets:N0}");
            foreach (IGrouping<string, TargetTexture> group in targets.Values
                         .GroupBy(value => value.PrimaryChampion, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                int tex = group.Count(value => value.Extension.Equals("tex", StringComparison.OrdinalIgnoreCase));
                int dds = group.Count(value => value.Extension.Equals("dds", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"    {group.Key,-16} total={group.Count(),4:N0}  TEX={tex,4:N0}  DDS={dds,4:N0}");
            }
        }

        private static Dictionary<string, HashSet<string>> IndexGlobalTexturePaths(
            IReadOnlyDictionary<ulong, string> catalog)
        {
            var pathsByChampion = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawPath in catalog.Values)
            {
                if (!TryGetChampionPath(rawPath, out string champion) || !IsTexturePath(rawPath)) continue;
                if (!pathsByChampion.TryGetValue(champion, out HashSet<string> paths))
                    pathsByChampion[champion] = paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                paths.Add(PathUtils.NormalizePath(rawPath));
            }

            Console.WriteLine($"  catalog champion texture contexts: {pathsByChampion.Count:N0}");
            return pathsByChampion;
        }

        private static Dictionary<string, HashSet<string>> IndexLocalTexturePaths(
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            IReadOnlyDictionary<ulong, string> catalog,
            out Dictionary<string, HashSet<int>> textureSizes,
            out Dictionary<string, HashSet<TextureMetadata>> textureMetadata)
        {
            var targetWads = targets.Values
                .SelectMany(value => value.Locations.Select(location => location.WadPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pathsByWad = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            textureSizes = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            textureMetadata = new Dictionary<string, HashSet<TextureMetadata>>(StringComparer.OrdinalIgnoreCase);
            int errors = 0;

            foreach (string wadPath in targetWads)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                pathsByWad[wadPath] = paths;
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach ((ulong hash, WadChunk chunk) in wad.Chunks)
                    {
                        if (catalog.TryGetValue(hash, out string path) &&
                            TryGetChampionPath(path, out _) &&
                            IsTexturePath(path))
                        {
                            string normalizedPath = PathUtils.NormalizePath(path);
                            paths.Add(normalizedPath);
                            if (!textureSizes.TryGetValue(normalizedPath, out HashSet<int> sizes))
                                textureSizes[normalizedPath] = sizes = new HashSet<int>();
                            sizes.Add(chunk.UncompressedSize);
                            try
                            {
                                using var owner = wad.LoadChunkDecompressed(chunk);
                                if (TryReadTextureMetadata(owner.DangerousGetArray(), Path.GetExtension(normalizedPath).TrimStart('.'), out TextureMetadata metadata))
                                {
                                    if (!textureMetadata.TryGetValue(normalizedPath, out HashSet<TextureMetadata> metadataValues))
                                        textureMetadata[normalizedPath] = metadataValues = new HashSet<TextureMetadata>();
                                    metadataValues.Add(metadata);
                                }
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                errors++;
                                if (errors <= 10)
                                    Console.WriteLine($"  [warn] texture metadata skipped {normalizedPath}: {exception.Message}");
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors++;
                    if (errors <= 10)
                        Console.WriteLine($"  [warn] local path index skipped {Path.GetFileName(wadPath)}: {exception.Message}");
                }
            }

            Console.WriteLine($"  WAD-local champion texture indexes: {pathsByWad.Count:N0}");
            if (errors > 0) Console.WriteLine($"  local index errors: {errors:N0}");
            return pathsByWad;
        }

        private static void PrintTargetContexts(
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            IReadOnlyDictionary<ulong, string> catalog)
        {
            Console.WriteLine();
            Console.WriteLine("[context] Unknown champion texture neighborhoods in their WADs...");
            foreach (TargetTexture target in targets.Values.OrderBy(value => value.PrimaryChampion).ThenBy(value => value.Hash))
            {
                foreach (TargetLocation location in target.Locations
                             .GroupBy(value => value.WadPath, StringComparer.OrdinalIgnoreCase)
                             .Select(group => group.First()))
                {
                    try
                    {
                        using var wad = new WadFile(location.WadPath);
                        var hashes = new List<ulong>();
                        foreach ((ulong hash, WadChunk _) in wad.Chunks) hashes.Add(hash);
                        int index = hashes.IndexOf(target.Hash);
                        Console.WriteLine(
                            $"  {target.Hash:x16} [{target.PrimaryChampion}/{target.Extension}/{target.Size:N0}B] " +
                            $"{location.RelativeWad} position={index}/{hashes.Count:N0}");
                        if (index < 0) continue;

                        int start = Math.Max(0, index - 2);
                        int end = Math.Min(hashes.Count - 1, index + 2);
                        for (int neighbor = start; neighbor <= end; neighbor++)
                        {
                            ulong hash = hashes[neighbor];
                            string path = hash == target.Hash
                                ? "<UNKNOWN TARGET>"
                                : catalog.TryGetValue(hash, out string knownPath)
                                    ? PathUtils.NormalizePath(knownPath)
                                    : "<unknown chunk>";
                            Console.WriteLine($"    {neighbor,6:N0}: {hash:x16} {path}");
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        Console.WriteLine($"    [warn] context unavailable: {exception.Message}");
                    }
                }
            }
        }

        private static void PrintLocalTextureContexts(
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            IReadOnlyDictionary<string, HashSet<string>> localPathsByWad,
            IReadOnlyDictionary<string, HashSet<int>> localTextureSizes,
            IReadOnlyDictionary<string, HashSet<TextureMetadata>> localTextureMetadata)
        {
            Console.WriteLine();
            Console.WriteLine("[context] Known local texture paths sharing target size/format...");
            foreach (TargetTexture target in targets.Values.OrderBy(value => value.PrimaryChampion).ThenBy(value => value.Hash))
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TargetLocation location in target.Locations)
                    if (localPathsByWad.TryGetValue(location.WadPath, out HashSet<string> localPaths))
                        foreach (string path in localPaths)
                            if (localTextureSizes.TryGetValue(path, out HashSet<int> sizes) && sizes.Contains(target.Size) &&
                                Path.GetExtension(path).TrimStart('.').Equals(target.Extension, StringComparison.OrdinalIgnoreCase) &&
                                (!target.Metadata.HasValue ||
                                 !localTextureMetadata.TryGetValue(path, out HashSet<TextureMetadata> metadataValues) ||
                                 metadataValues.Count == 0 ||
                                 metadataValues.Contains(target.Metadata.Value)))
                                paths.Add(path);

                Console.WriteLine(
                    $"  {target.Hash:x16} [{target.PrimaryChampion}/{target.Extension}/{target.Size:N0}B] " +
                    $"matching local paths={paths.Count:N0}");
                foreach (string path in paths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(30))
                    Console.WriteLine($"    {path}");
            }
        }

        private static Dictionary<string, IReadOnlyList<string>> BuildSkinTokens(
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            IReadOnlyDictionary<string, HashSet<string>> globalPathsByChampion,
            IReadOnlyDictionary<string, HashSet<string>> localPathsByWad)
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string champion in targets.Values.SelectMany(value => value.Champions).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base", "skin0", "skin00" };
                int maxAttested = 0;

                if (globalPathsByChampion.TryGetValue(champion, out HashSet<string> globalPaths))
                    CollectSkinTokens(globalPaths, tokens, ref maxAttested);

                foreach (TargetTexture target in targets.Values.Where(value => value.Champions.Contains(champion)))
                foreach (string wadPath in target.Locations.Select(value => value.WadPath).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (localPathsByWad.TryGetValue(wadPath, out HashSet<string> localPaths))
                        CollectSkinTokens(localPaths, tokens, ref maxAttested);
                }

                int limit = Math.Min(350, Math.Max(85, maxAttested + 15));
                for (int skin = 0; skin <= limit; skin++)
                {
                    tokens.Add($"skin{skin}");
                    if (skin <= 9) tokens.Add($"skin{skin:D2}");
                }
                for (int skin = 300; skin <= 350; skin++) tokens.Add($"skin{skin}");

                IReadOnlyList<string> ordered = new[] { "base" }
                    .Concat(tokens
                        .Where(value => !value.Equals("base", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(ParseSkinNumber)
                        .ThenBy(value => value, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                result[champion] = ordered;
            }

            return result;
        }

        private static IReadOnlyList<string> BuildAttestedSkinTokens(IEnumerable<string> paths)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base" };
            int maxAttested = 0;
            CollectSkinTokens(paths ?? Array.Empty<string>(), tokens, ref maxAttested);

            return new[] { "base" }
                .Concat(tokens
                    .Where(value => !value.Equals("base", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(ParseSkinNumber)
                    .ThenBy(value => value, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void CollectSkinTokens(
            IEnumerable<string> paths,
            ISet<string> tokens,
            ref int maxAttested)
        {
            foreach (string path in paths)
            {
                Match match = ChampionTexturePathRegex.Match(path);
                if (!match.Success) continue;
                string skin = match.Groups["skin"].Value.ToLowerInvariant();
                tokens.Add(skin);
                if (skin.StartsWith("skin", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(skin[4..], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                {
                    maxAttested = Math.Max(maxAttested, number);
                }
            }
        }

        private static int ParseSkinNumber(string value)
        {
            if (value.Equals("base", StringComparison.OrdinalIgnoreCase)) return -1;
            return value.StartsWith("skin", StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(value[4..], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? number
                : int.MaxValue;
        }

        private static StageResult RunLiteralReferencePass(
            HashGuessEngine engine,
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            IReadOnlyDictionary<ulong, string> catalog,
            IReadOnlyList<string> wadPaths,
            long candidateBudget)
        {
            var stopwatch = Stopwatch.StartNew();
            long beforeCandidates = engine.CheckedCandidates;
            int beforeMatches = engine.Matches.Count;
            int parseErrors = 0;
            int readErrors = 0;
            var seenCandidates = new HashSet<ulong>();
            var relevantWads = targets.Values
                .SelectMany(value => value.Locations.Select(location => location.WadPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string wadPath in wadPaths)
            {
                if (!relevantWads.Contains(wadPath) || engine.RemainingUnknownCount == 0 ||
                    engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                    continue;

                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach ((ulong chunkHash, WadChunk chunk) in wad.Chunks)
                    {
                        if (engine.RemainingUnknownCount == 0 ||
                            engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                            break;

                        string sourcePath = catalog.TryGetValue(chunkHash, out string knownPath)
                            ? PathUtils.NormalizePath(knownPath)
                            : string.Empty;
                        string extension = Path.GetExtension(sourcePath).TrimStart('.');
                        if (!StructuredExtensions.Contains(extension) && !string.IsNullOrEmpty(sourcePath)) continue;
                        if (chunk.Compression == WadChunkCompression.Satellite) continue;

                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(chunk);
                            ArraySegment<byte> data = owner.DangerousGetArray();
                            if (!IsLikelyStructuredPayload(data, extension)) continue;

                            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            parseErrors += CollectTextureReferences(data, references);
                            foreach (string rawReference in references)
                            foreach (string candidate in ExpandTextureReference(rawReference))
                            {
                                if (engine.RemainingUnknownCount == 0 ||
                                    engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                                    break;
                                TryCheckCandidate(
                                    engine,
                                    seenCandidates,
                                    candidate,
                                    HashGuessStrategy.BinEntry,
                                    $"literal champion texture reference: {Path.GetFileName(wadPath)}",
                                    chunkHash,
                                    candidateBudget,
                                    beforeCandidates);
                            }
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            readErrors++;
                            if (readErrors <= 10)
                                Console.WriteLine($"  [warn] literal scan skipped {chunkHash:x16} in {Path.GetFileName(wadPath)}: {exception.Message}");
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    readErrors++;
                    if (readErrors <= 10)
                        Console.WriteLine($"  [warn] literal WAD scan skipped {Path.GetFileName(wadPath)}: {exception.Message}");
                }
            }

            stopwatch.Stop();
            return new StageResult(
                "Literal texture references from champion payloads",
                engine.CheckedCandidates - beforeCandidates,
                engine.Matches.Count - beforeMatches,
                engine.RemainingUnknownCount,
                stopwatch.Elapsed,
                parseErrors,
                readErrors);
        }

        private static StageResult RunRawHashReferencePass(
            HashGuessEngine engine,
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            IReadOnlyDictionary<ulong, string> catalog,
            IReadOnlyList<string> wadPaths,
            IReadOnlyDictionary<string, HashSet<string>> localPathsByWad,
            IReadOnlyDictionary<string, IReadOnlyList<string>> skinsByChampion,
            long candidateBudget)
        {
            var stopwatch = Stopwatch.StartNew();
            long beforeCandidates = engine.CheckedCandidates;
            int beforeMatches = engine.Matches.Count;
            int scannedPayloads = 0;
            int rawHits = 0;
            int semanticHits = 0;
            int parseErrors = 0;
            int readErrors = 0;
            var references = new Dictionary<ulong, List<RawHashReference>>();
            var seenReferences = new HashSet<RawHashReferenceKey>();
            var seenSemanticReferences = new HashSet<RawSemanticReferenceKey>();
            var byteLevelTargets = new HashSet<ulong>();
            var structuredTargets = new HashSet<ulong>();
            HashSet<ulong> targetHashes = targets.Keys.ToHashSet();
            HashSet<string> relevantWads = targets.Values
                .SelectMany(value => value.Locations.Select(location => location.WadPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Console.WriteLine();
            Console.WriteLine("[raw-hash] Searching BIN payloads for byte-level and structured 64-bit evidence...");

            foreach (string wadPath in wadPaths)
            {
                if (!relevantWads.Contains(wadPath)) continue;

                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach ((ulong chunkHash, WadChunk chunk) in wad.Chunks)
                    {
                        if (chunk.Compression == WadChunkCompression.Satellite ||
                            !catalog.TryGetValue(chunkHash, out string rawPath))
                            continue;

                        string sourcePath = PathUtils.NormalizePath(rawPath);
                        string extension = Path.GetExtension(sourcePath).TrimStart('.');
                        if (!RawReferenceExtensions.Contains(extension)) continue;

                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(chunk);
                            ArraySegment<byte> data = owner.DangerousGetArray();
                            scannedPayloads++;
                            rawHits += CollectRawHashReferences(
                                data,
                                targetHashes,
                                sourcePath,
                                wadPath,
                                chunkHash,
                                references,
                                seenReferences,
                                byteLevelTargets);
                            semanticHits += CollectBinTreeHashReferences(
                                data,
                                targetHashes,
                                sourcePath,
                                wadPath,
                                chunkHash,
                                references,
                                seenSemanticReferences,
                                structuredTargets,
                                out bool semanticParseError);
                            if (semanticParseError)
                            {
                                parseErrors++;
                                if (parseErrors <= 3)
                                    Console.WriteLine($"  [warn] structured BIN reference parse failed: {sourcePath}");
                            }
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            readErrors++;
                            if (readErrors <= 10)
                                Console.WriteLine($"  [warn] raw BIN scan skipped {sourcePath}: {exception.Message}");
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    readErrors++;
                    if (readErrors <= 10)
                        Console.WriteLine($"  [warn] raw BIN WAD scan skipped {Path.GetFileName(wadPath)}: {exception.Message}");
                }
            }

            Console.WriteLine(
                $"  raw-hash scan: payloads={scannedPayloads:N0}, byte-level hits={rawHits:N0}, " +
                $"structured 64-bit hits={semanticHits:N0}, " +
                $"byte-level targets={byteLevelTargets.Count:N0}, structured targets={structuredTargets.Count:N0}, " +
                $"target hashes with evidence={references.Count:N0}, " +
                $"parse errors={parseErrors:N0}, read errors={readErrors:N0}");

            var seenCandidates = new HashSet<ulong>();
            foreach ((ulong targetHash, List<RawHashReference> targetReferences) in references
                         .OrderBy(pair => pair.Key))
            {
                if (!engine.UnknownHashes.Contains(targetHash) || !targets.TryGetValue(targetHash, out TargetTexture target))
                    continue;

                    foreach (RawHashReference reference in targetReferences
                             .OrderByDescending(value => value.Offset < 0)
                             .ThenBy(value => value.SourcePath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(value => value.Offset)
                             .Take(16))
                {
                    Console.WriteLine(
                        $"  raw reference {targetHash:x16} <- {reference.SourcePath} " +
                        $"{FormatRawReferenceLocation(reference)} evidence={reference.Evidence}");

                    if (!localPathsByWad.TryGetValue(reference.WadPath, out HashSet<string> localPaths))
                        continue;

                    string champion = target.PrimaryChampion;
                    string referenceSkin = TryGetChampionSkin(reference.SourcePath, out _, out string skin)
                        ? skin
                        : null;
                    string[] sourcePaths = localPaths
                        .Where(path => IsChampionPathForTarget(path, champion))
                        .Where(path => referenceSkin is null ||
                                       (TryGetChampionTexture(path, out _, out string sourceSkin, out _) &&
                                        sourceSkin.Equals(referenceSkin, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (sourcePaths.Length == 0) continue;

                    TextureFamilyContext familyContext = BuildTextureFamilyContext(sourcePaths);
                    IReadOnlyList<string> aliases = BuildChampionTokenAliases(champion, sourcePaths);
                    IReadOnlyList<string> attestedSkins = BuildAttestedSkinTokens(sourcePaths);
                    if (skinsByChampion.TryGetValue(champion, out IReadOnlyList<string> championSkins))
                        attestedSkins = attestedSkins
                            .Concat(championSkins)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                    foreach (string candidate in EnumerateRawReferenceCandidates(
                                 reference.SourcePath,
                                 target.Extension)
                             .Concat(EnumerateTargetedLocalTextureCandidates(
                                 sourcePaths,
                                 champion,
                                 attestedSkins,
                                 aliases,
                                 familyContext,
                                 preserveSourceSkin: true,
                                 target.Extension)))
                    {
                        if (engine.RemainingUnknownCount == 0 ||
                            engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                            break;

                        TryCheckCandidate(
                            engine,
                            seenCandidates,
                            candidate,
                            HashGuessStrategy.BinEntry,
                            $"raw BIN 64-bit evidence: {reference.SourcePath}",
                            reference.SourceChunkHash,
                            candidateBudget,
                            beforeCandidates);
                    }
                }

                if (engine.RemainingUnknownCount == 0 ||
                    engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                    break;
            }

            stopwatch.Stop();
            return new StageResult(
                "Raw BIN 64-bit evidence for champion textures",
                engine.CheckedCandidates - beforeCandidates,
                engine.Matches.Count - beforeMatches,
                engine.RemainingUnknownCount,
                stopwatch.Elapsed,
                parseErrors,
                readErrors);
        }

        private static int CollectRawHashReferences(
            ArraySegment<byte> data,
            IReadOnlySet<ulong> targetHashes,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            IDictionary<ulong, List<RawHashReference>> references,
            ISet<RawHashReferenceKey> seenReferences,
            ISet<ulong> byteLevelTargets)
        {
            if (data.Array is null || data.Count < sizeof(ulong)) return 0;

            ReadOnlySpan<byte> bytes = data.Array.AsSpan(data.Offset, data.Count);
            int hits = 0;
            for (int offset = 0; offset <= bytes.Length - sizeof(ulong); offset++)
            {
                ulong littleEndian = BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
                if (targetHashes.Contains(littleEndian))
                {
                    byteLevelTargets.Add(littleEndian);
                    hits += AddRawHashReference(
                        littleEndian,
                        sourcePath,
                        sourceWadPath,
                        sourceChunkHash,
                        offset,
                        bigEndian: false,
                        references,
                        seenReferences);
                }

                ulong bigEndian = BinaryPrimitives.ReadUInt64BigEndian(bytes[offset..]);
                if (bigEndian != littleEndian && targetHashes.Contains(bigEndian))
                {
                    byteLevelTargets.Add(bigEndian);
                    hits += AddRawHashReference(
                        bigEndian,
                        sourcePath,
                        sourceWadPath,
                        sourceChunkHash,
                        offset,
                        bigEndian: true,
                        references,
                        seenReferences);
                }
            }

            return hits;
        }

        private static int CollectBinTreeHashReferences(
            ArraySegment<byte> data,
            IReadOnlySet<ulong> targetHashes,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            IDictionary<ulong, List<RawHashReference>> references,
            ISet<RawSemanticReferenceKey> seenReferences,
            ISet<ulong> structuredTargets,
            out bool parseError)
        {
            parseError = false;
            if (data.Array is null || data.Count < sizeof(uint)) return 0;

            int magic = BitConverter.ToInt32(data.Array, data.Offset);
            if (magic != PropMagic && magic != PtchMagic) return 0;

            try
            {
                using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                var tree = new BinTree(stream);
                int hits = 0;
                foreach (BinTreeObject obj in tree.Objects.Values)
                foreach (BinTreeProperty property in obj.Properties.Values)
                foreach (BinTreeProperty nested in EnumerateProperties(property))
                    hits += CollectSemanticPropertyReference(
                        nested,
                        targetHashes,
                        sourcePath,
                        sourceWadPath,
                        sourceChunkHash,
                        references,
                        seenReferences,
                        structuredTargets);

                foreach (BinTreeDataOverride dataOverride in tree.DataOverrides)
                foreach (BinTreeProperty nested in EnumerateProperties(dataOverride.Property))
                    hits += CollectSemanticPropertyReference(
                        nested,
                        targetHashes,
                        sourcePath,
                        sourceWadPath,
                        sourceChunkHash,
                        references,
                        seenReferences,
                        structuredTargets);
                return hits;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                parseError = true;
                return 0;
            }
        }

        private static int CollectSemanticPropertyReference(
            BinTreeProperty property,
            IReadOnlySet<ulong> targetHashes,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            IDictionary<ulong, List<RawHashReference>> references,
            ISet<RawSemanticReferenceKey> seenReferences,
            ISet<ulong> structuredTargets)
        {
            ulong? value = property switch
            {
                BinTreeU64 unsigned when targetHashes.Contains(unsigned.Value) => unsigned.Value,
                BinTreeI64 signed when targetHashes.Contains(unchecked((ulong)signed.Value)) => unchecked((ulong)signed.Value),
                BinTreeWadChunkLink link when targetHashes.Contains(link.Value) => link.Value,
                _ => null
            };
            if (!value.HasValue) return 0;
            structuredTargets.Add(value.Value);

            string evidence = property switch
            {
                BinTreeU64 => "BinTreeU64",
                BinTreeI64 => "BinTreeI64",
                BinTreeWadChunkLink => "BinTreeWadChunkLink",
                _ => "BinTree64"
            };
            var key = new RawSemanticReferenceKey(
                value.Value,
                sourceWadPath,
                sourceChunkHash,
                property.NameHash,
                value.Value);
            if (!seenReferences.Add(key)) return 0;

            if (!references.TryGetValue(value.Value, out List<RawHashReference> values))
                references[value.Value] = values = new List<RawHashReference>();
            var semanticReference = new RawHashReference(
                value.Value,
                sourceChunkHash,
                sourcePath,
                sourceWadPath,
                -1,
                BigEndian: false,
                evidence,
                property.NameHash);
            if (values.Count < 64)
                values.Add(semanticReference);
            else
            {
                // Keep structured evidence visible even when byte-level scanning
                // already filled the per-target display cap.
                int byteLevelIndex = values.FindIndex(reference => reference.Offset >= 0);
                if (byteLevelIndex >= 0) values[byteLevelIndex] = semanticReference;
            }
            return 1;
        }

        private static int AddRawHashReference(
            ulong targetHash,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            int offset,
            bool bigEndian,
            IDictionary<ulong, List<RawHashReference>> references,
            ISet<RawHashReferenceKey> seenReferences)
        {
            var key = new RawHashReferenceKey(targetHash, sourceWadPath, sourceChunkHash, offset, bigEndian);
            if (!seenReferences.Add(key)) return 0;

            if (!references.TryGetValue(targetHash, out List<RawHashReference> values))
                references[targetHash] = values = new List<RawHashReference>();
            if (values.Count < 64)
                values.Add(new RawHashReference(
                    targetHash,
                    sourceChunkHash,
                    sourcePath,
                    sourceWadPath,
                    offset,
                    bigEndian,
                    "byte pattern",
                    null));
            return 1;
        }

        private static string FormatRawReferenceLocation(RawHashReference reference) =>
            reference.Offset >= 0
                ? $"offset=0x{reference.Offset:x} endian={(reference.BigEndian ? "BE" : "LE")}"
                : $"structured property=0x{reference.PropertyHash.GetValueOrDefault():x8}";

        private static IEnumerable<string> EnumerateRawReferenceCandidates(
            string sourcePath,
            string requiredExtension)
        {
            string normalized = PathUtils.NormalizePath(sourcePath);
            int extensionIndex = normalized.LastIndexOf('.');
            if (extensionIndex <= 0) yield break;

            string stem = normalized[..extensionIndex];
            string[] suffixes =
            {
                string.Empty,
                "_tx_cm",
                "_base_tx_cm",
                "_body_tx_cm",
                "_loadscreen",
                "loadscreen",
                "_circle",
                "_square"
            };
            foreach (string suffix in suffixes)
            foreach (string candidate in EnumerateTextureVariants(stem + suffix + "." + requiredExtension))
                if (MatchesRequiredTextureExtension(candidate, requiredExtension))
                    yield return candidate;
        }

        private static bool TryGetChampionSkin(
            string path,
            out string champion,
            out string skin)
        {
            Match match = ChampionSkinPathRegex.Match(PathUtils.NormalizePath(path));
            if (!match.Success)
            {
                champion = null;
                skin = null;
                return false;
            }

            champion = match.Groups["champion"].Value.ToLowerInvariant();
            skin = match.Groups["skin"].Value.ToLowerInvariant();
            return true;
        }

        private static StageResult RunLocalTopologyPass(
            HashGuessEngine engine,
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            IReadOnlyDictionary<string, HashSet<string>> globalPathsByChampion,
            IReadOnlyDictionary<string, HashSet<string>> localPathsByWad,
            IReadOnlyDictionary<string, HashSet<int>> localTextureSizes,
            IReadOnlyDictionary<string, HashSet<TextureMetadata>> localTextureMetadata,
            IReadOnlyDictionary<string, IReadOnlyList<string>> skinsByChampion,
            long candidateBudget)
        {
            var stopwatch = Stopwatch.StartNew();
            long beforeCandidates = engine.CheckedCandidates;
            int beforeMatches = engine.Matches.Count;
            // The engine hashes normalized routes with XXH64. Deduplicating by that
            // same observable key prevents duplicate work without reducing coverage.
            var seenCandidates = new HashSet<ulong>();

            foreach (IGrouping<string, TargetTexture> group in targets.Values
                         .Where(value => engine.UnknownHashes.Contains(value.Hash))
                         .GroupBy(value => value.PrimaryChampion, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                string champion = group.Key;
                var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TargetTexture target in group)
                foreach (TargetLocation location in target.Locations)
                {
                    if (localPathsByWad.TryGetValue(location.WadPath, out HashSet<string> localPaths))
                        foreach (string path in localPaths)
                            if (IsChampionPathForTarget(path, champion)) sourcePaths.Add(path);
                }
                foreach ((string sourceChampion, HashSet<string> globalPaths) in globalPathsByChampion)
                    if (AreChampionNamesRelated(sourceChampion, champion))
                        foreach (string path in globalPaths) sourcePaths.Add(path);

                IReadOnlyList<string> skins = skinsByChampion[champion];
                string[] contextPaths = sourcePaths
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                TextureFamilyContext familyContext = BuildTextureFamilyContext(contextPaths);
                IReadOnlyList<string> aliases = BuildChampionTokenAliases(champion, contextPaths);
                HashSet<int> targetSizes = group.Select(value => value.Size).ToHashSet();
                string[] prioritizedPaths = sourcePaths
                    .OrderBy(path => GetSourceTexturePriority(path, localTextureSizes, targetSizes))
                    .ThenByDescending(path =>
                        localTextureSizes.TryGetValue(path, out HashSet<int> sizes) &&
                        group.Any(target => sizes.Contains(target.Size) &&
                            Path.GetExtension(path).TrimStart('.').Equals(target.Extension, StringComparison.OrdinalIgnoreCase)))
                    .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                IReadOnlyList<string> attestedSkins = BuildAttestedSkinTokens(contextPaths);
                RunTargetedLocalTopologyWaves(
                    engine,
                    group,
                    champion,
                    sourcePaths,
                    localTextureSizes,
                    localTextureMetadata,
                    attestedSkins,
                    aliases,
                    familyContext,
                    seenCandidates,
                    candidateBudget,
                    beforeCandidates);

                foreach (string candidate in EnumerateLocalTextureCandidates(
                    prioritizedPaths,
                    champion,
                    skins,
                    aliases,
                    familyContext))
                {
                    if (engine.RemainingUnknownCount == 0 || engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                        break;
                    TryCheckCandidate(
                        engine,
                        seenCandidates,
                        candidate,
                        HashGuessStrategy.CharacterTemplate,
                        $"WAD-local champion texture topology: {champion}",
                        0,
                        candidateBudget,
                        beforeCandidates);
                }

                if (engine.RemainingUnknownCount == 0 || engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                    break;
            }

            stopwatch.Stop();
            return new StageResult(
                "WAD-local champion texture topology",
                engine.CheckedCandidates - beforeCandidates,
                engine.Matches.Count - beforeMatches,
                engine.RemainingUnknownCount,
                stopwatch.Elapsed,
                0,
                0);
        }

        private static void RunTargetedLocalTopologyWaves(
            HashGuessEngine engine,
            IEnumerable<TargetTexture> targets,
            string champion,
            IReadOnlyCollection<string> sourcePaths,
            IReadOnlyDictionary<string, HashSet<int>> localTextureSizes,
            IReadOnlyDictionary<string, HashSet<TextureMetadata>> localTextureMetadata,
            IReadOnlyList<string> attestedSkins,
            IReadOnlyList<string> aliases,
            TextureFamilyContext familyContext,
            ISet<ulong> seenCandidates,
            long candidateBudget,
            long beforeCandidates)
        {
            foreach (IGrouping<TextureSignature, TargetTexture> targetGroup in targets
                         .Where(value => engine.UnknownHashes.Contains(value.Hash))
                         .GroupBy(value => new TextureSignature(value.Size, value.Extension, value.Metadata))
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key.Size)
                         .ThenBy(group => group.Key.Extension, StringComparer.OrdinalIgnoreCase))
            {
                if (engine.RemainingUnknownCount == 0 ||
                    engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                    break;

                TextureSignature signature = targetGroup.Key;
                string[] sizeMatchingSources = sourcePaths
                    .Where(path => localTextureSizes.TryGetValue(path, out HashSet<int> sizes) &&
                                   sizes.Contains(signature.Size) &&
                                   Path.GetExtension(path).TrimStart('.').Equals(
                                       signature.Extension,
                                       StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => GetSourceTexturePriority(
                        path,
                        localTextureSizes,
                        new HashSet<int> { signature.Size }))
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (sizeMatchingSources.Length == 0) continue;

                string[] metadataMatchingSources = signature.Metadata is not TextureMetadata requiredMetadata
                    ? Array.Empty<string>()
                    : sizeMatchingSources
                        .Where(path => localTextureMetadata.TryGetValue(path, out HashSet<TextureMetadata> metadataValues) &&
                                       metadataValues.Contains(requiredMetadata))
                        .ToArray();
                string[] matchingSources = metadataMatchingSources.Length > 0
                    ? metadataMatchingSources
                    : sizeMatchingSources;

                Console.WriteLine(
                    $"  targeted texture wave {signature.Extension}/{signature.Size:N0}B: " +
                    $"targets={targetGroup.Count():N0}, local sources={matchingSources.Length:N0}, " +
                    $"metadata-filtered={metadataMatchingSources.Length:N0}");

                foreach (bool preserveSourceSkin in new[] { true, false })
                {
                    foreach (string candidate in EnumerateTargetedLocalTextureCandidates(
                        matchingSources,
                        champion,
                        attestedSkins,
                        aliases,
                        familyContext,
                        preserveSourceSkin,
                        signature.Extension))
                    {
                        if (engine.RemainingUnknownCount == 0 ||
                            engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                            break;

                        TryCheckCandidate(
                            engine,
                            seenCandidates,
                            candidate,
                            HashGuessStrategy.CharacterTemplate,
                            $"size-guided WAD-local texture wave: {champion}/{signature.Extension}/{signature.Size}B",
                            0,
                            candidateBudget,
                            beforeCandidates);
                    }

                    if (engine.RemainingUnknownCount == 0 ||
                        engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                        break;
                }

                if (metadataMatchingSources.Length > 0 &&
                    engine.RemainingUnknownCount > 0 &&
                    engine.CheckedCandidates - beforeCandidates < candidateBudget)
                {
                    HashSet<string> exactSources = metadataMatchingSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    string[] fallbackSources = sizeMatchingSources
                        .Where(path => !exactSources.Contains(path))
                        .ToArray();
                    if (fallbackSources.Length > 0)
                    {
                        Console.WriteLine(
                            $"  metadata fallback wave {signature.Extension}/{signature.Size:N0}B: " +
                            $"sources={fallbackSources.Length:N0}");
                        foreach (bool preserveSourceSkin in new[] { true, false })
                        {
                            foreach (string candidate in EnumerateTargetedLocalTextureCandidates(
                                fallbackSources,
                                champion,
                                attestedSkins,
                                aliases,
                                familyContext,
                                preserveSourceSkin,
                                signature.Extension))
                            {
                                if (engine.RemainingUnknownCount == 0 ||
                                    engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                                    break;

                                TryCheckCandidate(
                                    engine,
                                    seenCandidates,
                                    candidate,
                                    HashGuessStrategy.CharacterTemplate,
                                    $"size fallback WAD-local texture wave: {champion}/{signature.Extension}/{signature.Size}B",
                                    0,
                                    candidateBudget,
                                    beforeCandidates);
                            }

                            if (engine.RemainingUnknownCount == 0 ||
                                engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                                break;
                        }
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateTargetedLocalTextureCandidates(
            IEnumerable<string> sourcePaths,
            string champion,
            IReadOnlyList<string> skins,
            IReadOnlyList<string> aliases,
            TextureFamilyContext familyContext,
            bool preserveSourceSkin,
            string requiredExtension)
        {
            foreach (string sourcePath in sourcePaths)
            {
                if (!TryGetChampionPath(sourcePath, out string sourceChampion) ||
                    !AreChampionNamesRelated(sourceChampion, champion))
                    continue;

                if (!TryGetChampionTexture(sourcePath, out _, out string sourceSkin, out _))
                {
                    foreach (string aliasPath in EnumerateChampionAliasVariants(sourcePath, sourceChampion, aliases))
                    foreach (string candidate in EnumerateTextureVariants(aliasPath))
                        if (MatchesRequiredTextureExtension(candidate, requiredExtension))
                            yield return candidate;

                    foreach (string aliasPath in EnumerateChampionAliasVariants(sourcePath, sourceChampion, aliases))
                    foreach (string hudPath in EnumerateHudSkinTextureVariants(aliasPath, skins))
                    foreach (string candidate in EnumerateTextureVariants(hudPath))
                        if (MatchesRequiredTextureExtension(candidate, requiredExtension))
                            yield return candidate;

                    foreach (string aliasPath in EnumerateChampionAliasVariants(sourcePath, sourceChampion, aliases))
                    foreach (string numericPath in EnumerateNumericTextureVariants(aliasPath, familyContext))
                    foreach (string candidate in EnumerateTextureVariants(numericPath))
                        if (MatchesRequiredTextureExtension(candidate, requiredExtension))
                            yield return candidate;
                    continue;
                }

                IEnumerable<string> skinsToTry = preserveSourceSkin ? new[] { sourceSkin } : skins;
                foreach (string skin in skinsToTry)
                {
                    string resolved = ReplaceChampionAndSkin(
                        sourcePath,
                        sourceChampion,
                        sourceSkin,
                        champion,
                        skin);
                    foreach (string skinAlignedPath in EnumerateSkinAlignedTextureVariants(
                        resolved,
                        skin))
                    foreach (string aliasPath in EnumerateChampionAliasVariants(
                        skinAlignedPath,
                        champion,
                        aliases))
                    foreach (string candidate in EnumerateTextureVariants(aliasPath))
                        if (MatchesRequiredTextureExtension(candidate, requiredExtension))
                            yield return candidate;
                }
            }
        }

        private static IEnumerable<string> EnumerateHudSkinTextureVariants(
            string path,
            IReadOnlyList<string> skins)
        {
            string normalized = PathUtils.NormalizePath(path);
            int slash = normalized.LastIndexOf('/');
            if (slash < 0 || slash == normalized.Length - 1) yield break;

            string file = normalized[(slash + 1)..];
            Match match = Regex.Match(
                file,
                @"^(?<base>.*_(?:circle|square))\.(?<extension>tex|dds)$",
                RegexOptions.IgnoreCase);
            if (!match.Success) yield break;

            foreach (string skin in skins ?? Array.Empty<string>())
            {
                int number = ParseSkinNumber(skin);
                if (number < 0) continue;
                foreach (string numberText in FormatNumericTokens(number, 1))
                    yield return normalized[..(slash + 1)] +
                                 match.Groups["base"].Value + "_" + numberText + "." +
                                 match.Groups["extension"].Value;
            }
        }

        private static IEnumerable<string> EnumerateSkinAlignedTextureVariants(string path, string targetSkin)
        {
            string normalized = PathUtils.NormalizePath(path);
            yield return normalized;

            int slash = normalized.LastIndexOf('/');
            if (slash < 0 || slash == normalized.Length - 1) yield break;

            string file = normalized[(slash + 1)..];
            Match match = NumericTextureNameRegex.Match(file);
            if (!match.Success ||
                (!match.Groups["prefix"].Value.EndsWith("loadscreen_", StringComparison.OrdinalIgnoreCase) &&
                 !match.Groups["prefix"].Value.EndsWith("circle_", StringComparison.OrdinalIgnoreCase)))
                yield break;

            int targetNumber = ParseSkinNumber(targetSkin);
            if (targetNumber < 0) targetNumber = 0;
            int originalWidth = match.Groups["number"].Value.Length;
            foreach (string number in FormatNumericTokens(targetNumber, originalWidth))
            {
                string replacement = file[..match.Groups["number"].Index] +
                                     number +
                                     file[(match.Groups["number"].Index + match.Groups["number"].Length)..];
                string result = normalized[..(slash + 1)] + replacement;
                if (!result.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    yield return result;
            }
        }

        private static int GetSourceTexturePriority(
            string path,
            IReadOnlyDictionary<string, HashSet<int>> localTextureSizes,
            IReadOnlySet<int> targetSizes)
        {
            if (!localTextureSizes.TryGetValue(path, out HashSet<int> sizes) ||
                !sizes.Any(targetSizes.Contains)) return 100;

            int size = sizes.FirstOrDefault(targetSizes.Contains);

            string normalized = PathUtils.NormalizePath(path);
            if (size == 86_252)
            {
                if (normalized.Contains("loadscreen", StringComparison.OrdinalIgnoreCase)) return 0;
                if (normalized.Contains("/hud/", StringComparison.OrdinalIgnoreCase)) return 10;
            }
            else if (size is 174_788 or 699_076)
            {
                if (normalized.Contains("_tx_cm", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("/body", StringComparison.OrdinalIgnoreCase)) return 0;
            }
            else if (size <= 16_396 && normalized.Contains("/particles/", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return 20;
        }

        private static StageResult RunNearTwinPass(
            HashGuessEngine engine,
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            IReadOnlyDictionary<ulong, string> catalog,
            IReadOnlyDictionary<string, HashSet<int>> localTextureSizes,
            IReadOnlyDictionary<string, HashSet<TextureMetadata>> localTextureMetadata,
            IReadOnlyDictionary<string, IReadOnlyList<string>> skinsByChampion,
            int readBudget,
            long candidateBudget)
        {
            var stopwatch = Stopwatch.StartNew();
            long beforeCandidates = engine.CheckedCandidates;
            int beforeMatches = engine.Matches.Count;
            int sourceReads = 0;
            int targetReads = 0;
            int decodeErrors = 0;
            var seenCandidates = new HashSet<ulong>();

            Console.WriteLine();
            Console.WriteLine("[near-twins] Comparing bounded visual signatures of local champion textures...");
            Console.WriteLine(
                $"  limits: reads={readBudget:N0}, top-k={NearTwinTopK}, " +
                $"maximum distance={NearTwinMaximumDistance:F2}, sample={NearTwinSampleSize}x{NearTwinSampleSize}");

            var targetWads = targets.Values
                .Where(value => engine.UnknownHashes.Contains(value.Hash))
                .SelectMany(value => value.Locations.Select(location => location.WadPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string wadPath in targetWads)
            {
                if (sourceReads >= readBudget || engine.RemainingUnknownCount == 0) break;

                TargetTexture[] wadTargets = targets.Values
                    .Where(value => engine.UnknownHashes.Contains(value.Hash) &&
                                    value.Locations.Any(location => location.WadPath.Equals(wadPath, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (wadTargets.Length == 0) continue;

                try
                {
                    using var wad = new WadFile(wadPath);
                    var chunks = wad.Chunks.ToDictionary(pair => pair.Key, pair => pair.Value);
                    string[] championNames = wadTargets
                        .SelectMany(value => value.Champions)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var sources = new List<NearTwinSource>();

                    foreach ((ulong chunkHash, WadChunk chunk) in wad.Chunks)
                    {
                        if (sourceReads >= readBudget) break;
                        if (!catalog.TryGetValue(chunkHash, out string rawPath)) continue;

                        string path = PathUtils.NormalizePath(rawPath);
                        if (!IsTexturePath(path) ||
                            !TryGetChampionPath(path, out string sourceChampion) ||
                            !championNames.Any(champion => AreChampionNamesRelated(sourceChampion, champion)))
                            continue;

                        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                        if (!wadTargets.Any(target => target.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase) &&
                                                      target.Size == chunk.UncompressedSize &&
                                                      AreTargetChampionRelated(target, sourceChampion)))
                            continue;

                        if (!localTextureSizes.TryGetValue(path, out HashSet<int> sizes) ||
                            !sizes.Contains(chunk.UncompressedSize))
                            continue;

                        if (!TryLoadVisualSignature(
                                wad,
                                chunk,
                                extension,
                                out TextureVisualSignature signature,
                                out TextureMetadata? metadata,
                                out string error))
                        {
                            decodeErrors++;
                            if (decodeErrors <= 10)
                                Console.WriteLine($"  [warn] near-twin source skipped {path}: {error}");
                            continue;
                        }

                        sourceReads++;
                        sources.Add(new NearTwinSource(path, chunkHash, chunk.UncompressedSize, metadata, signature));
                    }

                    Console.WriteLine(
                        $"  near-twin WAD {Path.GetFileName(wadPath)}: targets={wadTargets.Length:N0}, " +
                        $"decoded sources={sources.Count:N0}, reads={sourceReads:N0}");

                    foreach (TargetTexture target in wadTargets
                                 .OrderBy(value => value.PrimaryChampion, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(value => value.Hash))
                    {
                        if (!engine.UnknownHashes.Contains(target.Hash) ||
                            engine.RemainingUnknownCount == 0 ||
                            engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                            continue;

                        TargetLocation location = target.Locations.FirstOrDefault(value =>
                            value.WadPath.Equals(wadPath, StringComparison.OrdinalIgnoreCase));
                        if (location is null || !chunks.TryGetValue(location.ChunkHash, out WadChunk targetChunk))
                            continue;

                        if (!TryLoadVisualSignature(
                                wad,
                                targetChunk,
                                target.Extension,
                                out TextureVisualSignature targetSignature,
                                out _,
                                out string error))
                        {
                            decodeErrors++;
                            if (decodeErrors <= 10)
                                Console.WriteLine($"  [warn] near-twin target skipped {target.Hash:x16}: {error}");
                            continue;
                        }

                        targetReads++;
                        string champion = target.PrimaryChampion;
                        NearTwinSourceDistance[] nearest = sources
                            .Where(source => source.Size == target.Size &&
                                             source.Path.EndsWith("." + target.Extension, StringComparison.OrdinalIgnoreCase) &&
                                             AreTargetChampionRelated(target, GetChampionFromPath(source.Path)) &&
                                             IsNearTwinMetadataCompatible(target, source, localTextureMetadata))
                            .Select(source => new NearTwinSourceDistance(source, GetVisualDistance(targetSignature, source.Signature)))
                            .Where(value => value.Distance <= NearTwinMaximumDistance)
                            .OrderBy(value => value.Distance)
                            .ThenBy(value => value.Source.Path, StringComparer.OrdinalIgnoreCase)
                            .Take(NearTwinTopK)
                            .ToArray();

                        if (nearest.Length == 0) continue;

                        Console.WriteLine(
                            $"  target {target.Hash:x16} [{champion}/{target.Extension}/{target.Size:N0}B] " +
                            $"near sources={nearest.Length:N0}, best={nearest[0].Distance:F3}");

                        string[] sourcePaths = sources.Select(value => value.Path).ToArray();
                        TextureFamilyContext familyContext = BuildTextureFamilyContext(sourcePaths);
                        IReadOnlyList<string> aliases = BuildChampionTokenAliases(champion, sourcePaths);
                        IReadOnlyList<string> attestedSkins = BuildAttestedSkinTokens(sourcePaths);
                        if (skinsByChampion.TryGetValue(champion, out IReadOnlyList<string> championSkins))
                            attestedSkins = attestedSkins
                                .Concat(championSkins)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();

                        foreach (NearTwinSourceDistance nearestSource in nearest)
                        foreach (bool preserveSourceSkin in new[] { true, false })
                        foreach (string candidate in EnumerateTargetedLocalTextureCandidates(
                            new[] { nearestSource.Source.Path },
                            champion,
                            attestedSkins,
                            aliases,
                            familyContext,
                            preserveSourceSkin,
                            target.Extension))
                        {
                            if (engine.RemainingUnknownCount == 0 ||
                                engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                                break;

                            TryCheckCandidate(
                                engine,
                                seenCandidates,
                                candidate,
                                HashGuessStrategy.CharacterTemplate,
                                $"visual near-twin texture family: {champion} ({nearestSource.Distance:F3})",
                                nearestSource.Source.ChunkHash,
                                candidateBudget,
                                beforeCandidates);
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    decodeErrors++;
                    if (decodeErrors <= 10)
                        Console.WriteLine($"  [warn] near-twin WAD skipped {Path.GetFileName(wadPath)}: {exception.Message}");
                }
            }

            stopwatch.Stop();
            Console.WriteLine($"  near-twin payload reads: targets={targetReads:N0}, sources={sourceReads:N0}, decode errors={decodeErrors:N0}");
            return new StageResult(
                "Visual near-twin champion texture variants",
                engine.CheckedCandidates - beforeCandidates,
                engine.Matches.Count - beforeMatches,
                engine.RemainingUnknownCount,
                stopwatch.Elapsed,
                0,
                decodeErrors);
        }

        private static bool AreTargetChampionRelated(TargetTexture target, string champion) =>
            target.Champions.Any(value => AreChampionNamesRelated(value, champion));

        private static string GetChampionFromPath(string path) =>
            TryGetChampionPath(path, out string champion) ? champion : string.Empty;

        private static bool IsNearTwinMetadataCompatible(
            TargetTexture target,
            NearTwinSource source,
            IReadOnlyDictionary<string, HashSet<TextureMetadata>> localTextureMetadata)
        {
            if (!target.Metadata.HasValue) return true;
            if (source.Metadata.HasValue) return source.Metadata.Value.Equals(target.Metadata.Value);
            return !localTextureMetadata.TryGetValue(source.Path, out HashSet<TextureMetadata> values) ||
                   values.Count == 0 ||
                   values.Contains(target.Metadata.Value);
        }

        private static bool TryLoadVisualSignature(
            WadFile wad,
            WadChunk chunk,
            string extension,
            out TextureVisualSignature signature,
            out TextureMetadata? metadata,
            out string error)
        {
            signature = null;
            metadata = null;
            error = null;
            try
            {
                using var owner = wad.LoadChunkDecompressed(chunk);
                ArraySegment<byte> data = owner.DangerousGetArray();
                if (TryReadTextureMetadata(data, extension, out TextureMetadata parsedMetadata))
                    metadata = parsedMetadata;

                using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                BitmapSource bitmap = TextureUtils.LoadViewerTexture(
                    stream,
                    "." + extension,
                    NearTwinSampleSize,
                    NearTwinSampleSize);
                if (bitmap is null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
                {
                    error = "texture decoder returned no pixels";
                    return false;
                }

                BitmapSource bgra = bitmap.Format == PixelFormats.Bgra32
                    ? bitmap
                    : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                if (bgra.CanFreeze) bgra.Freeze();
                int stride = checked(bgra.PixelWidth * 4);
                byte[] pixels = new byte[checked(stride * bgra.PixelHeight)];
                bgra.CopyPixels(pixels, stride, 0);
                signature = BuildVisualSignature(pixels, bgra.PixelWidth, bgra.PixelHeight);
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                error = exception.Message;
                return false;
            }
        }

        private static TextureVisualSignature BuildVisualSignature(byte[] pixels, int width, int height)
        {
            var rawLuminance = new byte[NearTwinGridSize * NearTwinGridSize];
            var alpha = new byte[NearTwinGridSize * NearTwinGridSize];
            int minimum = 255;
            int maximum = 0;

            for (int y = 0; y < NearTwinGridSize; y++)
            for (int x = 0; x < NearTwinGridSize; x++)
            {
                int sourceX = Math.Min(width - 1, (x * width + width / 2) / NearTwinGridSize);
                int sourceY = Math.Min(height - 1, (y * height + height / 2) / NearTwinGridSize);
                int offset = sourceY * width * 4 + sourceX * 4;
                int blue = pixels[offset];
                int green = pixels[offset + 1];
                int red = pixels[offset + 2];
                int pixelAlpha = pixels[offset + 3];
                int luminance = (29 * red + 150 * green + 77 * blue) >> 8;
                int index = y * NearTwinGridSize + x;
                rawLuminance[index] = (byte)luminance;
                alpha[index] = (byte)pixelAlpha;
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
            }

            var normalizedLuminance = new byte[rawLuminance.Length];
            int range = maximum - minimum;
            for (int index = 0; index < rawLuminance.Length; index++)
                normalizedLuminance[index] = range == 0
                    ? rawLuminance[index]
                    : (byte)((rawLuminance[index] - minimum) * 255 / range);

            return new TextureVisualSignature(normalizedLuminance, alpha);
        }

        private static double GetVisualDistance(
            TextureVisualSignature left,
            TextureVisualSignature right)
        {
            if (left is null || right is null || left.Luminance.Length != right.Luminance.Length)
                return double.MaxValue;

            double luminanceDistance = 0;
            double alphaDistance = 0;
            for (int index = 0; index < left.Luminance.Length; index++)
            {
                luminanceDistance += Math.Abs(left.Luminance[index] - right.Luminance[index]) / 255d;
                alphaDistance += Math.Abs(left.Alpha[index] - right.Alpha[index]) / 255d;
            }

            double count = left.Luminance.Length;
            return luminanceDistance / count * 0.75 + alphaDistance / count * 0.25;
        }

        private sealed record NearTwinSource(
            string Path,
            ulong ChunkHash,
            int Size,
            TextureMetadata? Metadata,
            TextureVisualSignature Signature);

        private readonly record struct NearTwinSourceDistance(NearTwinSource Source, double Distance);

        private sealed record TextureVisualSignature(byte[] Luminance, byte[] Alpha);

        private static IEnumerable<string> EnumerateLocalTextureCandidates(
            IEnumerable<string> sourcePaths,
            string champion,
            IReadOnlyList<string> skins,
            IReadOnlyList<string> aliases,
            TextureFamilyContext familyContext,
            bool preserveSourceSkin = false,
            string requiredExtension = null)
        {
            foreach (string sourcePath in sourcePaths)
            {
                if (!TryGetChampionPath(sourcePath, out string sourceChampion) ||
                    !AreChampionNamesRelated(sourceChampion, champion))
                    continue;

                if (!TryGetChampionTexture(sourcePath, out _, out string sourceSkin, out _))
                {
                    foreach (string aliasPath in EnumerateChampionAliasVariants(sourcePath, sourceChampion, aliases))
                    foreach (string candidate in EnumerateTextureFamilyVariants(aliasPath, familyContext))
                        if (MatchesRequiredTextureExtension(candidate, requiredExtension))
                            yield return candidate;
                    continue;
                }

                string template = sourcePath.Replace(sourceSkin, "{skin}", StringComparison.OrdinalIgnoreCase);
                IEnumerable<string> skinsToTry = preserveSourceSkin ? new[] { sourceSkin } : skins;
                foreach (string skin in skinsToTry)
                {
                    string resolved = template.Replace("{skin}", skin, StringComparison.OrdinalIgnoreCase);
                    foreach (string aliasPath in EnumerateChampionAliasVariants(resolved, sourceChampion, aliases))
                    foreach (string candidate in EnumerateTextureFamilyVariants(aliasPath, familyContext))
                        if (MatchesRequiredTextureExtension(candidate, requiredExtension))
                            yield return candidate;
                }
            }
        }

        private static bool MatchesRequiredTextureExtension(string path, string requiredExtension) =>
            string.IsNullOrEmpty(requiredExtension) ||
            Path.GetExtension(path).TrimStart('.').Equals(requiredExtension, StringComparison.OrdinalIgnoreCase);

        private static IReadOnlyList<string> BuildCrossChampionTemplates(
            IReadOnlyDictionary<string, HashSet<string>> globalPathsByChampion)
        {
            var supports = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach ((string champion, HashSet<string> paths) in globalPathsByChampion)
            foreach (string path in paths)
            {
                if (!TryGetChampionTexture(path, out string sourceChampion, out string skin, out _)) continue;
                string format = ReplaceChampionAndSkin(path, sourceChampion, skin, "{champion}", "{skin}");
                if (!supports.TryGetValue(format, out HashSet<string> championSupport))
                    supports[format] = championSupport = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                championSupport.Add(champion);
            }

            return supports
                .Where(pair => pair.Value.Count >= 2)
                .OrderByDescending(pair => pair.Value.Count)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key)
                .ToArray();
        }

        private static StageResult RunCrossChampionPass(
            HashGuessEngine engine,
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            IReadOnlyList<string> templates,
            IReadOnlyDictionary<string, IReadOnlyList<string>> skinsByChampion,
            long candidateBudget)
        {
            var stopwatch = Stopwatch.StartNew();
            long beforeCandidates = engine.CheckedCandidates;
            int beforeMatches = engine.Matches.Count;
            var seenCandidates = new HashSet<ulong>();

            foreach (string champion in targets.Values
                         .Where(value => engine.UnknownHashes.Contains(value.Hash))
                         .SelectMany(value => value.Champions)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (!skinsByChampion.TryGetValue(champion, out IReadOnlyList<string> skins)) continue;
                foreach (string template in templates)
                foreach (string skin in skins)
                {
                    if (engine.RemainingUnknownCount == 0 || engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                        break;
                    string resolved = template
                        .Replace("{champion}", champion, StringComparison.OrdinalIgnoreCase)
                        .Replace("{skin}", skin, StringComparison.OrdinalIgnoreCase);
                    foreach (string candidate in EnumerateTextureVariants(resolved))
                    {
                        if (engine.RemainingUnknownCount == 0 || engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                            break;
                        TryCheckCandidate(
                            engine,
                            seenCandidates,
                            candidate,
                            HashGuessStrategy.CharacterSubstitution,
                            $"cross-champion texture family: {champion}",
                            0,
                            candidateBudget,
                            beforeCandidates);
                    }
                }
                if (engine.RemainingUnknownCount == 0 || engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                    break;
            }

            stopwatch.Stop();
            return new StageResult(
                "Cross-champion texture families",
                engine.CheckedCandidates - beforeCandidates,
                engine.Matches.Count - beforeMatches,
                engine.RemainingUnknownCount,
                stopwatch.Elapsed,
                0,
                0);
        }

        private static ContentTwinIndex IndexContentTwins(
            HashGuessEngine engine,
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            IReadOnlyDictionary<ulong, string> catalog,
            IReadOnlyList<string> wadPaths,
            int readBudget)
        {
            Console.WriteLine();
            Console.WriteLine("[content] Searching known champion textures with identical payloads...");
            var fingerprintTargets = targets.Values
                .Where(value => engine.UnknownHashes.Contains(value.Hash))
                .SelectMany(value => value.ContentFingerprints.Select(fingerprint => (fingerprint, value.Hash)))
                .GroupBy(value => value.fingerprint)
                .ToDictionary(group => group.Key, group => group.Select(value => value.Hash).ToArray());
            var twins = new Dictionary<ulong, HashSet<string>>();
            var knownFingerprintCache = new Dictionary<ContentOccurrence, ContentFingerprint>();
            var targetSizes = fingerprintTargets.Keys.Select(value => value.Size).ToHashSet();
            int reads = 0;
            int cacheHits = 0;
            int errors = 0;
            int considered = 0;
            int processedWads = 0;

            foreach (string wadPath in wadPaths)
            {
                if (reads >= readBudget || fingerprintTargets.Count == 0) break;
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach ((ulong chunkHash, WadChunk chunk) in wad.Chunks)
                    {
                        if (reads >= readBudget) break;
                        if (chunk.Compression == WadChunkCompression.Satellite ||
                            chunk.UncompressedSize <= 0 || !targetSizes.Contains(chunk.UncompressedSize) ||
                            !catalog.TryGetValue(chunkHash, out string path) ||
                            !IsTexturePath(path))
                            continue;

                        considered++;
                        ContentFingerprint fingerprint;
                        ContentOccurrence occurrence = new(PathUtils.NormalizePath(wadPath), chunkHash);
                        if (knownFingerprintCache.TryGetValue(occurrence, out fingerprint))
                        {
                            cacheHits++;
                        }
                        else
                        {
                            try
                            {
                                using var owner = wad.LoadChunkDecompressed(chunk);
                                ArraySegment<byte> data = owner.DangerousGetArray();
                                fingerprint = new ContentFingerprint(data.Count, HashContent(data));
                                knownFingerprintCache[occurrence] = fingerprint;
                                reads++;
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                errors++;
                                if (errors <= 10)
                                    Console.WriteLine($"  [warn] content twin read skipped {chunkHash:x16}: {exception.Message}");
                                continue;
                            }
                        }

                        if (!fingerprintTargets.TryGetValue(fingerprint, out ulong[] targetHashes)) continue;
                        foreach (ulong targetHash in targetHashes)
                        {
                            if (!twins.TryGetValue(targetHash, out HashSet<string> paths))
                                twins[targetHash] = paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            paths.Add(PathUtils.NormalizePath(path));
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors++;
                    if (errors <= 10)
                        Console.WriteLine($"  [warn] content twin WAD skipped {Path.GetFileName(wadPath)}: {exception.Message}");
                }

                processedWads++;
                if (processedWads == 1 || processedWads % 100 == 0 || processedWads == wadPaths.Count)
                    Console.WriteLine($"  content scan {processedWads:N0}/{wadPaths.Count:N0} WADs, reads={reads:N0}, twins={twins.Count:N0}");
            }

            return new ContentTwinIndex(twins, processedWads, reads, considered, cacheHits, errors);
        }

        private static StageResult RunContentTwinPass(
            HashGuessEngine engine,
            IReadOnlyDictionary<ulong, TargetTexture> targets,
            ContentTwinIndex twins,
            IReadOnlyDictionary<string, HashSet<string>> globalPathsByChampion,
            IReadOnlyDictionary<string, IReadOnlyList<string>> skinsByChampion,
            long candidateBudget)
        {
            var stopwatch = Stopwatch.StartNew();
            long beforeCandidates = engine.CheckedCandidates;
            int beforeMatches = engine.Matches.Count;
            var seenCandidates = new HashSet<ulong>();

            foreach ((ulong targetHash, HashSet<string> sourcePaths) in twins.PathsByTarget)
            {
                if (!engine.UnknownHashes.Contains(targetHash) || !targets.TryGetValue(targetHash, out TargetTexture target))
                    continue;

                foreach (string champion in target.Champions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    if (!skinsByChampion.TryGetValue(champion, out IReadOnlyList<string> skins)) continue;
                    var contextSet = new HashSet<string>(sourcePaths, StringComparer.OrdinalIgnoreCase);
                    foreach ((string contextChampion, HashSet<string> globalPaths) in globalPathsByChampion)
                        if (AreChampionNamesRelated(contextChampion, champion))
                            contextSet.UnionWith(globalPaths);
                    string[] contextPaths = contextSet
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    TextureFamilyContext familyContext = BuildTextureFamilyContext(contextPaths);
                    IReadOnlyList<string> aliases = BuildChampionTokenAliases(champion, contextPaths);
                    foreach (string sourcePath in sourcePaths)
                    {
                        bool hasChampionPath = TryGetChampionPath(sourcePath, out string sourceChampion);
                        bool hasSkin = TryGetChampionTexture(sourcePath, out _, out string sourceSkin, out _);
                        IEnumerable<string> skinsToTry = hasSkin &&
                                                           AreChampionNamesRelated(sourceChampion, champion)
                            ? new[] { sourceSkin }
                            : skins;
                        IEnumerable<string> templates = hasSkin
                            ? skinsToTry.Select(skin => ReplaceChampionAndSkin(
                                sourcePath,
                                sourceChampion,
                                sourceSkin,
                                champion,
                                skin))
                            : hasChampionPath
                                ? new[] { ReplaceChampionPathSegment(sourcePath, sourceChampion, champion) }
                                : new[] { sourcePath };

                        foreach (string template in templates)
                        {
                            if (engine.RemainingUnknownCount == 0 || engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                                break;
                            foreach (string aliasPath in EnumerateChampionAliasVariants(
                                template,
                                hasChampionPath ? sourceChampion : champion,
                                aliases))
                            foreach (string candidate in EnumerateTextureFamilyVariants(aliasPath, familyContext))
                            {
                                if (engine.RemainingUnknownCount == 0 || engine.CheckedCandidates - beforeCandidates >= candidateBudget)
                                    break;
                                TryCheckCandidate(
                                    engine,
                                    seenCandidates,
                                    candidate,
                                    HashGuessStrategy.CharacterSubstitution,
                                    $"content twin texture family: {champion}",
                                    0,
                                    candidateBudget,
                                    beforeCandidates);
                            }
                        }
                    }
                }
            }

            stopwatch.Stop();
            return new StageResult(
                "Content-twin-guided champion texture variants",
                engine.CheckedCandidates - beforeCandidates,
                engine.Matches.Count - beforeMatches,
                engine.RemainingUnknownCount,
                stopwatch.Elapsed,
                0,
                0);
        }

        private static void PrintContentTwinSummary(
            ContentTwinIndex twins,
            IReadOnlyDictionary<ulong, TargetTexture> targets)
        {
            Console.WriteLine("  content twin evidence:");
            Console.WriteLine($"    WADs scanned:       {twins.ProcessedWads:N0}");
            Console.WriteLine($"    known texture reads: {twins.Reads:N0}");
            Console.WriteLine($"    considered chunks:   {twins.Considered:N0}");
            Console.WriteLine($"    fingerprint cache:   {twins.CacheHits:N0}");
            Console.WriteLine($"    target hashes with twins: {twins.PathsByTarget.Count:N0}");
            Console.WriteLine($"    read errors:         {twins.Errors:N0}");
            foreach (var group in twins.PathsByTarget
                         .OrderByDescending(pair => pair.Value.Count)
                         .ThenBy(pair => pair.Key)
                         .Take(12))
            {
                string champion = targets.TryGetValue(group.Key, out TargetTexture target)
                    ? target.PrimaryChampion
                    : "<unknown>";
                Console.WriteLine($"      {group.Key:x16} [{champion}] <- {group.Value.Count:N0} known path(s)");
            }

            Console.WriteLine("  all content-twin targets:");
            foreach ((ulong hash, HashSet<string> paths) in twins.PathsByTarget.OrderBy(pair => pair.Key))
            {
                if (!targets.TryGetValue(hash, out TargetTexture target)) continue;
                Console.WriteLine(
                    $"    {hash:x16} [{target.PrimaryChampion}/{target.Extension}/{target.Size:N0}B] " +
                    $"<- {string.Join(" | ", paths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(4))}");
            }
        }

        private static void PrintStageResult(StageResult result)
        {
            Console.WriteLine();
            Console.WriteLine($"[{result.Name}]");
            Console.WriteLine($"  new resolutions: {result.Resolved:N0}");
            Console.WriteLine($"  candidates:      {result.Candidates:N0}");
            Console.WriteLine($"  remaining:       {result.Remaining:N0}");
            Console.WriteLine($"  elapsed:         {result.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine($"  parse errors:    {result.ParseErrors:N0}");
            Console.WriteLine($"  read errors:     {result.ReadErrors:N0}");
        }

        private static void PrintFinalSummary(
            HashGuessEngine engine,
            IReadOnlyDictionary<ulong, TargetTexture> targets)
        {
            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("    CHAMPION TEXTURE LAB RESULT");
            Console.WriteLine("==================================================");
            Console.WriteLine($"Resolved:  {engine.Matches.Count:N0}/{targets.Count:N0}");
            Console.WriteLine($"Remaining: {engine.RemainingUnknownCount:N0}");
            Console.WriteLine($"Candidates: {engine.CheckedCandidates:N0}");
            foreach (IGrouping<string, HashGuessMatch> group in engine.Matches.Values
                         .GroupBy(value => Path.GetExtension(value.Path).TrimStart('.'), StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  resolved {group.Key.ToUpperInvariant(),-4}: {group.Count():N0}");

            foreach (IGrouping<string, TargetTexture> group in targets.Values
                         .GroupBy(value => value.PrimaryChampion, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                int resolved = group.Count(value => engine.Matches.ContainsKey(value.Hash));
                int tex = group.Count(value => value.Extension.Equals("tex", StringComparison.OrdinalIgnoreCase));
                int dds = group.Count(value => value.Extension.Equals("dds", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"  {group.Key,-16} resolved={resolved,4:N0}/{group.Count(),4:N0}  TEX={tex,4:N0} DDS={dds,4:N0}");
            }

            if (engine.Matches.Count > 0)
            {
                Console.WriteLine("Resolved paths:");
                foreach (HashGuessMatch match in engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"  {match.Hash:x16} => {match.Path} [{match.Strategy}]");
            }

            IReadOnlyList<TargetTexture> unresolved = targets.Values
                .Where(value => !engine.Matches.ContainsKey(value.Hash))
                .OrderBy(value => value.PrimaryChampion, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Size)
                .ThenBy(value => value.Hash)
                .ToArray();
            if (unresolved.Count > 0)
            {
                Console.WriteLine("Unresolved target details:");
                foreach (TargetTexture target in unresolved)
                {
                    TargetLocation location = target.Locations.FirstOrDefault();
                    string metadata = target.Metadata is TextureMetadata value
                        ? $" {value.Width}x{value.Height}/{value.MipCount}m/f{value.Format}"
                        : string.Empty;
                    Console.WriteLine(
                        $"  {target.Hash:x16} [{target.PrimaryChampion}/{target.Extension}/{target.Size:N0}B{metadata}] " +
                        $"{location?.RelativeWad ?? "<WAD unknown>"}");
                }
            }
        }

        private static void TryCheckCandidate(
            HashGuessEngine engine,
            ISet<ulong> seenCandidates,
            string candidate,
            HashGuessStrategy strategy,
            string source,
            ulong sourceChunkHash,
            long candidateBudget,
            long beforeCandidates)
        {
            string normalized = PathUtils.NormalizePath(candidate);
            if (normalized.Length == 0) return;
            ulong candidateHash = XxHash64Ext.Hash(normalized);
            // Keep the set bounded. This is an epoch reset: it trades a small amount
            // of repeated work for predictable memory while retaining exact coverage.
            if (seenCandidates.Count >= CandidateDeduplicationWindow)
                seenCandidates.Clear();
            if (!seenCandidates.Add(candidateHash)) return;
            if (engine.CheckedCandidates - beforeCandidates >= candidateBudget) return;
            if (TracedCandidates < TraceCandidateLimit)
            {
                TracedCandidates++;
                Console.WriteLine($"  [candidate {TracedCandidates:N0}] {normalized}");
            }
            engine.Check(normalized, strategy, source, sourceChunkHash);
        }

        private static IEnumerable<string> EnumerateTextureVariants(string path)
        {
            string normalized = PathUtils.NormalizePath(path);
            if (!IsTexturePath(normalized)) yield break;

            yield return normalized;

            if (normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                yield return "data/" + normalized[7..];
            else if (normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                yield return "assets/" + normalized[5..];

            if (normalized.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                string dds = normalized[..^4] + ".dds";
                yield return dds;
                if (dds.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) yield return "data/" + dds[7..];
                else if (dds.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) yield return "assets/" + dds[5..];
            }
            else if (normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                string tex = normalized[..^4] + ".tex";
                yield return tex;
                if (tex.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) yield return "data/" + tex[7..];
                else if (tex.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) yield return "assets/" + tex[5..];
            }

            if (normalized.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) &&
                !normalized.EndsWith(".project_jade.tex", StringComparison.OrdinalIgnoreCase))
            {
                yield return normalized[..^4] + ".project_jade.tex";
            }
            else if (normalized.EndsWith(".project_jade.tex", StringComparison.OrdinalIgnoreCase))
            {
                yield return normalized[..^".project_jade.tex".Length] + ".tex";
            }

            int slash = normalized.LastIndexOf('/');
            if (slash < 0 || slash == normalized.Length - 1) yield break;
            string directory = normalized[..(slash + 1)];
            string file = normalized[(slash + 1)..];
            foreach (string prefix in TexturePrefixes)
            {
                if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    yield return directory + prefix + file;
            }

            foreach (string prefix in TexturePrefixes)
            {
                if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && file.Length > prefix.Length)
                    yield return directory + file[prefix.Length..];
            }
        }

        private static IEnumerable<string> EnumerateTextureFamilyVariants(
            string path,
            TextureFamilyContext familyContext)
        {
            var seeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTextureSeed(seeds, path);

            foreach (string numeric in EnumerateNumericTextureVariants(path, familyContext))
                AddTextureSeed(seeds, numeric);
            foreach (string decorated in EnumerateDecoratedTextureVariants(path, familyContext))
                AddTextureSeed(seeds, decorated);

            foreach (string numeric in seeds.ToArray())
                foreach (string variant in EnumerateNumericTextureVariants(numeric, familyContext))
                    AddTextureSeed(seeds, variant);
            foreach (string decorated in seeds.ToArray())
                foreach (string variant in EnumerateDecoratedTextureVariants(decorated, familyContext))
                    AddTextureSeed(seeds, variant);

            foreach (string seed in seeds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            foreach (string variant in EnumerateTextureVariants(seed))
                yield return variant;
        }

        private static void AddTextureSeed(ISet<string> seeds, string path)
        {
            string normalized = PathUtils.NormalizePath(path);
            if (IsTexturePath(normalized)) seeds.Add(normalized);
        }

        private static TextureFamilyContext BuildTextureFamilyContext(IEnumerable<string> paths)
        {
            var numericValues = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            var decorators = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawPath in paths ?? Array.Empty<string>())
            {
                string path = PathUtils.NormalizePath(rawPath);
                int slash = path.LastIndexOf('/');
                if (slash < 0 || slash == path.Length - 1) continue;
                string file = path[(slash + 1)..];

                Match numeric = NumericTextureNameRegex.Match(file);
                if (numeric.Success &&
                    int.TryParse(
                        numeric.Groups["number"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int number))
                {
                    string key = BuildTextureFamilyKey(
                        path,
                        numeric.Groups["prefix"].Value,
                        numeric.Groups["suffix"].Value);
                    if (!numericValues.TryGetValue(key, out HashSet<int> values))
                        numericValues[key] = values = new HashSet<int>();
                    values.Add(number);
                }

                Match decorated = DecoratedTextureNameRegex.Match(file);
                if (!decorated.Success) continue;
                string decorator = decorated.Groups["decorators"].Value;
                if (decorator.Length == 0) continue;
                string decoratorKey = BuildTextureDecoratorFamilyKey(path, decorated.Groups["core"].Value);
                if (!decorators.TryGetValue(decoratorKey, out HashSet<string> valuesForFamily))
                    decorators[decoratorKey] = valuesForFamily = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                valuesForFamily.Add(decorator);
            }

            return new TextureFamilyContext(numericValues, decorators);
        }

        private static IEnumerable<string> EnumerateNumericTextureVariants(
            string path,
            TextureFamilyContext familyContext)
        {
            string normalized = PathUtils.NormalizePath(path);
            int slash = normalized.LastIndexOf('/');
            if (slash < 0 || slash == normalized.Length - 1) yield break;

            string file = normalized[(slash + 1)..];
            Match match = NumericTextureNameRegex.Match(file);
            if (!match.Success) yield break;

            string prefix = match.Groups["prefix"].Value;
            string suffix = match.Groups["suffix"].Value;
            string familyKey = BuildTextureFamilyKey(normalized, prefix, suffix);
            var numbers = new HashSet<int>();
            if (familyContext?.NumericValuesByFamily.TryGetValue(familyKey, out HashSet<int> knownNumbers) == true)
                numbers.UnionWith(knownNumbers);

            if (!int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                yield break;
            numbers.Add(number);

            bool isSequenceFamily = prefix.EndsWith("circle_", StringComparison.OrdinalIgnoreCase) ||
                                     prefix.EndsWith("loadscreen_", StringComparison.OrdinalIgnoreCase);
            if (isSequenceFamily)
            {
                int max = Math.Min(350, numbers.Max() + (numbers.Count > 1 ? 15 : 0));
                for (int candidate = 0; candidate <= max; candidate++) numbers.Add(candidate);
            }

            int originalWidth = match.Groups["number"].Value.Length;
            foreach (int candidate in numbers.OrderBy(value => value))
            foreach (string candidateText in FormatNumericTokens(candidate, originalWidth))
            {
                string replacement = file[..match.Groups["number"].Index] +
                                     candidateText +
                                     file[(match.Groups["number"].Index + match.Groups["number"].Length)..];
                string result = normalized[..(slash + 1)] + replacement;
                if (!result.Equals(normalized, StringComparison.OrdinalIgnoreCase)) yield return result;
            }
        }

        private static IEnumerable<string> FormatNumericTokens(int number, int originalWidth)
        {
            yield return number.ToString(CultureInfo.InvariantCulture);
            if (originalWidth > 1)
                yield return number.ToString($"D{originalWidth}", CultureInfo.InvariantCulture);
            if (number <= 99 && originalWidth != 2)
                yield return number.ToString("D2", CultureInfo.InvariantCulture);
        }

        private static IEnumerable<string> EnumerateDecoratedTextureVariants(
            string path,
            TextureFamilyContext familyContext)
        {
            string normalized = PathUtils.NormalizePath(path);
            int slash = normalized.LastIndexOf('/');
            if (slash < 0 || slash == normalized.Length - 1) yield break;

            string file = normalized[(slash + 1)..];
            Match match = DecoratedTextureNameRegex.Match(file);
            if (!match.Success) yield break;

            string familyKey = BuildTextureDecoratorFamilyKey(normalized, match.Groups["core"].Value);
            var decorators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (familyContext?.DecoratorsByFamily.TryGetValue(familyKey, out HashSet<string> knownDecorators) == true)
                decorators.UnionWith(knownDecorators);

            foreach (string decorator in decorators.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                string result = normalized[..(slash + 1)] +
                                match.Groups["core"].Value +
                                decorator +
                                "." +
                                match.Groups["extension"].Value;
                if (!result.Equals(normalized, StringComparison.OrdinalIgnoreCase)) yield return result;
            }
        }

        private static string BuildTextureFamilyKey(string path, string prefix, string suffix)
        {
            int slash = path.LastIndexOf('/');
            string directory = slash >= 0 ? path[..slash] : string.Empty;
            directory = CanonicalizeTextureDirectory(directory);
            return directory + "/" + prefix.ToLowerInvariant() + "#" + suffix.ToLowerInvariant();
        }

        private static string BuildTextureDecoratorFamilyKey(string path, string core)
        {
            int slash = path.LastIndexOf('/');
            string directory = slash >= 0 ? path[..slash] : string.Empty;
            directory = CanonicalizeTextureDirectory(directory);
            return directory + "/" + core.ToLowerInvariant();
        }

        private static string CanonicalizeTextureDirectory(string directory)
        {
            string result = directory.ToLowerInvariant();
            result = Regex.Replace(
                result,
                @"/characters/[^/]+/skins/(?:base|skin\d+)",
                "/characters/{character}/skins/{skin}",
                RegexOptions.IgnoreCase);
            result = Regex.Replace(
                result,
                @"/characters/[^/]+",
                "/characters/{character}",
                RegexOptions.IgnoreCase);
            return result;
        }

        private static IReadOnlyList<string> BuildChampionTokenAliases(
            string champion,
            IEnumerable<string> contextPaths)
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddChampionAliasGroup(aliases, champion);
            foreach (string contextPath in contextPaths ?? Array.Empty<string>())
            {
                if (TryGetChampionPath(contextPath, out string contextChampion) &&
                    AreChampionNamesRelated(contextChampion, champion))
                    AddChampionAliasGroup(aliases, contextChampion);
            }

            string canonicalChampion = GetChampionAliasBase(champion);
            return aliases
                .Where(value => value.Length > 0)
                .OrderBy(value => GetChampionAliasPriority(value, champion, canonicalChampion))
                .ThenByDescending(value => value.Length)
                .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static int GetChampionAliasPriority(string alias, string champion, string canonicalChampion)
        {
            if (alias.Equals(champion, StringComparison.OrdinalIgnoreCase)) return 0;
            if (alias.Equals(canonicalChampion, StringComparison.OrdinalIgnoreCase)) return 1;
            if (alias.StartsWith("jade_", StringComparison.OrdinalIgnoreCase)) return 2;
            if (alias.StartsWith("cherry_goh_", StringComparison.OrdinalIgnoreCase)) return 3;
            return 4;
        }

        private static void AddChampionAliasGroup(ISet<string> aliases, string champion)
        {
            if (string.IsNullOrWhiteSpace(champion)) return;
            aliases.Add(champion.ToLowerInvariant());
            string canonical = GetChampionAliasBase(champion);
            aliases.Add(canonical);
            foreach (string[] group in ChampionAliasGroups)
                if (group.Any(value => value.Equals(canonical, StringComparison.OrdinalIgnoreCase)))
                    foreach (string value in group) aliases.Add(value);
        }

        private static IEnumerable<string> EnumerateChampionAliasVariants(
            string path,
            string sourceChampion,
            IReadOnlyList<string> aliases)
        {
            var pathVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string alias in aliases ?? Array.Empty<string>())
            {
                string componentVariant = ReplaceChampionPathSegment(path, sourceChampion, alias);
                pathVariants.Add(componentVariant);
                pathVariants.Add(ReplaceChampionTokenInFileName(componentVariant, sourceChampion, alias));
            }
            pathVariants.Add(PathUtils.NormalizePath(path));

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pathVariant in pathVariants)
            {
                result.Add(pathVariant);
                int slash = pathVariant.LastIndexOf('/');
                if (slash < 0 || slash == pathVariant.Length - 1) continue;
                string file = pathVariant[(slash + 1)..];
                foreach (string token in aliases ?? Array.Empty<string>())
                {
                    if (!IsPreferredAliasToken(file, token, aliases)) continue;
                    foreach (string replacement in aliases)
                    {
                        if (replacement.Equals(token, StringComparison.OrdinalIgnoreCase)) continue;
                        string replacedFile = file.Replace(token, replacement, StringComparison.OrdinalIgnoreCase);
                        result.Add(pathVariant[..(slash + 1)] + replacedFile);
                    }
                }
            }

            foreach (string value in result)
                yield return value;
        }

        private static bool IsPreferredAliasToken(
            string file,
            string token,
            IReadOnlyList<string> aliases)
        {
            if (file.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return !(aliases ?? Array.Empty<string>()).Any(other =>
                other.Length > token.Length &&
                other.Contains(token, StringComparison.OrdinalIgnoreCase) &&
                file.Contains(other, StringComparison.OrdinalIgnoreCase));
        }

        private static string ReplaceChampionPathSegment(string path, string sourceChampion, string targetChampion)
        {
            if (string.IsNullOrEmpty(sourceChampion) || string.IsNullOrEmpty(targetChampion))
                return PathUtils.NormalizePath(path);

            string normalized = PathUtils.NormalizePath(path);
            string marker = "/characters/" + sourceChampion + "/";
            int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return normalized;
            return normalized[..(index + "/characters/".Length)] +
                   targetChampion +
                   normalized[(index + marker.Length - 1)..];
        }

        private static string ReplaceChampionTokenInFileName(string path, string sourceToken, string targetToken)
        {
            if (string.IsNullOrEmpty(sourceToken) || string.IsNullOrEmpty(targetToken))
                return PathUtils.NormalizePath(path);

            string normalized = PathUtils.NormalizePath(path);
            int slash = normalized.LastIndexOf('/');
            if (slash < 0 || slash == normalized.Length - 1) return normalized;
            string file = normalized[(slash + 1)..];
            if (file.IndexOf(sourceToken, StringComparison.OrdinalIgnoreCase) < 0) return normalized;
            return normalized[..(slash + 1)] + file.Replace(sourceToken, targetToken, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReplaceChampionAndSkin(
            string path,
            string sourceChampion,
            string sourceSkin,
            string targetChampion,
            string targetSkin)
        {
            string result = path.Replace(sourceChampion, targetChampion, StringComparison.OrdinalIgnoreCase);
            return result.Replace(sourceSkin, targetSkin, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsChampionPathForTarget(string path, string champion) =>
            TryGetChampionPath(path, out string pathChampion) &&
            AreChampionNamesRelated(pathChampion, champion);

        private static bool AreChampionNamesRelated(string first, string second) =>
            !string.IsNullOrWhiteSpace(first) &&
            !string.IsNullOrWhiteSpace(second) &&
            GetChampionAliasBase(first).Equals(GetChampionAliasBase(second), StringComparison.OrdinalIgnoreCase);

        private static string GetChampionAliasBase(string champion)
        {
            string normalized = champion?.Trim().ToLowerInvariant() ?? string.Empty;
            foreach (string prefix in ChampionAliasPrefixes)
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && normalized.Length > prefix.Length)
                {
                    normalized = normalized[prefix.Length..];
                    break;
                }

            foreach (string[] group in ChampionAliasGroups)
                if (group.Any(value => value.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                    return group[0];

            return normalized;
        }

        private static bool TryGetChampionPath(string path, out string champion)
        {
            Match match = ChampionPathRegex.Match(PathUtils.NormalizePath(path));
            champion = match.Success ? match.Groups["champion"].Value.ToLowerInvariant() : null;
            return champion != null;
        }

        private static bool TryGetChampionTexture(
            string path,
            out string champion,
            out string skin,
            out string extension)
        {
            Match match = ChampionTexturePathRegex.Match(PathUtils.NormalizePath(path));
            champion = match.Success ? match.Groups["champion"].Value.ToLowerInvariant() : null;
            skin = match.Success ? match.Groups["skin"].Value.ToLowerInvariant() : null;
            extension = match.Success ? Path.GetExtension(match.Groups["relative"].Value).TrimStart('.').ToLowerInvariant() : null;
            return match.Success;
        }

        private static bool IsTexturePath(string path)
        {
            string normalized = PathUtils.NormalizePath(path);
            return normalized.Contains('/', StringComparison.Ordinal) &&
                   IsTextureExtension(Path.GetExtension(normalized).TrimStart('.'));
        }

        private static bool IsTextureExtension(string extension) =>
            extension.Equals("tex", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals("dds", StringComparison.OrdinalIgnoreCase);

        private static string ExtractChampionFromWad(string wadPath)
        {
            string normalized = wadPath.Replace('\\', '/');
            if (!normalized.Contains("/champions/", StringComparison.OrdinalIgnoreCase)) return null;
            string name = Path.GetFileNameWithoutExtension(wadPath);
            name = Path.GetFileNameWithoutExtension(name);
            if (name.Length == 0 || GenericWadNames.Contains(name)) return null;
            return name.ToLowerInvariant();
        }

        private static bool IsLikelyStructuredPayload(ArraySegment<byte> data, string extension)
        {
            if (data.Array is null || data.Count < 4) return false;
            if (StructuredExtensions.Contains(extension)) return true;
            int magic = BitConverter.ToInt32(data.Array, data.Offset);
            return magic == PropMagic || magic == PtchMagic;
        }

        private static int CollectTextureReferences(ArraySegment<byte> data, ISet<string> references)
        {
            if (data.Array is null || data.Count == 0) return 0;
            string text = Encoding.ASCII.GetString(data.Array, data.Offset, data.Count);
            foreach (Match match in RawTexturePathRegex.Matches(text)) references.Add(match.Value);

            int parseErrors = 0;
            int magic = data.Count >= 4 ? BitConverter.ToInt32(data.Array, data.Offset) : 0;
            if (magic != PropMagic && magic != PtchMagic) return parseErrors;

            try
            {
                using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                var tree = new BinTree(stream);
                foreach (BinTreeObject obj in tree.Objects.Values)
                foreach (BinTreeProperty property in obj.Properties.Values)
                foreach (BinTreeProperty nested in EnumerateProperties(property))
                    if (nested is BinTreeString value && IsTexturePath(value.Value)) references.Add(value.Value);

                foreach (BinTreeDataOverride dataOverride in tree.DataOverrides)
                foreach (BinTreeProperty nested in EnumerateProperties(dataOverride.Property))
                    if (nested is BinTreeString value && IsTexturePath(value.Value)) references.Add(value.Value);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                parseErrors++;
                if (parseErrors <= 3)
                    Console.WriteLine($"  [warn] BIN texture reference parse failed: {exception.Message}");
            }

            return parseErrors;
        }

        private static IEnumerable<string> ExpandTextureReference(string value)
        {
            string normalized = PathUtils.NormalizePath(value?.Trim());
            if (!IsTexturePath(normalized)) yield break;

            if (normalized.StartsWith("characters/", StringComparison.OrdinalIgnoreCase))
            {
                yield return "assets/" + normalized;
                yield return "data/" + normalized;
            }
            else
            {
                yield return normalized;
            }
        }

        private static IEnumerable<BinTreeProperty> EnumerateProperties(BinTreeProperty property)
        {
            if (property is null) yield break;
            yield return property;

            IEnumerable<BinTreeProperty> children = property switch
            {
                BinTreeStruct structure => structure.Properties.Values,
                BinTreeOptional optional when optional.Value is not null => new[] { optional.Value },
                BinTreeContainer container => container.Elements,
                BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
                _ => Array.Empty<BinTreeProperty>()
            };
            foreach (BinTreeProperty child in children)
            foreach (BinTreeProperty nested in EnumerateProperties(child))
                yield return nested;
        }

        private static bool TryReadTextureMetadata(
            ArraySegment<byte> data,
            string extension,
            out TextureMetadata metadata)
        {
            metadata = default;
            if (data.Array is null || data.Count < 12) return false;

            ReadOnlySpan<byte> bytes = data.Array.AsSpan(data.Offset, data.Count);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
            if (extension.Equals("tex", StringComparison.OrdinalIgnoreCase) && magic == 0x00584554)
            {
                int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4, 2));
                int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
                int format = bytes[9];
                int resourceType = bytes[10];
                int flags = bytes[11];
                int mipCount = (flags & 1) != 0
                    ? (int)Math.Floor(Math.Log2(Math.Max(width, height)) + 1)
                    : 1;
                metadata = new TextureMetadata(extension.ToLowerInvariant(), width, height, format, resourceType, flags, mipCount);
                return true;
            }

            if (extension.Equals("dds", StringComparison.OrdinalIgnoreCase) &&
                magic == 0x20534444 && bytes.Length >= 128)
            {
                int height = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12, 4));
                int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16, 4));
                int mipCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4));
                int format = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(84, 4));
                metadata = new TextureMetadata(extension.ToLowerInvariant(), width, height, format, 0, 0, Math.Max(1, mipCount));
                return width > 0 && height > 0;
            }

            return false;
        }

        private static ulong HashContent(ArraySegment<byte> data) =>
            XxHash64.HashToUInt64(data.Array.AsSpan(data.Offset, data.Count));

        private sealed class TargetTexture
        {
            public TargetTexture(
                ulong hash,
                string extension,
                int size,
                ulong contentHash,
                TextureMetadata? metadata)
            {
                Hash = hash;
                Extension = extension.ToLowerInvariant();
                Size = size;
                ContentHash = contentHash;
                Metadata = metadata;
                ContentFingerprints.Add(new ContentFingerprint(size, contentHash));
                if (metadata.HasValue) MetadataValues.Add(metadata.Value);
            }

            public ulong Hash { get; }
            public string Extension { get; }
            public int Size { get; }
            public ulong ContentHash { get; }
            public TextureMetadata? Metadata { get; }
            public HashSet<ContentFingerprint> ContentFingerprints { get; } = new();
            public HashSet<TextureMetadata> MetadataValues { get; } = new();
            public bool HasPayloadConflict { get; private set; }
            public List<TargetLocation> Locations { get; } = new();
            public HashSet<string> Champions { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string PrimaryChampion => Champions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).First();

            public void AddPayload(
                string extension,
                int size,
                ulong contentHash,
                TextureMetadata? metadata)
            {
                if (!Extension.Equals(extension, StringComparison.OrdinalIgnoreCase) ||
                    Size != size ||
                    ContentHash != contentHash ||
                    (Metadata.HasValue != metadata.HasValue) ||
                    (Metadata.HasValue && metadata.HasValue && !Metadata.Value.Equals(metadata.Value)))
                    HasPayloadConflict = true;

                ContentFingerprints.Add(new ContentFingerprint(size, contentHash));
                if (metadata.HasValue) MetadataValues.Add(metadata.Value);
            }

            public void AddLocation(string champion, string wadPath, string relativeWad, ulong chunkHash, int chunkSize)
            {
                Champions.Add(champion);
                Locations.Add(new TargetLocation(champion, wadPath, relativeWad, chunkHash, chunkSize));
            }
        }

        private sealed record TargetLocation(string Champion, string WadPath, string RelativeWad, ulong ChunkHash, int ChunkSize);

        private sealed record RawHashReference(
            ulong TargetHash,
            ulong SourceChunkHash,
            string SourcePath,
            string WadPath,
            int Offset,
            bool BigEndian,
            string Evidence,
            uint? PropertyHash);

        private readonly record struct RawHashReferenceKey(
            ulong TargetHash,
            string WadPath,
            ulong SourceChunkHash,
            int Offset,
            bool BigEndian);

        private readonly record struct RawSemanticReferenceKey(
            ulong TargetHash,
            string WadPath,
            ulong SourceChunkHash,
            uint PropertyHash,
            ulong Value);

        private readonly record struct ContentFingerprint(int Size, ulong Hash);

        private readonly record struct ContentOccurrence(string WadPath, ulong ChunkHash);

        private readonly record struct TextureMetadata(
            string Extension,
            int Width,
            int Height,
            int Format,
            int ResourceType,
            int Flags,
            int MipCount);

        private sealed record TextureFamilyContext(
            IReadOnlyDictionary<string, HashSet<int>> NumericValuesByFamily,
            IReadOnlyDictionary<string, HashSet<string>> DecoratorsByFamily);

        private sealed record ContentTwinIndex(
            IReadOnlyDictionary<ulong, HashSet<string>> PathsByTarget,
            int ProcessedWads,
            int Reads,
            int Considered,
            int CacheHits,
            int Errors);

        private readonly record struct TextureSignature(int Size, string Extension, TextureMetadata? Metadata);

        private sealed record StageResult(
            string Name,
            long Candidates,
            int Resolved,
            int Remaining,
            TimeSpan Elapsed,
            int ParseErrors,
            int ReadErrors);

        private sealed record LabOptions(
            string PbeRoot,
            int CandidateBudget,
            int RawCandidateBudget,
            int ContentReadBudget,
            string ChampionFilter,
            bool SkipReferences,
            bool SkipTopology,
            bool SkipCrossChampion,
            bool SkipContent,
            bool PrintContext,
            bool RunNearTwins,
            int NearTwinReadBudget,
            int TraceCandidates);
    }
}
