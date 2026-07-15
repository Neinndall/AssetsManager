using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

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
            _gameGuesser = new GameHashGuesser(_gameHashFile, _logService);
            _lcuGuesser = new LcuHashGuesser(_lcuHashFile, _logService);
        }

        public async Task<HashGuessRunResult> RunEmbeddedPathGrepAsync(
            HashGuessDomain domain,
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            await _hashResolverService.LoadAllHashesAsync();

            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");

            HashGuesser guesser = CreateWadGuesser(domain);
            string[] wadPaths = guesser.FindWads(rootDirectory);
            var inventory = await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken);
            var unknownHashes = inventory.All;

            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(domain, unknownHashes);
                int processedChunks = 0;

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

                            string resolvedChunkPath = _hashResolverService.ResolveHash(chunk.PathHash);
                            string chunkExt = Path.GetExtension(resolvedChunkPath).TrimStart('.').ToLowerInvariant();
                            if (guesser.ShouldSkip(chunkExt)) continue;

                            try
                            {
                                using var dataOwner = wad.LoadChunkDecompressed(chunk);
                                byte[] data = dataOwner.Span.ToArray();
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

                    progress?.Report(new HashGuessProgress
                    {
                        ProcessedWads = wadIndex + 1,
                        TotalWads = wadPaths.Length,
                        ProcessedChunks = processedChunks,
                        FoundMatches = engine.Matches.Count,
                        CurrentWad = Path.GetFileName(wadPath)
                    });
                }

                var resultMatches = engine.Matches.Values.OrderBy(match => match.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (resultMatches, processedChunks, engine.UnknownHashes);
            }, cancellationToken);

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

        private HashGuesser CreateWadGuesser(HashGuessDomain domain)
        {
            return domain switch
            {
                HashGuessDomain.Game => _gameGuesser,
                HashGuessDomain.Lcu => _lcuGuesser,
                _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "Unsupported WAD hash domain.")
            };
        }

        public async Task<HashGuessRunResult> RunCanonicalGuessingAsync(
            HashGuessDomain domain,
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            ISet<ulong> sessionResolved = null,
            HashUnknownInventory preparedInventory = null)
        {
            await _hashResolverService.LoadAllHashesAsync();
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");

            HashGuesser guesser = CreateWadGuesser(domain);
            HashGuesser otherGuesser = CreateWadGuesser(domain == HashGuessDomain.Game ? HashGuessDomain.Lcu : HashGuessDomain.Game);
            string[] wadPaths = guesser.FindWads(rootDirectory);
            var inventory = preparedInventory ?? await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken, sessionResolved as IReadOnlySet<ulong>);
            var unknownHashes = CreateSessionPending(inventory.All, sessionResolved);
            int unknownAtStart = unknownHashes.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(domain, unknownHashes);
                int checkedCandidates = 0;

                IEnumerable<HashGuessCandidate> candidates = domain == HashGuessDomain.Lcu
                    ? _lcuGuesser.GuessFromGameHashes(otherGuesser)
                    : _gameGuesser.GenerateCanonicalCandidates(otherGuesser);
                foreach (HashGuessCandidate candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(candidate.Path, candidate.Strategy, "Generated canonical pattern");
                    checkedCandidates++;
                    if (checkedCandidates % 1000 == 0)
                    {
                        progress?.Report(new HashGuessProgress
                        {
                            ProcessedWads = 0,
                            TotalWads = 0,
                            ProcessedChunks = checkedCandidates,
                            FoundMatches = engine.Matches.Count,
                            CurrentWad = "Generating canonical paths"
                        });
                    }
                    if (engine.RemainingUnknownCount == 0) break;
                }

                var resultMatches = engine.Matches.Values.OrderBy(match => match.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (resultMatches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken);

            var resultMatches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            if (preparedInventory == null || sessionResolved == null)
                await PersistGuessingRunAsync(domain, resultMatches, remainingUnknowns, CreateSessionPending(inventory.Current, sessionResolved), inventory.PatchFingerprint, cancellationToken);
            sessionResolved?.UnionWith(resultMatches.Select(match => match.Hash));
            _logService.LogSuccess($"Hash Lab {domain} canonical guessing completed: {resultMatches.Count} paths resolved from {unknownAtStart} unknown hashes.");
            return new HashGuessRunResult
            {
                Domain = domain,
                UnknownHashesAtStart = unknownAtStart,
                ScannedChunks = checkedCandidates,
                Matches = resultMatches
            };
        }

        public async Task<HashGuessRunResult> RunGameBasicGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            await _hashResolverService.LoadAllHashesAsync();
            var inventory = await PrepareInventoryAsync(HashGuessDomain.Game, rootDirectory, cancellationToken);
            var sessionResolved = new HashSet<ulong>();
            var results = new List<HashGuessRunResult>();
            results.Add(await RunCanonicalGuessingAsync(HashGuessDomain.Game, rootDirectory, progress, cancellationToken, sessionResolved, inventory));
            results.Add(await RunLanguageGuessingAsync(HashGuessDomain.Game, rootDirectory, progress, cancellationToken, sessionResolved, inventory));
            results.Add(await RunNumberGuessingAsync(HashGuessDomain.Game, rootDirectory, progress, cancellationToken, sessionResolved, inventory));
            results.Add(await RunGameCrossDomainGuessingAsync(rootDirectory, progress, cancellationToken, sessionResolved, inventory));

            var matches = results.SelectMany(result => result.Matches)
                .GroupBy(match => match.Hash)
                .Select(group => group.First())
                .OrderBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await PersistGuessingRunAsync(
                HashGuessDomain.Game,
                matches,
                CreateSessionPending(inventory.All, sessionResolved),
                CreateSessionPending(inventory.Current, sessionResolved),
                inventory.PatchFingerprint,
                cancellationToken);

            return new HashGuessRunResult
            {
                Domain = HashGuessDomain.Game,
                UnknownHashesAtStart = results.FirstOrDefault()?.UnknownHashesAtStart ?? 0,
                ScannedChunks = results.Sum(result => result.ScannedChunks),
                Matches = matches
            };
        }

        public async Task<HashGuessRunResult> RunLcuBasicGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            await _hashResolverService.LoadAllHashesAsync();
            var inventory = await PrepareInventoryAsync(HashGuessDomain.Lcu, rootDirectory, cancellationToken);
            var sessionResolved = new HashSet<ulong>();
            var results = new List<HashGuessRunResult>
            {
                await RunCanonicalGuessingAsync(HashGuessDomain.Lcu, rootDirectory, progress, cancellationToken, sessionResolved, inventory),
                await RunLanguageGuessingAsync(HashGuessDomain.Lcu, rootDirectory, progress, cancellationToken, sessionResolved, inventory),
                await RunNumberGuessingAsync(HashGuessDomain.Lcu, rootDirectory, progress, cancellationToken, sessionResolved, inventory),
                await RunLcuSupplementalGuessingAsync(rootDirectory, progress, cancellationToken, sessionResolved, inventory)
            };

            var matches = results.SelectMany(result => result.Matches).GroupBy(match => match.Hash)
                .Select(group => group.First()).OrderBy(match => match.Path, StringComparer.OrdinalIgnoreCase).ToList();
            await PersistGuessingRunAsync(
                HashGuessDomain.Lcu,
                matches,
                CreateSessionPending(inventory.All, sessionResolved),
                CreateSessionPending(inventory.Current, sessionResolved),
                inventory.PatchFingerprint,
                cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Lcu, UnknownHashesAtStart = results.FirstOrDefault()?.UnknownHashesAtStart ?? 0, ScannedChunks = results.Sum(result => result.ScannedChunks), Matches = matches };
        }

        public async Task<HashGuessRunResult> RunLcuAdvancedGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            string[] wads = _lcuGuesser.FindWads(rootDirectory);
            var inventory = await BuildUnknownInventoryAsync(HashGuessDomain.Lcu, wads, cancellationToken);
            var unknown = inventory.All;
            int initial = unknown.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Lcu, unknown);
                int checkedCandidates = _lcuGuesser.RunAdvancedAttacks(engine, progress, cancellationToken);

                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await PersistGuessingRunAsync(HashGuessDomain.Lcu, matches, remainingUnknowns, inventory.Current, inventory.PatchFingerprint, cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Lcu, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        private async Task<HashGuessRunResult> RunLcuSupplementalGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken, ISet<ulong> sessionResolved, HashUnknownInventory inventory)
        {
            var unknown = CreateSessionPending(inventory.All, sessionResolved);
            int initial = unknown.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Lcu, unknown);
                int checkedCandidates = 0;
                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(new HashGuessProgress
                    {
                        ProcessedChunks = checkedCandidates,
                        FoundMatches = engine.Matches.Count,
                        CurrentWad = "LCU Basic: basename substitution"
                    });
                    checkedCandidates += _lcuGuesser.SubstituteBasenames(engine, cancellationToken);
                }
                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(new HashGuessProgress
                    {
                        ProcessedChunks = checkedCandidates,
                        FoundMatches = engine.Matches.Count,
                        CurrentWad = "LCU Basic: basename word substitution"
                    });
                    checkedCandidates += _lcuGuesser.SubstituteBasenameWords(engine, cancellationToken);
                }

                var phases = new (string Name, IEnumerable<HashGuessCandidate> Candidates)[]
                {
                    ("plugin variants", _lcuGuesser.SubstitutePlugin()),
                    ("extension variants", _lcuGuesser.GenerateLcuExtensionCandidates(int.MaxValue)),
                    ("LCU patterns", _lcuGuesser.GuessPatterns())
                };

                foreach (var phase in phases)
                {
                    progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = $"LCU Basic: {phase.Name}" });
                    foreach (var candidate in phase.Candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        engine.Check(candidate.Path, candidate.Strategy, "LCU Basic");
                        checkedCandidates++;
                        if (checkedCandidates % 5000 == 0)
                            progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = $"LCU Basic: {phase.Name}" });
                        if (engine.RemainingUnknownCount == 0) break;
                    }

                    if (engine.RemainingUnknownCount == 0) break;
                }

                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates);
            }, cancellationToken);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            sessionResolved?.UnionWith(matches.Select(match => match.Hash));
            return new HashGuessRunResult { Domain = HashGuessDomain.Lcu, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        public async Task<HashGuessRunResult> RunGameExtendedGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            string[] wads = _gameGuesser.FindWads(rootDirectory);
            var inventory = await BuildUnknownInventoryAsync(HashGuessDomain.Game, wads, cancellationToken);
            var unknown = inventory.All;
            int initial = unknown.Count;
            var runResult = await Task.Run(async () =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Game, unknown);
                int checkedCandidates = await _gameGuesser.RunExtendedAttacksAsync(engine, rootDirectory, progress, cancellationToken);
                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await PersistGuessingRunAsync(HashGuessDomain.Game, matches, remainingUnknowns, inventory.Current, inventory.PatchFingerprint, cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Game, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        private async Task<HashGuessRunResult> RunGameCrossDomainGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken, ISet<ulong> sessionResolved, HashUnknownInventory inventory)
        {
            var unknown = CreateSessionPending(inventory.All, sessionResolved);
            int initial = unknown.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Game, unknown);
                int candidates = _gameGuesser.RunCrossDomainAttacks(engine, _lcuGuesser, cancellationToken);

                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, candidates);
            }, cancellationToken);

            var matches = runResult.Item1;
            int candidates = runResult.Item2;
            sessionResolved?.UnionWith(matches.Select(match => match.Hash));
            return new HashGuessRunResult { Domain = HashGuessDomain.Game, UnknownHashesAtStart = initial, ScannedChunks = candidates, Matches = matches };
        }

        public async Task<HashGuessRunResult> RunLanguageGuessingAsync(
            HashGuessDomain domain,
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            ISet<ulong> sessionResolved = null,
            HashUnknownInventory preparedInventory = null)
        {
            await _hashResolverService.LoadAllHashesAsync();
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");

            HashGuesser guesser = CreateWadGuesser(domain);
            string[] wadPaths = guesser.FindWads(rootDirectory);
            var inventory = preparedInventory ?? await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken, sessionResolved as IReadOnlySet<ulong>);
            var unknownHashes = CreateSessionPending(inventory.All, sessionResolved);
            int unknownAtStart = unknownHashes.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(domain, unknownHashes);
                int checkedCandidates = 0;

                IEnumerable<HashGuessCandidate> candidates = domain == HashGuessDomain.Lcu
                    ? _lcuGuesser.SubstituteRegionLang()
                    : _gameGuesser.SubstituteLang();
                foreach (HashGuessCandidate candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(candidate.Path, candidate.Strategy, "Generated locale or region variant");
                    checkedCandidates++;
                    if (checkedCandidates % 5000 == 0)
                    {
                        progress?.Report(new HashGuessProgress
                        {
                            ProcessedChunks = checkedCandidates,
                            FoundMatches = engine.Matches.Count,
                            CurrentWad = "Generating locale and region variants"
                        });
                    }
                    if (engine.RemainingUnknownCount == 0) break;
                }

                var resultMatches = engine.Matches.Values.OrderBy(match => match.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (resultMatches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken);

            var resultMatches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            if (preparedInventory == null || sessionResolved == null)
                await PersistGuessingRunAsync(domain, resultMatches, remainingUnknowns, CreateSessionPending(inventory.Current, sessionResolved), inventory.PatchFingerprint, cancellationToken);
            sessionResolved?.UnionWith(resultMatches.Select(match => match.Hash));
            _logService.LogSuccess($"Hash Lab {domain} language guessing completed: {resultMatches.Count} paths resolved from {unknownAtStart} unknown hashes.");
            return new HashGuessRunResult
            {
                Domain = domain,
                UnknownHashesAtStart = unknownAtStart,
                ScannedChunks = checkedCandidates,
                Matches = resultMatches
            };
        }

        public async Task<HashGuessRunResult> RunNumberGuessingAsync(
            HashGuessDomain domain,
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            ISet<ulong> sessionResolved = null,
            HashUnknownInventory preparedInventory = null)
        {
            await _hashResolverService.LoadAllHashesAsync();
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");

            HashGuesser guesser = CreateWadGuesser(domain);
            string[] wadPaths = guesser.FindWads(rootDirectory);
            var inventory = preparedInventory ?? await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken, sessionResolved as IReadOnlySet<ulong>);
            var unknownHashes = CreateSessionPending(inventory.All, sessionResolved);
            int unknownAtStart = unknownHashes.Count;
            int numberLimit = domain == HashGuessDomain.Game ? 100 : 10_000;
            const int candidateBudget = int.MaxValue;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(domain, unknownHashes);
                int checkedCandidates = 0;

                IEnumerable<HashGuessCandidate> candidates = domain == HashGuessDomain.Lcu
                    ? _lcuGuesser.SubstituteNumbers(numberLimit)
                    : _gameGuesser.SubstituteNumbers(numberLimit);
                foreach (HashGuessCandidate candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(candidate.Path, candidate.Strategy, "Generated numeric variant");
                    checkedCandidates++;
                    if (checkedCandidates % 5000 == 0)
                    {
                        progress?.Report(new HashGuessProgress
                        {
                            ProcessedChunks = checkedCandidates,
                            FoundMatches = engine.Matches.Count,
                            CurrentWad = $"Generating numeric variants ({checkedCandidates:N0}/{candidateBudget:N0})"
                        });
                    }
                    if (engine.RemainingUnknownCount == 0) break;
                }

                var resultMatches = engine.Matches.Values.OrderBy(match => match.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (resultMatches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken);

            var resultMatches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            if (preparedInventory == null || sessionResolved == null)
                await PersistGuessingRunAsync(domain, resultMatches, remainingUnknowns, CreateSessionPending(inventory.Current, sessionResolved), inventory.PatchFingerprint, cancellationToken);
            sessionResolved?.UnionWith(resultMatches.Select(match => match.Hash));
            _logService.LogSuccess($"Hash Lab {domain} number guessing completed: {resultMatches.Count} paths resolved from {unknownAtStart} unknown hashes.");
            return new HashGuessRunResult
            {
                Domain = domain,
                UnknownHashesAtStart = unknownAtStart,
                ScannedChunks = checkedCandidates,
                Matches = resultMatches
            };
        }

        private Task<HashUnknownInventory> PrepareInventoryAsync(HashGuessDomain domain, string rootDirectory, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");
            HashGuesser guesser = CreateWadGuesser(domain);
            string[] wadPaths = guesser.FindWads(rootDirectory);
            return BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken);
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
