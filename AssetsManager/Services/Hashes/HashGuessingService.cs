using System;
using System.Collections.Generic;
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
        private static readonly Regex PreloadNameRegex = new("Name=\\\"([^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly Regex ShaderIncludeRegex = new("#include \\\"([^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly Regex LocaleRegex = new(@"(?<![a-z])(?:ar_ae|ar_eg|cs_cz|de_de|el_gr|en_au|en_gb|en_ph|en_pl|en_sg|en_us|es_ar|es_es|es_mx|fr_fr|hu_hu|id_id|it_it|ja_jp|ko_kr|ms_my|pl_pl|pt_br|ro_ro|ru_ru|th_th|tr_tr|vi_vn|vn_vn|zh_cn|zh_my|zh_tw)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BasenameNumberRegex = new(@"[0-9]+(?=[^/]*\.[^/]+$)", RegexOptions.Compiled);
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

        public Task<HashSet<ulong>> GetStoreUnknownsAsync(HashGuessDomain domain, CancellationToken cancellationToken)
        {
            return _store.LoadUnknownHashesAsync(domain, cancellationToken);
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
            var unknownHashes = await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken);

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
                                    engine.Check(candidate.Path, candidate.Strategy, wadPath, chunk.PathHash);
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
            await _store.SaveUnknownHashesAsync(domain, remainingUnknowns, cancellationToken);

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
            CancellationToken cancellationToken)
        {
            await _hashResolverService.LoadAllHashesAsync();
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");

            string pattern = domain == HashGuessDomain.Game ? "*.wad.client" : "*.wad";
            string[] wadPaths = Directory.EnumerateFiles(rootDirectory, pattern, SearchOption.AllDirectories).ToArray();
            var unknownHashes = await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken);
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
            await _store.SaveUnknownHashesAsync(domain, remainingUnknowns, cancellationToken);
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
            var results = new List<HashGuessRunResult>();
            results.Add(await RunCanonicalGuessingAsync(HashGuessDomain.Game, rootDirectory, progress, cancellationToken));
            results.Add(await RunLanguageGuessingAsync(HashGuessDomain.Game, rootDirectory, progress, cancellationToken));
            results.Add(await RunNumberGuessingAsync(HashGuessDomain.Game, rootDirectory, progress, cancellationToken));
            results.Add(await RunGameCrossDomainGuessingAsync(rootDirectory, progress, cancellationToken));

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
            var results = new List<HashGuessRunResult>
            {
                await RunCanonicalGuessingAsync(HashGuessDomain.Lcu, rootDirectory, progress, cancellationToken),
                await RunLanguageGuessingAsync(HashGuessDomain.Lcu, rootDirectory, progress, cancellationToken),
                await RunNumberGuessingAsync(HashGuessDomain.Lcu, rootDirectory, progress, cancellationToken),
                await RunLcuSupplementalGuessingAsync(rootDirectory, progress, cancellationToken)
            };

            var matches = results.SelectMany(result => result.Matches).GroupBy(match => match.Hash)
                .Select(group => group.First()).OrderBy(match => match.Path, StringComparer.OrdinalIgnoreCase).ToList();
            return new HashGuessRunResult { Domain = HashGuessDomain.Lcu, UnknownHashesAtStart = results.FirstOrDefault()?.UnknownHashesAtStart ?? 0, ScannedChunks = results.Sum(result => result.ScannedChunks), Matches = matches };
        }

        public async Task<HashGuessRunResult> RunLcuAdvancedGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            string[] wads = Directory.EnumerateFiles(rootDirectory, "*.wad", SearchOption.AllDirectories).ToArray();
            var unknown = await BuildUnknownInventoryAsync(HashGuessDomain.Lcu, wads, cancellationToken);
            int initial = unknown.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Lcu, unknown);
                var paths = LoadKnownPaths(HashGuessDomain.Lcu).ToList();
                var names = paths.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(25000).ToList();
                var directories = HashGuessEngine.BuildDirectoryList(paths).Take(25000).ToList();
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
                    var words = HashGuessEngine.BuildWordlist(paths).Take(5000).ToList();
                    int wordBudget = 500000;
                    foreach (string path in paths.Take(100000))
                    {
                        Match word = Regex.Match(path, @"([^/_.-]+)(?=[^/]*\.[^/]+$)");
                        if (!word.Success) continue;
                        string format = path[..word.Index] + "{0}" + path[(word.Index + word.Length)..];
                        foreach (string replacement in words)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            engine.Check(string.Format(format, replacement), HashGuessStrategy.PluginVariant, "LCU Advanced word substitution");
                            checkedCandidates++;
                            if (--wordBudget == 0 || engine.RemainingUnknownCount == 0) goto CompleteWords;
                        }
                    }
                }

            CompleteWords:
                if (engine.RemainingUnknownCount > 0)
                {
                    var words = HashGuessEngine.BuildWordlist(paths).Take(3000).ToList();
                    int insertionBudget = 500000;
                    foreach (string path in paths.Take(75000))
                    {
                        Match word = Regex.Match(path, @"([^/_.-]+)(?=[^/]*\.[^/]+$)");
                        if (!word.Success) continue;
                        foreach (string value in words)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            engine.Check(path.Insert(word.Index, value + "_"), HashGuessStrategy.PluginVariant, "LCU Advanced word insertion");
                            engine.Check(path.Insert(word.Index + word.Length, "_" + value), HashGuessStrategy.PluginVariant, "LCU Advanced word insertion");
                            checkedCandidates += 2;
                            insertionBudget -= 2;
                            if (insertionBudget <= 0 || engine.RemainingUnknownCount == 0) goto CompleteInsertions;
                        }
                    }
                }

            CompleteInsertions:
                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: LCU static-assets" });
                    var staticAssetsPaths = paths.Where(p => p.StartsWith("plugins/rcp-fe-lol-static-assets/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)).ToList();
                    var sswordlist = HashGuessEngine.BuildContextualWordlist(staticAssetsPaths, paths.Where(p => (p.Contains("-fe-lol-") && p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) || p.Contains("fe-lol-static-assets"))).Take(5000).ToList();
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
                        var partiesWords = HashGuessEngine.BuildContextualWordlist(partiesPaths, paths.Where(p => p.Contains("-fe-lol-") && p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))).Take(5000).ToList();
                        checkedCandidates += RunFocusedWordlistSubstitution(engine, partiesPaths, partiesWords, cancellationToken);
                    }

                    if (engine.RemainingUnknownCount > 0)
                    {
                        progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: LCU word additions" });
                        var lcuWords = HashGuessEngine.BuildWordlist(paths).Take(5000).ToList();
                        checkedCandidates += RunWordAdditionAttack(engine, paths.Take(20000), lcuWords, cancellationToken);
                    }
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Focused Attack: LCU TFT patterns" });
                    var words = HashGuessEngine.BuildWordlist(paths).Take(1500).ToList();
                    int tftBudget = 1_000_000;
                    foreach (string a in words)
                    {
                        foreach (string b in words)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{b}{a}s.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{b}-{a}s.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{a}-{b}s.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{a}{b}s.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{b}{a}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{b}-{a}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{a}-{b}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{a}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{b}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{a}s.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft{b}s.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/tft-{a}{b}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{a}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{b}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{b}{a}s.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{a}{b}n.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{b}-{a}s.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{a}-{b}s.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{a}{b}s.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{b}{a}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{b}-{a}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{a}-{b}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{a}{b}.json", HashGuessStrategy.LcuPattern, "TFT pattern variant");
                            engine.Check($"plugins/rcp-be-lol-game-data/global/default/v1/{a}{b}", HashGuessStrategy.LcuPattern, "TFT pattern variant");

                            checkedCandidates += 24;
                            tftBudget -= 24;
                            if (tftBudget <= 0 || engine.RemainingUnknownCount == 0) goto CompleteTft;
                        }
                    }
                }
            CompleteTft:
                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, checkedCandidates, engine.UnknownHashes);
            }, cancellationToken);

            var matches = runResult.Item1;
            int checkedCandidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await _store.SaveResearchMatchesAsync(matches, cancellationToken);
            await _store.SaveUnknownHashesAsync(HashGuessDomain.Lcu, remainingUnknowns, cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Lcu, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        private async Task<HashGuessRunResult> RunLcuSupplementalGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            string[] wads = Directory.EnumerateFiles(rootDirectory, "*.wad", SearchOption.AllDirectories).ToArray();
            var unknown = await BuildUnknownInventoryAsync(HashGuessDomain.Lcu, wads, cancellationToken);
            int initial = unknown.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Lcu, unknown);
                int checkedCandidates = 0;
                var knownPaths = LoadKnownPaths(HashGuessDomain.Lcu).ToList();
                var phases = new (string Name, IEnumerable<(string Path, HashGuessStrategy Strategy)> Candidates)[]
                {
                    ("plugin variants", GenerateLcuPluginCandidates(knownPaths, 1_000_000)),
                    ("extension variants", GenerateLcuExtensionCandidates(knownPaths, 500_000)),
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
            await _store.SaveUnknownHashesAsync(HashGuessDomain.Lcu, remainingUnknowns, cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Lcu, UnknownHashesAtStart = initial, ScannedChunks = checkedCandidates, Matches = matches };
        }

        public async Task<HashGuessRunResult> RunGameExtendedGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            string[] wads = Directory.EnumerateFiles(rootDirectory, "*.wad.client", SearchOption.AllDirectories).ToArray();
            var unknown = await BuildUnknownInventoryAsync(HashGuessDomain.Game, wads, cancellationToken);
            int initial = unknown.Count;
            var runResult = await Task.Run(async () =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Game, unknown);
                int checkedCandidates = 0;
                const int budget = 2_000_000;

                foreach (var candidate in GenerateGameExtendedCandidates(LoadKnownPaths(HashGuessDomain.Game), budget))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.Check(candidate.Path, candidate.Strategy, "GAME Extended");
                    checkedCandidates++;
                    if (checkedCandidates % 5000 == 0)
                        progress?.Report(new HashGuessProgress { ProcessedChunks = checkedCandidates, FoundMatches = engine.Matches.Count, CurrentWad = "Generating GAME Extended candidates" });
                    if (engine.RemainingUnknownCount == 0) break;
                }

                if (engine.RemainingUnknownCount > 0)
                {
                    var knownPaths = LoadKnownPaths(HashGuessDomain.Game).ToList();
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
                        var gameWords = HashGuessEngine.BuildWordlist(knownPaths).Take(20000).ToList();
                        checkedCandidates += RunWordAdditionAttack(engine, knownPaths.Take(20000), gameWords, cancellationToken);
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
                    var gameDirs = HashGuessEngine.BuildDirectoryList(gamePaths).Take(5000).ToList();
                    var gameNames = gamePaths.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5000).ToList();
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
            await _store.SaveUnknownHashesAsync(HashGuessDomain.Game, remainingUnknowns, cancellationToken);
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
                    catch
                    {
                        // Ignore file read errors
                    }
                }
            }
            catch
            {
                // Ignore directory enum errors
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

        private async Task<HashGuessRunResult> RunGameCrossDomainGuessingAsync(string rootDirectory, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            string[] wads = Directory.EnumerateFiles(rootDirectory, "*.wad.client", SearchOption.AllDirectories).ToArray();
            var unknown = await BuildUnknownInventoryAsync(HashGuessDomain.Game, wads, cancellationToken);
            int initial = unknown.Count;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(HashGuessDomain.Game, unknown);
                int candidates = 0;

                foreach (string lcuPath in LoadKnownPaths(HashGuessDomain.Lcu))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Match match = Regex.Match(lcuPath, @"^plugins/rcp-be-lol-game-data/global/default/((?:assets|data)/.*)\.(png|jpg|json)$", RegexOptions.IgnoreCase);
                    if (!match.Success) continue;

                    string path = match.Groups[1].Value;
                    string extension = match.Groups[2].Value;
                    engine.Check(extension.Equals("json", StringComparison.OrdinalIgnoreCase) ? path + ".json" : path + ".dds", HashGuessStrategy.CrossDomainGame, "LCU to GAME");
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

                string binEntriesPath = Path.Combine(_directoriesCreator.HashesPath, "hashes.binentries.txt");
                if (File.Exists(binEntriesPath))
                {
                    var extensions = LoadKnownPaths(HashGuessDomain.Game).Select(Path.GetExtension).Where(extension => !extension.Contains("dx", StringComparison.OrdinalIgnoreCase) && !extension.Contains("glsl", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
                    foreach (string line in File.ReadLines(binEntriesPath))
                    {
                        int separator = line.IndexOf(' ');
                        if (separator < 0 || separator == line.Length - 1) continue;
                        string basename = Path.GetFileName(line[(separator + 1)..]).ToLowerInvariant();
                        foreach (string extension in extensions) engine.Check(basename + extension, HashGuessStrategy.BinEntry, "BIN entry basename");
                        candidates += extensions.Count;
                        if (engine.RemainingUnknownCount == 0) break;
                    }
                }

                var matches = engine.Matches.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return (matches, candidates, engine.UnknownHashes);
            }, cancellationToken);

            var matches = runResult.Item1;
            int candidates = runResult.Item2;
            var remainingUnknowns = runResult.Item3;
            await _store.SaveResearchMatchesAsync(matches, cancellationToken);
            await _store.SaveUnknownHashesAsync(HashGuessDomain.Game, remainingUnknowns, cancellationToken);
            return new HashGuessRunResult { Domain = HashGuessDomain.Game, UnknownHashesAtStart = initial, ScannedChunks = candidates, Matches = matches };
        }

        public async Task<HashGuessRunResult> RunLanguageGuessingAsync(
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
            var unknownHashes = await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken);
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
            await _store.SaveUnknownHashesAsync(domain, remainingUnknowns, cancellationToken);
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
            CancellationToken cancellationToken)
        {
            await _hashResolverService.LoadAllHashesAsync();
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");

            string pattern = domain == HashGuessDomain.Game ? "*.wad.client" : "*.wad";
            string[] wadPaths = Directory.EnumerateFiles(rootDirectory, pattern, SearchOption.AllDirectories).ToArray();
            var unknownHashes = await BuildUnknownInventoryAsync(domain, wadPaths, cancellationToken);
            int unknownAtStart = unknownHashes.Count;
            int numberLimit = domain == HashGuessDomain.Game ? 100 : 10_000;
            const int candidateBudget = 2_000_000;
            var runResult = await Task.Run(() =>
            {
                var engine = new HashGuessEngine(domain, unknownHashes);
                int checkedCandidates = 0;

                foreach (string candidate in GenerateNumberCandidates(LoadKnownPaths(domain), numberLimit, candidateBudget))
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
            await _store.SaveUnknownHashesAsync(domain, remainingUnknowns, cancellationToken);
            _logService.LogSuccess($"Hash Lab {domain} number guessing completed: {resultMatches.Count} paths resolved from {unknownAtStart} unknown hashes.");
            return new HashGuessRunResult
            {
                Domain = domain,
                UnknownHashesAtStart = unknownAtStart,
                ScannedChunks = checkedCandidates,
                Matches = resultMatches
            };
        }

        private async Task<HashSet<ulong>> BuildUnknownInventoryAsync(HashGuessDomain domain, IEnumerable<string> wadPaths, CancellationToken cancellationToken)
        {
            var unknown = await _store.LoadUnknownHashesAsync(domain, cancellationToken);
            unknown.RemoveWhere(hash => _hashResolverService.IsKnownHash(hash));

            await Task.Run(() =>
            {
                foreach (string wadPath in wadPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var wad = new WadFile(wadPath);
                        foreach (ulong hash in wad.Chunks.Keys)
                        {
                            if (!_hashResolverService.IsKnownHash(hash))
                                unknown.Add(hash);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logService.LogError(ex, $"Hash Lab could not build inventory from WAD '{wadPath}'.");
                    }
                }
            }, cancellationToken);

            return unknown;
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

        private static IEnumerable<string> GenerateLcuCrossDomainCandidates(IEnumerable<string> gamePaths)
        {
            const string basePath = "plugins/rcp-be-lol-game-data/global/default/";
            foreach (string path in gamePaths)
            {
                if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = path[..^4];
                    yield return basePath + prefix + ".png";
                    yield return basePath + prefix + ".jpg";
                }
                else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    yield return basePath + path;
                }
            }
        }

        private static IEnumerable<string> GenerateLanguageCandidates(HashGuessDomain domain, IEnumerable<string> knownPaths)
        {
            foreach (string path in knownPaths)
            {
                if (domain == HashGuessDomain.Game)
                {
                    if (!LocaleRegex.IsMatch(path)) continue;
                    foreach (string locale in Locales)
                        yield return LocaleRegex.Replace(path, locale);
                    continue;
                }

                Match lcuPath = Regex.Match(path, @"^plugins/([^/]+)/([^/]+)/([^/]+)/(.*)$", RegexOptions.IgnoreCase);
                if (!lcuPath.Success) continue;

                string plugin = lcuPath.Groups[1].Value;
                string suffix = lcuPath.Groups[4].Value;
                foreach (string region in Regions)
                    foreach (string locale in Locales.Append("default"))
                        yield return $"plugins/{plugin}/{region}/{locale}/{suffix}";
            }
        }

        private static IEnumerable<string> GenerateNumberCandidates(IEnumerable<string> knownPaths, int numberLimit, int candidateBudget)
        {
            int produced = 0;
            foreach (string path in knownPaths)
            {
                foreach (Match match in BasenameNumberRegex.Matches(path))
                {
                    string format = path[..match.Index] + "{0}" + path[(match.Index + match.Length)..];
                    for (int value = 0; value < numberLimit; value++)
                    {
                        yield return string.Format(format, value);
                        produced++;
                        if (produced >= candidateBudget) yield break;

                        // Try with 2-digit padding (e.g. 01, 02)
                        yield return string.Format(format, value.ToString("D2"));
                        produced++;
                        if (produced >= candidateBudget) yield break;

                        // Try with 3-digit padding (e.g. 001, 002)
                        yield return string.Format(format, value.ToString("D3"));
                        produced++;
                        if (produced >= candidateBudget) yield break;
                    }
                }
            }
        }

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> GenerateLcuPluginCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            var paths = knownPaths.Where(path => path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)).ToList();
            var plugins = paths.Select(path => path.Split('/')[1]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int generated = 0;

            foreach (string path in paths.Take(100000))
            {
                string[] parts = path.Split('/', 3);
                if (parts.Length == 3)
                    foreach (string plugin in plugins)
                    {
                        yield return ("plugins/" + plugin + "/" + parts[2], HashGuessStrategy.PluginVariant);
                        if (++generated >= candidateBudget) yield break;
                    }
            }
        }

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> GenerateLcuExtensionCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            var paths = knownPaths.Where(path => path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)).ToList();
            var extensions = paths.Select(Path.GetExtension).Where(extension => extension.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
            int generated = 0;

            foreach (string path in paths.Take(100000))
            {
                string prefix = Path.ChangeExtension(path, null);
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

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> GenerateGameExtendedCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            var paths = knownPaths.ToList();
            var characters = paths.Select(path => Regex.Match(path, @"^(?:assets/|data/)?characters/([^/.]+)/", RegexOptions.IgnoreCase))
                .Where(match => match.Success).Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int generated = 0;
            var suffixes = paths.Select(value => Regex.Match(value, @"^.*?(\.[^.]+)?\.[^.]+$").Groups[1].Value)
                .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
            var extensions = paths.Select(Path.GetExtension).Where(extension => extension.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();

            foreach (string path in paths)
            {
                Match skin = Regex.Match(path, @"/characters/([^/]+)/skins/(base|skin\d+)/", RegexOptions.IgnoreCase);
                if (skin.Success)
                {
                    string format = path[..skin.Groups[2].Index] + "{0}" + path[(skin.Groups[2].Index + skin.Groups[2].Length)..];
                    foreach (string value in Enumerable.Range(0, 200).Select(number => "skin" + number).Append("base"))
                    {
                        yield return (string.Format(format, value), HashGuessStrategy.SkinNumberVariant);
                        if (++generated >= candidateBudget) yield break;
                    }
                }

                Match character = Regex.Match(path, @"^(.*?/characters/)([^/]+)(/.*)$", RegexOptions.IgnoreCase);
                if (character.Success)
                {
                    foreach (string value in characters)
                    {
                        yield return (character.Groups[1].Value + value + character.Groups[3].Value, HashGuessStrategy.CharacterSubstitution);
                        if (++generated >= candidateBudget) yield break;
                    }
                }

                Match suffix = Regex.Match(path, @"^(.*?)(\.[^.]+)?(\.[^.]+)$");
                if (suffix.Success)
                {
                    string prefix = suffix.Groups[1].Value;
                    string extension = suffix.Groups[3].Value;
                    foreach (string knownSuffix in suffixes)
                    {
                        yield return (prefix + knownSuffix + extension, HashGuessStrategy.SuffixVariant);
                        if (++generated >= candidateBudget) yield break;
                    }
                }

                string extPrefix = Path.ChangeExtension(path, null);
                if (!string.IsNullOrEmpty(extPrefix))
                {
                    foreach (string extension in extensions)
                    {
                        yield return (extPrefix + extension, HashGuessStrategy.ExtensionVariant);
                        if (++generated >= candidateBudget) yield break;
                    }
                }
            }
        }

        private static IEnumerable<(string Path, HashGuessStrategy Strategy)> ExtractCandidates(HashGuessDomain domain, byte[] data, string sourcePath)
        {
            if (data == null || data.Length == 0) yield break;

            string text = Encoding.ASCII.GetString(data);
            if (domain == HashGuessDomain.Lcu)
            {
                var candidates = new List<(string Path, HashGuessStrategy Strategy)>();

                if (sourcePath.EndsWith("trans.json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            candidates.Add(($"plugins/rcp-be-lol-game-data/global/default/v1/hextech-images/{prop.Name.ToLowerInvariant()}.png", HashGuessStrategy.LcuPattern));
                    }
                    catch { }
                }
                else if (sourcePath.EndsWith("champion-summary.json", StringComparison.OrdinalIgnoreCase))
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
                    catch { }
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
                    catch { }
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
                        }
                    }
                    catch { }
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
                    catch { }
                }

                foreach (var candidate in candidates)
                    yield return candidate;

                foreach (Match match in LcuPathRegex.Matches(text))
                    yield return (NormalizePath(match.Value), HashGuessStrategy.LcuEmbeddedPath);

                foreach (Match match in LcuFrontendPathRegex.Matches(text))
                    yield return ($"plugins/rcp-fe-{match.Groups[1].Value}/global/default/{match.Groups[2].Value}".ToLowerInvariant(), HashGuessStrategy.LcuEmbeddedPath);

                foreach (Match match in LcuDataPathRegex.Matches(text))
                    yield return ($"plugins/rcp-be-lol-game-data/global/default/data/{match.Groups[1].Value}".ToLowerInvariant(), HashGuessStrategy.LcuEmbeddedPath);

                foreach (Match match in LcuAssetPathRegex.Matches(text))
                    yield return ($"plugins/rcp-be-lol-game-data/global/default/{match.Groups[1].Value}".ToLowerInvariant(), HashGuessStrategy.LcuEmbeddedPath);

                var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in LcuRelativePathRegex.Matches(text)) relativePaths.Add(match.Groups[1].Value);
                foreach (Match match in LcuFileNameRegex.Matches(text)) relativePaths.Add(match.Groups[1].Value);
                foreach (Match match in Regex.Matches(text, "<template id=\\\"[^\\\"]*-template-([^\\\"]+)\\\"")) relativePaths.Add(match.Groups[1].Value + "/template.html");
                foreach (Match match in Regex.Matches(text, "sourceMappingURL=(.*?\\.js)\\.map")) relativePaths.Add(match.Groups[1].Value);

                foreach (string relativePath in relativePaths)
                    yield return (NormalizePath(relativePath), HashGuessStrategy.LcuEmbeddedPath);
                yield break;
            }

            string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            if (extension is "bin" or "inibin")
            {
                var binPrefixRegex = new Regex(@"(?:ASSETS|COMMON|DATA|DATA_SOON|GAMEPLAY|GLOBAL|LEVELS|LOADOUTS|UX|UIAUTOATLAS|CHARACTERS|SHADERS|MAPS|CLIENTSTATES|PATCHING)/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                foreach (Match match in binPrefixRegex.Matches(text))
                {
                    if (match.Index < 2) continue;
                    int length = data[match.Index - 2] | (data[match.Index - 1] << 8);
                    if (length <= 0 || match.Index + length > data.Length) continue;
                    string path = NormalizePath(Encoding.ASCII.GetString(data, match.Index, length));
                    foreach (var candidate in ExpandGamePath(path, HashGuessStrategy.BinLengthPath)) yield return candidate;
                }
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
                    for (int i = 0; i < matches.Count; i++)
                    {
                        for (int j = i + 1; j < matches.Count; j++)
                        {
                            var m1 = matches[i];
                            var m2 = matches[j];
                            string format = path[..m1.Index] + "{0}" + path[(m1.Index + m1.Length)..m2.Index] + "{1}" + path[(m2.Index + m2.Length)..];
                            formats.Add(format);
                        }
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

        private static int RunPrefixAttack(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> prefixes, CancellationToken cancellationToken)
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
                    if (engine.RemainingUnknownCount == 0) return checkedCount;
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
    }
}
