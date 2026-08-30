using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    /// <summary>
    /// Read-only GAME laboratory for measuring the hash-only resolution pipeline.
    /// It deliberately does not use HashGuessingStore or HashGuessPersistenceService.
    /// </summary>
    internal static class GameInMemoryResolutionDiagnostic
    {
        private const string DefaultPbeDirectory = @"C:\Riot Games\League of Legends (PBE)";
        private const int DefaultBasicBudget = 5_000_000;
        private const int PropMagic = 0x504F5250;
        private const int PtchMagic = 0x48435450;

        private static readonly string[] BinCatalogNames =
        {
            "hashes.binhashes.txt",
            "hashes.binentries.txt",
            "hashes.binfields.txt",
            "hashes.bintypes.txt"
        };

        private static readonly HashSet<string> GenericWadNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "global", "shared", "bootstrap", "champions", "maps", "map11", "map22", "mapgeo",
            "ui", "ux", "shaders", "particles", "common", "levels", "loadouts", "patching",
            "clientstates", "companions", "items", "spells", "data", "final", "shipping"
        };

        public static void Run(string[] args)
        {
            LabOptions options = ParseOptions(args);
            if (!Directory.Exists(options.PbeRoot))
            {
                Console.WriteLine($"GAME root not found: {options.PbeRoot}");
                return;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string hashesDirectory = options.HashesDirectory ?? Path.Combine(localAppData, "AssetsManager", "hashes");
            string unknownsPath = options.UnknownsPath ?? Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");
            string gameHashesPath = Path.Combine(hashesDirectory, "hashes.game.txt");
            string lcuHashesPath = Path.Combine(hashesDirectory, "hashes.lcu.txt");

            if (!File.Exists(gameHashesPath) || !File.Exists(unknownsPath))
            {
                Console.WriteLine("Missing hashes.game.txt or unknowns.game.txt in the local AssetsManager data.");
                return;
            }

            var gameHashFile = new HashFile(HashGuessDomain.Game, gameHashesPath);
            IReadOnlyDictionary<ulong, string> gamePaths = gameHashFile.Load();
            var lcuHashFile = File.Exists(lcuHashesPath)
                ? new HashFile(HashGuessDomain.Lcu, lcuHashesPath)
                : new HashFile(HashGuessDomain.Lcu, Array.Empty<string>());
            IReadOnlyDictionary<ulong, string> lcuPaths = lcuHashFile.Load();
            var resolvedPaths = MergeResolvedPaths(gamePaths, lcuPaths);
            IReadOnlyDictionary<uint, string> binNames = LoadBinNames(hashesDirectory);
            var persistedUnknowns = LoadUnknowns(unknownsPath);
            persistedUnknowns.ExceptWith(resolvedPaths.Keys);

            if (persistedUnknowns.Count == 0)
            {
                Console.WriteLine("No unresolved GAME hashes loaded from unknowns.game.txt.");
                return;
            }

            var gameGuesser = new GameHashGuesser(
                gameHashFile,
                resolveBinHash: hash => binNames.TryGetValue(hash, out string value) ? value : hash.ToString("x8"));
            string[] wadPaths;
            try
            {
                wadPaths = gameGuesser.FindWads(options.PbeRoot);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Could not enumerate GAME WADs: {exception.Message}");
                return;
            }
            string[] grepWadPaths = string.IsNullOrWhiteSpace(options.WadContains)
                ? wadPaths
                : wadPaths.Where(path => path.Contains(options.WadContains, StringComparison.OrdinalIgnoreCase)).ToArray();

            string gameDirectory = Directory.Exists(Path.Combine(options.PbeRoot, "Game"))
                ? Path.Combine(options.PbeRoot, "Game")
                : options.PbeRoot;

            Console.WriteLine("==================================================");
            Console.WriteLine("    GAME IN-MEMORY HASH RESOLUTION LAB");
            Console.WriteLine("==================================================");
            Console.WriteLine($"Root:              {options.PbeRoot}");
            Console.WriteLine($"WADs:              {wadPaths.Length:N0}");
            Console.WriteLine($"Persisted unknowns: {persistedUnknowns.Count:N0}");
            Console.WriteLine($"GAME catalog paths: {gamePaths.Count:N0}");
            Console.WriteLine($"LCU catalog paths:  {lcuPaths.Count:N0} (used only for GAME cross-domain parity)");
            Console.WriteLine("Persistence:        disabled (all engines and results are in memory)");
            if (options.MaxWads != int.MaxValue)
                Console.WriteLine("Grep scope note: --max-wads limits GrepWad only; inventory and location scans remain full.");
            if (!string.IsNullOrWhiteSpace(options.WadContains))
                Console.WriteLine($"Grep WAD filter:   '{options.WadContains}' ({grepWadPaths.Length:N0} matching WADs)");

            HashWadInventory inventory = BuildInventory(gameGuesser, wadPaths);
            HashSet<ulong> observedUnknowns = inventory.Hashes
                .Where(hash => !resolvedPaths.ContainsKey(hash))
                .ToHashSet();
            PrintInventoryComparison(persistedUnknowns, observedUnknowns, inventory);

            HashSet<ulong> targetUnknowns = options.IncludeObserved
                ? observedUnknowns
                : new HashSet<ulong>(persistedUnknowns);
            if (targetUnknowns.Count == 0)
            {
                Console.WriteLine("No target hashes remain after inventory filtering.");
                return;
            }

            IReadOnlyDictionary<ulong, UnknownLocation> locations = IndexUnknownLocations(
                wadPaths,
                gameDirectory,
                targetUnknowns,
                resolvedPaths);
            PrintLocationSummary(locations, targetUnknowns);

            IReadOnlyList<string> contextCharacters = DeriveContextCharacters(locations.Values);
            Console.WriteLine();
            Console.WriteLine($"Evidence-derived champion contexts: {contextCharacters.Count:N0}");
            Console.WriteLine(contextCharacters.Count == 0 ? "  (none)" : $"  {string.Join(", ", contextCharacters)}");

            PassResult grepResult = null;
            if (!options.SkipGrep)
            {
                var evidence = new List<StructuralEvidence>();
                grepResult = RunGrepPass(
                    "GAME GrepWad / in-memory target",
                    grepWadPaths,
                    targetUnknowns,
                    resolvedPaths,
                    binNames,
                    gameHashFile,
                    options.MaxWads,
                    collectEvidence: true,
                    evidence: evidence);
                PrintPassResult(grepResult);
                PrintStructuralEvidence(evidence, grepResult.RemainingUnknowns, gameDirectory);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("[4] GrepWad pass skipped by --skip-grep.");
            }

            if (!options.SkipBasic)
            {
                Console.WriteLine();
                Console.WriteLine("==================================================");
                Console.WriteLine("    GAME BASIC / BOUNDED IN-MEMORY COMPARISON");
                Console.WriteLine("==================================================");

                BasicPassResult corpusCharacters = RunCharacterPass(
                    "GAME Basic characters / full-catalog order (budgeted)",
                    targetUnknowns,
                    gameHashFile,
                    binNames,
                    characters: null,
                    options.BasicBudget);
                BasicPassResult contextCharactersPass = RunCharacterPass(
                    "GAME Basic characters / WAD-context order (budgeted)",
                    targetUnknowns,
                    gameHashFile,
                    binNames,
                    contextCharacters,
                    options.BasicBudget);
                PrintCharacterComparison(corpusCharacters, contextCharactersPass);

                BasicPassResult basicResult = RunBoundedBasicPass(
                    targetUnknowns,
                    options.PbeRoot,
                    gameHashFile,
                    lcuHashFile,
                    binNames,
                    contextCharacters,
                    options.BasicBudget);
                PrintBasicResult(basicResult);
            }

            if (!options.SkipCacheProbe)
                RunRunScopedCacheProbe(locations.Values, gameHashFile, binNames, resolvedPaths);

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
                    if (args[index].Equals("--max-wads", StringComparison.OrdinalIgnoreCase) ||
                        args[index].Equals("--basic-budget", StringComparison.OrdinalIgnoreCase) ||
                        args[index].Equals("--hashes-directory", StringComparison.OrdinalIgnoreCase) ||
                        args[index].Equals("--unknowns-path", StringComparison.OrdinalIgnoreCase) ||
                        args[index].Equals("--wad-contains", StringComparison.OrdinalIgnoreCase))
                        index++;
                    continue;
                }

                root ??= args[index];
            }

            root ??= DefaultPbeDirectory;
            int maxWads = ParseInt(args, "--max-wads", int.MaxValue);
            int basicBudget = ParseInt(args, "--basic-budget", DefaultBasicBudget);
            return new LabOptions(
                root,
                maxWads,
                basicBudget,
                args.Any(value => value.Equals("--include-observed", StringComparison.OrdinalIgnoreCase)),
                args.Any(value => value.Equals("--skip-basic", StringComparison.OrdinalIgnoreCase)),
                args.Any(value => value.Equals("--skip-cache-probe", StringComparison.OrdinalIgnoreCase)),
                args.Any(value => value.Equals("--skip-grep", StringComparison.OrdinalIgnoreCase)),
                ParseString(args, "--hashes-directory"),
                ParseString(args, "--unknowns-path"),
                ParseString(args, "--wad-contains"));
        }

        private static int ParseInt(string[] args, string option, int fallback)
        {
            int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value))
                return fallback;
            return Math.Max(1, value);
        }

        private static string ParseString(string[] args, string option)
        {
            int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static IReadOnlyDictionary<ulong, string> MergeResolvedPaths(
            IReadOnlyDictionary<ulong, string> gamePaths,
            IReadOnlyDictionary<ulong, string> lcuPaths)
        {
            var merged = new Dictionary<ulong, string>();
            foreach ((ulong hash, string path) in gamePaths)
                merged[hash] = path;
            foreach ((ulong hash, string path) in lcuPaths)
                merged.TryAdd(hash, path);
            return merged;
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

        private static IReadOnlyDictionary<uint, string> LoadBinNames(string hashesDirectory)
        {
            var values = new Dictionary<uint, string>();
            foreach (string fileName in BinCatalogNames)
            {
                string path = Path.Combine(hashesDirectory, fileName);
                if (!File.Exists(path)) continue;

                var file = new HashFile(HashGuessDomain.Game, path);
                foreach ((ulong hash, string value) in file.Load())
                {
                    if (hash > uint.MaxValue) continue;
                    values.TryAdd((uint)hash, value);
                }
            }
            return values;
        }

        private static HashWadInventory BuildInventory(GameHashGuesser guesser, IReadOnlyList<string> wadPaths)
        {
            Console.WriteLine();
            Console.WriteLine("[1] Building fresh WAD inventory in memory...");
            var stopwatch = Stopwatch.StartNew();
            HashWadInventory inventory = guesser.FromWads(
                wadPaths,
                CancellationToken.None,
                (path, exception) => Console.WriteLine($"  [warn] inventory skipped {Path.GetFileName(path)}: {exception.Message}"),
                (completed, total, name) =>
                {
                    if (completed == 1 || completed == total || completed % 100 == 0)
                        Console.WriteLine($"  inventory {completed:N0}/{total:N0}: {name}");
                });
            stopwatch.Stop();
            Console.WriteLine($"  chunks indexed: {inventory.ChunkCount:N0}");
            Console.WriteLine($"  unique path hashes (including atlas sprites): {inventory.Hashes.Count:N0}");
            Console.WriteLine($"  elapsed: {stopwatch.Elapsed:hh\\:mm\\:ss}");
            return inventory;
        }

        private static void PrintInventoryComparison(
            IReadOnlySet<ulong> persistedUnknowns,
            IReadOnlySet<ulong> observedUnknowns,
            HashWadInventory inventory)
        {
            var newHashes = observedUnknowns.Except(persistedUnknowns).ToList();
            var staleHashes = persistedUnknowns.Except(observedUnknowns).ToList();
            Console.WriteLine();
            Console.WriteLine("[2] Fresh inventory comparison (read-only)");
            Console.WriteLine($"  fresh unknowns in WAD inventory: {observedUnknowns.Count:N0}");
            Console.WriteLine($"  persisted unknowns:              {persistedUnknowns.Count:N0}");
            Console.WriteLine($"  new WAD unknowns absent on disk:  {newHashes.Count:N0}");
            Console.WriteLine($"  persisted hashes not observed:    {staleHashes.Count:N0}");
            Console.WriteLine($"  fingerprint: GAME:{inventory.ChunkCount}:{inventory.HashXor:x16}:{inventory.HashSum:x16}");
            PrintHashSample("new", newHashes);
            PrintHashSample("stale", staleHashes);
        }

        private static void PrintHashSample(string label, IEnumerable<ulong> hashes)
        {
            ulong[] sample = hashes.OrderBy(value => value).Take(8).ToArray();
            if (sample.Length > 0)
                Console.WriteLine($"  {label} sample: {string.Join(", ", sample.Select(value => value.ToString("x16")))}");
        }

        private static IReadOnlyDictionary<ulong, UnknownLocation> IndexUnknownLocations(
            IReadOnlyList<string> wadPaths,
            string gameDirectory,
            IReadOnlySet<ulong> targetUnknowns,
            IReadOnlyDictionary<ulong, string> resolvedPaths)
        {
            Console.WriteLine();
            Console.WriteLine("[3] Locating target hashes and classifying their payloads...");
            var locations = new Dictionary<ulong, UnknownLocation>();
            int errors = 0;

            foreach (string wadPath in wadPaths)
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach ((ulong hash, WadChunk chunk) in wad.Chunks)
                    {
                        if (!targetUnknowns.Contains(hash)) continue;

                        string relativeWad = Path.GetRelativePath(gameDirectory, wadPath).Replace('\\', '/');
                        if (!locations.TryGetValue(hash, out UnknownLocation location))
                        {
                            string sourcePath = resolvedPaths.TryGetValue(hash, out string knownPath)
                                ? knownPath
                                : hash.ToString("x16");
                            string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
                            if (extension.Length == 0 && chunk.Compression != WadChunkCompression.Satellite)
                            {
                                try
                                {
                                    using var owner = wad.LoadChunkDecompressed(chunk);
                                    extension = HashGuessingService.InferChunkExtension(owner.DangerousGetArray(), detectJson: false);
                                }
                                catch (Exception exception)
                                {
                                    errors++;
                                    Console.WriteLine($"  [warn] could not classify {hash:x16} in {relativeWad}: {exception.Message}");
                                }
                            }

                            if (extension.Length > 0 && Path.GetExtension(sourcePath).Length == 0)
                                sourcePath += "." + extension;

                            location = new UnknownLocation(
                                hash,
                                wadPath,
                                relativeWad,
                                sourcePath,
                                extension.Length == 0 ? "unknown" : extension,
                                chunk.UncompressedSize,
                                0);
                        }

                        locations[hash] = location with { Occurrences = location.Occurrences + 1 };
                    }
                }
                catch (Exception exception)
                {
                    errors++;
                    Console.WriteLine($"  [warn] could not inspect {Path.GetFileName(wadPath)}: {exception.Message}");
                }
            }

            Console.WriteLine($"  located: {locations.Count:N0}/{targetUnknowns.Count:N0}");
            Console.WriteLine($"  hashes with multiple WAD occurrences: {locations.Values.Count(value => value.Occurrences > 1):N0}");
            if (errors > 0) Console.WriteLine($"  non-fatal inspection errors: {errors:N0}");
            return locations;
        }

        private static void PrintLocationSummary(
            IReadOnlyDictionary<ulong, UnknownLocation> locations,
            IReadOnlySet<ulong> targetUnknowns)
        {
            Console.WriteLine("  payload types:");
            foreach (var group in locations.Values.GroupBy(value => value.Extension).OrderByDescending(group => group.Count()))
                Console.WriteLine($"    {group.Key,-18} x{group.Count():N0}");

            var missing = targetUnknowns.Where(hash => !locations.ContainsKey(hash)).OrderBy(hash => hash).Take(8).ToArray();
            if (missing.Length > 0)
                Console.WriteLine($"  not directly located sample: {string.Join(", ", missing.Select(value => value.ToString("x16")))}");

            Console.WriteLine("  WAD contexts with most target hashes:");
            foreach (var group in locations.Values
                .GroupBy(value => value.RelativeWad, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(12))
            {
                Console.WriteLine($"    {group.Count(),3}  {group.Key}");
            }
        }

        private static IReadOnlyList<string> DeriveContextCharacters(IEnumerable<UnknownLocation> locations)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UnknownLocation location in locations)
            {
                if (!location.RelativeWad.Contains("/champions/", StringComparison.OrdinalIgnoreCase) &&
                    !location.RelativeWad.StartsWith("champions/", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = Path.GetFileName(location.RelativeWad);
                name = Path.GetFileNameWithoutExtension(name);
                name = Path.GetFileNameWithoutExtension(name);
                if (name.Length == 0 || GenericWadNames.Contains(name)) continue;
                if (!name.All(value => char.IsLetterOrDigit(value) || value == '_')) continue;
                result.Add(name.ToLowerInvariant());
            }
            return result.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static PassResult RunGrepPass(
            string name,
            IReadOnlyList<string> allWadPaths,
            IReadOnlySet<ulong> targetUnknowns,
            IReadOnlyDictionary<ulong, string> resolvedPaths,
            IReadOnlyDictionary<uint, string> binNames,
            HashFile gameHashFile,
            int maxWads,
            bool collectEvidence,
            List<StructuralEvidence> evidence,
            GameHashGuesser existingGuesser = null)
        {
            IReadOnlyList<string> wadPaths = maxWads == int.MaxValue
                ? allWadPaths
                : allWadPaths.Take(maxWads).ToArray();
            var unknowns = new HashSet<ulong>(targetUnknowns);
            var engine = new HashGuessEngine(HashGuessDomain.Game, unknowns);
            var guesser = existingGuesser ?? new GameHashGuesser(
                gameHashFile,
                resolveBinHash: hash => binNames.TryGetValue(hash, out string value) ? value : hash.ToString("x8"));
            var process = Process.GetCurrentProcess();
            ForceCollection();
            process.Refresh();
            long managedBefore = GC.GetTotalMemory(true);
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            long workingSetBefore = process.WorkingSet64;
            long peakWorkingSetBefore = process.PeakWorkingSet64;
            int processedWads = 0;
            int processedChunks = 0;
            int grepChunks = 0;
            int skippedChunks = 0;
            int unreadableChunks = 0;
            int structuralParseErrors = 0;
            int extensionCacheHits = 0;
            var inferredExtensions = new Dictionary<ulong, string>();
            var stopwatch = Stopwatch.StartNew();

            Console.WriteLine();
            Console.WriteLine($"[4] {name}");
            Console.WriteLine($"  WAD scope: {wadPaths.Count:N0}/{allWadPaths.Count:N0}");

            foreach (string wadPath in wadPaths)
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach ((ulong hash, WadChunk chunk) in wad.Chunks)
                    {
                        CancellationToken.None.ThrowIfCancellationRequested();
                        processedChunks++;
                        if (chunk.Compression == WadChunkCompression.Satellite)
                        {
                            skippedChunks++;
                            continue;
                        }

                        string sourcePath = resolvedPaths.TryGetValue(hash, out string knownPath)
                            ? knownPath
                            : hash.ToString("x16");
                        string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
                        if (extension.Length == 0 && inferredExtensions.TryGetValue(hash, out string cachedExtension))
                        {
                            extension = cachedExtension;
                            extensionCacheHits++;
                        }
                        if (IsSkippedExtension(extension))
                        {
                            skippedChunks++;
                            continue;
                        }

                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(chunk);
                            ArraySegment<byte> data = owner.DangerousGetArray();
                            if (extension.Length == 0)
                            {
                                extension = HashGuessingService.InferChunkExtension(data, detectJson: false);
                                inferredExtensions[hash] = extension;
                                if (IsSkippedExtension(extension))
                                {
                                    skippedChunks++;
                                    continue;
                                }
                                if (extension.Length > 0) sourcePath += "." + extension;
                            }

                            if (collectEvidence && IsBinExtension(extension))
                            {
                                int parseErrors = CollectStructuralEvidence(
                                    data,
                                    sourcePath,
                                    wadPath,
                                    hash,
                                    targetUnknowns,
                                    binNames,
                                    evidence,
                                    out string parseError);
                                structuralParseErrors += parseErrors;
                                if (parseErrors > 0 && structuralParseErrors <= 10)
                                    Console.WriteLine($"  [warn] BIN evidence parse failed in {Path.GetFileName(wadPath)} ({hash:x16}): {parseError}");
                            }

                            guesser.GrepWad(engine, data, sourcePath, wadPath, hash, CancellationToken.None);
                            grepChunks++;
                        }
                        catch (Exception exception)
                        {
                            unreadableChunks++;
                            if (unreadableChunks <= 10)
                                Console.WriteLine($"  [warn] skipped chunk {hash:x16} in {Path.GetFileName(wadPath)}: {exception.Message}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"  [warn] skipped WAD {Path.GetFileName(wadPath)}: {exception.Message}");
                }

                processedWads++;
                if (processedWads == 1 || processedWads == wadPaths.Count || processedWads % 100 == 0)
                {
                    Console.WriteLine($"  progress {processedWads:N0}/{wadPaths.Count:N0} WADs, " +
                                      $"{processedChunks:N0} chunks, {engine.CheckedCandidates:N0} candidates, " +
                                      $"{engine.Matches.Count:N0} matches");
                }
            }

            stopwatch.Stop();
            long allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
            long managedAfter = GC.GetTotalMemory(true);
            process.Refresh();
            long workingSetAfter = process.WorkingSet64;
            long peakWorkingSetAfter = process.PeakWorkingSet64;
            return new PassResult(
                name,
                targetUnknowns.Count,
                engine.Matches.Values.ToList(),
                new HashSet<ulong>(engine.UnknownHashes),
                engine.CheckedCandidates,
                engine.DiscardedCandidates,
                stopwatch.Elapsed,
                managedAfter - managedBefore,
                allocatedAfter - allocatedBefore,
                workingSetAfter - workingSetBefore,
                Math.Max(0, peakWorkingSetAfter - peakWorkingSetBefore),
                processedWads,
                processedChunks,
                grepChunks,
                skippedChunks,
                unreadableChunks,
                structuralParseErrors,
                extensionCacheHits);
        }

        private static int CollectStructuralEvidence(
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            IReadOnlySet<ulong> targetUnknowns,
            IReadOnlyDictionary<uint, string> binNames,
            ICollection<StructuralEvidence> evidence,
            out string errorMessage)
        {
            errorMessage = null;
            if (data.Count < 4 || data.Array is null) return 0;
            int magic = BitConverter.ToInt32(data.Array, data.Offset);
            if (magic != PropMagic && magic != PtchMagic) return 0;

            try
            {
                using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                var tree = new BinTree(stream);
                foreach (BinTreeObject obj in tree.Objects.Values)
                {
                    foreach (BinTreeProperty property in obj.Properties.Values)
                    foreach (BinTreeProperty nested in EnumerateProperties(property))
                    {
                        if (TryGetStructuralTarget(nested, targetUnknowns, out ulong targetHash, out string referenceKind))
                        {
                            evidence.Add(new StructuralEvidence(
                                targetHash,
                                sourceChunkHash,
                                sourcePath,
                                sourceWadPath,
                                obj.PathHash,
                                obj.ClassHash,
                                nested.NameHash,
                                binNames.TryGetValue(nested.NameHash, out string propertyName) ? propertyName : nested.NameHash.ToString("x8"),
                                referenceKind));
                        }
                    }
                }

                foreach (var dataOverride in tree.DataOverrides)
                foreach (BinTreeProperty nested in EnumerateProperties(dataOverride.Property))
                {
                    if (TryGetStructuralTarget(nested, targetUnknowns, out ulong targetHash, out string referenceKind))
                    {
                        evidence.Add(new StructuralEvidence(
                            targetHash,
                            sourceChunkHash,
                            sourcePath,
                            sourceWadPath,
                            0,
                            0,
                            nested.NameHash,
                            binNames.TryGetValue(nested.NameHash, out string propertyName) ? propertyName : nested.NameHash.ToString("x8"),
                            referenceKind));
                    }
                }
                return 0;
            }
            catch (Exception exception)
            {
                errorMessage = exception.Message;
                return 1;
            }
        }

        private static bool TryGetStructuralTarget(
            BinTreeProperty property,
            IReadOnlySet<ulong> targetUnknowns,
            out ulong targetHash,
            out string referenceKind)
        {
            (targetHash, referenceKind) = property switch
            {
                BinTreeWadChunkLink link => (link.Value, nameof(BinTreeWadChunkLink)),
                BinTreeU64 unsigned => (unsigned.Value, nameof(BinTreeU64)),
                BinTreeI64 signed => (unchecked((ulong)signed.Value), nameof(BinTreeI64)),
                _ => (0, null)
            };
            return referenceKind is not null && targetUnknowns.Contains(targetHash);
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

        private static bool IsSkippedExtension(string extension) => extension is
            "dds" or "jpg" or "png" or "tga" or "ttf" or "otf" or "ogg" or "webm" or
            "anm" or "skl" or "skn" or "scb" or "sco" or "troybin" or "bnk" or "wpk" or "tex";

        private static bool IsBinExtension(string extension) => extension is "bin" or "inibin" or "preload";

        private static void PrintPassResult(PassResult result)
        {
            Console.WriteLine($"  elapsed:              {result.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine($"  resolved:             {result.Matches.Count:N0}/{result.StartUnknowns:N0}");
            Console.WriteLine($"  remaining:            {result.RemainingUnknowns.Count:N0}");
            Console.WriteLine($"  engine candidates:    {result.Candidates:N0}");
            Console.WriteLine($"  discarded/repeated:   {result.DiscardedCandidates:N0}");
            Console.WriteLine($"  processed WADs/chunks: {result.ProcessedWads:N0}/{result.ProcessedChunks:N0}");
            Console.WriteLine($"  payloads sent to grep: {result.GrepChunks:N0}");
            Console.WriteLine($"  skipped payloads:      {result.SkippedChunks:N0}");
            Console.WriteLine($"  unreadable chunks:     {result.UnreadableChunks:N0}");
            Console.WriteLine($"  evidence parse errors: {result.StructuralParseErrors:N0}");
            Console.WriteLine($"  inferred extension cache hits: {result.ExtensionCacheHits:N0}");
            Console.WriteLine($"  retained managed delta: {FormatBytes(result.ManagedMemoryDelta)} (after forced GC)");
            Console.WriteLine($"  allocated managed bytes: {FormatBytes(result.AllocatedBytes)}");
            Console.WriteLine($"  working-set delta:     {FormatBytes(result.WorkingSetDelta)}");
            Console.WriteLine($"  lifetime peak WS increase: {FormatBytes(result.PeakWorkingSetDelta)}");

            foreach (var group in result.Matches
                .GroupBy(match => match.Strategy)
                .OrderByDescending(group => group.Count()))
            {
                Console.WriteLine($"    match strategy {group.Key,-22} x{group.Count():N0}");
            }
        }

        private static void PrintStructuralEvidence(
            IReadOnlyCollection<StructuralEvidence> evidence,
            IReadOnlySet<ulong> grepRemaining,
            string gameDirectory)
        {
            Console.WriteLine();
            Console.WriteLine("[5] Unresolved structural evidence from typed BIN 64-bit values");
            Console.WriteLine($"  direct unresolved typed values observed: {evidence.Count:N0}");
            Console.WriteLine($"  unique target hashes:                 {evidence.Select(value => value.TargetHash).Distinct().Count():N0}");
            Console.WriteLine($"  target hashes still unresolved after GrepWad: " +
                              $"{evidence.Select(value => value.TargetHash).Where(grepRemaining.Contains).Distinct().Count():N0}");
            foreach (var group in evidence
                .GroupBy(value => value.ReferenceKind, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {group.Key,-22} events={group.Count(),6:N0} " +
                                  $"targets={group.Select(value => value.TargetHash).Distinct().Count(),6:N0}");
            }

            foreach (var group in evidence
                .GroupBy(value => new { value.ReferenceKind, value.PropertyName })
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.ReferenceKind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Key.PropertyName, StringComparer.OrdinalIgnoreCase)
                .Take(20))
            {
                Console.WriteLine($"  {group.Key.ReferenceKind,-22} {group.Key.PropertyName,-28} x{group.Count():N0} " +
                                  $"({group.Select(value => value.TargetHash).Distinct().Count():N0} targets)");
            }

            var unresolved = evidence
                .Where(value => grepRemaining.Contains(value.TargetHash))
                .GroupBy(value => value.TargetHash)
                .OrderByDescending(group => group.Count())
                .Take(20)
                .ToList();
            if (unresolved.Count == 0)
            {
                Console.WriteLine("  no unresolved direct typed 64-bit targets remained after GrepWad.");
                return;
            }

            Console.WriteLine("  top unresolved targets and their structural context:");
            foreach (IGrouping<ulong, StructuralEvidence> group in unresolved)
            {
                StructuralEvidence sample = group.First();
                string relativeWad = Path.GetRelativePath(gameDirectory, sample.SourceWadPath).Replace('\\', '/');
                Console.WriteLine($"    {group.Key:x16}  refs={group.Count():N0}  kind={sample.ReferenceKind}  field={sample.PropertyName}  " +
                                  $"source={relativeWad}::{sample.SourcePath}");
            }
        }

        private static BasicPassResult RunCharacterPass(
            string name,
            IReadOnlySet<ulong> targetUnknowns,
            HashFile gameHashFile,
            IReadOnlyDictionary<uint, string> binNames,
            IEnumerable<string> characters,
            int budget)
        {
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(targetUnknowns));
            var guesser = CreateGameGuesser(gameHashFile, binNames);
            ForceCollection();
            long managedBefore = GC.GetTotalMemory(true);
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var stopwatch = Stopwatch.StartNew();
            guesser.GuessCharactersFiles(engine, CancellationToken.None, characters, budget);
            stopwatch.Stop();
            long allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
            long managedAfter = GC.GetTotalMemory(true);
            return new BasicPassResult(
                name,
                targetUnknowns.Count,
                engine.Matches.Values.ToList(),
                new HashSet<ulong>(engine.UnknownHashes),
                engine.CheckedCandidates,
                stopwatch.Elapsed,
                managedAfter - managedBefore,
                allocatedAfter - allocatedBefore,
                Array.Empty<BasicStageResult>());
        }

        private static BasicPassResult RunBoundedBasicPass(
            IReadOnlySet<ulong> targetUnknowns,
            string pbeRoot,
            HashFile gameHashFile,
            HashFile lcuHashFile,
            IReadOnlyDictionary<uint, string> binNames,
            IReadOnlyList<string> contextCharacters,
            int budget)
        {
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(targetUnknowns));
            var gameGuesser = CreateGameGuesser(gameHashFile, binNames);
            var lcuGuesser = new LcuHashGuesser(lcuHashFile, logService: null);
            var stages = new List<BasicStageResult>();
            ForceCollection();
            long managedBefore = GC.GetTotalMemory(true);
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var stopwatch = Stopwatch.StartNew();

            RunStage(stages, engine, "cross-domain", () => gameGuesser.GuessFromLcuHashes(
                engine,
                lcuGuesser,
                CancellationToken.None,
                candidateBudget: budget));
            RunStage(stages, engine, "characters / WAD context", () => gameGuesser.GuessCharactersFiles(
                engine,
                CancellationToken.None,
                contextCharacters,
                candidateBudget: budget));
            RunStage(stages, engine, "regalia and loadout assets", () => gameGuesser.GuessRegaliaAssets(
                engine,
                CancellationToken.None));
            RunStage(stages, engine, "shader variants / capped", () => gameGuesser.GuessShaderVariants(
                engine,
                CancellationToken.None,
                candidateBudget: budget,
                rootDirectory: pbeRoot));
            RunStage(stages, engine, "locale variants", () => gameGuesser.SubstituteLang(
                engine,
                CancellationToken.None));
            RunStage(stages, engine, "extension substitution / capped", () => gameGuesser.SubstituteExtensions(
                engine,
                CancellationToken.None,
                candidateBudget: budget,
                source: "GAME diagnostic extension substitution"));
            RunStage(stages, engine, "basename prefixes / capped", () => gameGuesser.CheckBasenamePrefixes(
                engine,
                CancellationToken.None,
                candidateBudget: budget));
            RunStage(stages, engine, "numeric variants / capped", () => gameGuesser.CheckIter(
                engine,
                gameGuesser.GenerateNumberCandidates(200, budget, digits: null, inferDigits: false, includeCommonPadding: false),
                "GAME diagnostic numeric variants",
                CancellationToken.None));
            RunStage(stages, engine, "padded numeric variants / capped", () => gameGuesser.CheckIter(
                engine,
                gameGuesser.GenerateNumberCandidates(200, budget, digits: 2, inferDigits: false, includeCommonPadding: false),
                "GAME diagnostic padded numeric variants",
                CancellationToken.None));

            stopwatch.Stop();
            long allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
            long managedAfter = GC.GetTotalMemory(true);
            return new BasicPassResult(
                "GAME Basic / bounded context pipeline",
                targetUnknowns.Count,
                engine.Matches.Values.ToList(),
                new HashSet<ulong>(engine.UnknownHashes),
                engine.CheckedCandidates,
                stopwatch.Elapsed,
                managedAfter - managedBefore,
                allocatedAfter - allocatedBefore,
                stages);
        }

        private static void RunStage(
            ICollection<BasicStageResult> stages,
            HashGuessEngine engine,
            string name,
            Action action)
        {
            long candidatesBefore = engine.CheckedCandidates;
            int matchesBefore = engine.Matches.Count;
            var stopwatch = Stopwatch.StartNew();
            string error = null;
            try
            {
                if (engine.RemainingUnknownCount > 0) action();
            }
            catch (Exception exception)
            {
                error = exception.Message;
                Console.WriteLine($"  [warn] Basic stage '{name}' failed: {exception.Message}");
            }
            stopwatch.Stop();
            stages.Add(new BasicStageResult(
                name,
                engine.CheckedCandidates - candidatesBefore,
                engine.Matches.Count - matchesBefore,
                stopwatch.Elapsed,
                engine.RemainingUnknownCount,
                error is null,
                error ?? string.Empty));
        }

        private static void PrintCharacterComparison(BasicPassResult corpus, BasicPassResult context)
        {
            Console.WriteLine();
            Console.WriteLine("Character candidate comparison:");
            PrintBasicPassLine(corpus);
            PrintBasicPassLine(context);
            HashSet<ulong> contextOnly = context.Matches.Select(value => value.Hash)
                .Except(corpus.Matches.Select(value => value.Hash))
                .ToHashSet();
            HashSet<ulong> corpusOnly = corpus.Matches.Select(value => value.Hash)
                .Except(context.Matches.Select(value => value.Hash))
                .ToHashSet();
            Console.WriteLine($"  context-only resolutions: {contextOnly.Count:N0}");
            Console.WriteLine($"  corpus-only resolutions:  {corpusOnly.Count:N0}");
        }

        private static void PrintBasicResult(BasicPassResult result)
        {
            Console.WriteLine();
            Console.WriteLine("Bounded Basic result:");
            PrintBasicPassLine(result);
            Console.WriteLine($"  retained managed delta: {FormatBytes(result.ManagedMemoryDelta)} (after forced GC)");
            Console.WriteLine($"  allocated managed bytes: {FormatBytes(result.AllocatedBytes)}");
            Console.WriteLine("  stage budget: capped stages use the configured budget; regalia and locale APIs are measured uncapped.");
            Console.WriteLine("  stage metrics:");
            Console.WriteLine($"    {"Stage",-34} {"Candidates",12} {"Resolved",10} {"Elapsed",10} {"Remaining",10} {"Status",10}");
            foreach (BasicStageResult stage in result.Stages)
            {
                Console.WriteLine($"    {stage.Name,-34} {stage.Candidates,12:N0} {stage.Resolved,10:N0} " +
                                  $"{stage.Elapsed.ToString("hh\\:mm\\:ss"),10} {stage.Remaining,10:N0} " +
                                  $"{(stage.Succeeded ? "OK" : "FAILED"),10}");
            }

            if (result.Matches.Count > 0)
            {
                Console.WriteLine("  resolved paths:");
                foreach (HashGuessMatch match in result.Matches.OrderBy(value => value.Hash))
                    Console.WriteLine($"    {match.Hash:x16} => {match.Path} [{match.Strategy}]");
            }
        }

        private static void PrintBasicPassLine(BasicPassResult result)
        {
            Console.WriteLine($"  {result.Name,-44} resolved={result.Matches.Count,4:N0}  " +
                              $"candidates={result.Candidates,12:N0}  elapsed={result.Elapsed:hh\\:mm\\:ss}");
        }

        private static void RunRunScopedCacheProbe(
            IEnumerable<UnknownLocation> locations,
            HashFile gameHashFile,
            IReadOnlyDictionary<uint, string> binNames,
            IReadOnlyDictionary<ulong, string> resolvedPaths)
        {
            UnknownLocation probe = locations
                .Where(value => value.RelativeWad.Contains("/champions/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.RelativeWad, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            if (probe is null)
            {
                Console.WriteLine();
                Console.WriteLine("Cache probe: no champion WAD target was available.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("    RUN-SCOPED GAME GUESSER CACHE PROBE");
            Console.WriteLine("==================================================");
            Console.WriteLine($"Probe WAD: {probe.RelativeWad}");
            string champion = ExtractWadCharacter(probe.WadPath);
            string syntheticCandidate = $"gameplay.{champion}skin350viewcontroller.bin";
            ulong syntheticTarget = XxHash64Ext.Hash(syntheticCandidate);
            Console.WriteLine($"Synthetic candidate: {syntheticCandidate}");
            Console.WriteLine($"Synthetic hash:      {syntheticTarget:x16}");

            var warmupGuesser = CreateGameGuesser(gameHashFile, binNames);
            ulong sentinel = ulong.MaxValue;
            while (sentinel == syntheticTarget) sentinel--;
            var warmup = RunGrepPass(
                "cache warm-up / synthetic unresolved target",
                new[] { probe.WadPath },
                new HashSet<ulong> { sentinel },
                resolvedPaths,
                binNames,
                gameHashFile,
                maxWads: 1,
                collectEvidence: false,
                evidence: new List<StructuralEvidence>(),
                existingGuesser: warmupGuesser);
            var reused = RunGrepPass(
                "cache reused / same guesser instance",
                new[] { probe.WadPath },
                new HashSet<ulong> { syntheticTarget },
                resolvedPaths,
                binNames,
                gameHashFile,
                maxWads: 1,
                collectEvidence: false,
                evidence: new List<StructuralEvidence>(),
                existingGuesser: warmupGuesser);
            var fresh = RunGrepPass(
                "cache fresh / new guesser instance",
                new[] { probe.WadPath },
                new HashSet<ulong> { syntheticTarget },
                resolvedPaths,
                binNames,
                gameHashFile,
                maxWads: 1,
                collectEvidence: false,
                evidence: new List<StructuralEvidence>());

            HashSet<ulong> reusedHashes = reused.Matches.Select(value => value.Hash).ToHashSet();
            HashSet<ulong> freshOnly = fresh.Matches.Select(value => value.Hash).Except(reusedHashes).ToHashSet();
            bool reusedExact = reused.Matches.Any(value =>
                value.Hash == syntheticTarget &&
                string.Equals(value.Path, syntheticCandidate, StringComparison.OrdinalIgnoreCase));
            bool freshExact = fresh.Matches.Any(value =>
                value.Hash == syntheticTarget &&
                string.Equals(value.Path, syntheticCandidate, StringComparison.OrdinalIgnoreCase));
            HashGuessMatch freshMatch = fresh.Matches.FirstOrDefault(value =>
                value.Hash == syntheticTarget &&
                string.Equals(value.Path, syntheticCandidate, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  warm-up matches:       {warmup.Matches.Count:N0}");
            Console.WriteLine($"  reused-instance exact: {reusedExact}");
            Console.WriteLine($"  fresh-instance exact:  {freshExact}");
            Console.WriteLine($"  fresh strategy:        {freshMatch?.Strategy.ToString() ?? "(none)"}");
            Console.WriteLine($"  fresh-only hashes:     {freshOnly.Count:N0}");
            if (freshExact && !reusedExact)
                Console.WriteLine("  finding: the run-scoped cache hides a candidate when the target set changes.");
            else if (!freshExact)
                Console.WriteLine("  finding: the synthetic candidate was not reached; inspect generator/WAD assumptions.");
            else
                Console.WriteLine("  finding: the candidate was reachable despite cache reuse; no suppression observed.");
        }

        private static string ExtractWadCharacter(string wadPath)
        {
            string name = Path.GetFileName(wadPath);
            name = Path.GetFileNameWithoutExtension(name);
            name = Path.GetFileNameWithoutExtension(name);
            return name.ToLowerInvariant();
        }

        private static GameHashGuesser CreateGameGuesser(HashFile gameHashFile, IReadOnlyDictionary<uint, string> binNames) =>
            new(
                gameHashFile,
                resolveBinHash: hash => binNames.TryGetValue(hash, out string value) ? value : hash.ToString("x8"));

        private static void ForceCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static string FormatBytes(long bytes)
        {
            string sign = bytes < 0 ? "-" : "+";
            double value = Math.Abs(bytes);
            string[] units = { "B", "KB", "MB", "GB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return $"{sign}{value:F1} {units[unit]}";
        }

        private sealed record LabOptions(
            string PbeRoot,
            int MaxWads,
            int BasicBudget,
            bool IncludeObserved,
            bool SkipBasic,
            bool SkipCacheProbe,
            bool SkipGrep,
            string HashesDirectory,
            string UnknownsPath,
            string WadContains);

        private sealed record UnknownLocation(
            ulong Hash,
            string WadPath,
            string RelativeWad,
            string SourcePath,
            string Extension,
            int Size,
            int Occurrences);

        private sealed record StructuralEvidence(
            ulong TargetHash,
            ulong SourceChunkHash,
            string SourcePath,
            string SourceWadPath,
            uint ObjectPathHash,
            uint ObjectClassHash,
            uint PropertyHash,
            string PropertyName,
            string ReferenceKind);

        private sealed record PassResult(
            string Name,
            int StartUnknowns,
            IReadOnlyList<HashGuessMatch> Matches,
            IReadOnlySet<ulong> RemainingUnknowns,
            long Candidates,
            long DiscardedCandidates,
            TimeSpan Elapsed,
            long ManagedMemoryDelta,
            long AllocatedBytes,
            long WorkingSetDelta,
            long PeakWorkingSetDelta,
            int ProcessedWads,
            int ProcessedChunks,
            int GrepChunks,
            int SkippedChunks,
            int UnreadableChunks,
            int StructuralParseErrors,
            int ExtensionCacheHits);

        private sealed record BasicStageResult(
            string Name,
            long Candidates,
            int Resolved,
            TimeSpan Elapsed,
            int Remaining,
            bool Succeeded,
            string Error);

        private sealed record BasicPassResult(
            string Name,
            int StartUnknowns,
            IReadOnlyList<HashGuessMatch> Matches,
            IReadOnlySet<ulong> RemainingUnknowns,
            long Candidates,
            TimeSpan Elapsed,
            long ManagedMemoryDelta,
            long AllocatedBytes,
            IReadOnlyList<BasicStageResult> Stages);
    }
}
