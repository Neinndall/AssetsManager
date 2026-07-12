using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes
{
    public class HashGuessingService
    {
        private const int MaximumGrepChunkSize = 16 * 1024 * 1024;

        private static readonly Regex GamePathRegex = new(
            @"(?:assets|common|data|data_soon|gameplay|global|levels|loadouts|ux|uiautoatlas|characters|shaders|maps|clientstates|patching)/[0-9a-z_./ -]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LcuPathRegex = new(
            @"plugins/[0-9a-z_./@-]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LcuFrontendPathRegex = new(@"\bfe/([^/]+)/([a-zA-Z0-9/_.@-]+)", RegexOptions.Compiled);
        private static readonly Regex LcuDataPathRegex = new(@"/DATA/([a-zA-Z0-9/_.@-]+)", RegexOptions.Compiled);
        private static readonly Regex LcuAssetPathRegex = new(@"\blol-game-data/assets/([a-zA-Z0-9/_.@-]+)", RegexOptions.Compiled);
        private static readonly Regex LcuRelativePathRegex = new(@"[^a-zA-Z0-9/_.\\-]((?:\.|\.\.)/[a-zA-Z0-9/_.-]+)", RegexOptions.Compiled);
        private static readonly Regex LcuFileNameRegex = new("[\\\"']([a-zA-Z0-9][a-zA-Z0-9/_.@-]*\\.(?:js|json|webm|html|[a-z]{3}))\\b", RegexOptions.Compiled);
        private static readonly Regex LcuCssUrlRegex = new("url\\(\\s*[\\\"']?([^\\\"')?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LcuHtmlAssetRegex = new("(?:src|href|poster|data-src)\\s*=\\s*[\\\"']([^\\\"'?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PreloadNameRegex = new("Name=\\\"([^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly Regex ShaderIncludeRegex = new("#include \\\"([^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly Regex LocaleRegex = new(@"(?<![a-z])(?:ar_ae|ar_eg|cs_cz|de_de|el_gr|en_au|en_gb|en_ph|en_pl|en_sg|en_us|es_ar|es_es|es_mx|fr_fr|hu_hu|id_id|it_it|ja_jp|ko_kr|ms_my|pl_pl|pt_br|ro_ro|ru_ru|th_th|tr_tr|vi_vn|vn_vn|zh_cn|zh_my|zh_tw)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BasenameNumberRegex = new(@"[0-9]+(?=[^/]*\.[^/]+$)", RegexOptions.Compiled);
        private static readonly Regex LcuNumberExcludedPathRegex = new(@"(?:^(?:plugins/rcp-be-lol-game-data/[^/]+/[^/]+/v1/champion-|plugins/rcp-be-lol-game-data/global/default/(?:data|assets)/characters/|plugins/rcp-be-lol-game-data/global/default/data/items/icons2d/\d+_|plugins/rcp-be-lol-game-data/[^/]+/[^/]+/v1/champions/-1\.json)|/[0-9a-f]{32}\.)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LcuWordlistExcludedPathRegex = new(@"(?:^plugins/rcp-be-lol-game-data/global/default/data/characters/|/[0-9a-f]{32}\.)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly byte[][] GameBinPrefixesA = ToAsciiPrefixes("ASSETS/");
        private static readonly byte[][] GameBinPrefixesC = ToAsciiPrefixes("COMMON/", "CHARACTERS/", "CLIENTSTATES/");
        private static readonly byte[][] GameBinPrefixesD = ToAsciiPrefixes("DATA/", "DATA_SOON/");
        private static readonly byte[][] GameBinPrefixesG = ToAsciiPrefixes("GAMEPLAY/", "GLOBAL/");
        private static readonly byte[][] GameBinPrefixesL = ToAsciiPrefixes("LEVELS/", "LOADOUTS/");
        private static readonly byte[][] GameBinPrefixesM = ToAsciiPrefixes("MAPS/");
        private static readonly byte[][] GameBinPrefixesP = ToAsciiPrefixes("PATCHING/");
        private static readonly byte[][] GameBinPrefixesS = ToAsciiPrefixes("SHADERS/");
        private static readonly byte[][] GameBinPrefixesU = ToAsciiPrefixes("UX/", "UIAUTOATLAS/");
        private static readonly string[] Locales = { "ar_ae", "ar_eg", "cs_cz", "de_de", "el_gr", "en_au", "en_gb", "en_ph", "en_pl", "en_sg", "en_us", "es_ar", "es_es", "es_mx", "fr_fr", "hu_hu", "id_id", "it_it", "ja_jp", "ko_kr", "ms_my", "pl_pl", "pt_br", "ro_ro", "ru_ru", "th_th", "tr_tr", "vi_vn", "vn_vn", "zh_cn", "zh_my", "zh_tw" };
        private static readonly string[] Regions = { "br", "cn", "eun", "eune", "euw", "garena2", "garena3", "id", "jp", "kr", "la", "la1", "la2", "lan", "las", "na", "oc", "oc1", "oce", "pbe", "ph", "ph2", "ru", "sg", "tencent", "th", "th2", "tr", "tw", "tw2", "vn", "vn2", "global" };

        private readonly HashResolverService _hashResolverService;
        private readonly HashGuessingStore _store;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly HttpClient _httpClient;

        public HashGuessingService(
            HashResolverService hashResolverService,
            HashGuessingStore store,
            LogService logService,
            DirectoriesCreator directoriesCreator,
            HttpClient httpClient)
        {
            _hashResolverService = hashResolverService;
            _store = store;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _httpClient = httpClient;
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

            string pattern = domain == HashGuessDomain.Game ? "*.wad.client" : "*.wad";
            string[] wadPaths = Directory.EnumerateFiles(rootDirectory, pattern, SearchOption.AllDirectories).ToArray();
            var inventory = await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken);
            var unknownHashes = inventory.All;

            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(domain, unknownHashes);
                IReadOnlyList<string> lcuDirectories = domain == HashGuessDomain.Lcu
                    ? HashGuessEngine.BuildDirectoryList(LoadKnownPaths(HashGuessDomain.Lcu))
                    : Array.Empty<string>();
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

                            if (chunk.UncompressedSize > MaximumGrepChunkSize)
                                continue;

                            string resolvedChunkPath = _hashResolverService.ResolveHash(chunk.PathHash);
                            string chunkExt = Path.GetExtension(resolvedChunkPath).TrimStart('.').ToLowerInvariant();

                            if (domain == HashGuessDomain.Lcu)
                            {
                                if (chunkExt is "png" or "jpg" or "ttf" or "webm" or "ogg" or "dds" or "tga")
                                    continue;
                            }
                            else
                            {
                                if (chunkExt is "dds" or "jpg" or "png" or "tga" or "ttf" or "otf" or "ogg" or "webm" or "anm" or "skl" or "skn" or "scb" or "sco" or "troybin" or "bnk" or "wpk" or "tex")
                                    continue;
                            }

                            try
                            {
                                using var compressedData = wad.LoadChunk(chunk);
                                byte[] data = WadChunkUtils.DecompressChunk(compressedData.Span, chunk.Compression);
                                foreach (var candidate in ExtractCandidates(domain, data, resolvedChunkPath))
                                {
                                    if (domain == HashGuessDomain.Lcu &&
                                        candidate.Strategy == HashGuessStrategy.LcuEmbeddedPath &&
                                        !candidate.Path.StartsWith("plugins/", StringComparison.Ordinal))
                                    {
                                        foreach (string directory in lcuDirectories)
                                            engine.Check(directory + "/" + candidate.Path, candidate.Strategy, wadPath, chunk.PathHash);
                                    }
                                    else
                                    {
                                        engine.Check(candidate.Path, candidate.Strategy, wadPath, chunk.PathHash);
                                    }
                                }
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
            await _store.SaveResearchMatchesAsync(resultMatches, cancellationToken);
            await _store.SaveUnknownHashesAsync(domain, remainingUnknowns, inventory.Current, inventory.PatchFingerprint, cancellationToken);

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
            await _store.SaveHashesAsync(matches, cancellationToken);
            await _hashResolverService.ForceReloadHashesAsync();
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

            string pattern = domain == HashGuessDomain.Game ? "*.wad.client" : "*.wad";
            string[] wadPaths = Directory.EnumerateFiles(rootDirectory, pattern, SearchOption.AllDirectories).ToArray();
            var inventory = preparedInventory ?? await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken, sessionResolved as IReadOnlySet<ulong>);
            var unknownHashes = CreateSessionPending(inventory.All, sessionResolved);
            int unknownAtStart = unknownHashes.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(domain, unknownHashes);
                IEnumerable<string> candidates = domain == HashGuessDomain.Game
                    ? GenerateCharacterCandidates(LoadKnownPaths(HashGuessDomain.Game))
                    : GenerateLcuCrossDomainCandidates(LoadKnownPaths(HashGuessDomain.Game));
                HashGuessStrategy strategy = domain == HashGuessDomain.Game ? HashGuessStrategy.CharacterTemplate : HashGuessStrategy.CrossDomainAsset;
                int checkedCandidates = 0;

                foreach (string candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(candidate, strategy, "Generated canonical pattern");
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
            await _store.SaveResearchMatchesAsync(resultMatches, cancellationToken);
            await _store.SaveUnknownHashesAsync(domain, remainingUnknowns, CreateSessionPending(inventory.Current, sessionResolved), inventory.PatchFingerprint, cancellationToken);
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
            return new HashGuessRunResult { Domain = HashGuessDomain.Lcu, UnknownHashesAtStart = results.FirstOrDefault()?.UnknownHashesAtStart ?? 0, ScannedChunks = results.Sum(result => result.ScannedChunks), Matches = matches };
        }

        public async Task<HashGuessRunResult> RunLcuAdvancedGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            string[] wads = Directory.EnumerateFiles(rootDirectory, "*.wad", SearchOption.AllDirectories).ToArray();
            var inventory = await BuildUnknownInventoryAsync(HashGuessDomain.Lcu, wads, cancellationToken);
            var unknown = inventory.All;
            int initial = unknown.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Lcu, unknown);
                var paths = LoadKnownPaths(HashGuessDomain.Lcu).ToList();
                var wordPaths = paths.Where(path => !LcuWordlistExcludedPathRegex.IsMatch(path)).ToList();
                var names = HashGuessEngine.BuildRankedBasenames(paths).Take(1000).ToList();
                var directories = HashGuessEngine.BuildRankedDirectoryList(paths).Take(2000).ToList();
                int checkedCandidates = 0;
                const int budget = 2_000_000;

                foreach (string directory in directories)
                {
                    foreach (string name in names)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        engine.Check(directory + "/" + name, HashGuessStrategy.PluginVariant, "LCU Advanced basename");
                        checkedCandidates++;
                        if (checkedCandidates >= budget || engine.RemainingUnknownCount == 0) goto Complete;
                    }
                }

            Complete:
                if (engine.RemainingUnknownCount > 0)
                {
                    var words = HashGuessEngine.BuildBasenameWordlist(wordPaths).Take(5000).ToList();
                    checkedCandidates += RunFocusedWordlistSubstitution(engine, wordPaths, words, cancellationToken, 500_000);
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    var words = HashGuessEngine.BuildBasenameWordlist(wordPaths).Take(5000).ToList();
                    checkedCandidates += RunWordAdditionAttack(engine, paths, words, cancellationToken, 1_000_000);
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: LCU static-assets" });
                    var staticAssetsPaths = paths.Where(p => p.StartsWith("plugins/rcp-fe-lol-static-assets/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)).ToList();
                    var sswordlist = HashGuessEngine.BuildBasenameWordlist(staticAssetsPaths).Take(5000).ToList();
                    checkedCandidates += RunFocusedWordlistSubstitution(engine, staticAssetsPaths, sswordlist, cancellationToken);
                    if (engine.RemainingUnknownCount > 0)
                        checkedCandidates += RunFocusedWordlistDoubleSubstitution(engine, staticAssetsPaths, sswordlist, cancellationToken);

                    if (engine.RemainingUnknownCount > 0)
                    {
                        progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: LCU navigation" });
                        var navigationPaths = paths.Where(p => p.StartsWith("plugins/rcp-fe-lol-navigation/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)).ToList();
                        checkedCandidates += RunFocusedWordlistSubstitution(engine, navigationPaths, sswordlist, cancellationToken);
                        if (engine.RemainingUnknownCount > 0)
                            checkedCandidates += RunFocusedWordlistDoubleSubstitution(engine, navigationPaths, sswordlist, cancellationToken);
                    }

                    if (engine.RemainingUnknownCount > 0)
                    {
                        progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: LCU parties" });
                        var partiesPaths = paths.Where(p => p.StartsWith("plugins/rcp-fe-lol-parties/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
                        var partiesWords = HashGuessEngine.BuildBasenameWordlist(partiesPaths).Take(5000).ToList();
                        checkedCandidates += RunFocusedWordlistSubstitution(engine, partiesPaths, partiesWords, cancellationToken);
                    }

                }

                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await _store.SaveResearchMatchesAsync(matches, cancellationToken);
            await _store.SaveUnknownHashesAsync(HashGuessDomain.Lcu, remainingUnknowns, inventory.Current, inventory.PatchFingerprint, cancellationToken);
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
                var knownPaths = LoadKnownPaths(HashGuessDomain.Lcu).ToList();
                var phases = new (string Name, IEnumerable<(string Path, HashGuessStrategy Strategy)> Candidates)[]
                {
                    ("plugin variants", GenerateLcuPluginCandidates(knownPaths, 1_000_000)),
                    ("extension variants", GenerateLcuExtensionCandidates(knownPaths, 1_000_000)),
                    ("LCU patterns", GenerateLcuPatternCandidates(knownPaths))
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
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await _store.SaveResearchMatchesAsync(matches, cancellationToken);
            await _store.SaveUnknownHashesAsync(HashGuessDomain.Lcu, remainingUnknowns, CreateSessionPending(inventory.Current, sessionResolved), inventory.PatchFingerprint, cancellationToken);
            sessionResolved?.UnionWith(matches.Select(match => match.Hash));
            return new HashGuessRunResult { Domain = HashGuessDomain.Lcu, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        public async Task<HashGuessRunResult> RunGameExtendedGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            string[] wads = Directory.EnumerateFiles(rootDirectory, "*.wad.client", SearchOption.AllDirectories).ToArray();
            var inventory = await BuildUnknownInventoryAsync(HashGuessDomain.Game, wads, cancellationToken);
            var unknown = inventory.All;
            int initial = unknown.Count;
            var runResult = await Task.Run(async () =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Game, unknown);
                int checkedCandidates = 0;
                const int budget = 2_000_000;
                var knownPaths = LoadKnownPaths(HashGuessDomain.Game).ToList();

                foreach (var candidate in GenerateGameSkinNumberCandidates(knownPaths, budget))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(candidate.Path, candidate.Strategy, "GAME skin number combinations");
                    checkedCandidates++;
                    if (engine.RemainingUnknownCount == 0) break;
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    foreach (var candidate in GenerateExtensionCandidates(knownPaths, 1_000_000))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        engine.Check(candidate.Path, candidate.Strategy, "GAME extension substitution");
                        checkedCandidates++;
                        if (engine.RemainingUnknownCount == 0) break;
                    }
                }

                foreach (var candidate in GenerateGameCharacterSubstitutionCandidates(knownPaths, budget))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(candidate.Path, candidate.Strategy, "GAME character substitution");
                    checkedCandidates++;
                    if (checkedCandidates % 5000 == 0)
                        progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Generating GAME Extended candidates" });
                    if (engine.RemainingUnknownCount == 0) break;
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    foreach (var candidate in GenerateGameSuffixCandidates(knownPaths, budget))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        engine.Check(candidate.Path, candidate.Strategy, "GAME suffix substitution");
                        checkedCandidates++;
                        if (engine.RemainingUnknownCount == 0) break;
                    }
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: Bin paths" });
                    var binPaths = knownPaths.Where(p => p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToList();
                    var binWords = HashGuessEngine.BuildBasenameWordlist(binPaths).Take(20000).ToList();
                    checkedCandidates += RunFocusedWordlistSubstitution(engine, binPaths.Take(25000), binWords, cancellationToken);

                    if (engine.RemainingUnknownCount > 0)
                    {
                        progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: Data bin paths" });
                        var dataBinPaths = knownPaths.Where(p => p.StartsWith("data/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToList();
                        var dataBinWords = HashGuessEngine.BuildBasenameWordlist(dataBinPaths).Take(20000).ToList();
                        checkedCandidates += RunFocusedWordlistSubstitution(engine, dataBinPaths.Take(25000), dataBinWords, cancellationToken);
                    }

                    if (engine.RemainingUnknownCount > 0)
                    {
                        progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: Characters DDS paths" });
                        var charDdsPaths = knownPaths.Where(p => p.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToList();
                        var charDdsWords = HashGuessEngine.BuildBasenameWordlist(charDdsPaths).Take(20000).ToList();
                        checkedCandidates += RunFocusedWordlistSubstitution(engine, charDdsPaths.Take(25000), charDdsWords, cancellationToken);
                    }

                    if (engine.RemainingUnknownCount > 0)
                    {
                        progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: Characters TEX paths" });
                        var charTexPaths = knownPaths.Where(p => p.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)).ToList();
                        var charTexWords = HashGuessEngine.BuildBasenameWordlist(charTexPaths).Take(20000).ToList();
                        checkedCandidates += RunFocusedWordlistSubstitution(engine, charTexPaths.Take(25000), charTexWords, cancellationToken);
                    }

                    if (engine.RemainingUnknownCount > 0)
                    {
                        progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: Word insertions" });
                        var gameWords = HashGuessEngine.BuildBasenameWordlist(knownPaths).Take(20000).ToList();
                        var additionPaths = knownPaths.Where(path =>
                            !path.Contains("assets/characters/", StringComparison.OrdinalIgnoreCase) &&
                            !path.Contains("vo/", StringComparison.OrdinalIgnoreCase) &&
                            !path.Contains("sfx/", StringComparison.OrdinalIgnoreCase) &&
                            !path.Contains("skins_skin", StringComparison.OrdinalIgnoreCase));
                        checkedCandidates += RunWordAdditionAttack(engine, additionPaths.Take(20000), gameWords, cancellationToken);
                    }
                }

                checkedCandidates += await GuessChromaGroupsAsync(engine, cancellationToken);
                checkedCandidates += await GuessSkinGroupsBinLocalAsync(engine, cancellationToken);
                checkedCandidates += RunEsportsBannersAttack(engine, rootDirectory, LoadKnownPaths(HashGuessDomain.Game), cancellationToken);

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: GAME prefixes" });
                    var prefixes = new[] { "tft_", "2x_", "2x_sd_", "4x_", "4x_sd_", "sd_" };
                    checkedCandidates += RunPrefixAttack(engine, LoadKnownPaths(HashGuessDomain.Game), prefixes, cancellationToken);
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "GAME Cartesian Cross" });
                    var gamePaths = LoadKnownPaths(HashGuessDomain.Game).ToList();
                    var gameDirs = HashGuessEngine.BuildRankedDirectoryList(gamePaths).Take(2000).ToList();
                    var gameNames = HashGuessEngine.BuildRankedBasenames(gamePaths).Take(1000).ToList();
                    int cartesianChecked = 0;
                    const int cartesianBudget = 2_000_000;

                    foreach (string dir in gameDirs)
                    {
                        foreach (string name in gameNames)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            engine.Check(dir + "/" + name, HashGuessStrategy.PluginVariant, "GAME Advanced basename");
                            checkedCandidates++;
                            cartesianChecked++;
                            if (cartesianChecked % 100000 == 0)
                                progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = $"GAME Cartesian Cross · {cartesianChecked:N0}" });
                            if (cartesianChecked >= cartesianBudget || engine.RemainingUnknownCount == 0) goto CompleteCartesian;
                        }
                    }
                CompleteCartesian:;
                }

                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await _store.SaveResearchMatchesAsync(matches, cancellationToken);
            await _store.SaveUnknownHashesAsync(HashGuessDomain.Game, remainingUnknowns, inventory.Current, inventory.PatchFingerprint, cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Game, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        private int RunEsportsBannersAttack(HashGuessEngine engine, string rootDirectory, IEnumerable<string> knownPaths, CancellationToken cancellationToken)
        {
            var esportsPaths = knownPaths.Where(p => p.StartsWith("assets/esports/", StringComparison.OrdinalIgnoreCase)).ToList();
            if (esportsPaths.Count == 0) return 0;

            var words = HashGuessEngine.BuildWordlist(esportsPaths).ToList();
            var userKeywords = new[]
            {
                "halloflegends", "air", "pg", "action", "lrn",
                "faker", "es", "spirit", "blossom",
                "uzi", "gll", "kaktus", "kotsovolos", "kb",
                "trophy", "league", "legends", 
                "greek", "masters", "visa",
                "al", "2024", "arabian", "2025", "2026",
                "five", "elite", "series", "arcane", "lolesports", "omen", "moviestar", "audi", "kitkat", "emea"
            };
            foreach (var kw in userKeywords)
            {
                if (!words.Contains(kw, StringComparer.OrdinalIgnoreCase))
                    words.Add(kw);
            }

            var dynamicWords = ExtractWordsFromDirectoryJsons(rootDirectory, cancellationToken);
            foreach (var w in dynamicWords)
            {
                if (!words.Contains(w, StringComparer.OrdinalIgnoreCase))
                    words.Add(w);
            }

            int checkedCount = 0;
            checkedCount += RunFocusedWordlistSubstitution(engine, esportsPaths, words, cancellationToken);
            checkedCount += RunWordAdditionAttack(engine, esportsPaths, words, cancellationToken);
            return checkedCount;
        }

        private List<string> ExtractWordsFromDirectoryJsons(string rootDirectory, CancellationToken cancellationToken)
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                    return words.ToList();

                var jsonFiles = Directory.EnumerateFiles(rootDirectory, "*.json", SearchOption.AllDirectories)
                    .Take(100)
                    .ToList();

                var wordRegex = new Regex(@"[a-zA-Z0-9_]{3,20}", RegexOptions.Compiled);

                foreach (var file in jsonFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length > 2 * 1024 * 1024) continue;

                        string content = File.ReadAllText(file);
                        foreach (Match m in wordRegex.Matches(content))
                        {
                            string w = m.Value.ToLowerInvariant();
                            if (w.Length >= 4 && !int.TryParse(w, out _))
                            {
                                words.Add(w);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogDebug($"Hash Lab skipped JSON word source '{file}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.LogError(ex, $"Hash Lab could not enumerate JSON word sources under '{rootDirectory}'.");
            }
            return words.ToList();
        }

        private async Task<int> GuessChromaGroupsAsync(HashGuessEngine engine, CancellationToken cancellationToken)
        {
            try
            {
                string json = await _httpClient.GetStringAsync("https://raw.communitydragon.org/pbe/plugins/rcp-be-lol-game-data/global/default/v1/skins.json", cancellationToken);
                using var document = JsonDocument.Parse(json);
                var groups = new Dictionary<string, List<List<int>>>(StringComparer.OrdinalIgnoreCase);
                foreach (var skin in document.RootElement.EnumerateObject())
                {
                    var value = skin.Value;
                    if (!value.TryGetProperty("loadScreenPath", out var loadScreen) || !value.TryGetProperty("id", out var id)) continue;
                    Match champion = Regex.Match(loadScreen.GetString() ?? string.Empty, @"/assets/characters/([^/]+)/skins/", RegexOptions.IgnoreCase);
                    if (!champion.Success) continue;
                    var ids = new List<int> { (int)(id.GetInt64() % 1000) };
                    if (value.TryGetProperty("chromas", out var chromas) && chromas.ValueKind == JsonValueKind.Array)
                        foreach (var chroma in chromas.EnumerateArray()) if (chroma.TryGetProperty("id", out var chromaId)) ids.Add((int)(chromaId.GetInt64() % 1000));
                    groups.TryAdd(champion.Groups[1].Value.ToLowerInvariant(), new List<List<int>>());
                    groups[champion.Groups[1].Value.ToLowerInvariant()].Add(ids);
                }

                int generated = 0;
                foreach (var pair in groups)
                {
                    var tokens = pair.Value.Select(group => group.Select(id => "_skins_skin" + id).ToList()).Append(new List<string> { "_skins_root" }).Take(16).ToList();
                    for (int mask = 1; mask < (1 << tokens.Count) && generated < 100000; mask++)
                    {
                        string suffix = string.Concat(tokens.Where((_, index) => (mask & (1 << index)) != 0).SelectMany(value => value).OrderBy(value => value));
                        engine.Check("data/" + pair.Key + suffix + ".bin", HashGuessStrategy.ChromaGroupVariant, "CommunityDragon chroma groups");
                        generated++;
                    }
                }
                return generated;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.LogWarning("Hash Lab skipped chroma groups: " + ex.Message);
                return 0;
            }
        }

        private async Task<int> GuessSkinGroupsBinLocalAsync(HashGuessEngine engine, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var charToSkins = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
                var regex = new Regex(@"^assets/characters/([^/]+)/skins/skin(\d+)/", RegexOptions.IgnoreCase);
                foreach (string path in LoadKnownPaths(HashGuessDomain.Game))
                {
                    Match m = regex.Match(path);
                    if (!m.Success) continue;
                    string character = m.Groups[1].Value.ToLowerInvariant();
                    if (character == "sightward") continue;
                    int skinNum = int.Parse(m.Groups[2].Value);
                    if (!charToSkins.TryGetValue(character, out var skins))
                    {
                        skins = new HashSet<int> { 0 };
                        charToSkins[character] = skins;
                    }
                    skins.Add(skinNum);
                }

                int generated = 0;
                const int candidateBudget = 250_000;
                foreach (var pair in charToSkins)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var strSkins = pair.Value.Select(i => $"_skins_skin{i}").OrderBy(x => x).ToList();
                    for (int n = 0; n < strSkins.Count; n++)
                    {
                        foreach (var combination in GetCombinations(strSkins, n + 1))
                        {
                            string s = string.Concat(combination);
                            engine.Check($"data/{pair.Key}{s}.bin", HashGuessStrategy.ChromaGroupVariant, "Local skin groups");
                            generated++;
                            if (generated >= candidateBudget || engine.RemainingUnknownCount == 0)
                                return generated;
                        }
                    }
                }
                return generated;
            }, cancellationToken);
        }

        private async Task<HashGuessRunResult> RunGameCrossDomainGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken, ISet<ulong> sessionResolved, HashUnknownInventory inventory)
        {
            var unknown = CreateSessionPending(inventory.All, sessionResolved);
            int initial = unknown.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Game, unknown);
                int candidates = 0;

                foreach (string lcuPath in LoadKnownPaths(HashGuessDomain.Lcu))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Match match = Regex.Match(lcuPath, @"^plugins/rcp-be-lol-game-data/global/default/((?:assets|data)/.*)\.(png|jpg|dds|json)$", RegexOptions.IgnoreCase);
                    if (!match.Success) continue;

                    string path = match.Groups[1].Value;
                    string extension = match.Groups[2].Value;
                    if (extension.Equals("json", StringComparison.OrdinalIgnoreCase))
                    {
                        engine.Check(path + ".json", HashGuessStrategy.CrossDomainGame, "LCU to GAME");
                        candidates++;
                    }
                    else
                    {
                        engine.Check(path + "." + extension.ToLowerInvariant(), HashGuessStrategy.CrossDomainGame, "LCU to GAME");
                        candidates++;
                        if (!extension.Equals("dds", StringComparison.OrdinalIgnoreCase))
                        {
                            engine.Check(path + ".dds", HashGuessStrategy.CrossDomainGame, "LCU to GAME");
                            candidates++;
                        }
                    }
                    if (engine.RemainingUnknownCount == 0) break;
                }

                foreach (var candidate in GenerateExtensionCandidates(LoadKnownPaths(HashGuessDomain.Game), 1_000_000))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(candidate.Path, candidate.Strategy, "GAME extension substitution");
                    candidates++;
                    if (engine.RemainingUnknownCount == 0) break;
                }

                foreach (string gamePath in LoadKnownPaths(HashGuessDomain.Game).Take(250000))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string directory = Path.GetDirectoryName(gamePath)?.Replace('\\', '/') ?? string.Empty;
                    string name = Path.GetFileName(gamePath);
                    foreach (string prefix in new[] { "tft_", "2x_", "2x_sd_", "4x_", "4x_sd_", "sd_" })
                        engine.Check(directory + "/" + prefix + name, HashGuessStrategy.PrefixVariant, "GAME basename prefix");

                    if (Regex.IsMatch(gamePath, @"\.[pv]s(?:_[23]_0)?$", RegexOptions.IgnoreCase))
                    {
                        foreach (string variant in new[] { ".dx11", ".dx9", ".dx9sm3", ".glsl", ".metal" })
                        {
                            engine.Check(gamePath + variant, HashGuessStrategy.ShaderVariant, "GAME shader variant");
                            for (int n = 0; n < 20000; n += 100)
                            {
                                engine.Check($"{gamePath}{variant}_{n}", HashGuessStrategy.ShaderVariant, "GAME shader variant");
                            }
                        }
                    }
                    candidates++;
                    if (engine.RemainingUnknownCount == 0) break;
                }

                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, candidates, engine.UnknownHashes);
            }, cancellationToken);

            var matches = runResult.Item1;
            int candidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await _store.SaveResearchMatchesAsync(matches, cancellationToken);
            await _store.SaveUnknownHashesAsync(HashGuessDomain.Game, remainingUnknowns, CreateSessionPending(inventory.Current, sessionResolved), inventory.PatchFingerprint, cancellationToken);
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

            string pattern = domain == HashGuessDomain.Game ? "*.wad.client" : "*.wad";
            string[] wadPaths = Directory.EnumerateFiles(rootDirectory, pattern, SearchOption.AllDirectories).ToArray();
            var inventory = preparedInventory ?? await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken, sessionResolved as IReadOnlySet<ulong>);
            var unknownHashes = CreateSessionPending(inventory.All, sessionResolved);
            int unknownAtStart = unknownHashes.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(domain, unknownHashes);
                int checkedCandidates = 0;

                foreach (string candidate in GenerateLanguageCandidates(domain, LoadKnownPaths(domain)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(candidate, HashGuessStrategy.LanguageVariant, "Generated locale or region variant");
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
            await _store.SaveResearchMatchesAsync(resultMatches, cancellationToken);
            await _store.SaveUnknownHashesAsync(domain, remainingUnknowns, CreateSessionPending(inventory.Current, sessionResolved), inventory.PatchFingerprint, cancellationToken);
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

            string pattern = domain == HashGuessDomain.Game ? "*.wad.client" : "*.wad";
            string[] wadPaths = Directory.EnumerateFiles(rootDirectory, pattern, SearchOption.AllDirectories).ToArray();
            var inventory = preparedInventory ?? await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken, sessionResolved as IReadOnlySet<ulong>);
            var unknownHashes = CreateSessionPending(inventory.All, sessionResolved);
            int unknownAtStart = unknownHashes.Count;
            int numberLimit = domain == HashGuessDomain.Game ? 100 : 10_000;
            const int candidateBudget = 2_000_000;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(domain, unknownHashes);
                int checkedCandidates = 0;

                foreach (string candidate in GenerateNumberCandidates(domain, LoadKnownPaths(domain), numberLimit, candidateBudget))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(candidate, HashGuessStrategy.NumberVariant, "Generated numeric variant");
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
            await _store.SaveResearchMatchesAsync(resultMatches, cancellationToken);
            await _store.SaveUnknownHashesAsync(domain, remainingUnknowns, CreateSessionPending(inventory.Current, sessionResolved), inventory.PatchFingerprint, cancellationToken);
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
            string pattern = domain == HashGuessDomain.Game ? "*.wad.client" : "*.wad";
            string[] wadPaths = Directory.EnumerateFiles(rootDirectory, pattern, SearchOption.AllDirectories).ToArray();
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
            string[] paths = wadPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            var pending = await _store.LoadUnknownHashesAsync(domain, cancellationToken);
            pending.RemoveWhere(hash => _hashResolverService.IsKnownHash(hash));
            var current = new HashSet<ulong>();
            ulong patchXor = 0;
            ulong patchSum = 0;
            long patchChunkCount = 0;

            await Task.Run(() =>
            {
                foreach (string wadPath in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var wad = new WadFile(wadPath);
                        foreach (ulong hash in wad.Chunks.Keys)
                        {
                            patchXor ^= hash;
                            patchSum = unchecked(patchSum + hash);
                            patchChunkCount++;
                            if (!_hashResolverService.IsKnownHash(hash))
                                current.Add(hash);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogError(ex, $"Hash Lab could not build inventory from WAD '{wadPath}'.");
                    }
                }
            }, cancellationToken);

            pending.UnionWith(current);
            if (sessionResolved != null)
            {
                pending.ExceptWith(sessionResolved);
                current.ExceptWith(sessionResolved);
            }

            string fingerprint = $"{domain}:{patchChunkCount}:{patchXor:x16}:{patchSum:x16}";
            return new HashUnknownInventory { All = pending, Current = current, PatchFingerprint = fingerprint };
        }

        public Task<HashUnknownSummary> GetUnknownSummaryAsync(HashGuessDomain domain, CancellationToken cancellationToken)
        {
            return _store.LoadUnknownSummaryAsync(domain, cancellationToken);
        }

        private IEnumerable<string> LoadKnownPaths(HashGuessDomain domain)
        {
            string fileName = domain == HashGuessDomain.Game ? "hashes.game.txt" : "hashes.lcu.txt";
            string path = Path.Combine(_directoriesCreator.HashesPath, fileName);
            if (!File.Exists(path)) yield break;

            foreach (string line in File.ReadLines(path))
            {
                int separator = line.IndexOf(' ');
                if (separator < 16 || separator == line.Length - 1) continue;
                yield return NormalizePath(line[(separator + 1)..]);
            }
        }

        private static IEnumerable<string> GenerateCharacterCandidates(IEnumerable<string> knownPaths)
        {
            var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in knownPaths)
            {
                Match match = Regex.Match(path, @"^(?:assets/|data/)?characters/([^/.]+)/", RegexOptions.IgnoreCase);
                if (match.Success) characters.Add(match.Groups[1].Value);
            }

            foreach (string character in characters.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"data/characters/{character}/skins/root.bin";
                yield return $"data/characters/{character}/skins/base/{character}.skl";
                yield return $"data/characters/{character}/skins/base/{character}.skn";
                yield return $"data/characters/{character}/skins/base/{character}_tx_cm.dds";
                yield return $"data/characters/{character}/tiers/root.bin";
                yield return $"data/characters/{character}/{character}.bin";
                yield return $"data/characters/{character}/{character}.ddf";
                yield return $"data/characters/{character}/hud/{character}_circle.dds";
                yield return $"data/characters/{character}/hud/{character}_square.dds";
                yield return $"assets/characters/{character}/hud/{character}_circle.dds";
                yield return $"assets/characters/{character}/hud/{character}_square.dds";
                yield return $"characters/{character}";

                int skinLimit = character.Equals("sightward", StringComparison.OrdinalIgnoreCase) ? 500 : 200;
                for (int skin = 0; skin < skinLimit; skin++)
                {
                    yield return $"data/characters/{character}/skins/skin{skin}.bin";
                    yield return $"data/characters/{character}/animations/skin{skin}.bin";
                }

                if (character.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                {
                    for (int tier = 0; tier < 10; tier++)
                    {
                        yield return $"data/characters/{character}/tiers/tier{tier}.bin";
                    }
                }
            }
        }

        private static IEnumerable<string> GenerateLcuCrossDomainCandidates(IEnumerable<string> gamePaths, int candidateBudget = 2_000_000)
        {
            const string basePath = "plugins/rcp-be-lol-game-data/global/default/";
            int generated = 0;
            foreach (string path in gamePaths)
            {
                if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = path[..^4];
                    yield return basePath + prefix + ".dds";
                    if (++generated >= candidateBudget) yield break;
                    yield return basePath + prefix + ".png";
                    if (++generated >= candidateBudget) yield break;
                    yield return basePath + prefix + ".jpg";
                    if (++generated >= candidateBudget) yield break;
                }
                else if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                {
                    yield return basePath + path;
                    if (++generated >= candidateBudget) yield break;
                }
                else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    yield return basePath + path;
                    if (++generated >= candidateBudget) yield break;
                }
            }
        }

        private static IEnumerable<string> GenerateLanguageCandidates(HashGuessDomain domain, IEnumerable<string> knownPaths, int candidateBudget = 500_000)
        {
            int generated = 0;
            if (domain == HashGuessDomain.Game)
            {
                var formats = knownPaths.Where(path => LocaleRegex.IsMatch(path))
                    .Select(path => LocaleRegex.Replace(path, "{locale}"))
                    .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);
                foreach (string format in formats)
                    foreach (string locale in Locales)
                    {
                        yield return format.Replace("{locale}", locale, StringComparison.Ordinal);
                        if (++generated >= candidateBudget) yield break;
                    }
                yield break;
            }

            var lcuFormats = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in knownPaths)
            {
                Match match = Regex.Match(path, @"^plugins/([^/]+)/[^/]+/[^/]+/(.*)$", RegexOptions.IgnoreCase);
                if (match.Success) lcuFormats.Add($"plugins/{match.Groups[1].Value}/{{region}}/{{locale}}/{match.Groups[2].Value}");
            }
            foreach (string format in lcuFormats.OrderBy(path => path, StringComparer.Ordinal))
                foreach (string region in Regions)
                    foreach (string locale in Locales.Append("default"))
                    {
                        yield return format.Replace("{region}", region, StringComparison.Ordinal).Replace("{locale}", locale, StringComparison.Ordinal);
                        if (++generated >= candidateBudget) yield break;
                    }
        }

        private static IEnumerable<string> GenerateNumberCandidates(HashGuessDomain domain, IEnumerable<string> knownPaths, int numberLimit, int candidateBudget)
        {
            var formats = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in knownPaths)
            {
                if (domain == HashGuessDomain.Lcu && LcuNumberExcludedPathRegex.IsMatch(path)) continue;
                foreach (Match match in BasenameNumberRegex.Matches(path))
                    formats.Add(path[..match.Index] + "{number}" + path[(match.Index + match.Length)..]);
            }

            int produced = 0;
            var orderedFormats = formats.OrderBy(path => path, StringComparer.Ordinal).ToList();
            for (int value = 0; value < numberLimit; value++)
            {
                foreach (string format in orderedFormats)
                {
                    foreach (string candidate in FormatNumberVariants(format, value))
                    {
                        yield return candidate;
                        if (++produced >= candidateBudget) yield break;
                    }
                }
            }
        }

        private static IEnumerable<string> FormatNumberVariants(string format, int value)
        {
            yield return format.Replace("{number}", value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (value < 10)
                yield return format.Replace("{number}", value.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (value < 100)
                yield return format.Replace("{number}", value.ToString("D3", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> GenerateLcuPluginCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            var paths = knownPaths.Where(path => path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)).ToList();
            var plugins = paths.Select(path => path.Split('/')[1]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var formats = paths.Select(path => Regex.Replace(path, @"^plugins/[^/]+/", "plugins/{plugin}/", RegexOptions.IgnoreCase))
                .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToList();
            int generated = 0;

            foreach (string format in formats.Take(100000))
            {
                foreach (string plugin in plugins)
                {
                    yield return (format.Replace("{plugin}", plugin, StringComparison.Ordinal), HashGuessStrategy.PluginVariant);
                    if (++generated >= candidateBudget) yield break;
                }
            }
        }

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> GenerateLcuExtensionCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            return GenerateExtensionCandidates(knownPaths.Where(path => path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)), candidateBudget);
        }

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> GenerateExtensionCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            var paths = knownPaths.ToList();
            var extensions = paths.Select(Path.GetExtension)
                .Where(extension => extension.Length > 0 && !extension.EndsWith("00", StringComparison.Ordinal))
                .GroupBy(extension => extension, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key)
                .ToList();
            var prefixes = paths.Select(path => Path.ChangeExtension(path, null))
                .Where(prefix => !string.IsNullOrEmpty(prefix))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(prefix => prefix, StringComparer.Ordinal);
            int generated = 0;

            foreach (string prefix in prefixes)
            {
                foreach (string extension in extensions)
                {
                    yield return (prefix + extension, HashGuessStrategy.ExtensionVariant);
                    if (++generated >= candidateBudget) yield break;
                }
            }
        }

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> GenerateLcuPatternCandidates(IEnumerable<string> knownPaths)
        {
            var paths = knownPaths.Where(path => path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)).ToList();
            var perkPrimary = Enumerable.Range(80, 6).Select(value => value * 100).ToList();
            foreach (int i in perkPrimary)
            {
                var perkSecondary = Enumerable.Range(i, 100).ToList();
                foreach (int j in perkPrimary.Prepend(0))
                {
                    foreach (int k in perkSecondary.Prepend(0))
                    {
                        yield return ($"plugins/rcp-fe-lol-perks/global/default/images/inventory-card/{i}/p{i}_s{j}_k{k}.jpg", HashGuessStrategy.LcuPattern);
                    }
                }
                yield return ($"plugins/rcp-fe-lol-perks/global/default/images/construct/{i}/environment.jpg", HashGuessStrategy.LcuPattern);
                yield return ($"plugins/rcp-fe-lol-perks/global/default/images/construct/{i}/construct.png", HashGuessStrategy.LcuPattern);
                foreach (int j in perkSecondary)
                {
                    yield return ($"plugins/rcp-fe-lol-perks/global/default/images/construct/{i}/keystones/{j}.png", HashGuessStrategy.LcuPattern);
                }
                foreach (int j in perkPrimary)
                {
                    yield return ($"plugins/rcp-fe-lol-perks/global/default/images/construct/{i}/second/{j}.png", HashGuessStrategy.LcuPattern);
                }
            }

            var sanitizerLangs = Locales;
            var sanitizerRegions = Regions;
            foreach (string action in new[] { "filter", "unfilter", "whitelist" })
            {
                for (int i = 0; i < 5; i++)
                {
                    yield return ($"plugins/rcp-be-sanitizer/global/default/{i}.{action}.csv", HashGuessStrategy.LcuPattern);
                    foreach (string x in sanitizerLangs)
                    {
                        string lang = x.Split('_')[0];
                        string country = x.Split('_')[1];
                        yield return ($"plugins/rcp-be-sanitizer/global/default/{i}.{action}.language.{lang}.csv", HashGuessStrategy.LcuPattern);
                        yield return ($"plugins/rcp-be-sanitizer/global/default/{i}.{action}.country.{country}.csv", HashGuessStrategy.LcuPattern);
                        yield return ($"plugins/rcp-be-sanitizer/global/default/{i}.{action}.locale.{x}.csv", HashGuessStrategy.LcuPattern);
                    }
                    foreach (string region in sanitizerRegions)
                    {
                        yield return ($"plugins/rcp-be-sanitizer/global/default/{i}.{action}.region.{region}.csv", HashGuessStrategy.LcuPattern);
                    }
                }
            }
            foreach (string p in new[] { "allowedchars", "breakingchars", "projectedchars", "projectedchars1337", "punctuationchars", "variantaliases" })
            {
                foreach (string x in sanitizerLangs)
                {
                    string lang = x.Split('_')[0];
                    yield return ($"plugins/rcp-be-sanitizer/global/default/{p}.locale.{x}.txt", HashGuessStrategy.LcuPattern);
                    yield return ($"plugins/rcp-be-sanitizer/global/default/{p}.language.{lang}.txt", HashGuessStrategy.LcuPattern);
                }
            }

            foreach (string path in paths.Where(path => path.StartsWith("plugins/rcp-fe-lol-loot/global/default/assets/loot_item_icons/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                yield return (path[..^4] + "_splash.png", HashGuessStrategy.LcuPattern);
        }

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> GenerateGameCharacterSubstitutionCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            var paths = knownPaths.ToList();
            var characterCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var formatCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var regex = new Regex(@"^(?:assets|data)/characters/([^/]+)/", RegexOptions.IgnoreCase);
            foreach (string path in paths)
            {
                Match match = regex.Match(path);
                if (!match.Success) continue;
                string character = match.Groups[1].Value;
                characterCounts.TryGetValue(character, out int characterSupport);
                characterCounts[character] = characterSupport + 1;
                string format = path.Replace(character, "{character}", StringComparison.Ordinal);
                formatCounts.TryGetValue(format, out int formatSupport);
                formatCounts[format] = formatSupport + 1;
            }
            var characters = characterCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).ToList();
            var formats = formatCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key);
            int generated = 0;
            foreach (string format in formats)
            {
                foreach (string character in characters)
                {
                    yield return (format.Replace("{character}", character, StringComparison.Ordinal), HashGuessStrategy.CharacterSubstitution);
                    if (++generated >= candidateBudget) yield break;
                }
            }
        }

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> GenerateGameSuffixCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            var suffixCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = int.MaxValue };
            var formatCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var regex = new Regex(@"^(.*?)(\.[^.]+)?(\.[^.]+)$");
            foreach (string path in knownPaths)
            {
                Match match = regex.Match(path);
                if (!match.Success) continue;
                string suffix = match.Groups[2].Value;
                if (suffix.Length > 0)
                {
                    suffixCounts.TryGetValue(suffix, out int suffixSupport);
                    suffixCounts[suffix] = suffixSupport + 1;
                }
                string format = match.Groups[1].Value + "{suffix}" + match.Groups[3].Value;
                formatCounts.TryGetValue(format, out int formatSupport);
                formatCounts[format] = formatSupport + 1;
            }
            int generated = 0;
            var formats = formatCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key);
            var suffixes = suffixCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).ToList();
            foreach (string format in formats)
            foreach (string suffix in suffixes)
            {
                yield return (format.Replace("{suffix}", suffix, StringComparison.Ordinal), HashGuessStrategy.SuffixVariant);
                if (++generated >= candidateBudget) yield break;
            }
        }

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> GenerateGameSkinNumberCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            var directoryRegex = new Regex(@"/characters/([^/]+)/skins/(base|skin\d+)/", RegexOptions.IgnoreCase);
            var skinRegex = new Regex(@"(?:base|skin\d+)", RegexOptions.IgnoreCase);
            var characters = new Dictionary<string, (HashSet<string> Skins, Dictionary<(string Format, int Count), (int Support, int DistinctSupport)> Formats)>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in knownPaths)
            {
                Match directory = directoryRegex.Match(path);
                if (!directory.Success || directory.Groups[1].Value.Equals("sightward", StringComparison.OrdinalIgnoreCase)) continue;
                string character = directory.Groups[1].Value;
                if (!characters.TryGetValue(character, out var data))
                {
                    data = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<(string Format, int Count), (int Support, int DistinctSupport)>());
                    characters[character] = data;
                }
                data.Skins.Add(directory.Groups[2].Value.ToLowerInvariant());
                MatchCollection matches = skinRegex.Matches(path);
                int count = matches.Count;
                var format = (skinRegex.Replace(path, "{skin}"), count);
                data.Formats.TryGetValue(format, out var support);
                bool distinct = matches.Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == count;
                data.Formats[format] = (support.Support + 1, support.DistinctSupport + (distinct ? 1 : 0));
            }

            int generated = 0;
            var formats = characters.SelectMany(character => character.Value.Formats.Select(format => new
            {
                Character = character.Key,
                Skins = character.Value.Skins,
                Format = format.Key.Format,
                Count = format.Key.Count,
                Support = format.Value.Support,
                DistinctSupport = format.Value.DistinctSupport
            })).OrderBy(value => value.DistinctSupport > 0 ? 0 : 1)
                .ThenBy(value => value.Count == 2 ? 0 : value.Count == 1 ? 1 : value.Count)
                .ThenByDescending(value => value.DistinctSupport)
                .ThenByDescending(value => value.Support)
                .ThenBy(value => value.Character, StringComparer.Ordinal)
                .ThenBy(value => value.Format, StringComparer.Ordinal);

            foreach (var format in formats)
            {
                List<string> skins = format.Skins.OrderBy(value => value, StringComparer.Ordinal).ToList();
                if (format.Count > skins.Count) continue;
                foreach (IEnumerable<string> combination in GetPermutations(skins, format.Count))
                {
                    string candidate = format.Format;
                    foreach (string skin in combination)
                    {
                        int marker = candidate.IndexOf("{skin}", StringComparison.Ordinal);
                        candidate = candidate[..marker] + skin + candidate[(marker + 6)..];
                    }
                    yield return (candidate, HashGuessStrategy.SkinNumberVariant);
                    if (++generated >= candidateBudget) yield break;
                }
            }
        }

        private IEnumerable<(string Path, HashGuessStrategy Strategy)> ExtractCandidates(HashGuessDomain domain, byte[] data, string sourcePath)
        {
            if (data == null || data.Length == 0) yield break;

            string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            if (domain == HashGuessDomain.Game && extension is "bin" or "inibin")
            {
                foreach (int offset in FindGameBinPathOffsets(data))
                {
                    if (offset < 2) continue;
                    int length = data[offset - 2] | (data[offset - 1] << 8);
                    if (length <= 0 || offset + length > data.Length) continue;
                    string path = NormalizePath(Encoding.ASCII.GetString(data, offset, length));
                    foreach (var candidate in ExpandGamePath(path, HashGuessStrategy.BinLengthPath)) yield return candidate;
                }
                yield break;
            }

            string text = Encoding.ASCII.GetString(data);
            if (domain == HashGuessDomain.Lcu)
            {
                var candidates = new List<(string Path, HashGuessStrategy Strategy)>();
                bool stopAfterStructuredJson = false;

                if (sourcePath.Equals("plugins/rcp-fe-lol-loot/global/default/trans.json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            candidates.Add(($"plugins/rcp-be-lol-game-data/global/default/v1/hextech-images/{prop.Name.ToLowerInvariant()}.png", HashGuessStrategy.LcuPattern));
                        stopAfterStructuredJson = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogDebug($"Hash Lab skipped invalid translation JSON '{sourcePath}': {ex.Message}");
                    }
                }
                else if (sourcePath.Equals("plugins/rcp-be-lol-game-data/global/default/v1/champion-summary.json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        foreach (var elem in doc.RootElement.EnumerateArray())
                        {
                            if (elem.TryGetProperty("id", out var idProp))
                            {
                                int cid = idProp.GetInt32();
                                candidates.Add(($"plugins/rcp-be-lol-game-data/global/default/v1/champions/{cid}.json", HashGuessStrategy.LcuPattern));
                                candidates.Add(($"plugins/rcp-be-lol-game-data/global/default/v1/champion-splashes/{cid}/metadata.json", HashGuessStrategy.LcuPattern));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogDebug($"Hash Lab skipped invalid champion summary JSON '{sourcePath}': {ex.Message}");
                    }
                }

                if (text.Contains("pluginDependencies") && text.Contains("name"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("name", out var nameProp))
                        {
                            string name = nameProp.GetString()?.ToLowerInvariant();
                            if (!string.IsNullOrEmpty(name))
                            {
                                foreach (string subpath in new[] { "index.html", "init.js", "init.js.map", "bundle.js", "trans.json", "css/main.css", "license.json" })
                                    candidates.Add(($"plugins/{name}/global/default/{subpath}", HashGuessStrategy.LcuPattern));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogDebug($"Hash Lab skipped invalid plugin metadata JSON '{sourcePath}': {ex.Message}");
                    }
                }

                if (text.Contains("musicVolume") && text.Contains("files"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Object)
                        {
                            var splashNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var prop in filesProp.EnumerateObject())
                            {
                                string filePath = prop.Value.GetString()?.ToLowerInvariant() ?? string.Empty;
                                var splashMatch = Regex.Match(filePath, @"-splash-([^.]+)");
                                if (splashMatch.Success)
                                {
                                    string sName = splashMatch.Groups[1].Value;
                                    splashNames.Add(sName);
                                    candidates.Add(($"plugins/rcp-fe-lol-splash/global/default/splash-assets/{sName}/{filePath}", HashGuessStrategy.LcuPattern));
                                }
                            }
                            foreach (string sName in splashNames)
                            {
                                candidates.Add(($"plugins/rcp-fe-lol-splash/global/default/splash-assets/{sName}/config.json", HashGuessStrategy.LcuPattern));
                            }
                            stopAfterStructuredJson = true;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogDebug($"Hash Lab skipped invalid splash configuration JSON '{sourcePath}': {ex.Message}");
                    }
                }

                if (text.Contains("recommendedItemDefaults"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("recommendedItemDefaults", out var recProp) && recProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var val in recProp.EnumerateArray())
                            {
                                string valStr = val.GetString()?.ToLowerInvariant();
                                if (!string.IsNullOrEmpty(valStr))
                                    candidates.Add(($"plugins/rcp-be-lol-game-data/global/default{valStr}", HashGuessStrategy.LcuPattern));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogDebug($"Hash Lab skipped invalid recommended items JSON '{sourcePath}': {ex.Message}");
                    }
                }

                foreach (var candidate in candidates)
                    yield return candidate;

                if (stopAfterStructuredJson)
                    yield break;

                foreach (Match match in LcuPathRegex.Matches(text))
                    yield return (NormalizePath(match.Value), HashGuessStrategy.LcuEmbeddedPath);

                foreach (Match match in LcuFrontendPathRegex.Matches(text))
                    yield return ($"plugins/rcp-fe-{match.Groups[1].Value}/global/default/{match.Groups[2].Value}".ToLowerInvariant(), HashGuessStrategy.LcuEmbeddedPath);

                foreach (Match match in LcuDataPathRegex.Matches(text))
                    yield return ($"plugins/rcp-be-lol-game-data/global/default/data/{match.Groups[1].Value}".ToLowerInvariant(), HashGuessStrategy.LcuEmbeddedPath);

                foreach (Match match in LcuAssetPathRegex.Matches(text))
                    yield return ($"plugins/rcp-be-lol-game-data/global/default/{match.Groups[1].Value}".ToLowerInvariant(), HashGuessStrategy.LcuEmbeddedPath);

                foreach (Match match in LcuCssUrlRegex.Matches(text))
                {
                    string contextualPath = ResolveRelativeLcuPath(sourcePath, match.Groups[1].Value);
                    if (contextualPath.Length > 0)
                        yield return (contextualPath, HashGuessStrategy.LcuEmbeddedPath);
                }

                foreach (Match match in LcuHtmlAssetRegex.Matches(text))
                {
                    string contextualPath = ResolveRelativeLcuPath(sourcePath, match.Groups[1].Value);
                    if (contextualPath.Length > 0)
                        yield return (contextualPath, HashGuessStrategy.LcuEmbeddedPath);
                }

                var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in LcuRelativePathRegex.Matches(text))
                {
                    string relativePath = match.Groups[1].Value;
                    relativePaths.Add(relativePath);
                    string contextualPath = ResolveRelativeLcuPath(sourcePath, relativePath);
                    if (contextualPath.Length > 0)
                        yield return (contextualPath, HashGuessStrategy.LcuEmbeddedPath);
                }
                foreach (Match match in LcuFileNameRegex.Matches(text)) relativePaths.Add(match.Groups[1].Value);
                foreach (Match match in Regex.Matches(text, "<template id=\\\"[^\\\"]*-template-([^\\\"]+)\\\"")) relativePaths.Add(match.Groups[1].Value + "/template.html");
                foreach (Match match in Regex.Matches(text, "sourceMappingURL=(.*?\\.js)\\.map")) relativePaths.Add(match.Groups[1].Value);

                foreach (string relativePath in relativePaths)
                    yield return (NormalizePath(relativePath), HashGuessStrategy.LcuEmbeddedPath);
                yield break;
            }

            if (extension == "preload")
            {
                string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
                foreach (Match match in PreloadNameRegex.Matches(text))
                {
                    string path = NormalizePath(match.Groups[1].Value);
                    foreach (var candidate in ExpandGamePath(path, HashGuessStrategy.PreloadReference)) yield return candidate;
                    if (path.EndsWith(".troy", StringComparison.OrdinalIgnoreCase))
                        yield return ($"data/shared/particles/{path[..^5]}.troybin", HashGuessStrategy.PreloadReference);

                    if (!string.IsNullOrEmpty(directory))
                        yield return (directory + "/" + path + ".preload", HashGuessStrategy.PreloadReference);
                }
                yield break;
            }

            if (extension is "hls" or "ps_2_0" or "ps_3_0" or "vs_2_0" or "vs_3_0")
            {
                string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
                foreach (Match match in ShaderIncludeRegex.Matches(text))
                    yield return (NormalizePath(Path.Combine(directory, match.Groups[1].Value)), HashGuessStrategy.ShaderInclude);
                yield break;
            }

            if (extension == "atlas")
            {
                string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
                foreach (string line in text.Split('\n'))
                {
                    string candidate = NormalizePath(Path.Combine(directory, line.Trim()));
                    if (candidate.Length > 0) yield return (candidate, HashGuessStrategy.AtlasReference);
                }
                yield break;
            }

            foreach (Match match in GamePathRegex.Matches(text))
            {
                string candidate = NormalizePath(match.Value);
                foreach (var expanded in ExpandGamePath(candidate, HashGuessStrategy.EmbeddedPathGrep)) yield return expanded;

                if (match.Index >= 2)
                {
                    int length = data[match.Index - 2] | (data[match.Index - 1] << 8);
                    if (length == 0 && match.Index >= 4)
                    {
                        length = data[match.Index - 4] | (data[match.Index - 3] << 8) | (data[match.Index - 2] << 16) | (data[match.Index - 1] << 24);
                    }
                    if (length > 0 && length < match.Length && match.Index + length <= data.Length)
                    {
                        string shortPath = NormalizePath(Encoding.ASCII.GetString(data, match.Index, length));
                        foreach (var expanded in ExpandGamePath(shortPath, HashGuessStrategy.EmbeddedPathGrep)) yield return expanded;
                    }
                }
            }
        }

        private static string ResolveRelativeLcuPath(string sourcePath, string relativePath)
        {
            if (!sourcePath.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            relativePath = relativePath.Trim();
            if (relativePath.Length == 0 || relativePath.StartsWith('/') || relativePath.StartsWith('#') ||
                relativePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            int separator = sourcePath.LastIndexOf('/');
            if (separator < 0) return string.Empty;

            var segments = new List<string>(sourcePath[..separator].Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (string segment in relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue;
                if (segment == "..")
                {
                    if (segments.Count <= 1) return string.Empty;
                    segments.RemoveAt(segments.Count - 1);
                }
                else
                {
                    segments.Add(segment);
                }
            }
            return NormalizePath(string.Join('/', segments));
        }

        private static IReadOnlyList<int> FindGameBinPathOffsets(byte[] data)
        {
            var offsets = new HashSet<int>();
            for (int offset = 0; offset < data.Length; offset++)
            {
                byte first = ToUpperAscii(data[offset]);
                byte[][] needles = first switch
                {
                    (byte)'A' => GameBinPrefixesA,
                    (byte)'C' => GameBinPrefixesC,
                    (byte)'D' => GameBinPrefixesD,
                    (byte)'G' => GameBinPrefixesG,
                    (byte)'L' => GameBinPrefixesL,
                    (byte)'M' => GameBinPrefixesM,
                    (byte)'P' => GameBinPrefixesP,
                    (byte)'S' => GameBinPrefixesS,
                    (byte)'U' => GameBinPrefixesU,
                    _ => null
                };
                if (needles == null) continue;
                foreach (byte[] needle in needles)
                {
                    if (offset + needle.Length > data.Length) continue;
                    int index = 1;
                    while (index < needle.Length && ToUpperAscii(data[offset + index]) == needle[index]) index++;
                    if (index == needle.Length) offsets.Add(offset);
                }
            }
            return offsets.OrderBy(offset => offset).ToList();
        }

        private static byte ToUpperAscii(byte value) => value is >= (byte)'a' and <= (byte)'z'
            ? (byte)(value - ('a' - 'A'))
            : value;

        private static byte[][] ToAsciiPrefixes(params string[] prefixes) => prefixes.Select(Encoding.ASCII.GetBytes).ToArray();

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> ExpandGamePath(string candidate, HashGuessStrategy strategy)
        {
            if (candidate.Length == 0) yield break;
            yield return (candidate, strategy);

            if (candidate.Contains("data_soon/", StringComparison.OrdinalIgnoreCase))
            {
                string replaced = Regex.Replace(candidate, "data_soon/", "data/", RegexOptions.IgnoreCase);
                yield return (replaced, strategy);
            }

            if (candidate.StartsWith("characters/", StringComparison.OrdinalIgnoreCase))
            {
                yield return ($"assets/{candidate}", strategy);
                yield return ($"data/{candidate}", strategy);
            }

            if (candidate.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                string prefix = candidate[..^4];
                yield return (prefix + ".luabin", HashGuessStrategy.LuaVariant);
                yield return (prefix + ".luabin64", HashGuessStrategy.LuaVariant);
                yield return (prefix + ".preload", HashGuessStrategy.LuaVariant);
            }
            else if (candidate.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                yield return (candidate[..^4] + ".dds", HashGuessStrategy.ImageExtensionVariant);
            }
            else if (candidate.StartsWith("maps/mapgeometry/", StringComparison.OrdinalIgnoreCase))
            {
                yield return ($"data/{candidate}.mapgeo", strategy);
                yield return ($"data/{candidate}.materials.bin", strategy);
            }

            if (candidate.StartsWith("clientstates/", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("patching/", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("loadouts/", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
            {
                int firstSlash = candidate.LastIndexOf('/');
                if (firstSlash > 0)
                {
                    string parent = candidate[..firstSlash];
                    yield return (parent, strategy);
                    int secondSlash = parent.LastIndexOf('/');
                    if (secondSlash > 0)
                    {
                        yield return (parent[..secondSlash], strategy);
                    }
                }
            }

            if (candidate.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string ext in new[] { ".ps_2_0", ".ps_3_0", ".vs_2_0", ".vs_3_0", ".ps", ".vs" })
                {
                    yield return ($"assets/shaders/generated/{candidate}{ext}", strategy);
                    foreach (string variant in new[] { ".dx11", ".dx9", ".dx9sm3", ".glsl", ".metal" })
                    {
                        yield return ($"assets/shaders/generated/{candidate}{ext}{variant}", strategy);
                        for (int n = 0; n < 20000; n += 100)
                        {
                            yield return ($"assets/shaders/generated/{candidate}{ext}{variant}_{n}", strategy);
                        }
                    }
                }
            }
        }

        private static string NormalizePath(string value)
        {
            return HashGuessEngine.NormalizePath(value);
        }

        private static int RunFocusedWordlistSubstitution(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
        {
            var pathsList = paths.ToList();
            var wordsList = words.ToList();
            if (pathsList.Count == 0 || wordsList.Count == 0) return 0;

            var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var regexExtract = new Regex(@"([^/_.-]+)(?=[^/]*\.[^/]+$)", RegexOptions.Compiled);

            foreach (string path in pathsList)
            {
                if (path.Contains('%')) continue;
                foreach (Match m in regexExtract.Matches(path))
                {
                    string prefix = path[..m.Index];
                    string suffix = path[(m.Index + m.Length)..];
                    formats.Add(prefix + "{0}" + suffix);
                }
            }

            int checkedCount = 0;
            foreach (string format in formats)
            {
                foreach (string word in wordsList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(string.Format(format, word), HashGuessStrategy.WordlistVariant, "Focused Wordlist");
                    checkedCount++;
                    if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) return checkedCount;
                }
            }
            return checkedCount;
        }

        private static int RunFocusedWordlistDoubleSubstitution(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
        {
            var pathsList = paths.ToList();
            var wordsList = words.ToList();
            if (pathsList.Count == 0 || wordsList.Count == 0) return 0;

            var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var regexExtract = new Regex(@"([^/_.-]+)(?=[^/]*\.[^/]+$)", RegexOptions.Compiled);

            foreach (string path in pathsList)
            {
                if (path.Contains('%')) continue;
                var matches = regexExtract.Matches(path);
                if (matches.Count >= 2)
                {
                    for (int i = 0; i < matches.Count - 1; i++)
                    {
                        var m1 = matches[i];
                        var m2 = matches[i + 1];
                        string format = path[..m1.Index] + "{0}" + path[(m1.Index + m1.Length)..m2.Index] + "{1}" + path[(m2.Index + m2.Length)..];
                        formats.Add(format);
                    }
                }
            }

            int checkedCount = 0;
            foreach (string format in formats)
            {
                foreach (string w1 in wordsList.Take(150))
                {
                    foreach (string w2 in wordsList.Take(150))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        engine.Check(string.Format(format, w1, w2), HashGuessStrategy.WordlistVariant, "Double Wordlist");
                        checkedCount++;
                        if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) return checkedCount;
                    }
                }
            }
            return checkedCount;
        }

        private static int RunWordAdditionAttack(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
        {
            var pathsList = paths.ToList();
            var wordsList = words.ToList();
            if (pathsList.Count == 0 || wordsList.Count == 0) return 0;

            var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var regexExtract = new Regex(@"([^/_.-]+)(?=[^/]*\.[^/]+$)", RegexOptions.Compiled);

            foreach (string path in pathsList)
            {
                foreach (Match m in regexExtract.Matches(path))
                {
                    string prefix = path[..m.Index];
                    string suffix = path[m.Index..];
                    foreach (string sep in new[] { "-", "_" })
                    {
                        formats.Add(prefix + "{0}" + sep + suffix);
                        formats.Add(path[..(m.Index + m.Length)] + sep + "{0}" + path[(m.Index + m.Length)..]);
                    }
                }
            }

            int checkedCount = 0;
            foreach (string format in formats)
            {
                foreach (string word in wordsList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(string.Format(format, word), HashGuessStrategy.WordlistVariant, "Word Insertion");
                    checkedCount++;
                    if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) return checkedCount;
                }
            }
            return checkedCount;
        }

        private static int RunPrefixAttack(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> prefixes, CancellationToken cancellationToken, int candidateBudget = 2_000_000)
        {
            var pathsList = paths.ToList();
            var prefixesList = prefixes.ToList();
            int checkedCount = 0;

            foreach (string path in pathsList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int separator = path.LastIndexOf('/');
                string dir = separator >= 0 ? path[..(separator + 1)] : string.Empty;
                string file = separator >= 0 ? path[(separator + 1)..] : path;

                foreach (string prefix in prefixesList)
                {
                    engine.Check(dir + prefix + file, HashGuessStrategy.PrefixVariant, "Prefix variant");
                    checkedCount++;
                    if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) return checkedCount;
                }
            }

            return checkedCount;
        }

        private static IEnumerable<IEnumerable<T>> GetCombinations<T>(List<T> list, int length)
        {
            if (length == 1) return list.Select(t => new[] { t });
            return GetCombinations(list, length - 1)
                .SelectMany(t => list.Where(o => list.IndexOf(o) > list.IndexOf(t.Last())), (t1, t2) => t1.Concat(new[] { t2 }));
        }

        private static IEnumerable<IEnumerable<T>> GetPermutations<T>(IReadOnlyList<T> values, int length)
        {
            if (length == 1) return values.Select(value => new[] { value }.AsEnumerable());
            return values.SelectMany(
                (value, index) => GetPermutations(values.Where((_, candidateIndex) => candidateIndex != index).ToList(), length - 1),
                (value, tail) => new[] { value }.Concat(tail));
        }
    }
}
