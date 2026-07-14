using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes
{
    public sealed class BinRstHashGuessingService
    {
        private const int MaximumTextChunkSize = 16 * 1024 * 1024;
        private const int NumericBudget = 5_000_000;
        private static readonly Regex NumberRegex = new(@"[0-9]+", RegexOptions.Compiled);
        private readonly BinRstHashGuessingStore _store;
        private readonly HashGuessPersistenceService _persistence;
        private readonly HashResolverService _resolver;
        private readonly DirectoriesCreator _directories;
        private readonly LogService _log;

        public BinRstHashGuessingService(
            BinRstHashGuessingStore store,
            HashGuessPersistenceService persistence,
            HashResolverService resolver,
            DirectoriesCreator directories,
            LogService log)
        {
            _store = store;
            _persistence = persistence;
            _resolver = resolver;
            _directories = directories;
            _log = log;
        }

        public Task<InternalHashSummary> GetSummaryAsync(CancellationToken cancellationToken) => _store.LoadSummaryAsync(cancellationToken);

        public async Task<InternalHashInventory> BuildInventoryAsync(
            string rootDirectory,
            bool includeBin,
            bool includeRst,
            IProgress<InternalHashProgress> progress,
            CancellationToken cancellationToken)
        {
            ValidateRoot(rootDirectory);
            if (!includeBin && !includeRst) throw new ArgumentException("At least one internal hash domain must be selected.");
            await _resolver.LoadAllHashesAsync();
            string[] wads = EnumerateWadContainers(rootDirectory);
            var gamePaths = await LoadGamePathsAsync(cancellationToken);
            var observed = CreateObservedSets();
            int scannedBins = 0, scannedRst = 0;

            await Task.Run(() =>
            {
                for (int index = 0; index < wads.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string wadPath = wads[index];
                    try
                    {
                        using var wad = new WadFile(wadPath);
                        foreach (var pair in wad.Chunks)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string path = null;
                            bool isBin = false;
                            bool isRst = false;
                            if (gamePaths.TryGetValue(pair.Key, out path))
                            {
                                isBin = includeBin && path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                                isRst = includeRst && path.EndsWith(".stringtable", StringComparison.OrdinalIgnoreCase);
                                if (includeBin && !isBin && !isRst)
                                {
                                    string sig = GetChunkSignature(wad, pair.Value);
                                    if (sig == "PROP" || sig == "PTCH")
                                    {
                                        isBin = true;
                                    }
                                }
                            }
                            else
                            {
                                if (includeBin || includeRst)
                                {
                                    string sig = GetChunkSignature(wad, pair.Value);
                                    if (includeBin && (sig == "PROP" || sig == "PTCH"))
                                    {
                                        isBin = true;
                                        path = $"[unknown_bin_{pair.Key:x16}]";
                                    }
                                    else if (includeRst && sig.StartsWith("RST"))
                                    {
                                        isRst = true;
                                        path = $"[unknown_rst_{pair.Key:x16}]";
                                    }
                                }
                            }
                            if (!isBin && !isRst) continue;
                            try
                            {
                                using var data = wad.LoadChunkDecompressed(pair.Value);
                                using var stream = new MemoryStream(data.Memory.ToArray(), false);
                                if (isBin)
                                {
                                    ReadBinInventory(stream, observed);
                                    scannedBins++;
                                }
                                else
                                {
                                    ReadRstInventory(stream, observed[InternalHashKind.RstXxh3]);
                                    scannedRst++;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _log.LogDebug($"Internal Hash Lab skipped '{path}' in {Path.GetFileName(wadPath)}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, $"Internal Hash Lab could not read WAD '{wadPath}'.");
                    }
                    progress?.Report(new InternalHashProgress
                    {
                        ProcessedWads = index + 1, TotalWads = wads.Length,
                        ProcessedFiles = scannedBins + scannedRst,
                        CurrentStage = includeBin ? "Building BIN inventory" : "Building RST inventory"
                    });
                }
            }, cancellationToken);

            string fingerprint = BuildFingerprint(wads);
            var selectedObserved = observed.Where(pair =>
                (includeBin && pair.Key is not (InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64)) ||
                (includeRst && pair.Key is InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            await HashResolverService._hashFileAccessLock.WaitAsync(CancellationToken.None);
            try
            {
                await _persistence.CommitInternalInventoryAsync(selectedObserved, fingerprint, includeBin ? "bin" : "rst", CancellationToken.None);
            }
            finally
            {
                HashResolverService._hashFileAccessLock.Release();
            }
            var unknowns = new Dictionary<InternalHashKind, HashSet<ulong>>();
            foreach (InternalHashKind kind in Enum.GetValues<InternalHashKind>())
                unknowns[kind] = await _store.LoadUnknownAsync(kind, cancellationToken);
            _log.LogSuccess($"Internal Hash Lab inventory completed: {scannedBins} BIN and {scannedRst} RST files parsed.");
            return new InternalHashInventory
            {
                Unknowns = unknowns, PatchFingerprint = fingerprint,
                ScannedBins = scannedBins, ScannedStringTables = scannedRst
            };
        }

        public async Task<InternalHashRunResult> RunContentGuessingAsync(
            string rootDirectory,
            bool includeBin,
            bool includeRst,
            IProgress<InternalHashProgress> progress,
            CancellationToken cancellationToken)
        {
            ValidateRoot(rootDirectory);
            await EnsureInventoryAsync(rootDirectory, includeBin, includeRst, progress, cancellationToken);
            var matcher = await CreateMatcherAsync(includeBin, includeRst, cancellationToken);
            int initial = matcher.Remaining;
            string[] wads = EnumerateWadContainers(rootDirectory);
            var gamePaths = await LoadGamePathsAsync(cancellationToken);
            int scanned = 0;

            // Build a unified casing map and resolver for cross-dictionary bin hash resolution.
            // This allows us to reconstruct the proper uppercase/lowercase path names of files
            // and combine them with resolved fields/types to recreate the original string representations.
            var casingMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var binResolver = new Dictionary<uint, string>();
            if (includeBin)
            {
                foreach (string gp in gamePaths.Values)
                {
                    foreach (string segment in gp.Split('/'))
                    {
                        if (segment.Length > 0)
                        {
                            casingMap.TryAdd(segment, segment);
                        }
                    }
                }

                foreach (InternalHashKind kind in new[] { InternalHashKind.BinEntries, InternalHashKind.BinFields, InternalHashKind.BinTypes, InternalHashKind.BinHashes })
                {
                    foreach (var pair in await _store.LoadKnownAsync(kind, cancellationToken))
                    {
                        uint key = unchecked((uint)pair.Key);
                        binResolver.TryAdd(key, pair.Value);
                    }
                }
            }

            await Task.Run(() =>
            {
                for (int index = 0; index < wads.Length && matcher.Remaining > 0; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string wadPath = wads[index];
                    try
                    {
                        using var wad = new WadFile(wadPath);
                        foreach (var pair in wad.Chunks)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string path = null;
                            bool isBin = false;
                            bool isText = false;
                            if (gamePaths.TryGetValue(pair.Key, out path))
                            {
                                isBin = path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                                isText = IsTextCandidatePath(path) && pair.Value.UncompressedSize <= MaximumTextChunkSize;
                                if (!isBin && !isText)
                                {
                                    string sig = GetChunkSignature(wad, pair.Value);
                                    if (sig == "PROP" || sig == "PTCH")
                                    {
                                        isBin = true;
                                    }
                                }
                            }
                            else
                            {
                                if (includeBin)
                                {
                                    string sig = GetChunkSignature(wad, pair.Value);
                                    if (sig == "PROP" || sig == "PTCH")
                                    {
                                        isBin = true;
                                        path = $"[unknown_bin_{pair.Key:x16}]";
                                    }
                                }
                            }
                            if (!isBin && !isText) continue;
                            try
                            {
                                using var data = wad.LoadChunkDecompressed(pair.Value);
                                if (isBin)
                                {
                                    using var stream = new MemoryStream(data.Memory.ToArray(), false);
                                    var tree = new BinTree(stream);
                                    
                                    // 1. Visit raw string values inside the bin
                                    VisitBinStrings(tree, value => matcher.Check(value, InternalHashGuessStrategy.BinContent, path, wadPath, path));
                                    
                                    // 2. Perform "double work": reconstruct the cased path of the bin, resolve known hashes,
                                    // and generate combinations (simulating scanning the resolved .bin.json)
                                    if (binResolver.Count > 0)
                                    {
                                        string casedBasePath = ReconstructCasedPath(path, casingMap);
                                        VisitBinResolvedNamesAndPaths(tree, casedBasePath, binResolver, value => matcher.Check(value, InternalHashGuessStrategy.BinContent, path, wadPath, path));
                                    }
                                }
                                else
                                {
                                    CheckTextCandidates(data.Memory.Span, value => matcher.Check(value, InternalHashGuessStrategy.TextContent, path, wadPath, path));
                                }
                                scanned++;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _log.LogDebug($"Internal Hash Lab content scan skipped '{path}': {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, $"Internal Hash Lab could not scan WAD '{wadPath}'.");
                    }
                    progress?.Report(new InternalHashProgress
                    {
                        ProcessedWads = index + 1, TotalWads = wads.Length, ProcessedFiles = scanned,
                        FoundMatches = matcher.Matches.Count, CurrentStage = "Scanning BIN and text content"
                    });
                }
            }, cancellationToken);

            if (matcher.Remaining > 0)
            {
                var filesToScan = Directory.EnumerateFiles(rootDirectory, "*.*", SearchOption.AllDirectories)
                    .Where(file =>
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        return ext is ".exe" or ".dll" or ".json" or ".yaml" or ".yml" or ".xml" or ".cfg" or ".ini" or ".txt" or ".csv" or ".stringtable" or ".material" or ".troybin" or ".preload" or ".luabin64" or ".luabin" or ".css" or ".js" or ".html" or ".log" or ".info";
                    })
                    .ToList();

                foreach (string file in filesToScan)
                {
                    if (matcher.Remaining == 0) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await ScanTextFileAsync(file, value => matcher.Check(value, InternalHashGuessStrategy.TextContent, file, null, file), cancellationToken);
                        scanned++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogDebug($"Internal Hash Lab content scan skipped local file '{file}': {ex.Message}");
                    }
                }
            }
            return await CompleteRunAsync(matcher, initial, scanned, cancellationToken);
        }

        public async Task<InternalHashRunResult> RunStructuralGuessingAsync(
            string rootDirectory,
            bool includeBin,
            bool includeRst,
            IProgress<InternalHashProgress> progress,
            CancellationToken cancellationToken)
        {
            ValidateRoot(rootDirectory);
            await EnsureInventoryAsync(rootDirectory, includeBin, includeRst, progress, cancellationToken);
            var matcher = await CreateMatcherAsync(includeBin, includeRst, cancellationToken);
            int initial = matcher.Remaining;
            var binKnown = new List<string>();
            foreach (InternalHashKind kind in new[] { InternalHashKind.BinEntries, InternalHashKind.BinFields, InternalHashKind.BinTypes, InternalHashKind.BinHashes })
                binKnown.AddRange((await _store.LoadKnownAsync(kind, cancellationToken)).Values);
            var rst3 = (await _store.LoadKnownAsync(InternalHashKind.RstXxh3, cancellationToken)).Values.ToList();
            var rst64 = (await _store.LoadKnownAsync(InternalHashKind.RstXxh64, cancellationToken)).Values.ToList();
            var gamePaths = (await LoadGamePathsAsync(cancellationToken)).Values;
            long checkedCandidates = 0;

            await Task.Run(() =>
            {
                if (includeBin)
                {
                    CheckCandidates(binKnown, InternalHashGuessStrategy.CrossDictionary, "BIN dictionaries");
                    CheckCandidates(gamePaths, InternalHashGuessStrategy.GamePath, "GAME paths");
                }
                if (includeRst)
                {
                    CheckCandidates(binKnown, InternalHashGuessStrategy.CrossDictionary, "BIN dictionary keys");
                    CheckCandidates(rst3, InternalHashGuessStrategy.CrossVersion, "RST XXH3 keys");
                    CheckCandidates(rst64, InternalHashGuessStrategy.CrossVersion, "RST XXH64 keys");
                }

                // Run advanced structural candidate generation
                if (matcher.Remaining > 0)
                {
                    var wordlist = new TokenWordlist();
                    foreach (string val in binKnown) wordlist.AddName(val);
                    foreach (string val in rst3) wordlist.AddName(val);
                    foreach (string val in rst64) wordlist.AddName(val);
                    foreach (string val in gamePaths) wordlist.AddName(val);
                    wordlist.FinalizeList();

                    CheckCandidates(GenerateStructuralCandidates(wordlist, NumericBudget, cancellationToken), InternalHashGuessStrategy.NumericVariant, "Advanced Structural Generation");
                }

                void CheckCandidates(IEnumerable<string> candidates, InternalHashGuessStrategy strategy, string source)
                {
                    foreach (string candidate in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        matcher.Check(candidate, strategy, source);
                        checkedCandidates++;
                        if ((checkedCandidates & 0x3ffff) == 0)
                            progress?.Report(new InternalHashProgress
                            {
                                ProcessedFiles = checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates,
                                FoundMatches = matcher.Matches.Count, CurrentStage = source
                            });
                        if (matcher.Remaining == 0) break;
                    }
                }
            }, cancellationToken);

            return await CompleteRunAsync(matcher, initial, checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates, cancellationToken);
        }

        private async Task<InternalHashRunResult> CompleteRunAsync(CandidateMatcher matcher, int initial, int scanned, CancellationToken cancellationToken)
        {
            var matches = matcher.Matches.OrderBy(match => match.Kind).ThenBy(match => match.Value, StringComparer.Ordinal).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            await HashResolverService._hashFileAccessLock.WaitAsync(CancellationToken.None);
            try
            {
                await _persistence.CommitInternalMatchesAsync(matches, CancellationToken.None);
            }
            finally
            {
                HashResolverService._hashFileAccessLock.Release();
            }
            if (matches.Count > 0) await _resolver.ForceReloadHashesAsync();
            _log.LogSuccess($"Internal Hash Lab completed: {matches.Count} values resolved from {initial} unknown hashes.");
            return new InternalHashRunResult { UnknownHashesAtStart = initial, ScannedFiles = scanned, Matches = matches };
        }

        private async Task EnsureInventoryAsync(string rootDirectory, bool includeBin, bool includeRst, IProgress<InternalHashProgress> progress, CancellationToken cancellationToken)
        {
            string[] wads = EnumerateWadContainers(rootDirectory);
            string fingerprint = BuildFingerprint(wads);
            foreach (string domain in GetSelectedDomains(includeBin, includeRst))
            {
                string marker = Path.Combine(_directories.HashLabPath, $"internal.{domain}.patch.txt");
                string stored = File.Exists(marker) ? (await File.ReadAllTextAsync(marker, cancellationToken)).Trim() : string.Empty;
                if (!string.Equals(stored, fingerprint, StringComparison.Ordinal))
                    await BuildInventoryAsync(rootDirectory, domain == "bin", domain == "rst", progress, cancellationToken);
            }
        }

        private static IEnumerable<string> GetSelectedDomains(bool includeBin, bool includeRst)
        {
            if (includeBin) yield return "bin";
            if (includeRst) yield return "rst";
        }

        private static string[] EnumerateWadContainers(string rootDirectory) =>
            Directory.EnumerateFiles(rootDirectory, "*.wad*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private async Task<CandidateMatcher> CreateMatcherAsync(bool includeBin, bool includeRst, CancellationToken cancellationToken)
        {
            var targets = new Dictionary<InternalHashKind, HashSet<ulong>>();
            foreach (InternalHashKind kind in Enum.GetValues<InternalHashKind>())
            {
                bool isRst = kind is InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64;
                targets[kind] = (isRst ? includeRst : includeBin)
                    ? await _store.LoadUnknownAsync(kind, cancellationToken)
                    : new HashSet<ulong>();
            }
            return new CandidateMatcher(targets);
        }

        private async Task<Dictionary<ulong, string>> LoadGamePathsAsync(CancellationToken cancellationToken)
        {
            var result = new Dictionary<ulong, string>();
            string path = Path.Combine(_directories.HashesPath, "hashes.game.txt");
            if (!File.Exists(path)) return result;
            using var reader = new StreamReader(path);
            while (await reader.ReadLineAsync(cancellationToken) is string line)
                if (line.Length > 17 && ulong.TryParse(line.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    result[hash] = line[17..];
            return result;
        }

        private static Dictionary<InternalHashKind, HashSet<ulong>> CreateObservedSets() => new()
        {
            [InternalHashKind.BinEntries] = new(), [InternalHashKind.BinFields] = new(),
            [InternalHashKind.BinTypes] = new(), [InternalHashKind.BinHashes] = new(),
            [InternalHashKind.RstXxh3] = new(), [InternalHashKind.RstXxh64] = new()
        };

        private static void ReadBinInventory(Stream stream, Dictionary<InternalHashKind, HashSet<ulong>> observed)
        {
            var tree = new BinTree(stream);
            foreach (var pair in tree.Objects)
            {
                if (pair.Key != 0) observed[InternalHashKind.BinEntries].Add(pair.Key);
                if (pair.Value.ClassHash != 0) observed[InternalHashKind.BinTypes].Add(pair.Value.ClassHash);
                foreach (var property in pair.Value.Properties.Values) VisitBinProperty(property, observed);
            }
            foreach (var item in tree.DataOverrides)
            {
                if (item.ObjectPathHash != 0) observed[InternalHashKind.BinEntries].Add(item.ObjectPathHash);
                VisitBinProperty(item.Property, observed);
            }
        }

        private static void VisitBinProperty(BinTreeProperty property, Dictionary<InternalHashKind, HashSet<ulong>> observed)
        {
            if (property.NameHash != 0) observed[InternalHashKind.BinFields].Add(property.NameHash);
            switch (property)
            {
                case BinTreeHash hash when hash.Value != 0: observed[InternalHashKind.BinHashes].Add(hash.Value); break;
                case BinTreeStruct structure:
                    if (structure.ClassHash != 0) observed[InternalHashKind.BinTypes].Add(structure.ClassHash);
                    foreach (var child in structure.Properties.Values) VisitBinProperty(child, observed);
                    break;
                case BinTreeContainer container:
                    foreach (var child in container.Elements) VisitBinProperty(child, observed);
                    break;
                case BinTreeOptional option when option.Value != null: VisitBinProperty(option.Value, observed); break;
                case BinTreeMap map:
                    foreach (var child in map) { VisitBinProperty(child.Key, observed); VisitBinProperty(child.Value, observed); }
                    break;
            }
        }

        private static void VisitBinStrings(BinTree tree, Action<string> check)
        {
            foreach (string dependency in tree.Dependencies) check(dependency);
            foreach (var item in tree.Objects.Values)
                foreach (var property in item.Properties.Values) Visit(property);
            foreach (var item in tree.DataOverrides)
            {
                check(item.PropertyPath);
                Visit(item.Property);
            }

            void Visit(BinTreeProperty property)
            {
                switch (property)
                {
                    case BinTreeString text: check(text.Value); break;
                    case BinTreeStruct structure:
                        foreach (var child in structure.Properties.Values) Visit(child);
                        break;
                    case BinTreeContainer container:
                        foreach (var child in container.Elements) Visit(child);
                        break;
                    case BinTreeOptional option when option.Value != null: Visit(option.Value); break;
                    case BinTreeMap map:
                        foreach (var child in map) { Visit(child.Key); Visit(child.Value); }
                        break;
                }
            }
        }

        private static string ReconstructCasedPath(string path, Dictionary<string, string> casingMap)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            string clean = path;
            if (clean.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                clean = clean[..^4];
            if (clean.StartsWith("[unknown_bin_", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var segments = clean.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (casingMap.TryGetValue(segments[i], out string cased))
                {
                    segments[i] = cased;
                }
                else if (segments[i].Length > 0)
                {
                    // Fallback capitalization if segment is not found in known game paths
                    segments[i] = char.ToUpper(segments[i][0]) + segments[i][1..];
                }
            }
            return string.Join('/', segments);
        }

        /// <summary>
        /// Collects raw strings and resolved known hashes from the BinTree, and evaluates
        /// combined path candidates (recreating the .bin.json structure).
        /// </summary>
        private static void VisitBinResolvedNamesAndPaths(BinTree tree, string casedBasePath, Dictionary<uint, string> resolver, Action<string> check)
        {
            var plainStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Collect plain strings
            VisitBinStrings(tree, val => { if (!string.IsNullOrWhiteSpace(val)) plainStrings.Add(val.Trim()); });

            // Collect resolved names of objects, classes, fields, and values
            foreach (var pair in tree.Objects)
            {
                if (pair.Key != 0 && resolver.TryGetValue(unchecked((uint)pair.Key), out string entryName))
                    resolvedNames.Add(entryName);
                if (pair.Value.ClassHash != 0 && resolver.TryGetValue(pair.Value.ClassHash, out string className))
                    resolvedNames.Add(className);
                foreach (var property in pair.Value.Properties.Values)
                    CollectResolvedPropertyNames(property, resolver, resolvedNames);
            }
            foreach (var item in tree.DataOverrides)
            {
                if (item.ObjectPathHash != 0 && resolver.TryGetValue(unchecked((uint)item.ObjectPathHash), out string overrideName))
                    resolvedNames.Add(overrideName);
                CollectResolvedPropertyNames(item.Property, resolver, resolvedNames);
            }

            // Reconstruct and check combined candidates
            if (!string.IsNullOrEmpty(casedBasePath))
            {
                // BasePath/PlainString
                foreach (string s in plainStrings)
                {
                    check($"{casedBasePath}/{s}");
                }

                // BasePath/ResolvedName
                foreach (string r in resolvedNames)
                {
                    check($"{casedBasePath}/{r}");
                }

                // BasePath/ResolvedName/PlainString
                foreach (string r in resolvedNames)
                {
                    foreach (string s in plainStrings)
                    {
                        check($"{casedBasePath}/{r}/{s}");
                    }
                }
            }
        }

        private static void CollectResolvedPropertyNames(BinTreeProperty property, Dictionary<uint, string> resolver, HashSet<string> resolvedNames)
        {
            if (property.NameHash != 0 && resolver.TryGetValue(property.NameHash, out string fieldName))
                resolvedNames.Add(fieldName);
            switch (property)
            {
                case BinTreeHash hash when hash.Value != 0:
                    if (resolver.TryGetValue(hash.Value, out string hashName))
                        resolvedNames.Add(hashName);
                    break;
                case BinTreeStruct structure:
                    if (structure.ClassHash != 0 && resolver.TryGetValue(structure.ClassHash, out string structClass))
                        resolvedNames.Add(structClass);
                    foreach (var child in structure.Properties.Values)
                        CollectResolvedPropertyNames(child, resolver, resolvedNames);
                    break;
                case BinTreeContainer container:
                    foreach (var child in container.Elements)
                        CollectResolvedPropertyNames(child, resolver, resolvedNames);
                    break;
                case BinTreeOptional option when option.Value != null:
                    CollectResolvedPropertyNames(option.Value, resolver, resolvedNames);
                    break;
                case BinTreeMap map:
                    foreach (var child in map)
                    {
                        CollectResolvedPropertyNames(child.Key, resolver, resolvedNames);
                        CollectResolvedPropertyNames(child.Value, resolver, resolvedNames);
                    }
                    break;
            }
        }

        private static void ReadRstInventory(Stream stream, HashSet<ulong> observed)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (Encoding.ASCII.GetString(reader.ReadBytes(3)) != "RST") throw new InvalidDataException("Invalid RST signature.");
            int version = reader.ReadByte();
            int bits = 40;
            if (version == 2 && reader.ReadBoolean()) reader.BaseStream.Seek(reader.ReadUInt32(), SeekOrigin.Current);
            else if (version is 4 or 5) bits = 38;
            if (version is < 2 or > 5) throw new InvalidDataException($"Unsupported RST version {version}.");
            ulong mask = (1UL << bits) - 1;
            uint count = reader.ReadUInt32();
            for (int index = 0; index < count; index++) observed.Add(reader.ReadUInt64() & mask);
        }

        private static void CheckTextCandidates(ReadOnlySpan<byte> data, Action<string> check)
        {
            int start = -1;
            for (int index = 0; index <= data.Length; index++)
            {
                bool accepted = index < data.Length && IsCandidateByte(data[index]);
                if (accepted)
                {
                    if (start < 0) start = index;
                    continue;
                }
                if (start < 0) continue;
                int length = index - start;
                if (length is >= 5 and <= 512)
                {
                    CheckCandidateSlice(data, start, length, check);
                    if (start >= 2)
                    {
                        int declared = data[start - 2] | data[start - 1] << 8;
                        if (declared == 0 && start >= 4)
                            declared = data[start - 4] | data[start - 3] << 8 | data[start - 2] << 16 | data[start - 1] << 24;
                        if (declared is >= 5 and < 513 && declared < length) CheckCandidateSlice(data, start, declared, check);
                    }
                }
                start = -1;
            }

            static bool IsCandidateByte(byte value) => value is >= (byte)'0' and <= (byte)'9' or
                >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or
                (byte)'_' or (byte)'.' or (byte)' ' or (byte)'/' or (byte)'-';
        }

        private static void CheckCandidateSlice(ReadOnlySpan<byte> data, int offset, int length, Action<string> check)
        {
            string candidate = Encoding.ASCII.GetString(data.Slice(offset, length)).Trim();
            if (candidate.Length >= 5) check(candidate);
        }

        private static async Task ScanTextFileAsync(string path, Action<string> check, CancellationToken cancellationToken)
        {
            const int blockSize = 4 * 1024 * 1024;
            const int overlap = 512;
            byte[] buffer = new byte[blockSize + overlap];
            int carried = 0;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, blockSize, true);
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(carried, blockSize), cancellationToken);
                if (read == 0) break;
                int length = carried + read;
                CheckTextCandidates(buffer.AsSpan(0, length), check);
                carried = Math.Min(overlap, length);
                buffer.AsSpan(length - carried, carried).CopyTo(buffer);
            }
        }

        private static IEnumerable<string> GenerateNumberCandidates(IEnumerable<string> values, int limit, int budget, CancellationToken cancellationToken)
        {
            var formats = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in values)
                foreach (Match match in NumberRegex.Matches(value ?? string.Empty))
                    formats.Add(value[..match.Index] + "{0}" + value[(match.Index + match.Length)..]);
            int generated = 0;
            foreach (string format in formats.OrderBy(value => value, StringComparer.Ordinal))
                for (int number = 0; number < limit && generated < budget; number++, generated++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return string.Format(CultureInfo.InvariantCulture, format, number);
                }
        }

        private static bool IsTextCandidatePath(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".inibin", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".material", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".troybin", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".preload", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".luabin64", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".luabin", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".info", StringComparison.OrdinalIgnoreCase);

        private static string BuildFingerprint(IEnumerable<string> paths)
        {
            ulong xor = 0, sum = 0;
            long count = 0;
            foreach (string path in paths)
            {
                var info = new FileInfo(path);
                ulong value = unchecked((ulong)info.Length ^ (ulong)info.LastWriteTimeUtc.Ticks);
                xor ^= value; sum = unchecked(sum + value); count++;
            }
            return $"internal:{count}:{xor:x16}:{sum:x16}";
        }

        private static void ValidateRoot(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");
        }

        private static string GetChunkSignature(WadFile wad, WadChunk chunk)
        {
            try
            {
                using Stream stream = wad.OpenChunk(chunk);
                Span<byte> buffer = stackalloc byte[4];
                int read = stream.Read(buffer);
                if (read < 3) return string.Empty;
                if (read == 3) return Encoding.ASCII.GetString(buffer.Slice(0, 3));
                return Encoding.ASCII.GetString(buffer);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static IEnumerable<string> SplitCamelCaseAndSymbols(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) yield break;
            
            var segments = input.Split(new[] { '/', '\\', '.', '_', '-', ' ', ':', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                yield return segment.ToLowerInvariant();
                
                int lastStart = 0;
                for (int i = 1; i < segment.Length; i++)
                {
                    bool isUpper = char.IsUpper(segment[i]);
                    bool isDigit = char.IsDigit(segment[i]);
                    bool prevLower = char.IsLower(segment[i - 1]);
                    
                    if ((isUpper || isDigit) && prevLower)
                    {
                        yield return segment[lastStart..i].ToLowerInvariant();
                        lastStart = i;
                    }
                    else if (char.IsLower(segment[i]) && char.IsDigit(segment[i - 1]))
                    {
                        yield return segment[lastStart..i].ToLowerInvariant();
                        lastStart = i;
                    }
                }
                if (lastStart < segment.Length)
                {
                    yield return segment[lastStart..].ToLowerInvariant();
                }
            }
        }

        private sealed class TokenWordlist
        {
            public List<string> AllTokens { get; } = new();
            public Dictionary<string, int> TokenCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Characters { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> PathTemplates { get; } = new();
            public List<string> FieldTemplates { get; } = new();

            public void AddName(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;

                if (name.Contains('/'))
                {
                    if (name.Contains("characters", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = name.Split('/');
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (string.Equals(parts[i], "characters", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                            {
                                string character = parts[i + 1];
                                Characters.Add(character);
                                string templated = name.Replace(character, "{character}", StringComparison.OrdinalIgnoreCase);
                                PathTemplates.Add(templated);
                            }
                        }
                    }
                }
                else
                {
                    bool hasDigit = false;
                    for (int i = 0; i < name.Length; i++)
                    {
                        if (char.IsDigit(name[i])) { hasDigit = true; break; }
                    }
                    if (hasDigit && NumberRegex.IsMatch(name))
                    {
                        string templated = NumberRegex.Replace(name, "{0}");
                        FieldTemplates.Add(templated);
                    }
                }

                foreach (string token in SplitCamelCaseAndSymbols(name))
                {
                    if (token.Length >= 2)
                    {
                        TokenCounts.TryGetValue(token, out int count);
                        TokenCounts[token] = count + 1;
                    }
                }
            }

            public void FinalizeList()
            {
                AllTokens.Clear();
                AllTokens.AddRange(TokenCounts.OrderByDescending(pair => pair.Value).Select(pair => pair.Key));
                PathTemplates.Clear();
                PathTemplates.AddRange(PathTemplates.Distinct(StringComparer.OrdinalIgnoreCase));
                FieldTemplates.Clear();
                FieldTemplates.AddRange(FieldTemplates.Distinct(StringComparer.OrdinalIgnoreCase));
            }
        }

        private static IEnumerable<string> GenerateStructuralCandidates(
            TokenWordlist wordlist,
            int budget,
            CancellationToken cancellationToken)
        {
            var generatedSet = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;

            bool Emit(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 512) return false;
                string clean = candidate.Trim().ToLowerInvariant();
                if (generatedSet.Add(clean))
                {
                    count++;
                    return true;
                }
                return false;
            }

            // 1. Path template substitution
            foreach (string template in wordlist.PathTemplates)
            {
                foreach (string character in wordlist.Characters)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string candidate = template.Replace("{character}", character, StringComparison.OrdinalIgnoreCase);
                    if (Emit(candidate))
                    {
                        yield return candidate;
                        if (count >= budget) yield break;
                    }
                }
            }

            // 2. Field template numeric substitution
            foreach (string template in wordlist.FieldTemplates)
            {
                for (int num = 0; num <= 200; num++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string candidate = string.Format(CultureInfo.InvariantCulture, template, num);
                    if (Emit(candidate))
                    {
                        yield return candidate;
                        if (count >= budget) yield break;
                    }
                }
            }

            // 3. Plurals and singulars
            var topTokens = wordlist.AllTokens.Take(1000).ToList();
            foreach (string token in topTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string plural = Pluralize(token);
                if (Emit(plural))
                {
                    yield return plural;
                    if (count >= budget) yield break;
                }
                if (token.EndsWith('s') && token.Length > 3)
                {
                    string singular = token[..^1];
                    if (Emit(singular))
                    {
                        yield return singular;
                        if (count >= budget) yield break;
                    }
                }
            }

            // 4. Prefix & Suffix addition
            string[] prefixes = { "m_", "m", "is", "has", "get", "set" };
            string[] suffixes = { "s", "es", "list", "map", "array", "hash", "id", "name", "type", "file", "path", "vector", "color", "override", "data", "config", "event", "trigger" };
            
            foreach (string token in topTokens.Take(500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string pref in prefixes)
                {
                    string candidate = pref + token;
                    if (Emit(candidate))
                    {
                        yield return candidate;
                        if (count >= budget) yield break;
                    }
                }
                foreach (string suff in suffixes)
                {
                    string candidate = token + suff;
                    if (Emit(candidate))
                    {
                        yield return candidate;
                        if (count >= budget) yield break;
                    }
                    string candidateUnderscore = token + "_" + suff;
                    if (Emit(candidateUnderscore))
                    {
                        yield return candidateUnderscore;
                        if (count >= budget) yield break;
                    }
                }
            }

            // 5. Token combinations (2-word combinations)
            var combTokens = topTokens.Take(300).ToList();
            for (int i = 0; i < combTokens.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int j = 0; j < combTokens.Count; j++)
                {
                    if (i == j) continue;
                    
                    string comb1 = combTokens[i] + combTokens[j];
                    if (Emit(comb1))
                    {
                        yield return comb1;
                        if (count >= budget) yield break;
                    }

                    string comb2 = combTokens[i] + "_" + combTokens[j];
                    if (Emit(comb2))
                    {
                        yield return comb2;
                        if (count >= budget) yield break;
                    }
                }
            }
        }

        private static string Pluralize(string word)
        {
            if (word.EndsWith("y", StringComparison.OrdinalIgnoreCase) &&
                !word.EndsWith("ay", StringComparison.OrdinalIgnoreCase) &&
                !word.EndsWith("ey", StringComparison.OrdinalIgnoreCase) &&
                !word.EndsWith("oy", StringComparison.OrdinalIgnoreCase) &&
                !word.EndsWith("uy", StringComparison.OrdinalIgnoreCase))
            {
                return word[..^1] + "ies";
            }
            if (word.EndsWith("s", StringComparison.OrdinalIgnoreCase) ||
                word.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
                word.EndsWith("z", StringComparison.OrdinalIgnoreCase) ||
                word.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
                word.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
            {
                return word + "es";
            }
            return word + "s";
        }

        private sealed class CandidateMatcher
        {
            private const ulong Rst38Mask = (1UL << 38) - 1;
            private readonly Dictionary<InternalHashKind, HashSet<ulong>> _targets;
            private readonly Dictionary<(InternalHashKind Kind, ulong Hash), InternalHashGuessMatch> _matches = new();
            public CandidateMatcher(Dictionary<InternalHashKind, HashSet<ulong>> targets) => _targets = targets;
            public IReadOnlyCollection<InternalHashGuessMatch> Matches => _matches.Values;
            public int Remaining => _targets.Values.Sum(values => values.Count);

            public void Check(string value, InternalHashGuessStrategy strategy, string source, string sourceWad = null, string sourceBin = null)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 512) return;
                string candidate = value.Trim().ToLowerInvariant();
                uint fnv = Fnv1a.HashLower(candidate);
                bool content = strategy is InternalHashGuessStrategy.BinContent or InternalHashGuessStrategy.TextContent;
                bool crossDictionary = strategy == InternalHashGuessStrategy.CrossDictionary;
                bool gamePath = strategy == InternalHashGuessStrategy.GamePath;
                if (content || crossDictionary || gamePath)
                {
                    if (candidate.Contains('/'))
                        Check32(InternalHashKind.BinEntries, fnv, candidate, strategy, source, sourceWad, sourceBin);
                    Check32(InternalHashKind.BinHashes, fnv, candidate, strategy, source, sourceWad, sourceBin);
                }
                if ((content || crossDictionary) && IsIdentifier(candidate))
                {
                    Check32(InternalHashKind.BinFields, fnv, candidate, strategy, source, sourceWad, sourceBin);
                    Check32(InternalHashKind.BinTypes, fnv, candidate, strategy, source, sourceWad, sourceBin);
                }

                if (content || strategy is InternalHashGuessStrategy.CrossDictionary or InternalHashGuessStrategy.CrossVersion or InternalHashGuessStrategy.NumericVariant)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(candidate);
                    ulong xxh3 = XxHash3.HashToUInt64(bytes);
                    CheckRst(InternalHashKind.RstXxh3, xxh3, candidate, strategy, source, new[] { 38 }, sourceWad, sourceBin);
                    ulong xxh64 = XxHash64.HashToUInt64(bytes);
                    CheckRst(InternalHashKind.RstXxh64, xxh64, candidate, strategy, source, new[] { 64, 38, 39, 40 }, sourceWad, sourceBin);
                }
            }

            private static bool IsIdentifier(string value)
            {
                if (value.Length == 0 || value.Length > 128 || !(char.IsLetter(value[0]) || value[0] == '_')) return false;
                for (int index = 1; index < value.Length; index++)
                    if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_')) return false;
                return true;
            }

            private void Check32(InternalHashKind kind, uint hash, string value, InternalHashGuessStrategy strategy, string source, string sourceWad = null, string sourceBin = null)
            {
                if (!_targets[kind].Remove(hash)) return;
                _matches[(kind, hash)] = new InternalHashGuessMatch
                {
                    Hash = hash, LookupHash = hash, HashBits = 32, Value = value,
                    Kind = kind, Strategy = strategy, Source = source,
                    SourceWad = sourceWad, SourceBin = sourceBin
                };
            }

            private void CheckRst(InternalHashKind kind, ulong fullHash, string value, InternalHashGuessStrategy strategy, string source, IEnumerable<int> bitOptions, string sourceWad = null, string sourceBin = null)
            {
                foreach (int bits in bitOptions)
                {
                    ulong lookup = bits == 64 ? fullHash : fullHash & ((1UL << bits) - 1);
                    if (!_targets[kind].Remove(lookup)) continue;
                    _matches[(kind, fullHash)] = new InternalHashGuessMatch
                    {
                        Hash = fullHash, LookupHash = lookup, HashBits = bits, Value = value,
                        Kind = kind, Strategy = strategy, Source = source,
                        SourceWad = sourceWad, SourceBin = sourceBin
                    };
                    break;
                }
            }
        }
    }
}
