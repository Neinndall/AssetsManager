using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using LeagueToolkit.Utils;

namespace AssetsManager.Services.Hashes
{
    public class HashGuessingService
    {
        private readonly HashResolverService _hashResolverService;
        private readonly HashGuessingStore _store;
        private readonly HashGuessPersistenceService _persistence;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly HashFile _gameHashFile;
        private readonly HashFile _lcuHashFile;
        private readonly HashFile _binEntriesHashFile;
        private readonly GameHashGuesser _gameGuesser;
        private readonly LcuHashGuesser _lcuGuesser;

        public HashGuessingService(
            HashResolverService hashResolverService,
            HashGuessingStore store,
            HashGuessPersistenceService persistence,
            LogService logService,
            DirectoriesCreator directoriesCreator)
        {
            _hashResolverService = hashResolverService;
            _store = store;
            _persistence = persistence;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _gameHashFile = new HashFile(HashGuessDomain.Game, Path.Combine(_directoriesCreator.HashesPath, "hashes.game.txt"));
            _lcuHashFile = new HashFile(HashGuessDomain.Lcu, Path.Combine(_directoriesCreator.HashesPath, "hashes.lcu.txt"));
            _binEntriesHashFile = new HashFile(HashGuessDomain.Game, Path.Combine(_directoriesCreator.HashesPath, "hashes.binentries.txt"));
            _gameGuesser = new GameHashGuesser(_gameHashFile, _logService);
            _lcuGuesser = new LcuHashGuesser(_lcuHashFile, _logService);
        }

        public async Task<HashGuessRunResult> RunEmbeddedPathGrepAsync(
            HashGuessDomain domain,
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IProgress<HashGuessMatch> matchProgress = null)
        {
            await _hashResolverService.LoadAllHashesAsync();

            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");

            HashGuesser guesser = CreateWadGuesser(domain);
            string[] wadPaths = guesser.FindWads(rootDirectory);
            var inventory = await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken);
            var unknownHashes = inventory.All;

            HashGuessEngine engine = null;
            var runResult = await RunWithCancellationPersistenceAsync(() => Task.Run(() =>
            {
                Action<HashGuessMatch> reportMatch =
                    matchProgress is null ? null : matchProgress.Report;
                engine = new HashGuessEngine(domain, unknownHashes, reportMatch);
                int processedChunks = 0;
                var inferredExtensions = new Dictionary<ulong, string>();

                progress?.Report(engine.CreateProgress("Catalog ready, starting scan...", 0, 0, wadPaths.Length));

                for (int wadIndex = 0; wadIndex < wadPaths.Length; wadIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string wadPath = wadPaths[wadIndex];

                    try
                    {
                        using var wad = new WadFile(wadPath);
                        foreach (var chunk in wad.Chunks.Values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            processedChunks++;

                            if (processedChunks % 100 == 0)
                            {
                                progress?.Report(engine.CreateProgress(
                                    Path.GetFileName(wadPath),
                                    processedChunks,
                                    wadIndex,
                                    wadPaths.Length));
                            }

                            // CDTB type 2 entries are redirects and contain no searchable file payload.
                            if (chunk.Compression == WadChunkCompression.Satellite) continue;

                            string resolvedChunkPath = _hashResolverService.ResolveHash(chunk.PathHash);
                            string chunkExt = Path.GetExtension(resolvedChunkPath).TrimStart('.').ToLowerInvariant();
                            if (chunkExt.Length == 0 && inferredExtensions.TryGetValue(chunk.PathHash, out string cachedExtension))
                                chunkExt = cachedExtension;
                            if (guesser.ShouldSkip(chunkExt)) continue;

                            try
                            {
                                using var dataOwner = wad.LoadChunkDecompressed(chunk);
                                ArraySegment<byte> data = dataOwner.DangerousGetArray();
                                if (chunkExt.Length == 0)
                                {
                                    chunkExt = InferChunkExtension(data, domain == HashGuessDomain.Lcu);
                                    inferredExtensions[chunk.PathHash] = chunkExt;
                                    if (guesser.ShouldSkip(chunkExt)) continue;
                                    if (chunkExt.Length > 0) resolvedChunkPath += "." + chunkExt;
                                }
                                guesser.GrepWad(engine, data, resolvedChunkPath, wadPath, chunk.PathHash);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logService.LogDebug($"Hash Lab skipped unreadable chunk {chunk.PathHash:x16} in {Path.GetFileName(wadPath)}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogError(ex, $"Hash Lab could not read WAD '{wadPath}'.");
                    }

                    progress?.Report(engine.CreateProgress(
                        Path.GetFileName(wadPath),
                        processedChunks,
                        wadIndex + 1,
                        wadPaths.Length));
                }

                var resultMatches = engine.Matches.Values.OrderBy(match => match.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (resultMatches, processedChunks, engine.UnknownHashes);
            }, cancellationToken), () => engine, domain, inventory);

            var resultMatches = runResult.Item1;
            int processedChunks = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await PersistGuessingRunAsync(domain, resultMatches, remainingUnknowns, inventory.Current, inventory.PatchFingerprint, cancellationToken);

            _logService.LogSuccess($"Hash Lab {domain} GREP completed: {resultMatches.Count} paths resolved from {unknownHashes.Count + resultMatches.Count} unknown hashes.");
            return new HashGuessRunResult
            {
                Domain = domain,
                UnknownHashesAtStart = unknownHashes.Count + resultMatches.Count,
                ScannedChunks = processedChunks,
                Matches = resultMatches
            };
        }

        internal static string InferChunkExtension(ArraySegment<byte> data, bool detectJson)
        {
            ReadOnlySpan<byte> bytes = data.AsSpan();
            string extension = LeagueFile.GetExtension(LeagueFile.GetFileType(bytes));
            if (extension.Length > 0 || !detectJson) return extension;

            int offset = 0;
            while (offset < bytes.Length && bytes[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') offset++;
            if (offset >= bytes.Length || bytes[offset] is not ((byte)'{' or (byte)'[')) return string.Empty;
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    new ReadOnlyMemory<byte>(data.Array, data.Offset, data.Count));
                return "json";
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        public async Task SaveMatchesAsync(IEnumerable<HashGuessMatch> matches, CancellationToken cancellationToken)
        {
            await _persistence.PromotePathMatchesAsync(matches, cancellationToken);
            _gameHashFile.Invalidate();
            _lcuHashFile.Invalidate();
            await _hashResolverService.ForceReloadHashesAsync();
        }

        private async Task PersistGuessingRunAsync(
            HashGuessDomain domain,
            IReadOnlyCollection<HashGuessMatch> matches,
            IEnumerable<ulong> remainingUnknowns,
            IReadOnlySet<ulong> currentHashes,
            string patchFingerprint,
            CancellationToken cancellationToken)
        {
            await _persistence.CommitPathRunAsync(domain, matches, remainingUnknowns, currentHashes, patchFingerprint, cancellationToken);
        }

        private async Task<T> RunWithCancellationPersistenceAsync<T>(
            Func<Task<T>> run,
            Func<HashGuessEngine> getEngine,
            HashGuessDomain domain,
            HashUnknownInventory inventory,
            ISet<ulong> sessionResolved = null)
        {
            try
            {
                return await run();
            }
            catch (OperationCanceledException)
            {
                HashGuessEngine engine = getEngine();
                if (engine != null)
                {
                    var matches = engine.Matches.Values.ToList();
                    if (sessionResolved != null)
                    {
                        sessionResolved.UnionWith(matches.Select(match => match.Hash));
                    }
                    else
                    {
                        await PersistGuessingRunAsync(
                            domain,
                            matches,
                            engine.UnknownHashes,
                            inventory.Current,
                            inventory.PatchFingerprint,
                            CancellationToken.None);
                    }
                    if (matches.Count > 0)
                        await SaveMatchesAsync(matches, CancellationToken.None);
                }
                throw;
            }
        }

        private HashGuesser CreateWadGuesser(HashGuessDomain domain)
        {
            return domain switch
            {
                HashGuessDomain.Game => _gameGuesser,
                HashGuessDomain.Lcu => _lcuGuesser,
                _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "Unsupported WAD hash domain.")
            };
        }

        public async Task<HashGuessRunResult> RunGameBasicGuessingAsync(
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IProgress<HashGuessMatch> matchProgress = null)
        {
            await _hashResolverService.LoadAllHashesAsync();
            var inventory = await LoadPersistedInventoryAsync(HashGuessDomain.Game, rootDirectory, cancellationToken);
            return await RunGameBasicMethodsGuessingAsync(
                progress,
                cancellationToken,
                null,
                inventory,
                matchProgress);
        }

        private async Task<HashGuessRunResult> RunGameBasicMethodsGuessingAsync(
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            ISet<ulong> sessionResolved,
            HashUnknownInventory inventory,
            IProgress<HashGuessMatch> matchProgress)
        {
            var unknown = CreateSessionPending(inventory.All, sessionResolved);
            int initial = unknown.Count;
            HashGuessEngine engine = null;
            var runResult = await RunWithCancellationPersistenceAsync(() => Task.Run(() =>
            {
                Action<HashGuessMatch> reportMatch = matchProgress is null ? null : matchProgress.Report;
                engine = new HashGuessEngine(HashGuessDomain.Game, unknown, reportMatch);
                int checkedCandidates = 0;

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("GAME Basic: GAME hash cross-domain", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _gameGuesser.GuessFromLcuHashes(
                        engine,
                        _lcuGuesser,
                        cancellationToken,
                        progress: count => progress?.Report(engine.CreateProgress("GAME Basic: GAME hash cross-domain", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("GAME Basic: character files", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _gameGuesser.GuessCharacterFiles(
                        engine,
                        cancellationToken,
                        progress: count => progress?.Report(engine.CreateProgress("GAME Basic: character files", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("GAME Basic: shader variants", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _gameGuesser.GuessShaderVariants(
                        engine,
                        cancellationToken,
                        progress: count => progress?.Report(engine.CreateProgress("GAME Basic: shader variants", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("GAME Basic: locale variants", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _gameGuesser.SubstituteLang(
                        engine,
                        cancellationToken,
                        progress: count => progress?.Report(engine.CreateProgress("GAME Basic: locale variants", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("GAME Basic: extension substitution", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _gameGuesser.SubstituteExtensions(
                        engine,
                        cancellationToken,
                        candidateBudget: int.MaxValue,
                        source: "GAME Basic: extension substitution",
                        progress: count => progress?.Report(engine.CreateProgress("GAME Basic: extension substitution", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("GAME Basic: basename prefixes", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _gameGuesser.CheckBasenamePrefixes(
                        engine,
                        cancellationToken,
                        progress: count => progress?.Report(engine.CreateProgress("GAME Basic: basename prefixes", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("GAME Basic: numeric variants", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _gameGuesser.SubstituteNumbers(
                        engine,
                        cancellationToken,
                        maximum: 200,
                        progress: count => progress?.Report(engine.CreateProgress("GAME Basic: numeric variants", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("GAME Basic: padded numeric variants", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _gameGuesser.SubstituteNumbers(
                        engine,
                        cancellationToken,
                        maximum: 200,
                        digits: 2,
                        progress: count => progress?.Report(
                            engine.CreateProgress("GAME Basic: padded numeric variants", progressOffset + count)));
                }

                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken), () => engine, HashGuessDomain.Game, inventory, sessionResolved);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await PersistGuessingRunAsync(
                HashGuessDomain.Game,
                matches,
                remainingUnknowns,
                inventory.Current,
                inventory.PatchFingerprint,
                cancellationToken);
            sessionResolved?.UnionWith(matches.Select(match => match.Hash));
            return new HashGuessRunResult
            {
                Domain = HashGuessDomain.Game,
                UnknownHashesAtStart = initial,
                ScannedChunks = checkedCandidates,
                Matches = matches
            };
        }

        public async Task<HashGuessRunResult> RunLcuBasicGuessingAsync(
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IProgress<HashGuessMatch> matchProgress = null)
        {
            await _hashResolverService.LoadAllHashesAsync();
            var inventory = await LoadPersistedInventoryAsync(HashGuessDomain.Lcu, rootDirectory, cancellationToken);
            return await RunLcuBasicMethodsGuessingAsync(
                progress,
                cancellationToken,
                inventory,
                matchProgress);
        }

        private async Task<HashGuessRunResult> RunLcuBasicMethodsGuessingAsync(
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            HashUnknownInventory inventory,
            IProgress<HashGuessMatch> matchProgress)
        {
            var unknown = inventory.All;
            int initial = unknown.Count;
            HashGuessEngine engine = null;
            var runResult = await RunWithCancellationPersistenceAsync(() => Task.Run(() =>
            {
                Action<HashGuessMatch> reportMatch = matchProgress is null ? null : matchProgress.Report;
                engine = new HashGuessEngine(HashGuessDomain.Lcu, unknown, reportMatch);
                int checkedCandidates = 0;

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("LCU Basic: extension variants", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _lcuGuesser.SubstituteExtensions(
                        engine,
                        cancellationToken,
                        candidateBudget: int.MaxValue,
                        source: "LCU Basic: extension substitution",
                        progress: count => progress?.Report(
                            engine.CreateProgress("LCU Basic: extension variants", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("LCU Basic: patterns", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _lcuGuesser.GuessPatterns(
                        engine,
                        cancellationToken,
                        candidateBudget: int.MaxValue,
                        progress: count => progress?.Report(
                            engine.CreateProgress("LCU Basic: patterns", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("LCU Basic: GAME hash cross-domain", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _lcuGuesser.GuessFromGameHashes(
                        engine,
                        _gameGuesser,
                        cancellationToken,
                        progress: count => progress?.Report(
                            engine.CreateProgress("LCU Basic: GAME hash cross-domain", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("LCU Basic: plugin variants", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _lcuGuesser.SubstitutePlugin(
                        engine,
                        cancellationToken,
                        candidateBudget: int.MaxValue,
                        progress: count => progress?.Report(
                            engine.CreateProgress("LCU Basic: plugin variants", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("LCU Basic: numeric variants", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _lcuGuesser.SubstituteNumbers(
                        engine,
                        cancellationToken,
                        maximum: 10_000,
                        progress: count => progress?.Report(
                            engine.CreateProgress("LCU Basic: numeric variants", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("LCU Basic: region and locale variants", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _lcuGuesser.SubstituteRegionLang(
                        engine,
                        cancellationToken,
                        progress: count => progress?.Report(
                            engine.CreateProgress("LCU Basic: region and locale variants", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("LCU Basic: basename substitution", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _lcuGuesser.SubstituteBasenames(
                        engine,
                        cancellationToken,
                        candidateBudget: 10_000_000,
                        progress: count => progress?.Report(
                            engine.CreateProgress("LCU Basic: basename substitution", progressOffset + count)));
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("LCU Basic: basename word substitution", checkedCandidates));
                    int progressOffset = checkedCandidates;
                    checkedCandidates += _lcuGuesser.SubstituteBasenameWords(
                        engine,
                        cancellationToken,
                        candidateBudget: 10_000_000,
                        progress: count => progress?.Report(
                            engine.CreateProgress("LCU Basic: basename word substitution", progressOffset + count)));
                }

                var matches = engine.Matches.Values
                    .OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken), () => engine, HashGuessDomain.Lcu, inventory);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await PersistGuessingRunAsync(
                HashGuessDomain.Lcu,
                matches,
                remainingUnknowns,
                inventory.Current,
                inventory.PatchFingerprint,
                cancellationToken);
            return new HashGuessRunResult
            {
                Domain = HashGuessDomain.Lcu,
                UnknownHashesAtStart = initial,
                ScannedChunks = checkedCandidates,
                Matches = matches
            };
        }

        public async Task<HashGuessRunResult> RunLcuExtendedGuessingAsync(
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IProgress<HashGuessMatch> matchProgress = null)
        {
            var inventory = await LoadPersistedInventoryAsync(HashGuessDomain.Lcu, rootDirectory, cancellationToken);
            var unknown = inventory.All;
            int initial = unknown.Count;
            HashGuessEngine engine = null;
            var runResult = await RunWithCancellationPersistenceAsync(() => Task.Run(() =>
            {
                Action<HashGuessMatch> reportMatch = matchProgress is null ? null : matchProgress.Report;
                engine = new HashGuessEngine(HashGuessDomain.Lcu, unknown, reportMatch);
                progress?.Report(engine.CreateProgress("LCU Extended: basename word addition", 0));
                int checkedCandidates = _lcuGuesser.AddBasenameWord(
                    engine,
                    cancellationToken,
                    count => progress?.Report(
                        engine.CreateProgress("LCU Extended: basename word addition", count)));
                progress?.Report(engine.CreateProgress("LCU Extended: basename word addition", checkedCandidates));

                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken), () => engine, HashGuessDomain.Lcu, inventory);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await PersistGuessingRunAsync(HashGuessDomain.Lcu, matches, remainingUnknowns, inventory.Current, inventory.PatchFingerprint, cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Lcu, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        public async Task<HashGuessRunResult> RunLcuCustomGuessingAsync(
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IProgress<HashGuessMatch> matchProgress = null)
        {
            var inventory = await LoadPersistedInventoryAsync(HashGuessDomain.Lcu, rootDirectory, cancellationToken);
            var unknown = inventory.All;
            int initial = unknown.Count;
            HashGuessEngine engine = null;
            var runResult = await RunWithCancellationPersistenceAsync(() => Task.Run(() =>
            {
                Action<HashGuessMatch> reportMatch = matchProgress is null ? null : matchProgress.Report;
                engine = new HashGuessEngine(HashGuessDomain.Lcu, unknown, reportMatch);
                int checkedCandidates = _lcuGuesser.RunCustomAttacks(engine, progress, cancellationToken);
                var matches = engine.Matches.Values
                    .OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken), () => engine, HashGuessDomain.Lcu, inventory);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await PersistGuessingRunAsync(
                HashGuessDomain.Lcu,
                matches,
                remainingUnknowns,
                inventory.Current,
                inventory.PatchFingerprint,
                cancellationToken);
            return new HashGuessRunResult
            {
                Domain = HashGuessDomain.Lcu,
                UnknownHashesAtStart = initial,
                ScannedChunks = checkedCandidates,
                Matches = matches
            };
        }

        public async Task<HashGuessRunResult> RunLcuV1PathGuessingAsync(
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IProgress<HashGuessMatch> matchProgress = null)
        {
            await _hashResolverService.LoadAllHashesAsync();
            var inventory = await LoadPersistedInventoryAsync(HashGuessDomain.Lcu, rootDirectory, cancellationToken);
            var unknown = inventory.All;
            int initial = unknown.Count;
            HashGuessEngine engine = null;
            var runResult = await RunWithCancellationPersistenceAsync(() => Task.Run(() =>
            {
                Action<HashGuessMatch> reportMatch = matchProgress is null ? null : matchProgress.Report;
                engine = new HashGuessEngine(HashGuessDomain.Lcu, unknown, reportMatch);
                int checkedCandidates = _lcuGuesser.RunV1PathPatterns(engine, progress, cancellationToken);
                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken), () => engine, HashGuessDomain.Lcu, inventory);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await PersistGuessingRunAsync(HashGuessDomain.Lcu, matches, remainingUnknowns, inventory.Current, inventory.PatchFingerprint, cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Lcu, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        public async Task<HashGuessRunResult> RunGameExtendedGuessingAsync(
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IProgress<HashGuessMatch> matchProgress = null)
        {
            var inventory = await LoadPersistedInventoryAsync(HashGuessDomain.Game, rootDirectory, cancellationToken);
            var unknown = inventory.All;
            int initial = unknown.Count;
            HashGuessEngine engine = null;
            var runResult = await RunWithCancellationPersistenceAsync(() => Task.Run(async () =>
            {
                Action<HashGuessMatch> reportMatch = matchProgress is null ? null : matchProgress.Report;
                engine = new HashGuessEngine(HashGuessDomain.Game, unknown, reportMatch);
                int checkedCandidates = await _gameGuesser.RunExtendedAttacksAsync(
                    engine, rootDirectory, progress, cancellationToken);
                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken), () => engine, HashGuessDomain.Game, inventory);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await PersistGuessingRunAsync(HashGuessDomain.Game, matches, remainingUnknowns, inventory.Current, inventory.PatchFingerprint, cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Game, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        public async Task<HashGuessRunResult> RunGameCustomGuessingAsync(
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IProgress<HashGuessMatch> matchProgress = null)
        {
            var inventory = await LoadPersistedInventoryAsync(HashGuessDomain.Game, rootDirectory, cancellationToken);
            var unknown = inventory.All;
            int initial = unknown.Count;
            HashGuessEngine engine = null;
            var runResult = await RunWithCancellationPersistenceAsync(() => Task.Run(() =>
            {
                Action<HashGuessMatch> reportMatch = matchProgress is null ? null : matchProgress.Report;
                engine = new HashGuessEngine(HashGuessDomain.Game, unknown, reportMatch);
                int checkedCandidates = _gameGuesser.RunCustomAttacks(engine, progress, cancellationToken);
                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken), () => engine, HashGuessDomain.Game, inventory);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await PersistGuessingRunAsync(HashGuessDomain.Game, matches, remainingUnknowns, inventory.Current, inventory.PatchFingerprint, cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Game, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        private async Task<HashUnknownInventory> LoadPersistedInventoryAsync(
            HashGuessDomain domain,
            string rootDirectory,
            CancellationToken cancellationToken,
            IReadOnlySet<ulong> sessionResolved = null)
        {
            HashUnknownInventory inventory = await _store.LoadUnknownInventoryAsync(domain, cancellationToken);
            if (inventory == null)
                throw new InvalidOperationException($"Run {domain} WAD Path Grep first to build the unknown hash inventory.");

            // Verify if the game patch has changed since the inventory was built
            HashGuesser guesser = CreateWadGuesser(domain);
            string[] wadPaths = guesser.FindWads(rootDirectory);
            HashWadInventory wadInventory = await Task.Run(() => guesser.FromWads(wadPaths, cancellationToken), cancellationToken);
            string currentFingerprint = $"{domain}:{wadInventory.ChunkCount}:{wadInventory.HashXor:x16}:{wadInventory.HashSum:x16}";

            if (currentFingerprint != inventory.PatchFingerprint)
            {
                throw new InvalidOperationException($"Game patch changed. Run {domain} WAD Path Grep first to rebuild the unknown inventory.");
            }

            inventory.All.RemoveWhere(hash => _hashResolverService.IsKnownHash(hash));
            inventory.Current.RemoveWhere(hash => _hashResolverService.IsKnownHash(hash));
            if (sessionResolved != null)
            {
                inventory.All.ExceptWith(sessionResolved);
                inventory.Current.ExceptWith(sessionResolved);
            }
            return inventory;
        }

        private static HashSet<ulong> CreateSessionPending(IEnumerable<ulong> hashes, ISet<ulong> sessionResolved)
        {
            var pending = hashes.ToHashSet();
            if (sessionResolved != null)
                pending.ExceptWith(sessionResolved);
            return pending;
        }

        private async Task<HashUnknownInventory> BuildUnknownInventoryAsync(
            HashGuessDomain domain,
            IEnumerable<string> wadPaths,
            CancellationToken cancellationToken,
            IReadOnlySet<ulong> sessionResolved = null)
        {
            HashGuesser guesser = CreateWadGuesser(domain);
            var pending = await _store.LoadUnknownHashesAsync(domain, cancellationToken);
            pending.RemoveWhere(hash => _hashResolverService.IsKnownHash(hash));
            HashWadInventory wadInventory = await Task.Run(
                () => guesser.FromWads(wadPaths, cancellationToken,
                    (wadPath, exception) => _logService.LogError(exception, $"Hash Lab could not build inventory from WAD '{wadPath}'.")),
                cancellationToken);
            var current = wadInventory.Hashes.Where(hash => !_hashResolverService.IsKnownHash(hash)).ToHashSet();

            pending.UnionWith(current);
            if (sessionResolved != null)
            {
                pending.ExceptWith(sessionResolved);
                current.ExceptWith(sessionResolved);
            }

            string fingerprint = $"{domain}:{wadInventory.ChunkCount}:{wadInventory.HashXor:x16}:{wadInventory.HashSum:x16}";
            return new HashUnknownInventory { All = pending, Current = current, PatchFingerprint = fingerprint };
        }

        public Task<HashUnknownSummary> GetUnknownSummaryAsync(HashGuessDomain domain, CancellationToken cancellationToken)
        {
            return _store.LoadUnknownSummaryAsync(domain, cancellationToken);
        }

    }
}
