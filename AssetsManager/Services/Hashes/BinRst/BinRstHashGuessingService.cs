using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using AssetsManager.Services.Monitor;
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


        private static readonly string[] Common3DBones =
        {
            "Root", "Buffbone_C", "Buffbone_Glb_Center_Loc", "Buffbone_Glb_Layout_Loc", "Buffbone_Glb_Overhead_Loc",
            "Buffbone_Glb_Ground_Loc", "L_Hand", "R_Hand", "L_Foot", "R_Foot", "L_Arm", "R_Arm", "Head", "Spine",
            "Spine1", "Spine2", "Chest", "Neck", "Weapon", "L_Weapon", "R_Weapon", "Wing_L", "Wing_R", "Tail", "Pelvis"
        };

        private const int MaximumTextChunkSize = 16 * 1024 * 1024;
        private const int ContentBudget = 25_000_000;
        private readonly BinRstHashGuessingStore _store;
        private readonly HashGuessPersistenceService _persistence;
        private readonly HashResolverService _resolver;
        private readonly DirectoriesCreator _directories;
        private readonly LogService _log;
        private readonly MetaSchemaHashSource _metaSchema;
        private readonly VersionService _versionService;

        public BinRstHashGuessingService(
            BinRstHashGuessingStore store,
            HashGuessPersistenceService persistence,
            HashResolverService resolver,
            DirectoriesCreator directories,
            LogService log,
            MetaSchemaHashSource metaSchema,
            VersionService versionService = null)
        {
            _store = store;
            _persistence = persistence;
            _resolver = resolver;
            _directories = directories;
            _log = log;
            _metaSchema = metaSchema;
            _versionService = versionService;
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
            string[] looseBins = includeBin ? EnumerateLooseBinFiles(rootDirectory) : Array.Empty<string>();
            var wadPaths = await LoadWadPathsAsync(includeRst, cancellationToken);
            var observed = CreateObservedSets();
            int scannedBins = 0, scannedRst = 0;
            MetaSchemaHashSnapshot metaSchema = includeBin
                ? await _metaSchema.GetSnapshotAsync(cancellationToken)
                : new MetaSchemaHashSnapshot();
            int? gameVersion = includeRst
                ? await DetectGameVersionAsync(rootDirectory)
                : null;

            var stopwatch = Stopwatch.StartNew();

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
                                    ReadRstInventory(stream, observed[InternalHashKind.RstXxh3], observed[InternalHashKind.RstXxh64], gameVersion);
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
                        TotalWads = wads.Length + looseBins.Length,
                        ProcessedFiles = scannedBins + scannedRst,
                        CurrentStage = Path.GetFileName(wadPath),
                        Elapsed = stopwatch.Elapsed
                    });
                }

                if (includeBin)
                {
                    for (int index = 0; index < looseBins.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string binPath = looseBins[index];
                        string path = GetRelativeSourcePath(rootDirectory, binPath);
                        try
                        {
                            using var stream = new FileStream(
                                binPath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                64 * 1024,
                                FileOptions.SequentialScan);
                            ReadBinInventory(stream, observed);
                            scannedBins++;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _log.LogDebug($"Internal Hash Lab skipped loose BIN '{path}': {ex.Message}");
                        }
                        progress?.Report(new InternalHashProgress
                        {
                            ProcessedWads = wads.Length + index + 1,
                            TotalWads = wads.Length + looseBins.Length,
                            ProcessedFiles = scannedBins + scannedRst,
                            CurrentStage = Path.GetFileName(binPath),
                            Elapsed = stopwatch.Elapsed
                        });
                    }
                }
            }, cancellationToken);

            string inventoryDomain = includeBin ? "bin" : "rst";
            string[] fingerprintSources = includeBin
                ? wads.Concat(looseBins).ToArray()
                : wads;
            string fingerprintVersion = includeRst
                ? $"{metaSchema.Version};rst-game={gameVersion?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}"
                : metaSchema.Version;
            string fingerprint = BuildFingerprint(fingerprintSources, inventoryDomain, fingerprintVersion);
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
            _log.LogSuccess($"Internal Hash Lab inventory completed: {scannedBins} BIN and {scannedRst} RST files parsed.");
            return new InternalHashInventory
            {
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
            CancellationToken cancellationToken,
            IReadOnlySet<string> selectedSubMethods = null,
            IProgress<InternalHashGuessMatch> matchProgress = null)
        {
            ValidateRoot(rootDirectory);
            await EnsureInventoryAsync(rootDirectory, includeBin, includeRst, progress, cancellationToken);
            await _resolver.LoadAllHashesAsync();
            var matcher = await CreateMatcherAsync(includeBin, includeRst, cancellationToken, matchProgress == null ? null : matchProgress.Report);
            var stopwatch = Stopwatch.StartNew();
            progress?.Report(CreateProgress(matcher, stopwatch, "Loading game hashes dictionary", 0));
            string[] wads = EnumerateWadContainers(rootDirectory, includeBin, includeRst);
            bool shouldScanBinContent = selectedSubMethods == null || selectedSubMethods.Any(id =>
                id.StartsWith("bin-context-", StringComparison.Ordinal) ||
                string.Equals(id, "rst-content-binstrings", StringComparison.Ordinal));
            string[] looseBins = shouldScanBinContent ? EnumerateLooseBinFiles(rootDirectory) : Array.Empty<string>();
            var wadPaths = await LoadWadPathsAsync(includeRst, cancellationToken);
            bool ShouldRunBin(string id) => includeBin && (selectedSubMethods == null || selectedSubMethods.Contains(id));
            BinPathCasing casing = ShouldRunBin("bin-context-filepath") || ShouldRunBin("bin-context-learned")
                ? BinPathCasing.FromKnownNames((await _store.LoadKnownAsync(InternalHashKind.BinEntries, cancellationToken)).Values)
                : BinPathCasing.Empty;
            BinLearnedTemplateSource learned = ShouldRunBin("bin-context-learned") ? new BinLearnedTemplateSource(_resolver, casing) : null;
            int totalSources = wads.Length + looseBins.Length;
            int scanned = 0;
            int contentStartedCandidates = matcher.CheckedCandidates > int.MaxValue
                ? int.MaxValue
                : (int)matcher.CheckedCandidates;

            bool ContentBudgetExceeded() => matcher.CheckedCandidates - contentStartedCandidates >= ContentBudget;
            try
            {
                await Task.Run(() =>
                {
                    progress?.Report(CreateProgress(matcher, stopwatch, "Scanning BIN context files", 0, 0, totalSources));
                    for (int index = 0; index < wads.Length && matcher.Remaining > 0 && !ContentBudgetExceeded(); index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string wadPath = wads[index];
                        try
                        {
                            using var wad = new WadFile(wadPath);
                            foreach (var pair in wad.Chunks)
                            {
                                if (ContentBudgetExceeded()) break;
                                cancellationToken.ThrowIfCancellationRequested();
                                string path = null;
                                bool isBin = false;
                                bool isText = false;
                                if (wadPaths.TryGetValue(pair.Key, out path))
                                {
                                    isBin = shouldScanBinContent && path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                                    isText = includeRst && IsTextCandidatePath(path) && pair.Value.UncompressedSize <= MaximumTextChunkSize;
                                    if (!isBin && !isText)
                                    {
                                        string sig = GetChunkSignature(wad, pair.Value);
                                        if (shouldScanBinContent && (sig == "PROP" || sig == "PTCH"))
                                        {
                                            isBin = true;
                                        }
                                    }
                                }
                                else
                                {
                                    if (shouldScanBinContent)
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

                                using var data = wad.LoadChunkDecompressed(pair.Value);
                                if (isBin && shouldScanBinContent)
                                {
                                    try
                                    {
                                        ArraySegment<byte> buffer = data.DangerousGetArray();
                                        using var stream = new MemoryStream(buffer.Array, buffer.Offset, buffer.Count, false);
                                        var tree = new BinTree(stream);
                                        BinContentEvidenceSource.MatchBinContentEvidence(
                                            tree,
                                            matcher,
                                            path,
                                            wadPath,
                                            _resolver,
                                            selectedSubMethods,
                                            casing);
                                        learned?.Observe(tree, matcher, path, wadPath);
                                    }
                                    catch (Exception)
                                    {
                                        // Ignore malformed individual chunks and continue scanning
                                    }
                                }
                                else if (isText && (selectedSubMethods == null || selectedSubMethods.Contains("rst-content-text")))
                                {
                                    CheckTextCandidates(data.Memory.Span, value =>
                                    {
                                        if (ContentBudgetExceeded()) return;
                                        matcher.Check(value, InternalHashGuessStrategy.TextContent, path, wadPath, path);
                                    });
                                }
                                scanned++;
                                if ((scanned & 0x7f) == 0)
                                {
                                    progress?.Report(CreateProgress(matcher, stopwatch, $"Scanning {Path.GetFileName(wadPath)}", scanned, index, totalSources));
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Skip corrupted container
                        }
                        string stageText = $"Scanning {Path.GetFileName(wadPath)}";
                        progress?.Report(CreateProgress(matcher, stopwatch, stageText, scanned, index + 1, totalSources));
                    }

                    if (shouldScanBinContent && matcher.Remaining > 0 && !ContentBudgetExceeded())
                    {
                        for (int lIdx = 0; lIdx < looseBins.Length; lIdx++)
                        {
                            string looseBin = looseBins[lIdx];
                            if (ContentBudgetExceeded()) break;
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                using var stream = File.OpenRead(looseBin);
                                var tree = new BinTree(stream);
                                string loosePath = GetRelativeSourcePath(rootDirectory, looseBin);
                                BinContentEvidenceSource.MatchBinContentEvidence(
                                    tree,
                                    matcher,
                                    loosePath,
                                    resolver: _resolver,
                                    selectedSubMethods: selectedSubMethods,
                                    casing: casing);
                                learned?.Observe(tree, matcher, loosePath, null);
                            }
                            catch (Exception)
                            {
                                // Continue
                            }
                            scanned++;
                            progress?.Report(CreateProgress(matcher, stopwatch, $"Scanning loose BIN: {Path.GetFileName(looseBin)}", scanned, wads.Length + lIdx + 1, totalSources));
                            if (matcher.Remaining == 0) break;
                        }
                    }

                    if (learned != null)
                    {
                        progress?.Report(CreateProgress(matcher, stopwatch, $"Applying learned BIN templates to {learned.PendingCount} hashes", scanned));
                        int learnedHits = learned.Apply(matcher);
                        _log.LogDebug($"Learned BIN templates resolved {learnedHits} hashes.");
                        if (learned.LastVocabularyGate is { } vocabularyGate) LogGate(vocabularyGate);
                    }
                }, cancellationToken);

                int checkedCount = matcher.CheckedCandidates > int.MaxValue ? int.MaxValue : (int)matcher.CheckedCandidates;
                LogGate(matcher.ResolveUntargetedGate());
                progress?.Report(CreateProgress(matcher, stopwatch, "Persisting discovered internal hashes", checkedCount));
                return await CompleteRunAsync(matcher, checkedCount);
            }
            catch (OperationCanceledException)
            {
                int checkedCount = matcher.CheckedCandidates > int.MaxValue ? int.MaxValue : (int)matcher.CheckedCandidates;
                LogGate(matcher.ResolveUntargetedGate());
                return await CompleteRunAsync(matcher, checkedCount);
            }
        }

        private void LogGate(InternalHashEvidenceMatcher.NoiseGateResult gate)
        {
            if (gate.Hits == 0) return;
            string report = $"{gate.Name}: {gate.Hits} hits; {gate.ExpectedChanceMatches:F4} expected chance matches.";
            if (gate.Accepted)
                _log.LogDebug(report);
            else
                _log.LogWarning($"{report} Findings kept as candidates: chance collisions exceed {InternalHashEvidenceMatcher.MaxGateFalseDiscoveryRate:P0} of hits.");
        }


        public async Task<InternalHashRunResult> RunStructuralGuessingAsync(
            string rootDirectory,
            bool includeBin,
            bool includeRst,
            IProgress<InternalHashProgress> progress,
            CancellationToken cancellationToken,
            IReadOnlySet<string> selectedSubMethods = null,
            IProgress<InternalHashGuessMatch> matchProgress = null)
        {
            ValidateRoot(rootDirectory);
            await EnsureInventoryAsync(rootDirectory, includeBin, includeRst, progress, cancellationToken);
            var matcher = await CreateMatcherAsync(includeBin, includeRst, cancellationToken, matchProgress == null ? null : matchProgress.Report);
            var stopwatch = Stopwatch.StartNew();
            progress?.Report(CreateProgress(matcher, stopwatch, "Loading catalogs", 0));
            var binKnown = new List<string>();
            foreach (InternalHashKind kind in new[] { InternalHashKind.BinEntries, InternalHashKind.BinFields, InternalHashKind.BinTypes, InternalHashKind.BinHashes })
                binKnown.AddRange((await _store.LoadKnownAsync(kind, cancellationToken)).Values);
            var rst3 = (await _store.LoadKnownAsync(InternalHashKind.RstXxh3, cancellationToken)).Values.ToList();
            var rst64 = (await _store.LoadKnownAsync(InternalHashKind.RstXxh64, cancellationToken)).Values.ToList();
            var wadPaths = (await LoadWadPathsAsync(includeRst, cancellationToken)).Values;
            MetaSchemaHashSnapshot metaSchema = includeBin
                ? await _metaSchema.GetSnapshotAsync(cancellationToken)
                : new MetaSchemaHashSnapshot();

            if (includeBin && metaSchema.InterfaceTypes != null)
            {
                matcher.InterfaceTypes = metaSchema.InterfaceTypes;
            }

            long checkedCandidates = 0;
            bool ShouldRun(string id) => selectedSubMethods == null || selectedSubMethods.Contains(id);

            try
            {
                await Task.Run(() =>
                {
                    progress?.Report(CreateProgress(matcher, stopwatch, "Synthesizing structural candidates", 0));

                    // RST Submethods
                    if (includeRst)
                    {
                        if (ShouldRun("rst-struct-binkeys"))
                            CheckCandidates(binKnown, InternalHashGuessStrategy.CrossDictionary, "BIN dictionary keys");
                        if (ShouldRun("rst-struct-crossversion"))
                        {
                            CheckCandidates(rst3, InternalHashGuessStrategy.CrossVersion, "RST XXH3 keys");
                            CheckCandidates(rst64, InternalHashGuessStrategy.CrossVersion, "RST XXH64 keys");
                        }
                        if (matcher.Remaining > 0 && ShouldRun("rst-struct-gamepaths"))
                        {
                            progress?.Report(CreateProgress(matcher, stopwatch, "Cross-referencing GAME/LCU paths for RST", 0));
                            GamePathCandidateSource.Discover(wadPaths, matcher, "GAME/LCU path catalogs", cancellationToken);
                            progress?.Report(CreateProgress(matcher, stopwatch, "GAME/LCU paths cross-referenced", 0));
                        }
                    }

                    // BIN Direct Cross-Domain Submethod
                    if (includeBin && ShouldRun("bin-schema-crossdomain"))
                    {
                        CheckCandidates(metaSchema.KnownTypeNames, InternalHashGuessStrategy.CrossDictionary, MetaSchemaClassSource, preserveCasing: true);
                        CheckCandidates(metaSchema.KnownFieldNames, InternalHashGuessStrategy.CrossDictionary, MetaSchemaPropertySource, preserveCasing: true);
                        CheckCandidates(Common3DBones, InternalHashGuessStrategy.CrossDictionary, "Common 3D Skeleton Bones");
                    }

                    void RunGated(string name, Action pass)
                    {
                        matcher.BeginGate(name);
                        try
                        {
                            pass();
                        }
                        finally
                        {
                            LogGate(matcher.EndGate());
                            progress?.Report(CreateProgress(matcher, stopwatch, name, checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates));
                        }
                    }

                    // Build token wordlist for combinatorial and suffix folding passes
                    if (matcher.Remaining > 0)
                    {
                        var wordlist = new TokenWordlist();
                        foreach (string val in binKnown) wordlist.AddName(val);
                        foreach (string val in rst3) wordlist.AddName(val);
                        foreach (string val in rst64) wordlist.AddName(val);
                        foreach (string val in wadPaths) wordlist.AddName(val);
                        foreach (string val in metaSchema.KnownTypeNames) wordlist.AddTypeName(val);
                        foreach (string val in metaSchema.KnownFieldNames) wordlist.AddFieldName(val);
                        wordlist.FinalizeList();

                        // 1. Suffix Folding in State Space (O(Words) with 0 noise explosion)
                        if (includeBin && matcher.Remaining > 0 && ShouldRun("bin-schema-reverse-suffix"))
                        {
                            progress?.Report(CreateProgress(matcher, stopwatch, "State Space Suffix Folding", checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates));
                            RunGated("State Space Suffix Folding", () =>
                                ExecuteSuffixFoldingPass(matcher, wordlist, metaSchema, progress, stopwatch, cancellationToken));
                        }

                        // 2. Base Class Family Sibling Lattice
                        if (includeBin && matcher.Remaining > 0 && ShouldRun("bin-schema-family-lattice"))
                        {
                            progress?.Report(CreateProgress(matcher, stopwatch, "Base Class Family Lattice", checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates));
                            RunGated("Base Class Family Lattice", () =>
                                ExecuteFamilyLatticePass(matcher, wordlist, metaSchema, progress, stopwatch, cancellationToken));
                        }

                        // 3. Container Element Synthesizer ([ParentClass][FieldSingular]Definition/Data)
                        if (includeBin && matcher.Remaining > 0 && ShouldRun("bin-schema-container-def"))
                        {
                            progress?.Report(CreateProgress(matcher, stopwatch, "Container Element Synthesizer", checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates));
                            RunGated("Container Element Synthesizer", () =>
                                ExecuteContainerElementPass(matcher, wordlist, metaSchema, progress, stopwatch, cancellationToken));
                        }

                        // 4. Dual-Stem Affix Cracker (64-bit Joint Interface & Field Constraints)
                        if (includeBin && matcher.Remaining > 0 && ShouldRun("bin-schema-dual-stem"))
                        {
                            progress?.Report(CreateProgress(matcher, stopwatch, "Dual-Stem Affix Cracker", checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates));
                            RunGated("Dual-Stem Affix Cracker", () =>
                                ExecuteDualStemPass(matcher, wordlist, metaSchema, progress, stopwatch, cancellationToken));
                        }

                        // 5. Registration Bracket Audit & Disproven Collision Detection
                        if (includeBin && ShouldRun("bin-schema-bracket-audit"))
                        {
                            progress?.Report(CreateProgress(matcher, stopwatch, "Registration Bracket Audit", checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates));
                            RunGated("Registration Bracket Audit", () =>
                                ExecuteRegistrationBracketAuditPass(matcher, metaSchema, progress, stopwatch, cancellationToken));
                        }

                        // 6. Bin Object Path Lattices (PR #49: Challenges, Gear, DamageSkins, MapChunks, Boons)
                        if (includeBin && matcher.Remaining > 0 && ShouldRun("bin-schema-object-lattice"))
                        {
                            progress?.Report(CreateProgress(matcher, stopwatch, "Bin Object Path Lattices", checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates));
                            RunGated("Bin Object Path Lattices", () =>
                                ExecuteBinObjectLatticePass(matcher, wordlist, metaSchema, progress, stopwatch, cancellationToken));
                        }
                    }

                    void CheckCandidates(IEnumerable<string> candidates, InternalHashGuessStrategy strategy, string source, bool preserveCasing = false)
                    {
                        if (includeBin)
                            RunGated(source, () => CheckCandidatesCore(candidates, strategy, source, preserveCasing));
                        else
                            CheckCandidatesCore(candidates, strategy, source, preserveCasing);
                    }

                    void CheckCandidatesCore(IEnumerable<string> candidates, InternalHashGuessStrategy strategy, string source, bool preserveCasing)
                    {
                        foreach (string candidate in candidates)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (includeBin)
                            {
                                if (source != MetaSchemaPropertySource)
                                    matcher.CheckSchemaCandidate(InternalHashKind.BinTypes, candidate, strategy, source, preserveCasing: preserveCasing);
                                if (source != MetaSchemaClassSource)
                                    matcher.CheckSchemaCandidate(InternalHashKind.BinFields, candidate, strategy, source, preserveCasing: preserveCasing);
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
                progress?.Report(CreateProgress(matcher, stopwatch, "Saving resolved internal hashes", checkedCount));
                return await CompleteRunAsync(matcher, checkedCount);
            }
            catch (OperationCanceledException)
            {
                int checkedCount = checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates;
                await HandleCancelledRunAsync(matcher, stopwatch, progress, checkedCount);
                throw;
            }
        }

        private async Task HandleCancelledRunAsync(
            InternalHashEvidenceMatcher matcher,
            Stopwatch stopwatch,
            IProgress<InternalHashProgress> progress,
            int processedFiles)
        {
            progress?.Report(CreateProgress(matcher, stopwatch, "Saving matches found before cancellation", processedFiles));
            await PersistCancelledMatchesAsync(matcher);
        }

        private async Task<InternalHashRunResult> CompleteRunAsync(InternalHashEvidenceMatcher matcher, int scanned)
        {
            var matches = matcher.Matches.OrderBy(match => match.Kind).ThenBy(match => match.Value, StringComparer.Ordinal).ToList();
            await PersistMatchesAsync(matches);
            return new InternalHashRunResult { ScannedFiles = scanned, Matches = matches };
        }

        private async Task PersistCancelledMatchesAsync(InternalHashEvidenceMatcher matcher)
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
                if (materialized.Count > 0) _resolver.ReloadBinRstHashes();
            }
            finally
            {
                HashResolverService._hashFileAccessLock.Release();
            }
        }

        private static InternalHashProgress CreateProgress(
            InternalHashEvidenceMatcher matcher,
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
                Elapsed = stopwatch.Elapsed,
                CurrentStage = stage,
                NewMatches = matcher.TakePendingMatches()
            };

        private async Task EnsureInventoryAsync(string rootDirectory, bool includeBin, bool includeRst, IProgress<InternalHashProgress> progress, CancellationToken cancellationToken)
        {
            foreach (string domain in GetSelectedDomains(includeBin, includeRst))
            {
                bool isBinDomain = string.Equals(domain, "bin", StringComparison.Ordinal);
                string marker = Path.Combine(_directories.HashLabPath, $"internal.{domain}.patch.txt");
                if (File.Exists(marker)) continue;

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

        private static string[] EnumerateLooseBinFiles(string rootDirectory) =>
            Directory.EnumerateFiles(rootDirectory, "*.bin", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static string GetRelativeSourcePath(string rootDirectory, string filePath) =>
            Path.GetRelativePath(rootDirectory, filePath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

        private async Task<InternalHashEvidenceMatcher> CreateMatcherAsync(
            bool includeBin,
            bool includeRst,
            CancellationToken cancellationToken,
            Action<InternalHashGuessMatch> onMatchFound = null)
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
                        ? await _store.LoadUnknownAsync(kind, cancellationToken)
                        : new HashSet<ulong>();
                else
                    targets[kind] = new HashSet<ulong>();
            }
            return new InternalHashEvidenceMatcher(targets, onMatchFound);
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


        internal static void ReadRstInventory(
            Stream stream,
            HashSet<ulong> observedXxh3,
            HashSet<ulong> observedXxh64,
            int? gameVersion = null)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (Encoding.ASCII.GetString(reader.ReadBytes(3)) != "RST") throw new InvalidDataException("Invalid RST signature.");
            int version = reader.ReadByte();
            if (version == 2 && reader.ReadBoolean()) reader.BaseStream.Seek(reader.ReadUInt32(), SeekOrigin.Current);
            if (version is < 2 or > 5) throw new InvalidDataException($"Unsupported RST version {version}.");
            int[] bitOptions = GetRstHashBitOptions(version, gameVersion);
            bool? useXxh3 = gameVersion.HasValue ? gameVersion.Value >= 1415 : null;
            uint count = reader.ReadUInt32();
            for (int index = 0; index < count; index++)
            {
                ulong packedHash = reader.ReadUInt64();
                foreach (int bits in bitOptions)
                {
                    ulong entryHash = packedHash & ((1UL << bits) - 1);
                    if (useXxh3 != false) observedXxh3.Add(entryHash);
                    if (useXxh3 != true) observedXxh64.Add(entryHash);
                }
            }
        }

        private static int[] GetRstHashBitOptions(int rstVersion, int? gameVersion)
        {
            if (rstVersion is 2 or 3) return new[] { 40 };
            if (gameVersion.HasValue) return new[] { gameVersion.Value >= 1502 ? 38 : 39 };
            return new[] { 38, 39 };
        }

        private async Task<int?> DetectGameVersionAsync(string rootDirectory)
        {
            if (_versionService == null) return null;
            try
            {
                string version = await _versionService.GetGameVersionAsync(rootDirectory);
                if (string.IsNullOrWhiteSpace(version)) return null;
                string[] parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 ||
                    !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
                    return null;
                return major * 100 + minor;
            }
            catch (Exception ex)
            {
                _log.LogDebug($"RST game-version detection skipped: {ex.Message}");
                return null;
            }
        }

        private static void CheckTextCandidates(ReadOnlySpan<byte> data, Action<string> check)
        {
            var scanner = new BinaryTextCandidateScanner(check);
            scanner.Append(data);
            scanner.Complete();
        }

        private static void ExecuteSuffixFoldingPass(
            InternalHashEvidenceMatcher matcher,
            TokenWordlist wordlist,
            MetaSchemaHashSnapshot metaSchema,
            IProgress<InternalHashProgress> progress,
            Stopwatch stopwatch,
            CancellationToken cancellationToken)
        {
            var remainingTypes = matcher.GetRemaining(InternalHashKind.BinTypes).Select(h => (uint)h).ToHashSet();
            var remainingFields = matcher.GetRemaining(InternalHashKind.BinFields).Select(h => (uint)h).ToHashSet();
            if (remainingTypes.Count == 0 && remainingFields.Count == 0) return;

            // FoldedState -> List<(uint TargetHash, string Suffix, InternalHashKind Kind)>
            var stateMap = new Dictionary<uint, List<(uint TargetHash, string Suffix, InternalHashKind Kind)>>();

            void RegisterFoldedStates(HashSet<uint> targets, IEnumerable<string> suffixes, InternalHashKind kind)
            {
                foreach (uint target in targets)
                {
                    foreach (string suffix in suffixes)
                    {
                        uint foldedState = Fnv1aIncremental.Rewind(target, suffix);
                        if (!stateMap.TryGetValue(foldedState, out var list))
                        {
                            list = new List<(uint, string, InternalHashKind)>();
                            stateMap[foldedState] = list;
                        }
                        list.Add((target, suffix, kind));
                    }
                }
            }

            var typeSuffixes = wordlist.TypeSuffixes.Count > 0 ? wordlist.TypeSuffixes : wordlist.Suffixes.Select(s => wordlist.Case(s)).ToList();
            var fieldSuffixes = wordlist.FieldSuffixes.Count > 0 ? wordlist.FieldSuffixes : wordlist.Suffixes.Select(s => wordlist.Case(s)).ToList();

            if (remainingTypes.Count > 0)
                RegisterFoldedStates(remainingTypes, typeSuffixes, InternalHashKind.BinTypes);
            if (remainingFields.Count > 0)
                RegisterFoldedStates(remainingFields, fieldSuffixes, InternalHashKind.BinFields);

            if (stateMap.Count == 0) return;

            // Test Single word stems
            foreach (string token in wordlist.AllTokens.Take(2000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TestStem(wordlist.Case(token));
            }

            // Test Bigrams transitions
            foreach ((string a, string b) in wordlist.Bigrams.Take(3000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TestStem(wordlist.Case(a) + wordlist.Case(b));
            }

            void TestStem(string stem)
            {
                if (string.IsNullOrEmpty(stem) || stem.Length < 2) return;
                matcher.AddGateNoise(stateMap.Count / 4294967296.0);
                uint stemHash = Fnv1a.HashLower(stem);
                if (stateMap.TryGetValue(stemHash, out var targets))
                {
                    foreach (var (targetHash, suffix, kind) in targets)
                    {
                        string candidate = stem + suffix;
                        matcher.CheckSchemaCandidate(
                            kind,
                            candidate,
                            InternalHashGuessStrategy.NumericVariant,
                            $"SuffixFolding({suffix})",
                            InternalHashEvidence.MetaSchemaWordset,
                            preserveCasing: true);
                    }
                }
            }
        }

        private static void ExecuteFamilyLatticePass(
            InternalHashEvidenceMatcher matcher,
            TokenWordlist wordlist,
            MetaSchemaHashSnapshot metaSchema,
            IProgress<InternalHashProgress> progress,
            Stopwatch stopwatch,
            CancellationToken cancellationToken)
        {
            if (metaSchema.BaseToChildren == null || metaSchema.BaseToChildren.Count == 0) return;

            foreach (var (baseHash, childHashes) in metaSchema.BaseToChildren)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unknownChildren = childHashes
                    .Where(h => matcher.IsRemaining(InternalHashKind.BinTypes, h))
                    .Select(h => (uint)h)
                    .ToList();

                if (unknownChildren.Count == 0) continue;

                string baseName = metaSchema.KnownTypeEntries.TryGetValue(baseHash, out string bName) ? bName : null;
                var suffixes = new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(baseName))
                {
                    string cleanBase = baseName.StartsWith('I') && baseName.Length > 2 && char.IsUpper(baseName[1])
                        ? baseName[1..]
                        : baseName;
                    suffixes.Add(cleanBase);
                    var words = WordSplitter.Split(cleanBase).ToList();
                    if (words.Count > 1)
                    {
                        suffixes.Add(words[^1]);
                        if (words.Count > 2)
                            suffixes.Add(words[^2] + words[^1]);
                    }
                }

                foreach (ulong siblingHash in childHashes)
                {
                    if (metaSchema.KnownTypeEntries.TryGetValue(siblingHash, out string sibName))
                    {
                        var sibWords = WordSplitter.Split(sibName).ToList();
                        if (sibWords.Count > 0)
                            suffixes.Add(sibWords[^1]);
                        if (sibWords.Count > 1)
                            suffixes.Add(sibWords[^2] + sibWords[^1]);
                    }
                }

                if (suffixes.Count == 0) continue;

                var familyStateMap = new Dictionary<uint, List<(uint TargetHash, string Suffix)>>();
                foreach (uint target in unknownChildren)
                {
                    foreach (string suffix in suffixes)
                    {
                        uint folded = Fnv1aIncremental.Rewind(target, suffix);
                        if (!familyStateMap.TryGetValue(folded, out var list))
                        {
                            list = new List<(uint, string)>();
                            familyStateMap[folded] = list;
                        }
                        list.Add((target, suffix));
                    }
                }

                foreach (string token in wordlist.AllTokens.Take(2500))
                {
                    string word = wordlist.Case(token);
                    matcher.AddGateNoise(familyStateMap.Count / 4294967296.0);
                    uint wHash = Fnv1a.HashLower(word);
                    if (familyStateMap.TryGetValue(wHash, out var hits))
                    {
                        foreach (var (targetHash, suffix) in hits)
                        {
                            string candidate = word + suffix;
                            matcher.CheckSchemaCandidate(
                                InternalHashKind.BinTypes,
                                candidate,
                                InternalHashGuessStrategy.CrossDictionary,
                                $"FamilyLattice({baseName ?? baseHash.ToString("x8")})",
                                InternalHashEvidence.MetaSchemaRelation,
                                preserveCasing: true);
                        }
                    }
                }

                foreach ((string a, string b) in wordlist.Bigrams.Take(3000))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string bigram = wordlist.Case(a) + wordlist.Case(b);
                    matcher.AddGateNoise(familyStateMap.Count / 4294967296.0);
                    uint bHash = Fnv1a.HashLower(bigram);
                    if (familyStateMap.TryGetValue(bHash, out var hits))
                    {
                        foreach (var (targetHash, suffix) in hits)
                        {
                            string candidate = bigram + suffix;
                            matcher.CheckSchemaCandidate(
                                InternalHashKind.BinTypes,
                                candidate,
                                InternalHashGuessStrategy.CrossDictionary,
                                $"FamilyLatticeBigram({baseName ?? baseHash.ToString("x8")})",
                                InternalHashEvidence.MetaSchemaRelation,
                                preserveCasing: true);
                        }
                    }
                }
            }
        }

        private static void ExecuteContainerElementPass(
            InternalHashEvidenceMatcher matcher,
            TokenWordlist wordlist,
            MetaSchemaHashSnapshot metaSchema,
            IProgress<InternalHashProgress> progress,
            Stopwatch stopwatch,
            CancellationToken cancellationToken)
        {
            var remainingTypes = matcher.GetRemaining(InternalHashKind.BinTypes).Select(h => (uint)h).ToHashSet();
            if (remainingTypes.Count == 0) return;

            var candidateParents = metaSchema.KnownTypeNames
                .Where(name => name.Length >= 4 && (!name.StartsWith('I') || (name.Length > 2 && !char.IsUpper(name[1]))))
                .Take(2500)
                .ToList();

            var candidateFields = metaSchema.KnownFieldNames
                .Where(name => name.Length >= 3 && (name.EndsWith('s') || name.EndsWith("List", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Map", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Array", StringComparison.OrdinalIgnoreCase)))
                .Take(2500)
                .ToList();

            var commonSuffixes = new[] { "Definition", "Data", "Link", "Settings", "Config" };

            foreach (string parentName in candidateParents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stem = StripContainerParentSuffix(parentName);

                foreach (string fieldName in candidateFields)
                {
                    string singular = SingularizeWord(fieldName);
                    if (string.IsNullOrEmpty(singular)) continue;

                    var candidates = new List<string>(16);

                    foreach (string sfx in commonSuffixes)
                    {
                        if (!string.IsNullOrEmpty(stem))
                            candidates.Add(stem + singular + sfx);
                        candidates.Add(parentName + singular + sfx);
                        candidates.Add(singular + sfx);
                    }

                    if (!string.IsNullOrEmpty(stem))
                    {
                        candidates.Add(stem + singular);
                        candidates.Add(stem + UpperFirst(fieldName));
                    }
                    candidates.Add(parentName + singular);

                    if (fieldName.EndsWith("tags", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(stem))
                    {
                        candidates.Add(stem + "TagDefinition");
                        candidates.Add(stem + "RegionTagDefinition");
                        candidates.Add(stem + "TerrainTagDefinition");
                        candidates.Add(stem + "TagsLink");
                        candidates.Add(stem + "RegionTagsLink");
                    }

                    if (fieldName.EndsWith("VfxSystems", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(stem))
                    {
                        candidates.Add(stem + "VfxData");
                        candidates.Add(stem + "VfxSystemData");
                    }

                    foreach (string candidate in candidates)
                    {
                        uint hash = Fnv1a.HashLower(candidate);
                        if (remainingTypes.Contains(hash))
                        {
                            matcher.CheckSchemaCandidate(
                                InternalHashKind.BinTypes,
                                candidate,
                                InternalHashGuessStrategy.CrossDictionary,
                                $"ContainerElement({parentName}.{fieldName})",
                                InternalHashEvidence.ContainerElementConvention,
                                preserveCasing: true);
                        }
                    }
                }
            }
        }

        private static string StripContainerParentSuffix(string parentName)
        {
            if (string.IsNullOrEmpty(parentName)) return parentName;
            foreach (string suffix in new[] { "Config", "Definition", "Settings", "Parameters", "Data", "Container", "List", "Manager", "System" })
            {
                if (parentName.EndsWith(suffix, StringComparison.Ordinal) && parentName.Length > suffix.Length + 2)
                    return parentName[..^suffix.Length];
            }
            return parentName;
        }

        private static string SingularizeWord(string plural)
        {
            if (string.IsNullOrWhiteSpace(plural) || plural.Length < 3) return null;
            string p = plural;
            if (p.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && p.Length > 4)
                return UpperFirst(p[..^3] + "y");
            if (p.EndsWith("List", StringComparison.OrdinalIgnoreCase) && p.Length > 4)
                return UpperFirst(p[..^4]);
            if (p.EndsWith("Map", StringComparison.OrdinalIgnoreCase) && p.Length > 4)
                return UpperFirst(p[..^3]);
            if (p.EndsWith("Array", StringComparison.OrdinalIgnoreCase) && p.Length > 5)
                return UpperFirst(p[..^5]);
            if (p.EndsWith('s') || p.EndsWith('S'))
            {
                if (p.EndsWith("ss", StringComparison.OrdinalIgnoreCase)) return UpperFirst(p);
                return UpperFirst(p[..^1]);
            }
            return UpperFirst(p);
        }

        private static void ExecuteDualStemPass(
            InternalHashEvidenceMatcher matcher,
            TokenWordlist wordlist,
            MetaSchemaHashSnapshot metaSchema,
            IProgress<InternalHashProgress> progress,
            Stopwatch stopwatch,
            CancellationToken cancellationToken)
        {
            var remainingTypes = matcher.GetRemaining(InternalHashKind.BinTypes).Select(h => (uint)h).ToHashSet();
            var remainingFields = matcher.GetRemaining(InternalHashKind.BinFields).Select(h => (uint)h).ToHashSet();
            if (remainingTypes.Count == 0 && remainingFields.Count == 0) return;

            var interfaceTargets = metaSchema.InterfaceTypes != null && metaSchema.InterfaceTypes.Count > 0
                ? remainingTypes.Where(h => metaSchema.InterfaceTypes.Contains(h)).ToList()
                : remainingTypes.ToList();

            if (interfaceTargets.Count == 0) interfaceTargets = remainingTypes.ToList();
            if (interfaceTargets.Count == 0) return;

            var dualStemSuffixes = new List<string>
            {
                "DataGenerator", "SpawnDataGenerator", "Generator",
                "Container", "MaterialContainer",
                "Driver", "MaterialDriver",
                "Surface", "EmissionSurface",
                "Source", "EmissionSource",
                "Resolver", "ResourceResolver",
                "Provider", "Controller", "Processor", "Calculator", "Modifier", "Updater",
                "Definition", "DefinitionData", "Data", "Component", "Manager", "Handler", "Listener"
            };

            foreach (string sfx in wordlist.TypeSuffixes.Take(30))
            {
                if (!dualStemSuffixes.Contains(sfx, StringComparer.OrdinalIgnoreCase))
                    dualStemSuffixes.Add(sfx);
            }

            var ifaceMap = new Dictionary<uint, List<(uint InterfaceHash, string Suffix)>>();
            foreach (uint ifaceHash in interfaceTargets)
            {
                foreach (string suffix in dualStemSuffixes)
                {
                    uint rewound = Fnv1aIncremental.Rewind(ifaceHash, suffix);
                    if (!ifaceMap.TryGetValue(rewound, out var list))
                    {
                        list = new List<(uint, string)>();
                        ifaceMap[rewound] = list;
                    }
                    list.Add((ifaceHash, suffix));
                }
            }

            // Test single-token prefixes
            foreach (string token in wordlist.AllTokens.Take(2500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TestPrefix(wordlist.Case(token));
            }

            // Test 2-token bigram prefixes
            foreach ((string a, string b) in wordlist.Bigrams.Take(3500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TestPrefix(wordlist.Case(a) + wordlist.Case(b));
            }

            void TestPrefix(string prefix)
            {
                if (string.IsNullOrEmpty(prefix) || prefix.Length < 2) return;
                matcher.AddGateNoise(ifaceMap.Count / 4294967296.0);

                uint ifacePrefixHash = Fnv1a.HashLower("i" + prefix);
                if (ifaceMap.TryGetValue(ifacePrefixHash, out var hits))
                {
                    foreach (var (ifaceHash, suffix) in hits)
                    {
                        string stem = prefix + suffix;
                        string interfaceName = "I" + stem;

                        string fieldCandidate = char.ToLowerInvariant(stem[0]) + stem[1..];
                        uint fieldHash = Fnv1a.HashLower(fieldCandidate);

                        if (remainingFields.Contains(fieldHash))
                        {
                            matcher.CheckSchemaCandidate(
                                InternalHashKind.BinFields,
                                fieldCandidate,
                                InternalHashGuessStrategy.CrossDictionary,
                                $"DualStem64({stem} / {interfaceName})",
                                InternalHashEvidence.DualStemAffixPair,
                                preserveCasing: true);

                            matcher.CheckSchemaCandidate(
                                InternalHashKind.BinTypes,
                                interfaceName,
                                InternalHashGuessStrategy.CrossDictionary,
                                $"DualStem64({stem} / {interfaceName})",
                                InternalHashEvidence.DualStemAffixPair,
                                preserveCasing: true);
                        }
                        else
                        {
                            uint typeHash = Fnv1a.HashLower(stem);
                            if (remainingTypes.Contains(typeHash))
                            {
                                matcher.CheckSchemaCandidate(
                                    InternalHashKind.BinTypes,
                                    stem,
                                    InternalHashGuessStrategy.CrossDictionary,
                                    $"DualStem64({stem} / {interfaceName})",
                                    InternalHashEvidence.DualStemAffixPair,
                                    preserveCasing: true);

                                matcher.CheckSchemaCandidate(
                                    InternalHashKind.BinTypes,
                                    interfaceName,
                                    InternalHashGuessStrategy.CrossDictionary,
                                    $"DualStem64({stem} / {interfaceName})",
                                    InternalHashEvidence.DualStemAffixPair,
                                    preserveCasing: true);
                            }
                            else if (matcher.IsRemaining(InternalHashKind.BinTypes, ifaceHash))
                            {
                                matcher.CheckSchemaCandidate(
                                    InternalHashKind.BinTypes,
                                    interfaceName,
                                    InternalHashGuessStrategy.CrossDictionary,
                                    $"InterfaceStem({interfaceName})",
                                    InternalHashEvidence.MetaSchemaRelation,
                                    preserveCasing: true);
                            }
                        }
                    }
                }
            }
        }

        private static void ExecuteRegistrationBracketAuditPass(
            InternalHashEvidenceMatcher matcher,
            MetaSchemaHashSnapshot metaSchema,
            IProgress<InternalHashProgress> progress,
            Stopwatch stopwatch,
            CancellationToken cancellationToken)
        {
            var remainingTypes = matcher.GetRemaining(InternalHashKind.BinTypes).Select(h => (uint)h).ToHashSet();

            var knownClusterCandidates = new (uint Hash, string Name, string Reason)[]
            {
                (0x2d00e4da, "NavGridRegionInputData", "First of three consecutive NavGrid registrars"),
                (0x6b91544a, "NavGridRegionRenderData", "Second of three consecutive NavGrid registrars"),
                (0x41c19efe, "NavGridRegionVfxData", "Replaces false collision VfxPrimitiveCameraSegmentSeriesBeam"),
                (0xd2807c60, "VfxEmbeddedMaterial", "Material container subclass directly before 526478f0"),
                (0x526478f0, "VfxEmissionLinkedMeshData", "Emission surface subclass registered between VfxEmbeddedMaterial and VfxEmissionMeshData"),
                (0xcd5a34f5, "VfxEmissionMeshData", "Skinned mesh emission surface registered between 526478f0 and 3df230bf"),
                (0x3df230bf, "VfxEmissionSkeletonData", "Skeleton emission surface registered between cd5a34f5 and VfxEmissionSurfaceData"),
                (0x3bf517c5, "VfxMaterialContainer", "Interface pointer type of VfxMaterialRenderComponent.MaterialContainer"),
                (0x2bfb084c, "NavGridRegionGroupDefinition", "Element of NavGridConfig.RegionGroups"),
                (0x0f6f4bb5f, "NavGridRegionTagDefinition", "Element of NavGridRegionGroupDefinition.tags"),
                (0x0f42cd443, "NavGridRegionTagsLink", "Tags link struct registered between f6f4bb5f and NavGridTerrainConfig"),
                (0xd82714cc, "NavGridTerrainTagDefinition", "Element of NavGridTerrainConfig.tags"),
                (0x1519e8d2, "IParticleSpawnDataGenerator", "Interface registered between FloatGraphMaterialDriver and IVfxEmissionSource"),
                (0x671b7351, "NavigationGridParticleSpawnDataGenerator", "Subclass of IParticleSpawnDataGenerator between MutatorMapVisibilityController and RefundAbilityPointsCheat")
            };

            foreach (var (hash, name, reason) in knownClusterCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remainingTypes.Contains(hash))
                {
                    matcher.CheckSchemaCandidate(
                        InternalHashKind.BinTypes,
                        name,
                        InternalHashGuessStrategy.CrossDictionary,
                        $"RegistrationBracket({reason})",
                        InternalHashEvidence.RegistrationBracketMatch,
                        preserveCasing: true);
                }
            }

            if (matcher.IsRemaining(InternalHashKind.BinFields, 0xf8b81c77))
            {
                matcher.CheckSchemaCandidate(
                    InternalHashKind.BinFields,
                    "ParticleSpawnDataGenerator",
                    InternalHashGuessStrategy.CrossDictionary,
                    "RegistrationBracket(Pointer on VfxEmissionSurfaceData)",
                    InternalHashEvidence.RegistrationBracketMatch,
                    preserveCasing: true);
            }

            if (matcher.IsRemaining(InternalHashKind.BinFields, 0xec01928c))
            {
                matcher.CheckSchemaCandidate(
                    InternalHashKind.BinFields,
                    "IsGameplayGroup",
                    InternalHashGuessStrategy.CrossDictionary,
                    "RegistrationBracket(Bool on NavGridRegionGroupDefinition)",
                    InternalHashEvidence.RegistrationBracketMatch,
                    preserveCasing: true);
            }
        }

        private static void ExecuteBinObjectLatticePass(
            InternalHashEvidenceMatcher matcher,
            TokenWordlist wordlist,
            MetaSchemaHashSnapshot metaSchema,
            IProgress<InternalHashProgress> progress,
            Stopwatch stopwatch,
            CancellationToken cancellationToken)
        {
            if (matcher.GetRemainingCount(InternalHashKind.BinEntries) == 0) return;

            int checkCount = 0;
            void CheckEntry(string candidate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                matcher.CheckResearchCandidate(
                    InternalHashKind.BinEntries,
                    candidate,
                    InternalHashGuessStrategy.CrossDictionary,
                    "BinObjectLattice",
                    InternalHashEvidence.BinObjectLatticePattern,
                    verified: true);
                checkCount++;
                if ((checkCount & 0x1fff) == 0)
                {
                    progress?.Report(CreateProgress(matcher, stopwatch, "Bin Object Path Lattices", checkCount));
                }
            }

            // 1. LCU Challenges Lattice (LCU/Challenges/Config/{id}/Config)
            // PR #49: Crauzer identified 413 ChallengeConfigData entries under LCU/Challenges/Config/<ID>/Config
            var challengeIds = new List<int>();
            for (int i = 1; i <= 250; i++) challengeIds.Add(i);
            for (int baseId = 100000; baseId <= 600000; baseId += 100000)
            {
                for (int i = 0; i <= 4000; i++)
                {
                    challengeIds.Add(baseId + i);
                }
            }

            foreach (int id in challengeIds)
            {
                CheckEntry($"LCU/Challenges/Config/{id}/Config");
                CheckEntry($"LCU/Challenges/Config/{id}");
                if (matcher.GetRemainingCount(InternalHashKind.BinEntries) == 0) return;
            }

            // 2. Champion Skin Gear Lattice (Characters/{Champ}/Skins/Skin{N}/Gear/{GearName})
            var gearNames = new[]
            {
                "Default", "Ult", "Ultimate", "Base", "Melee", "Ranged", "Shadow",
                "Form1", "Form2", "Form3", "Form4", "Level6", "level11", "Level11", "Level16", "level16",
                "Normal", "Spell1", "Spell2", "Spell3", "Spell4", "Empowered", "Frenzy", "Mounted", "Biped",
                "Transformation", "WraithForm", "Demon", "Wings", "Crown_1", "Crown_2", "Bound", "Unbound",
                "UltForm", "Rage", "Tier1", "Tier2", "Tier3", "SlotMachine1", "SlotMachine2", "SlotMachine3",
                "Weapon0", "Weapon1", "Weapon2", "Weapon3", "Level1", "Level2", "Level3", "Level4"
            };

            var candidateChamps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (metaSchema?.KnownTypeNames != null)
            {
                foreach (string typeName in metaSchema.KnownTypeNames)
                {
                    if (typeName.StartsWith("Character", StringComparison.OrdinalIgnoreCase) && typeName.Length > 9)
                        candidateChamps.Add(typeName[9..]);
                }
            }
            if (wordlist?.AllTokens != null)
            {
                foreach (string tok in wordlist.AllTokens)
                {
                    if (tok.Length >= 3 && char.IsUpper(tok[0]) && !tok.EndsWith("Data", StringComparison.Ordinal) && !tok.EndsWith("Def", StringComparison.Ordinal))
                    {
                        candidateChamps.Add(wordlist.Case(tok));
                    }
                }
            }

            foreach (string champ in candidateChamps.Take(300))
            {
                for (int skin = 0; skin <= 60; skin++)
                {
                    string skinPath = $"Characters/{champ}/Skins/Skin{skin}";
                    CheckEntry($"{skinPath}/Resources");

                    foreach (string gear in gearNames)
                    {
                        CheckEntry($"{skinPath}/Gear/{gear}");
                    }
                    CheckEntry($"{skinPath}/Gear/Skin{skin}");

                    if (matcher.GetRemainingCount(InternalHashKind.BinEntries) == 0) return;
                }
            }

            // 3. TFT Damage Skins Lattice (Loadouts/TFTDamageSkins/{Name}/{Name}_Tier{1..3})
            var damageSkinStems = new[]
            {
                "AatroxTheDarkinBlade", "AbyssalChasm", "AhriOrbofDeception", "Ahri_Prestige_SpiritBlossom",
                "AkaliPerfectExecution", "Akali_StarGuardian", "AnnieSummonTibbers", "Annie_BlueHaired",
                "AprilFools_Celebration", "ArcaneWarwick_Skin", "Arm", "Ashe_Volley", "Baron_Bite",
                "Blitzcrank_RocketGrab", "BloodMoon_Execution", "Brand_Pyroclasm", "Caitlyn_AceInTheHole",
                "Choncc_BellyFlop", "Cyberpunk_Laser", "DarkStar_Singularity", "Dragon_Breath",
                "Ekko_Chronobreak", "Elementalist_Lux", "Ezreal_TrueshotBarrage", "Garen_DemacianJustice",
                "Hextech_Beam", "Jinx_SuperMegaDeathRocket", "KaiSa_IcathianRain", "KhaZix_TasteTheirFear",
                "LeeSin_DragonRage", "Lux_FinalSpark", "Malphite_UnstoppableForce", "MissFortune_BulletTime",
                "Mordekaiser_RealmOfDeath", "Morgana_SoulShackles", "Nami_TidalWave", "Nautilus_DepthCharge",
                "Nocturne_Paranoia", "Pengu_Party", "Poro_King", "Pyke_DeathFromBelow", "Riven_BladeOfTheExile",
                "Senna_DawningShadow", "Sett_Showstopper", "StarGuardian_MagicalBlast", "Teemo_NoxiousTrap",
                "Thresh_DeathSentence", "Vayne_SilverBolts", "Veigar_PrimordialBurst", "Warwick_InfiniteDuress",
                "Yasuo_LastBreath", "Yone_FateSealed", "Zed_DeathMark"
            };

            foreach (string dName in damageSkinStems)
            {
                for (int tier = 1; tier <= 3; tier++)
                {
                    CheckEntry($"Loadouts/TFTDamageSkins/{dName}/{dName}_Tier{tier}");
                    CheckEntry($"Loadouts/TFTDamageSkins/{dName}/{dName}_Tier{tier}/ResourceBin/Resources");
                }
            }

            // 4. ARAM Boons Lattice (Loadouts/AramBoons/ARAM_Boon_{1..30}/ARAM_Boon_{1..30})
            for (int boon = 1; boon <= 30; boon++)
            {
                CheckEntry($"Loadouts/AramBoons/ARAM_Boon_{boon}/ARAM_Boon_{boon}");
            }

            // 5. Map Placeable Chunks Lattice (Map11 & Map22)
            var map11Themes = new[] { "Bloom", "Boba_SRS", "Boba_SRS_Act2A", "Boba_SRS_Act2B" };
            var map11SubChunks = new[]
            {
                "Art", "Audio", "VFX", "Lighting", "Esports_Banners", "Hall_Of_Legends",
                "SRS_Chemtech", "SRS_Cloud", "SRS_Earth", "SRS_Fire", "SRS_Geometry",
                "SRS_Ground", "SRS_Hextech", "SRS_Ocean", "SRS_Shop", "SRS_Structures",
                "SRX_Particles", "SRX_Chemtech_VFX"
            };

            foreach (string theme in map11Themes)
            {
                CheckEntry($"Maps/MapGeometry/Map11/Chunks/{theme}");
                CheckEntry($"Maps/MapGeometry/Map11/Chunks/{theme}_Audio");
                foreach (string sc in map11SubChunks)
                {
                    CheckEntry($"Maps/MapGeometry/Map11/Chunks/{theme}/{sc}");
                }
            }

            var map22Themes = new[]
            {
                "Anniversary", "7yAnniversary", "Astronaut", "BattleStadium", "BilgewaterBay",
                "ChosenOfTheWolf", "Cyberpunk", "DawnbringerNightbringer", "EliteVipRoom",
                "EnchantedForest", "FirelightsHideout", "ImmortalJourney", "LanternFestivalStreet",
                "LunarRevelModern", "MagicDuel", "MagicLibrary", "NeonDJ", "RetroDreamscape",
                "Shurima", "SoulFighter2026", "StreetFashion", "SushiConveyorBelt", "Targon",
                "TeaTerraces", "TheBridge", "Worlds", "BattleAcademia", "InkBiome_Earth",
                "InkBiome_Ocean", "InkBiome_Sky"
            };

            var map22SubChunks = new[] { "Art", "Audio", "VFX", "Lighting", "Props", "Skybox", "Ground", "Structures", "Geometry" };

            foreach (string theme in map22Themes)
            {
                CheckEntry($"Maps/MapGeometry/Map22/Chunks/{theme}");
                CheckEntry($"Maps/MapGeometry/Map22/Chunks/{theme}_Audio");
                foreach (string sc in map22SubChunks)
                {
                    CheckEntry($"Maps/MapGeometry/Map22/Chunks/{theme}/{sc}");
                    CheckEntry($"Maps/MapGeometry/Map22/Chunks/{theme}/{sc}_{theme}");
                }
            }
            CheckEntry("Maps/MapGeometry/Map22/Chunks/BattleAcademia/Intor_BattleAcademia");

            // 6. TFT Augments Lattice (Maps/Shipping/Map22/Augments/Set{Set}/{AugmentName})
            for (int set = 10; set <= 18; set++)
            {
                foreach (string tok in (wordlist?.AllTokens ?? Enumerable.Empty<string>()).Take(150))
                {
                    string stem = wordlist.Case(tok);
                    CheckEntry($"Maps/Shipping/Map22/Augments/Set{set}/DA_{set}_{stem}");
                    CheckEntry($"Maps/Shipping/Map22/Augments/Set{set}/DA_{set}_{stem}Augment");
                    CheckEntry($"Maps/Shipping/Map22/Augments/Set{set}/DA_{set}_{stem}TraitAugment");
                }
            }

            // 7. Universal Animation Clips and Graph Structures (InternalHashKind.BinHashes)
            if (matcher.GetRemainingCount(InternalHashKind.BinHashes) > 0)
            {
                foreach (string clip in BinContentEvidenceSource.CommonAnimationClipNames)
                {
                    uint h = Fnv1a.HashLower(clip);
                    if (matcher.IsRemaining(InternalHashKind.BinHashes, h))
                    {
                        matcher.CheckResearchCandidate(
                            InternalHashKind.BinHashes,
                            clip,
                            InternalHashGuessStrategy.CrossDictionary,
                            "AnimationClipLattice",
                            InternalHashEvidence.BinObjectLatticePattern,
                            verified: true);
                    }
                }

                foreach (string track in BinContentEvidenceSource.CommonAnimationTracks)
                {
                    uint h = Fnv1a.HashLower(track);
                    if (matcher.IsRemaining(InternalHashKind.BinHashes, h))
                    {
                        matcher.CheckResearchCandidate(
                            InternalHashKind.BinHashes,
                            track,
                            InternalHashGuessStrategy.CrossDictionary,
                            "AnimationTrackLattice",
                            InternalHashEvidence.BinObjectLatticePattern,
                            verified: true);
                    }
                }

                foreach (string mask in BinContentEvidenceSource.CommonAnimationMasks)
                {
                    uint h = Fnv1a.HashLower(mask);
                    if (matcher.IsRemaining(InternalHashKind.BinHashes, h))
                    {
                        matcher.CheckResearchCandidate(
                            InternalHashKind.BinHashes,
                            mask,
                            InternalHashGuessStrategy.CrossDictionary,
                            "AnimationMaskLattice",
                            InternalHashEvidence.BinObjectLatticePattern,
                            verified: true);
                    }
                }

                foreach (string sync in BinContentEvidenceSource.CommonAnimationSyncGroups)
                {
                    uint h = Fnv1a.HashLower(sync);
                    if (matcher.IsRemaining(InternalHashKind.BinHashes, h))
                    {
                        matcher.CheckResearchCandidate(
                            InternalHashKind.BinHashes,
                            sync,
                            InternalHashGuessStrategy.CrossDictionary,
                            "AnimationSyncGroupLattice",
                            InternalHashEvidence.BinObjectLatticePattern,
                            verified: true);
                    }
                }
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
            string schema = string.Equals(domain, "bin", StringComparison.Ordinal) ? "4" : "1";
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

        internal sealed class TokenWordlist
        {
            public List<string> AllTokens { get; } = new();
            public Dictionary<string, int> TokenCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Attested casing per word: the spelling the known corpus uses most.
            public Dictionary<string, string> PreferredSpellings { get; } = new(StringComparer.OrdinalIgnoreCase);
            // Word pairs attested in known names, case-folded, sorted for determinism.
            public List<(string A, string B)> Bigrams { get; } = new();
            // Commonest final words of known names, for tail combination.
            public List<string> Suffixes { get; } = new();

            public List<string> TypeSuffixes { get; } = new();
            public List<string> FieldSuffixes { get; } = new();

            private readonly Dictionary<(string A, string B), int> _bigramCounts = new();
            private readonly Dictionary<string, Dictionary<string, int>> _spellings = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _suffixCounts = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _typeSuffixCounts = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _fieldSuffixCounts = new(StringComparer.OrdinalIgnoreCase);

            public void AddTypeName(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                AddName(name);
                string[] words = WordSplitter.Split(name).ToArray();
                if (words.Length > 0)
                {
                    string last = words[^1].ToLowerInvariant();
                    _typeSuffixCounts.TryGetValue(last, out int count);
                    _typeSuffixCounts[last] = count + 1;
                }
            }

            public void AddFieldName(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                AddName(name);
                string[] words = WordSplitter.Split(name).ToArray();
                if (words.Length > 0)
                {
                    string last = words[^1].ToLowerInvariant();
                    _fieldSuffixCounts.TryGetValue(last, out int count);
                    _fieldSuffixCounts[last] = count + 1;
                }
            }

            public void AddName(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;

                if (!name.Contains('/'))
                {
                    // Attested casing, bigrams and tails come from the standard splitter.
                    string[] words = WordSplitter.Split(name).ToArray();
                    if (words.Length > 0)
                    {
                        string last = words[^1].ToLowerInvariant();
                        _suffixCounts.TryGetValue(last, out int tailCount);
                        _suffixCounts[last] = tailCount + 1;
                    }
                    for (int i = 0; i < words.Length; i++)
                    {
                        string lower = words[i].ToLowerInvariant();
                        if (!_spellings.TryGetValue(lower, out Dictionary<string, int> spellings))
                        {
                            spellings = new Dictionary<string, int>(StringComparer.Ordinal);
                            _spellings[lower] = spellings;
                        }
                        spellings.TryGetValue(words[i], out int spellingCount);
                        spellings[words[i]] = spellingCount + 1;
                        if (i > 0)
                        {
                            string prev = words[i - 1].ToLowerInvariant();
                            _bigramCounts.TryGetValue((prev, lower), out int bigramCount);
                            _bigramCounts[(prev, lower)] = bigramCount + 1;
                        }
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

                PreferredSpellings.Clear();
                foreach (var pair in _spellings)
                    PreferredSpellings[pair.Key] = pair.Value.OrderByDescending(spelling => spelling.Value)
                        .ThenBy(spelling => spelling.Key, StringComparer.Ordinal).First().Key;

                Bigrams.Clear();
                Bigrams.AddRange(_bigramCounts.OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key.A, StringComparer.Ordinal)
                    .ThenBy(pair => pair.Key.B, StringComparer.Ordinal)
                    .Select(pair => pair.Key));

                Suffixes.Clear();
                Suffixes.AddRange(_suffixCounts.OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(25)
                    .Select(pair => pair.Key));

                TypeSuffixes.Clear();
                TypeSuffixes.AddRange(_typeSuffixCounts.OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(50)
                    .Select(pair => Case(pair.Key)));

                FieldSuffixes.Clear();
                FieldSuffixes.AddRange(_fieldSuffixCounts.OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(50)
                    .Select(pair => Case(pair.Key)));
            }

            // Attested spelling when the corpus knows one, else conventional upper-first casing.
            public string Case(string token)
            {
                if (token.Length == 0) return token;
                if (PreferredSpellings.TryGetValue(token, out string spelling)) return spelling;
                return char.ToUpperInvariant(token[0]) + token[1..];
            }
        }

        private static string UpperFirst(string value) =>
            string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
    }
}
