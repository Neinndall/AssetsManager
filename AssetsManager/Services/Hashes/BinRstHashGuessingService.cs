using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const string MetaSchemaClassSource = "Meta Schema class names";
        private const string MetaSchemaPropertySource = "Meta Schema property names";

        private static readonly HashSet<uint> NamedEntryTypes = new[]
        {
            "StaticMaterialDef", "UISceneData", "UiElementEffectAmmoData", "UiElementEffectAnimatedRotatingIconData",
            "UiElementEffectAnimationData", "UiElementEffectArcFillData", "UiElementEffectCircleMaskCooldownData",
            "UiElementEffectCircleMaskDesaturateData", "UiElementEffectCooldownData", "UiElementEffectCooldownRadialData",
            "UiElementEffectCustomMaterialData", "UiElementEffectData", "UiElementEffectDesaturateData",
            "UiElementEffectFillPercentageData", "UiElementEffectGlowConstantData", "UiElementEffectGlowData",
            "UiElementEffectGlowingRotatingIconData", "UiElementGroupButtonData", "UiElementGroupData", "UiElementGroupFramedData",
            "UiElementGroupManagedLayoutData", "UiElementGroupMeterData", "UiElementGroupSliderData", "UiElementIconData",
            "UiElementParticleSystemData", "UiElementRegionData", "UiElementScissorRegionData", "UiElementSpineAnimationData",
            "UiElementTextData", "UiSceneViewPaneData"
        }.Select(Fnv1a.HashLower).ToHashSet();

        private static readonly Dictionary<uint, uint> DirectEntryFields = new()
        {
            [Fnv1a.HashLower("ContextualActionData")] = Fnv1a.HashLower("mObjectPath"),
            [Fnv1a.HashLower("CustomShaderDef")] = Fnv1a.HashLower("objectPath"),
            [Fnv1a.HashLower("MapContainer")] = Fnv1a.HashLower("mapPath"),
            [Fnv1a.HashLower("RewardGroup")] = Fnv1a.HashLower("internalName"),
            [Fnv1a.HashLower("VfxSystemDefinitionData")] = Fnv1a.HashLower("particlePath"),
            [Fnv1a.HashLower("Sequence")] = Fnv1a.HashLower("path")
        };

        private static readonly string[] Common3DBones =
        {
            "Root", "Buffbone_C", "Buffbone_Glb_Center_Loc", "Buffbone_Glb_Layout_Loc", "Buffbone_Glb_Overhead_Loc",
            "Buffbone_Glb_Ground_Loc", "L_Hand", "R_Hand", "L_Foot", "R_Foot", "L_Arm", "R_Arm", "Head", "Spine",
            "Spine1", "Spine2", "Chest", "Neck", "Weapon", "L_Weapon", "R_Weapon", "Wing_L", "Wing_R", "Tail", "Pelvis"
        };

        private const int MaximumTextChunkSize = 16 * 1024 * 1024;
        private const int NumericBudget = 5_000_000;
        private static readonly Regex NumberRegex = new(@"[0-9]+", RegexOptions.Compiled);
        private readonly BinRstHashGuessingStore _store;
        private readonly HashGuessPersistenceService _persistence;
        private readonly HashResolverService _resolver;
        private readonly DirectoriesCreator _directories;
        private readonly LogService _log;
        private readonly MetaSchemaHashSource _metaSchema;

        public BinRstHashGuessingService(
            BinRstHashGuessingStore store,
            HashGuessPersistenceService persistence,
            HashResolverService resolver,
            DirectoriesCreator directories,
            LogService log,
            MetaSchemaHashSource metaSchema)
        {
            _store = store;
            _persistence = persistence;
            _resolver = resolver;
            _directories = directories;
            _log = log;
            _metaSchema = metaSchema;
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
            string[] wads = EnumerateWadContainers(rootDirectory, includeBin, includeRst);
            var wadPaths = await LoadWadPathsAsync(includeRst, cancellationToken);
            var observed = CreateObservedSets();
            int scannedBins = 0, scannedRst = 0;
            MetaSchemaHashSnapshot metaSchema = includeBin
                ? await _metaSchema.GetSnapshotAsync(cancellationToken)
                : new MetaSchemaHashSnapshot();
            observed[InternalHashKind.BinTypes].UnionWith(metaSchema.UnknownTypes);
            observed[InternalHashKind.BinFields].UnionWith(metaSchema.UnknownFields);

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
                            if (wadPaths.TryGetValue(pair.Key, out path))
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
                                ArraySegment<byte> buffer = data.DangerousGetArray();
                                using var stream = new MemoryStream(buffer.Array, buffer.Offset, buffer.Count, false);
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
                        ProcessedWads = index + 1,
                        TotalWads = wads.Length,
                        ProcessedFiles = scannedBins + scannedRst,
                        CurrentStage = includeBin ? "Building BIN inventory" : "Building RST inventory"
                    });
                }
            }, cancellationToken);

            string inventoryDomain = includeBin ? "bin" : "rst";
            string fingerprint = BuildFingerprint(wads, inventoryDomain, metaSchema.Version);
            var selectedObserved = observed.Where(pair =>
                (includeBin && IsBinKind(pair.Key)) ||
                (includeRst && pair.Key is InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            await HashResolverService._hashFileAccessLock.WaitAsync(CancellationToken.None);
            try
            {
                await _persistence.CommitInternalInventoryAsync(selectedObserved, fingerprint, inventoryDomain, CancellationToken.None);
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
                Unknowns = unknowns,
                PatchFingerprint = fingerprint,
                ScannedBins = scannedBins,
                ScannedStringTables = scannedRst,
                MetaSchemaVersion = metaSchema.Version,
                MetaSchemaTypes = metaSchema.UnknownTypes.Count,
                MetaSchemaFields = metaSchema.UnknownFields.Count
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
            var stopwatch = Stopwatch.StartNew();
            int initial = matcher.Remaining;
            progress?.Report(CreateProgress(matcher, stopwatch, "Session inventory ready", 0));
            string[] wads = EnumerateWadContainers(rootDirectory, includeBin, includeRst);
            var wadPaths = await LoadWadPathsAsync(includeRst, cancellationToken);
            int scanned = 0;

            try
            {
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
                                if (wadPaths.TryGetValue(pair.Key, out path))
                                {
                                    isBin = includeBin && path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                                    isText = includeRst && IsTextCandidatePath(path) && pair.Value.UncompressedSize <= MaximumTextChunkSize;
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
                                        ArraySegment<byte> buffer = data.DangerousGetArray();
                                        using var stream = new MemoryStream(buffer.Array, buffer.Offset, buffer.Count, false);
                                        ScanBinContextualMatches(stream, matcher, path, wadPath, _resolver);
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
                        progress?.Report(CreateProgress(matcher, stopwatch, "Scanning BIN and text content", scanned, index + 1, wads.Length));
                    }
                }, cancellationToken);

                if (includeRst && matcher.Remaining > 0)
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
                progress?.Report(CreateProgress(matcher, stopwatch, "Saving resolved internal hashes", scanned));
                return await CompleteRunAsync(matcher, initial, scanned);
            }
            catch (OperationCanceledException)
            {
                await HandleCancelledRunAsync(matcher, stopwatch, progress, scanned);
                throw;
            }
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
            var stopwatch = Stopwatch.StartNew();
            int initial = matcher.Remaining;
            progress?.Report(CreateProgress(matcher, stopwatch, "Session inventory ready", 0));
            var binKnown = new List<string>();
            foreach (InternalHashKind kind in new[] { InternalHashKind.BinEntries, InternalHashKind.BinFields, InternalHashKind.BinTypes, InternalHashKind.BinHashes })
                binKnown.AddRange((await _store.LoadKnownAsync(kind, cancellationToken)).Values);
            var rst3 = (await _store.LoadKnownAsync(InternalHashKind.RstXxh3, cancellationToken)).Values.ToList();
            var rst64 = (await _store.LoadKnownAsync(InternalHashKind.RstXxh64, cancellationToken)).Values.ToList();
            var wadPaths = (await LoadWadPathsAsync(includeRst, cancellationToken)).Values;
            MetaSchemaHashSnapshot metaSchema = includeBin
                ? await _metaSchema.GetSnapshotAsync(cancellationToken)
                : new MetaSchemaHashSnapshot();
            long checkedCandidates = 0;

            try
            {
                await Task.Run(() =>
                {
                    if (includeRst)
                    {
                        CheckCandidates(binKnown, InternalHashGuessStrategy.CrossDictionary, "BIN dictionary keys");
                        CheckCandidates(rst3, InternalHashGuessStrategy.CrossVersion, "RST XXH3 keys");
                        CheckCandidates(rst64, InternalHashGuessStrategy.CrossVersion, "RST XXH64 keys");
                    }
                    if (includeBin)
                    {
                        CheckCandidates(metaSchema.KnownTypeNames, InternalHashGuessStrategy.CrossDictionary, MetaSchemaClassSource);
                        CheckCandidates(metaSchema.KnownFieldNames, InternalHashGuessStrategy.CrossDictionary, MetaSchemaPropertySource);
                    }
                    CheckCandidates(Common3DBones, InternalHashGuessStrategy.CrossDictionary, "Common 3D Skeleton Bones");

                    // Run advanced structural candidate generation
                    if (matcher.Remaining > 0)
                    {
                        var wordlist = new TokenWordlist();
                        foreach (string val in binKnown) wordlist.AddName(val);
                        foreach (string val in rst3) wordlist.AddName(val);
                        foreach (string val in rst64) wordlist.AddName(val);
                        foreach (string val in wadPaths) wordlist.AddName(val);
                        foreach (string val in metaSchema.KnownTypeNames) wordlist.AddName(val);
                        foreach (string val in metaSchema.KnownFieldNames) wordlist.AddName(val);
                        wordlist.FinalizeList();

                        CheckCandidates(GenerateStructuralCandidates(wordlist, NumericBudget, cancellationToken), InternalHashGuessStrategy.NumericVariant, "Advanced Structural Generation");
                    }

                    void CheckCandidates(IEnumerable<string> candidates, InternalHashGuessStrategy strategy, string source)
                    {
                        foreach (string candidate in candidates)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (includeBin)
                            {
                                matcher.CheckSchemaCandidate(InternalHashKind.BinTypes, candidate, strategy, source);
                                matcher.CheckSchemaCandidate(InternalHashKind.BinFields, candidate, strategy, source);
                            }
                            if (includeRst)
                                matcher.Check(candidate, strategy, source);
                            checkedCandidates++;
                            if ((checkedCandidates & 0x3ffff) == 0)
                                progress?.Report(CreateProgress(matcher, stopwatch, source,
                                    checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates));
                            if (matcher.Remaining == 0) break;
                        }
                    }
                }, cancellationToken);

                int checkedCount = checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates;
                IReadOnlyList<InternalHashGuessMatch> existingResearch = await _store.LoadResearchAsync(cancellationToken);
                matcher.PromoteUniqueSchemaCandidates(existingResearch);
                progress?.Report(CreateProgress(matcher, stopwatch, "Saving resolved internal hashes", checkedCount));
                return await CompleteRunAsync(matcher, initial, checkedCount);
            }
            catch (OperationCanceledException)
            {
                int checkedCount = checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates;
                await HandleCancelledRunAsync(matcher, stopwatch, progress, checkedCount);
                throw;
            }
        }

        private async Task HandleCancelledRunAsync(
            LocalEvidenceMatcher matcher,
            Stopwatch stopwatch,
            IProgress<InternalHashProgress> progress,
            int processedFiles)
        {
            progress?.Report(CreateProgress(matcher, stopwatch, "Saving matches found before cancellation", processedFiles));
            await PersistCancelledMatchesAsync(matcher);
        }

        private async Task<InternalHashRunResult> CompleteRunAsync(LocalEvidenceMatcher matcher, int initial, int scanned)
        {
            var matches = matcher.Matches.OrderBy(match => match.Kind).ThenBy(match => match.Value, StringComparer.Ordinal).ToList();
            await PersistMatchesAsync(matches);
            int verified = matches.Count(match => match.CanPromote);
            int candidates = matches.Count - verified;
            _log.LogSuccess($"Internal Hash Lab completed: {verified} verified values and {candidates} candidates from {initial} unknown hashes.");
            return new InternalHashRunResult { UnknownHashesAtStart = initial, ScannedFiles = scanned, Matches = matches };
        }

        private async Task PersistCancelledMatchesAsync(LocalEvidenceMatcher matcher)
        {
            if (matcher.Matches.Count == 0) return;
            try
            {
                await PersistMatchesAsync(matcher.Matches);
                _log.LogSuccess($"Internal Hash Lab preserved {matcher.Matches.Count} findings found before cancellation.");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Internal Hash Lab could not save matches found before cancellation.");
            }
        }

        private async Task PersistMatchesAsync(IEnumerable<InternalHashGuessMatch> matches)
        {
            var materialized = matches as IReadOnlyCollection<InternalHashGuessMatch> ?? matches.ToList();
            await HashResolverService._hashFileAccessLock.WaitAsync(CancellationToken.None);
            try
            {
                await _persistence.CommitInternalMatchesAsync(materialized, CancellationToken.None);
            }
            finally
            {
                HashResolverService._hashFileAccessLock.Release();
            }
            if (materialized.Count > 0) await _resolver.ForceReloadHashesAsync();
        }

        private static InternalHashProgress CreateProgress(
            LocalEvidenceMatcher matcher,
            Stopwatch stopwatch,
            string stage,
            int processedFiles,
            int processedWads = 0,
            int totalWads = 0) => new()
            {
                ProcessedWads = processedWads,
                TotalWads = totalWads,
                ProcessedFiles = processedFiles,
                FoundMatches = matcher.Matches.Count,
                RemainingUnknowns = matcher.Remaining,
                CheckedCandidates = matcher.CheckedCandidates,
                DiscardedCandidates = matcher.DiscardedCandidates,
                CandidatesPerSecond = matcher.CheckedCandidates / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001),
                Elapsed = stopwatch.Elapsed,
                ManagedMemoryBytes = GC.GetTotalMemory(false),
                CurrentStage = stage,
                NewMatches = matcher.TakePendingMatches()
            };

        private async Task EnsureInventoryAsync(string rootDirectory, bool includeBin, bool includeRst, IProgress<InternalHashProgress> progress, CancellationToken cancellationToken)
        {
            foreach (string domain in GetSelectedDomains(includeBin, includeRst))
            {
                bool isBinDomain = string.Equals(domain, "bin", StringComparison.Ordinal);
                string[] wads = EnumerateWadContainers(rootDirectory, isBinDomain, !isBinDomain);
                string schemaVersion = isBinDomain
                    ? (await _metaSchema.GetSnapshotAsync(cancellationToken)).Version
                    : "none";
                string fingerprint = BuildFingerprint(wads, domain, schemaVersion);
                string marker = Path.Combine(_directories.HashLabPath, $"internal.{domain}.patch.txt");
                string stored = File.Exists(marker) ? (await File.ReadAllTextAsync(marker, cancellationToken)).Trim() : string.Empty;
                if (!string.Equals(stored, fingerprint, StringComparison.Ordinal))
                    await BuildInventoryAsync(rootDirectory, isBinDomain, !isBinDomain, progress, cancellationToken);
            }
        }

        private static IEnumerable<string> GetSelectedDomains(bool includeBin, bool includeRst)
        {
            if (includeBin) yield return "bin";
            if (includeRst) yield return "rst";
        }

        private static bool IsBinKind(InternalHashKind kind) => kind is
            InternalHashKind.BinEntries or
            InternalHashKind.BinFields or
            InternalHashKind.BinTypes or
            InternalHashKind.BinHashes;

        private static string[] EnumerateWadContainers(string rootDirectory, bool includeBin, bool includeRst)
        {
            string searchRoot = rootDirectory;
            string trimmedRoot = Path.TrimEndingDirectorySeparator(rootDirectory);
            string gameDirectory = string.Equals(Path.GetFileName(trimmedRoot), "Game", StringComparison.OrdinalIgnoreCase)
                ? trimmedRoot
                : Path.Combine(trimmedRoot, "Game");
            if (Directory.Exists(gameDirectory)) searchRoot = gameDirectory;

            return Directory.EnumerateFiles(searchRoot, "*.wad*", SearchOption.AllDirectories)
                .Where(path =>
                {
                    if (includeBin && path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)) return true;
                    if (includeRst && (path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))) return true;
                    return false;
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private async Task<LocalEvidenceMatcher> CreateMatcherAsync(bool includeBin, bool includeRst, CancellationToken cancellationToken)
        {
            var targets = new Dictionary<InternalHashKind, HashSet<ulong>>();
            foreach (InternalHashKind kind in Enum.GetValues<InternalHashKind>())
            {
                bool isRst = kind is InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64;
                if (isRst)
                    targets[kind] = includeRst
                        ? await _store.LoadUnknownAsync(kind, cancellationToken)
                        : new HashSet<ulong>();
                else if (IsBinKind(kind))
                    targets[kind] = includeBin
                        ? await _store.LoadCurrentUnknownAsync(kind, cancellationToken)
                        : new HashSet<ulong>();
                else
                    targets[kind] = new HashSet<ulong>();
            }
            return new LocalEvidenceMatcher(targets);
        }

        private async Task<Dictionary<ulong, string>> LoadWadPathsAsync(bool includeLcu, CancellationToken cancellationToken)
        {
            var result = new Dictionary<ulong, string>();
            await LoadFileAsync("hashes.game.txt");
            if (includeLcu) await LoadFileAsync("hashes.lcu.txt");
            return result;

            async Task LoadFileAsync(string fileName)
            {
                string path = Path.Combine(_directories.HashesPath, fileName);
                if (!File.Exists(path)) return;
                using var reader = new StreamReader(path);
                while (await reader.ReadLineAsync(cancellationToken) is string line)
                    if (line.Length > 17 && ulong.TryParse(line.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                        result.TryAdd(hash, line[17..]);
            }
        }

        private static Dictionary<InternalHashKind, HashSet<ulong>> CreateObservedSets() => new()
        {
            [InternalHashKind.BinEntries] = new(),
            [InternalHashKind.BinFields] = new(),
            [InternalHashKind.BinTypes] = new(),
            [InternalHashKind.BinHashes] = new(),
            [InternalHashKind.RstXxh3] = new(),
            [InternalHashKind.RstXxh64] = new()
        };

        private static void ReadBinInventory(Stream stream, Dictionary<InternalHashKind, HashSet<ulong>> observed)
        {
            var tree = new BinTree(stream);
            ReadBinInventory(tree, observed);
        }

        internal static void ReadBinInventory(BinTree tree, Dictionary<InternalHashKind, HashSet<ulong>> observed)
        {
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
                case BinTreeObjectLink link when link.Value != 0: observed[InternalHashKind.BinEntries].Add(link.Value); break;
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

        private static void ScanBinContextualMatches(Stream stream, LocalEvidenceMatcher matcher, string path, string wadPath, HashResolverService resolver)
        {
            var tree = new BinTree(stream);
            MatchBinContentEvidence(tree, matcher, path, wadPath, resolver);
        }

        internal static void MatchBinContentEvidence(
            BinTree tree,
            LocalEvidenceMatcher matcher,
            string path,
            string wadPath = null,
            HashResolverService resolver = null)
        {
            MatchBinContextualEvidence(tree, matcher, path, wadPath, resolver);
            MatchObjectLocalHashEvidence(tree, matcher, path, wadPath);
            VisitBinStrings(tree, value => matcher.Check(value, InternalHashGuessStrategy.BinContent, path, wadPath, path));
        }

        internal static void MatchObjectLocalHashEvidence(
            BinTree tree,
            LocalEvidenceMatcher matcher,
            string path,
            string wadPath = null)
        {
            foreach (BinTreeObject item in tree.Objects.Values)
            {
                MatchDirectProperties(item.Properties.Values);
                foreach (BinTreeProperty property in item.Properties.Values)
                    VisitScope(property);
            }
            foreach (var item in tree.DataOverrides)
                VisitScope(item.Property);

            void MatchDirectProperties(IEnumerable<BinTreeProperty> properties)
            {
                var strings = new HashSet<string>(StringComparer.Ordinal);
                var observedHashes = new HashSet<uint>();
                foreach (BinTreeProperty property in properties)
                {
                    if (property is BinTreeString text && !string.IsNullOrWhiteSpace(text.Value))
                        strings.Add(text.Value.Trim());
                    else if (property is BinTreeHash hash)
                        observedHashes.Add(hash.Value);
                }
                MatchCandidates(strings, observedHashes);
            }

            void MatchPair(BinTreeProperty key, BinTreeProperty value)
            {
                var strings = new HashSet<string>(StringComparer.Ordinal);
                var observedHashes = new HashSet<uint>();
                Collect(key, strings, observedHashes);
                Collect(value, strings, observedHashes);
                MatchCandidates(strings, observedHashes);
            }

            void MatchCandidates(HashSet<string> strings, HashSet<uint> observedHashes)
            {
                foreach (var group in strings
                    .Where(LocalEvidenceMatcher.IsIdentifier)
                    .GroupBy(Fnv1a.HashLower))
                {
                    string[] candidates = group.Distinct(StringComparer.Ordinal).ToArray();
                    if (candidates.Length == 1 && observedHashes.Contains(group.Key))
                        matcher.CheckContextualCandidate(InternalHashKind.BinHashes, candidates[0], path, wadPath, group.Key);
                }
            }

            void VisitScope(BinTreeProperty property)
            {
                switch (property)
                {
                    case BinTreeStruct structure:
                        MatchDirectProperties(structure.Properties.Values);
                        foreach (BinTreeProperty child in structure.Properties.Values)
                            VisitScope(child);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements)
                            VisitScope(child);
                        break;
                    case BinTreeOptional option when option.Value != null:
                        VisitScope(option.Value);
                        break;
                    case BinTreeMap map:
                        foreach (var child in map)
                        {
                            MatchPair(child.Key, child.Value);
                            VisitScope(child.Key);
                            VisitScope(child.Value);
                        }
                        break;
                }
            }

            static void Collect(
                BinTreeProperty property,
                HashSet<string> strings,
                HashSet<uint> observedHashes)
            {
                switch (property)
                {
                    case BinTreeString text:
                        if (!string.IsNullOrWhiteSpace(text.Value)) strings.Add(text.Value.Trim());
                        break;
                    case BinTreeHash hash:
                        observedHashes.Add(hash.Value);
                        break;
                    case BinTreeStruct structure:
                        foreach (BinTreeProperty child in structure.Properties.Values)
                            Collect(child, strings, observedHashes);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements)
                            Collect(child, strings, observedHashes);
                        break;
                    case BinTreeOptional option when option.Value != null:
                        Collect(option.Value, strings, observedHashes);
                        break;
                    case BinTreeMap map:
                        foreach (var child in map)
                        {
                            Collect(child.Key, strings, observedHashes);
                            Collect(child.Value, strings, observedHashes);
                        }
                        break;
                }
            }
        }

        internal static void MatchBinContextualEvidence(
            BinTree tree,
            LocalEvidenceMatcher matcher,
            string path,
            string wadPath = null,
            HashResolverService resolver = null)
        {
            foreach (var pair in tree.Objects)
            {
                BinTreeObject item = pair.Value;
                MatchEntry(pair.Key, pair.Value);
                foreach (BinTreeProperty property in item.Properties.Values)
                    Visit(property);
            }
            foreach (var item in tree.DataOverrides)
                Visit(item.Property);

            void MatchEntry(uint entryHash, BinTreeObject item)
            {
                if (NamedEntryTypes.Contains(item.ClassHash)) MatchEntryFromString(entryHash, item, "name");
                if (DirectEntryFields.TryGetValue(item.ClassHash, out uint directField)) MatchEntryFromFieldHash(entryHash, item, directField);

                if (item.ClassHash is uint classHash)
                {
                    if (classHash == Fnv1a.HashLower("CharacterRecord") || classHash == Fnv1a.HashLower("TFTCharacterRecord"))
                        MatchEntryPattern(entryHash, item, "mCharacterName", value => $"Characters/{value}/CharacterRecords/Root");
                    else if (classHash == Fnv1a.HashLower("GameFontDescription"))
                        MatchEntryPattern(entryHash, item, "name", value => $"UX/Fonts/Descriptions/{value}");
                    else if (classHash == Fnv1a.HashLower("TFTRoundData"))
                        MatchEntryPattern(entryHash, item, "mName", value => $"Maps/Shipping/Map22/Rounds/{value}");
                    else if (classHash == Fnv1a.HashLower("TftItemData"))
                        MatchEntryPattern(entryHash, item, "mName", value => $"Maps/Shipping/Map22/Items/{value}");
                    else if (classHash == Fnv1a.HashLower("TftSetData"))
                        MatchEntryPattern(entryHash, item, "name", value => $"Maps/Shipping/Map22/Sets/{value}");
                    else if (classHash == Fnv1a.HashLower("TooltipFormat"))
                        MatchEntryPattern(entryHash, item, "mObjectName", value => $"UX/Tooltips/{value}");
                    else if (classHash == Fnv1a.HashLower("X3DSharedConstantBufferDef"))
                        MatchEntryPattern(entryHash, item, "name", value => $"Shaders/SharedData/{value}");
                    else if (classHash == Fnv1a.HashLower("ItemData"))
                        MatchEntryFromU32(entryHash, item, "itemID", value => $"Items/{value}");
                    else if (classHash == Fnv1a.HashLower("SummonerEmote"))
                        MatchEntryFromU32(entryHash, item, "summonerEmoteId", value => $"Loadouts/SummonerEmotes/{value}");
                    else if (classHash == Fnv1a.HashLower("TftMapSkin"))
                    {
                        if (TryGetString(item.Properties, "mapContainer", out string mapContainer))
                            MatchObservedEntry(entryHash, $"Loadouts/TFTMapSkins/{mapContainer[(mapContainer.LastIndexOf('/') + 1)..]}");
                        MatchAnyEntryString(item, "speciesLink");
                    }
                    else if (classHash == Fnv1a.HashLower("SpellObject"))
                        MatchSpellObject(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("ScriptCheat"))
                        MatchEntryFromCandidates(entryHash, item, "mName", new[] { "TFT", "Cherry", "Slime", "Strawberry", "Ultbook" }
                            .Select(mode => (Func<string, string>)(value => $"Cheats/GameModes/{mode}/{value}")));
                    else if (classHash == Fnv1a.HashLower("TftTraitData"))
                        MatchEntryFromCandidates(entryHash, item, "mName", Enumerable.Range(1, 29)
                            .Select(set => (Func<string, string>)(value => $"Maps/Shipping/Map22/Sets/TFTSet{set}/Traits/{value}")));
                    else if (classHash == Fnv1a.HashLower("MapSkin"))
                        MatchEntryFromCandidates(entryHash, item, "name", new[] { 11, 12, 21, 22, 30, 33, 35 }
                            .Select(map => (Func<string, string>)(value => $"Maps/Shipping/Map{map}/MapSkins/{value}")));
                    else if (classHash == Fnv1a.HashLower("AugmentData"))
                        MatchEntryPattern(entryHash, item, "AugmentNameId", value => $"Maps/ModeSpecificData/Augments/{value}");
                    else if (classHash == Fnv1a.HashLower("CompanionData"))
                        MatchAnyEntryString(item, "speciesLink");
                    else if (classHash == Fnv1a.HashLower("ViewControllerSet"))
                        MatchStringsInField(item, "SpecifiedGameModes");
                    else if (classHash == Fnv1a.HashLower("ViewControllerList"))
                        foreach (BinTreeProperty property in item.Properties.Values) VisitStrings(property, MatchAnyEntry);
                    else if (classHash == Fnv1a.HashLower("ResourceResolver") || classHash == Fnv1a.HashLower("GlobalResourceResolver"))
                        MatchHashLinkMap(item, "resourceMap");
                    else if (classHash == Fnv1a.HashLower("MapContainer"))
                        MatchHashLinkMap(item, "chunks");
                }
            }

            void MatchSpellObject(uint entryHash, BinTreeObject item)
            {
                if (TryGetString(item.Properties, "mScriptName", out string name))
                {
                    var candidates = new List<string> { $"Items/Spells/{name}", $"Shared/Spells/{name}" };
                    candidates.AddRange(new[] { 11, 12, 21, 22, 30, 33, 35 }.Select(map => $"Maps/Shipping/Map{map}/Spells/{name}"));
                    int digitCount = name.TakeWhile(char.IsAsciiDigit).Count();
                    if (digitCount > 0) candidates.Add($"Items/{name[..digitCount]}/Spells/{name}");
                    foreach (string candidate in candidates)
                        if (MatchObservedEntry(entryHash, candidate)) break;
                }

                if (item.Properties.TryGetValue(Fnv1a.HashLower("mSpell"), out BinTreeProperty spellProperty) && spellProperty is BinTreeStruct spell &&
                    spell.Properties.TryGetValue(Fnv1a.HashLower("DataValues"), out BinTreeProperty valuesProperty) && valuesProperty is BinTreeContainer values)
                {
                    foreach (BinTreeProperty value in values.Elements)
                        if (value is BinTreeStruct dataValue && TryGetString(dataValue.Properties, "name", out string dataName))
                            matcher.CheckContextualCandidate(InternalHashKind.BinHashes, dataName, path, wadPath);
                }
            }

            void MatchHashLinkMap(BinTreeObject item, string field)
            {
                if (resolver == null || !item.Properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) || property is not BinTreeMap map) return;
                foreach (var pair in map)
                {
                    if (pair.Key is not BinTreeHash key || pair.Value is not BinTreeObjectLink link) continue;
                    string target = resolver.ResolveBinEntry(link.Value);
                    if (string.Equals(target, link.Value.ToString("x8"), StringComparison.Ordinal)) continue;
                    if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, target, path, wadPath, key.Value)) continue;
                    int slash = target.LastIndexOf('/');
                    if (slash < 0) continue;
                    string basename = target[(slash + 1)..];
                    if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename, path, wadPath, key.Value) ||
                        matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename + "_BV2", path, wadPath, key.Value)) continue;
                    if (basename.Contains("Base_", StringComparison.Ordinal))
                        matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename.Replace("Base_", "", StringComparison.Ordinal), path, wadPath, key.Value);
                    for (int skin = 1; skin < 30; skin++)
                    {
                        string prefix = $"Skin{skin:00}_";
                        if (basename.Contains(prefix, StringComparison.Ordinal))
                        {
                            matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename.Replace(prefix, "", StringComparison.Ordinal), path, wadPath, key.Value);
                            break;
                        }
                    }
                }
            }

            void MatchEntryFromString(uint entryHash, BinTreeObject item, string field) => MatchEntryFromFieldHash(entryHash, item, Fnv1a.HashLower(field));
            void MatchEntryFromFieldHash(uint entryHash, BinTreeObject item, uint field)
            {
                if (item.Properties.TryGetValue(field, out BinTreeProperty property) && property is BinTreeString text)
                    MatchObservedEntry(entryHash, text.Value);
            }
            void MatchEntryPattern(uint entryHash, BinTreeObject item, string field, Func<string, string> format)
            {
                if (TryGetString(item.Properties, field, out string value)) MatchObservedEntry(entryHash, format(value));
            }
            void MatchEntryFromU32(uint entryHash, BinTreeObject item, string field, Func<uint, string> format)
            {
                if (item.Properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) && property is BinTreeU32 value)
                    MatchObservedEntry(entryHash, format(value.Value));
            }
            void MatchEntryFromCandidates(uint entryHash, BinTreeObject item, string field, IEnumerable<Func<string, string>> formats)
            {
                if (!TryGetString(item.Properties, field, out string value)) return;
                foreach (Func<string, string> format in formats) if (MatchObservedEntry(entryHash, format(value))) break;
            }
            void MatchAnyEntryString(BinTreeObject item, string field)
            {
                if (TryGetString(item.Properties, field, out string value)) MatchAnyEntry(value);
            }
            void MatchStringsInField(BinTreeObject item, string field)
            {
                if (item.Properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property)) VisitStrings(property, MatchAnyEntry);
            }
            bool MatchObservedEntry(uint hash, string value) => matcher.CheckContextualCandidate(InternalHashKind.BinEntries, value, path, wadPath, hash);
            void MatchAnyEntry(string value) => matcher.CheckContextualCandidate(InternalHashKind.BinEntries, value, path, wadPath);

            void Visit(BinTreeProperty property)
            {
                switch (property)
                {
                    case BinTreeStruct structure:
                        foreach (BinTreeProperty child in structure.Properties.Values) Visit(child);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements) Visit(child);
                        break;
                    case BinTreeOptional option when option.Value != null:
                        Visit(option.Value);
                        break;
                    case BinTreeMap map:
                        foreach (var child in map) { Visit(child.Key); Visit(child.Value); }
                        break;
                }
            }

            static bool TryGetString(Dictionary<uint, BinTreeProperty> properties, string field, out string value)
            {
                if (properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) && property is BinTreeString text)
                {
                    value = text.Value;
                    return true;
                }
                value = null;
                return false;
            }

            static void VisitStrings(BinTreeProperty property, Action<string> check)
            {
                switch (property)
                {
                    case BinTreeString text: check(text.Value); break;
                    case BinTreeStruct structure:
                        foreach (BinTreeProperty child in structure.Properties.Values) VisitStrings(child, check);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements) VisitStrings(child, check);
                        break;
                    case BinTreeOptional option when option.Value != null: VisitStrings(option.Value, check); break;
                }
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
            if (candidate.StartsWith("@V", StringComparison.Ordinal) && candidate.Length > 3)
            {
                check(candidate[2..]);
            }
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

        private static string BuildFingerprint(IEnumerable<string> paths, string domain, string sourceVersion = "none")
        {
            ulong xor = 0, sum = 0;
            long count = 0;
            foreach (string path in paths)
            {
                var info = new FileInfo(path);
                ulong value = unchecked((ulong)info.Length ^ (ulong)info.LastWriteTimeUtc.Ticks);
                xor ^= value; sum = unchecked(sum + value); count++;
            }
            string schema = string.Equals(domain, "bin", StringComparison.Ordinal) ? "3" : "1";
            return $"internal:{domain}:v{schema}:{sourceVersion}:{count}:{xor:x16}:{sum:x16}";
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
            var generatedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;

            bool Emit(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 512) return false;
                string clean = candidate.Trim();
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
                    string candidate = pref + UpperFirst(token);
                    if (Emit(candidate))
                    {
                        yield return candidate;
                        if (count >= budget) yield break;
                    }
                }
                foreach (string suff in suffixes)
                {
                    string candidate = token + UpperFirst(suff);
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

                    string comb1 = UpperFirst(combTokens[i]) + UpperFirst(combTokens[j]);
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

        private static string UpperFirst(string value) =>
            string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

        internal sealed class LocalEvidenceMatcher
        {
            private const ulong Rst38Mask = (1UL << 38) - 1;
            private readonly Dictionary<InternalHashKind, HashSet<ulong>> _targets;
            private readonly Dictionary<(InternalHashKind Kind, ulong Hash, string Value), InternalHashGuessMatch> _matches = new();
            private readonly List<InternalHashGuessMatch> _pendingMatches = new();
            public LocalEvidenceMatcher(Dictionary<InternalHashKind, HashSet<ulong>> targets) => _targets = targets;
            public IReadOnlyCollection<InternalHashGuessMatch> Matches => _matches.Values;
            public int Remaining => _targets.Values.Sum(values => values.Count);
            public long CheckedCandidates { get; private set; }
            public long DiscardedCandidates { get; private set; }

            public IReadOnlyList<InternalHashGuessMatch> TakePendingMatches()
            {
                if (_pendingMatches.Count == 0) return Array.Empty<InternalHashGuessMatch>();
                InternalHashGuessMatch[] matches = _pendingMatches.ToArray();
                _pendingMatches.Clear();
                return matches;
            }

            public void Check(
                string value,
                InternalHashGuessStrategy strategy,
                string source,
                string sourceWad = null,
                string sourceBin = null,
                IReadOnlyDictionary<InternalHashKind, HashSet<ulong>> localTargets = null)
            {
                CheckedCandidates++;
                if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || value.Length > 512)
                {
                    DiscardedCandidates++;
                    return;
                }
                string candidate = PathUtils.NormalizePath(value.Trim());
                uint fnv = Fnv1a.HashLower(candidate);
                bool content = strategy is InternalHashGuessStrategy.BinContent or InternalHashGuessStrategy.TextContent;
                bool crossDictionary = strategy == InternalHashGuessStrategy.CrossDictionary;
                bool gamePath = strategy == InternalHashGuessStrategy.GamePath;
                if (content || crossDictionary || gamePath)
                {
                    bool isAssetPath = candidate.Contains('.') && !candidate.EndsWith(".bin");
                    if (!isAssetPath)
                    {
                        if (candidate.Contains('/'))
                            Check32(InternalHashKind.BinEntries, fnv, candidate, strategy, source, sourceWad, sourceBin, HasLocalEvidence(fnv, localTargets, InternalHashKind.BinEntries));
                        Check32(InternalHashKind.BinHashes, fnv, candidate, strategy, source, sourceWad, sourceBin, HasLocalEvidence(fnv, localTargets, InternalHashKind.BinHashes));
                    }
                }
                if ((content || crossDictionary) && IsIdentifier(candidate))
                {
                    Check32(InternalHashKind.BinFields, fnv, candidate, strategy, source, sourceWad, sourceBin, HasLocalEvidence(fnv, localTargets, InternalHashKind.BinFields));
                    Check32(InternalHashKind.BinTypes, fnv, candidate, strategy, source, sourceWad, sourceBin, HasLocalEvidence(fnv, localTargets, InternalHashKind.BinTypes));

                }

                if (content || strategy is InternalHashGuessStrategy.CrossDictionary or InternalHashGuessStrategy.CrossVersion or InternalHashGuessStrategy.NumericVariant)
                {
                    int byteCount = Encoding.UTF8.GetByteCount(candidate);
                    Span<byte> bytes = byteCount <= 1536 ? stackalloc byte[byteCount] : new byte[byteCount];
                    Encoding.UTF8.GetBytes(candidate, bytes);
                    ulong xxh3 = XxHash3.HashToUInt64(bytes);
                    CheckRst(InternalHashKind.RstXxh3, xxh3, candidate, strategy, source, new[] { 38 }, sourceWad, sourceBin);
                    ulong xxh64 = XxHash64.HashToUInt64(bytes);
                    CheckRst(InternalHashKind.RstXxh64, xxh64, candidate, strategy, source, new[] { 64, 38, 39, 40 }, sourceWad, sourceBin);
                }
            }

            internal bool CheckContextualCandidate(
                InternalHashKind kind,
                string value,
                string source,
                string sourceWad = null,
                uint? observedHash = null)
            {
                CheckedCandidates++;
                if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
                {
                    DiscardedCandidates++;
                    return false;
                }

                string candidate = PathUtils.NormalizePath(value.Trim());
                uint computedHash = Fnv1a.HashLower(candidate);
                if (observedHash.HasValue && computedHash != observedHash.Value)
                {
                    DiscardedCandidates++;
                    return false;
                }

                int before = _matches.Count;
                Check32(
                    kind,
                    computedHash,
                    candidate,
                    InternalHashGuessStrategy.BinContent,
                    source,
                    sourceWad,
                    source,
                    true);
                return _matches.Count != before;
            }

            internal bool CheckSchemaCandidate(
                InternalHashKind kind,
                string value,
                InternalHashGuessStrategy strategy,
                string source)
            {
                CheckedCandidates++;
                if (kind is not (InternalHashKind.BinTypes or InternalHashKind.BinFields) ||
                    string.IsNullOrWhiteSpace(value) ||
                    value.Length > 128)
                {
                    DiscardedCandidates++;
                    return false;
                }
                string candidate = value.Trim();
                if (!IsIdentifier(candidate))
                {
                    DiscardedCandidates++;
                    return false;
                }
                candidate = kind == InternalHashKind.BinTypes
                    ? UpperFirst(candidate)
                    : char.ToLowerInvariant(candidate[0]) + candidate[1..];
                uint hash = Fnv1a.HashLower(candidate);
                if (!_targets[kind].Contains(hash))
                {
                    DiscardedCandidates++;
                    return false;
                }
                var key = (kind, (ulong)hash, candidate);
                if (_matches.ContainsKey(key)) return false;
                var match = new InternalHashGuessMatch
                {
                    Hash = hash,
                    LookupHash = hash,
                    HashBits = 32,
                    Value = candidate,
                    Kind = kind,
                    Strategy = strategy,
                    Source = source,
                    IsVerified = false,
                    VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                    Confidence = InternalHashConfidence.Candidate,
                    Evidence = InternalHashEvidence.MetaSchemaWordset
                };
                _matches[key] = match;
                _pendingMatches.Add(match);
                return true;
            }

            internal void PromoteUniqueSchemaCandidates(IEnumerable<InternalHashGuessMatch> existingResearch)
            {
                var candidates = existingResearch
                    .Concat(_matches.Values)
                    .Where(match =>
                        match.VerificationSchema >= InternalHashGuessMatch.CurrentVerificationSchema &&
                        match.Evidence == InternalHashEvidence.MetaSchemaWordset &&
                        !match.IsVerified &&
                        match.Confidence == InternalHashConfidence.Candidate &&
                        !string.IsNullOrWhiteSpace(match.Value) &&
                        match.Source is MetaSchemaClassSource or MetaSchemaPropertySource &&
                        match.Kind is InternalHashKind.BinTypes or InternalHashKind.BinFields &&
                        _targets[match.Kind].Contains(match.LookupHash))
                    .GroupBy(match => (match.Kind, match.LookupHash));

                foreach (var group in candidates)
                {
                    InternalHashGuessMatch[] distinct = group
                        .GroupBy(match => match.Value, StringComparer.Ordinal)
                        .Select(values => values.OrderByDescending(match => match.FoundAtUtc).First())
                        .ToArray();
                    if (distinct.Length != 1) continue;

                    InternalHashGuessMatch candidate = distinct[0];
                    _targets[candidate.Kind].Remove(candidate.LookupHash);
                    foreach (var key in _matches.Keys
                        .Where(key => key.Kind == candidate.Kind && key.Hash == candidate.Hash)
                        .ToList())
                        _matches.Remove(key);

                    var promoted = new InternalHashGuessMatch
                    {
                        Hash = candidate.Hash,
                        LookupHash = candidate.LookupHash,
                        HashBits = candidate.HashBits,
                        Value = candidate.Value,
                        Kind = candidate.Kind,
                        Strategy = candidate.Strategy,
                        Source = candidate.Source,
                        SourceWad = candidate.SourceWad,
                        SourceBin = candidate.SourceBin,
                        IsVerified = true,
                        VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                        Confidence = InternalHashConfidence.Verified,
                        Evidence = InternalHashEvidence.MetaSchemaUnique
                    };
                    _matches[(promoted.Kind, promoted.Hash, promoted.Value)] = promoted;
                    _pendingMatches.Add(promoted);
                }
            }

            internal static bool IsIdentifier(string value)
            {
                if (value.Length == 0 || value.Length > 128 || !(char.IsLetter(value[0]) || value[0] == '_')) return false;
                for (int index = 1; index < value.Length; index++)
                    if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_')) return false;
                return true;
            }

            private static bool HasLocalEvidence(
                uint hash,
                IReadOnlyDictionary<InternalHashKind, HashSet<ulong>> localTargets,
                InternalHashKind kind)
            {
                if (localTargets == null) return false;
                return localTargets.TryGetValue(kind, out var values) && values.Contains(hash);
            }

            private void Check32(
                InternalHashKind kind,
                uint hash,
                string value,
                InternalHashGuessStrategy strategy,
                string source,
                string sourceWad,
                string sourceBin,
                bool hasLocalEvidence)
            {
                if (!hasLocalEvidence || !_targets[kind].Remove(hash)) return;
                foreach (var candidateKey in _matches.Keys
                    .Where(key => key.Kind == kind && key.Hash == hash).ToList())
                    _matches.Remove(candidateKey);
                var key = (kind, (ulong)hash, value);
                var match = new InternalHashGuessMatch
                {
                    Hash = hash,
                    LookupHash = hash,
                    HashBits = 32,
                    Value = value,
                    Kind = kind,
                    Strategy = strategy,
                    Source = source,
                    SourceWad = sourceWad,
                    SourceBin = sourceBin,
                    IsVerified = true,
                    VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                    Confidence = InternalHashConfidence.Verified,
                    Evidence = InternalHashEvidence.RuntimeContext
                };
                _matches[key] = match;
                _pendingMatches.Add(match);
            }

            private void CheckRst(InternalHashKind kind, ulong fullHash, string value, InternalHashGuessStrategy strategy, string source, IEnumerable<int> bitOptions, string sourceWad = null, string sourceBin = null)
            {
                foreach (int bits in bitOptions)
                {
                    ulong lookup = bits == 64 ? fullHash : fullHash & ((1UL << bits) - 1);
                    if (!_targets[kind].Remove(lookup)) continue;
                    var match = new InternalHashGuessMatch
                    {
                        Hash = fullHash,
                        LookupHash = lookup,
                        HashBits = bits,
                        Value = value,
                        Kind = kind,
                        Strategy = strategy,
                        Source = source,
                        SourceWad = sourceWad,
                        SourceBin = sourceBin,
                        IsVerified = true,
                        VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                        Confidence = InternalHashConfidence.Verified,
                        Evidence = InternalHashEvidence.RstHashMatch
                    };
                    _matches[(kind, fullHash, value)] = match;
                    _pendingMatches.Add(match);
                    break;
                }
            }
        }
    }
}
