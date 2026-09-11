using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal sealed partial class GameHashGuesser
    {

        internal int AddBasenameWord(HashGuessEngine engine, CancellationToken cancellationToken, int candidateBudget = int.MaxValue)
        {
            var paths = Corpus.GetOrCreate("word-addition-paths", values => values.Where(path =>
                !path.Contains("assets/characters/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("vo/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("sfx/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("skins_skin", StringComparison.OrdinalIgnoreCase)).ToList());
            IReadOnlyList<string> words = BuildWordlist();
            return _AddBasenameWord(
                engine,
                paths,
                words,
                cancellationToken,
                candidateBudget,
                source: "GAME basename word addition");
        }

        internal IEnumerable<HashGuessCandidate> SubstituteCharacter(int candidateBudget = int.MaxValue) => GenerateCharacterSubstitutionCandidates(candidateBudget);
        internal IEnumerable<HashGuessCandidate> SubstituteSkinNumbers(int candidateBudget = int.MaxValue) => GenerateSkinNumberCandidates(candidateBudget);
        internal IEnumerable<HashGuessCandidate> SubstituteSuffixes(int candidateBudget = int.MaxValue) => GenerateSuffixCandidates(candidateBudget);

        internal IEnumerable<HashGuessCandidate> GenerateCharacterSubstitutionCandidates(int candidateBudget)
        {
            var characterCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var formatCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var regex = new Regex(@"^(?:assets|data)/characters/([^/]+)/", RegexOptions.IgnoreCase);
            foreach (string path in KnownPaths)
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
            int generated = 0;
            foreach (string format in formatCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key))
            foreach (string character in characters)
            {
                yield return new HashGuessCandidate(format.Replace("{character}", character, StringComparison.Ordinal), HashGuessStrategy.CharacterSubstitution);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal IEnumerable<HashGuessCandidate> GenerateSuffixCandidates(int candidateBudget)
        {
            var suffixCounts = new Dictionary<string, int>(StringComparer.Ordinal) { [string.Empty] = int.MaxValue };
            var formatCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var regex = new Regex(@"^(.*?)(\.[^.]+)?(\.[^.]+)$");
            foreach (string path in KnownPaths)
            {
                Match match = regex.Match(path);
                if (!match.Success) continue;
                string suffix = match.Groups[2].Value;
                if (suffix.Length > 0)
                {
                    suffixCounts.TryGetValue(suffix, out int support);
                    suffixCounts[suffix] = support + 1;
                }
                string format = match.Groups[1].Value + "{suffix}" + match.Groups[3].Value;
                formatCounts.TryGetValue(format, out int formatSupport);
                formatCounts[format] = formatSupport + 1;
            }
            int generated = 0;
            var suffixes = suffixCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).ToList();
            foreach (string format in formatCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key))
            foreach (string suffix in suffixes)
            {
                yield return new HashGuessCandidate(format.Replace("{suffix}", suffix, StringComparison.Ordinal), HashGuessStrategy.SuffixVariant);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal IEnumerable<HashGuessCandidate> GenerateSkinNumberCandidates(int candidateBudget)
        {
            var directoryRegex = new Regex(@"/characters/([^/]+)/skins/(base|skin\d+)/", RegexOptions.IgnoreCase);
            var skinRegex = new Regex(@"(?:base|skin\d+)", RegexOptions.IgnoreCase);
            var characters = new Dictionary<string, (HashSet<string> Skins, HashSet<(string Format, int Count)> Formats)>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in KnownPaths)
            {
                Match directory = directoryRegex.Match(path);
                if (!directory.Success || directory.Groups[1].Value.Equals("sightward", StringComparison.OrdinalIgnoreCase)) continue;
                string character = directory.Groups[1].Value;
                if (!characters.TryGetValue(character, out var data))
                {
                    data = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<(string Format, int Count)>());
                    characters[character] = data;
                }
                data.Skins.Add(directory.Groups[2].Value.ToLowerInvariant());
                MatchCollection matches = skinRegex.Matches(path);
                data.Formats.Add((skinRegex.Replace(path, "{skin}"), matches.Count));
            }
            int generated = 0;
            foreach (var character in characters.OrderBy(value => value.Key, StringComparer.Ordinal))
            foreach ((string format, int count) in character.Value.Formats
                .OrderBy(value => value.Format, StringComparer.Ordinal)
                .ThenBy(value => value.Count))
            {
                List<string> skins = character.Value.Skins.OrderBy(value => value, StringComparer.Ordinal).ToList();
                if (count > skins.Count) continue;
                foreach (IEnumerable<string> combination in GetCombinations(skins, count))
                {
                    string candidate = format;
                    foreach (string skin in combination)
                    {
                        int marker = candidate.IndexOf("{skin}", StringComparison.Ordinal);
                        candidate = candidate[..marker] + skin + candidate[(marker + 6)..];
                    }
                    yield return new HashGuessCandidate(candidate, HashGuessStrategy.SkinNumberVariant);
                    if (CountCandidate(ref generated, candidateBudget)) yield break;
                }
            }
        }

        internal async Task<int> RunExtendedAttacksAsync(
            HashGuessEngine engine,
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IReadOnlySet<string> selectedSubMethods = null)
        {
            int checkedCandidates = 0;
            bool ShouldRun(string subId) => selectedSubMethods == null || selectedSubMethods.Contains(subId);

            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-skingroups"))
                checkedCandidates += await GuessSkinGroupsBin(engine, cancellationToken, progress, checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-chromas"))
                checkedCandidates += await GuessSkinGroupsBinUsingChromas(engine, rootDirectory, cancellationToken, progress, checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-suffixes"))
                checkedCandidates += CheckCandidates(engine, SubstituteSuffixes(), "GAME suffix substitution", cancellationToken, progress, checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-skinnumbers"))
                checkedCandidates += CheckCandidates(
                    engine,
                    SubstituteSkinNumbers(),
                    "GAME skin number combinations",
                    cancellationToken,
                    progress,
                    checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-characters"))
                checkedCandidates += CheckCandidates(
                    engine,
                    SubstituteCharacter(),
                    "GAME character substitution",
                    cancellationToken,
                    progress,
                    checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-wordaddition"))
            {
                int progressOffset = checkedCandidates;
                checkedCandidates += AddBasenameWord(
                    engine,
                    cancellationToken,
                    candidateBudget: int.MaxValue);
                progress?.Report(engine.CreateProgress("GAME basename word addition", checkedCandidates));
            }

            GetCharacters();
            return checkedCandidates;
        }

        private int CheckCandidates(
            HashGuessEngine engine,
            IEnumerable<HashGuessCandidate> candidates,
            string source,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0)
        {
            progress?.Report(engine.CreateProgress(source, progressOffset));
            return CheckIter(
                engine,
                candidates,
                source,
                cancellationToken,
                count => progress?.Report(engine.CreateProgress(source, progressOffset + count)),
                5000);
        }

        internal async Task<int> GuessChromaGroupsAsync(
            HashGuessEngine engine,
            string rootDirectory,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory)) return 0;
            const string stageName = "GAME Extended: chroma group bins";
            progress?.Report(engine.CreateProgress(stageName, progressOffset));

            string json = await Task.Run(() => LoadLocalSkinsJson(rootDirectory, cancellationToken), cancellationToken);
            if (json == null) return 0;
            try
            {
                using var document = JsonDocument.Parse(json);
                var groups = new Dictionary<string, List<List<int>>>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty skin in document.RootElement.EnumerateObject())
                {
                    JsonElement value = skin.Value;
                    if (!value.TryGetProperty("loadScreenPath", out JsonElement loadScreen)) continue;
                    Match champion = Regex.Match(loadScreen.GetString() ?? string.Empty, @"/assets/characters/([^/]+)/skins/", RegexOptions.IgnoreCase);
                    if (!champion.Success) continue;
                    long skinId;
                    if (!long.TryParse(skin.Name, out skinId) &&
                        (!value.TryGetProperty("id", out JsonElement id) || !id.TryGetInt64(out skinId))) continue;
                    var ids = new HashSet<int> { (int)(skinId % 1000) };
                    if (value.TryGetProperty("chromas", out JsonElement chromas) && chromas.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement chroma in chromas.EnumerateArray())
                            if (chroma.TryGetProperty("id", out JsonElement chromaId) && chromaId.TryGetInt64(out long chromaValue))
                                ids.Add((int)(chromaValue % 1000));
                    string character = champion.Groups[1].Value.ToLowerInvariant();
                    groups.TryAdd(character, new List<List<int>>());
                    groups[character].Add(ids.OrderBy(id => id).ToList());
                }
                var knownCharacters = GetCharacters();
                if (knownCharacters.Count > 0)
                {
                    var filtered = groups.Where(pair => knownCharacters.Contains(pair.Key))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    if (filtered.Count > 0) groups = filtered;
                }
                int generated = 0;
                foreach (var pair in groups)
                {
                    var tokens = pair.Value
                        .Select(group => group.Select(id => "_skins_skin" + id).ToList())
                        .GroupBy(group => string.Join('\0', group), StringComparer.Ordinal)
                        .Select(group => group.First())
                        .Append(new List<string> { "_skins_root" }).ToList();
                    for (int length = 1; length <= tokens.Count; length++)
                    {
                        foreach (var combination in GetCombinations(tokens, length))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string suffix = string.Concat(combination.SelectMany(value => value).OrderBy(value => value, StringComparer.Ordinal));
                            Check(engine, "data/" + pair.Key + suffix + ".bin", HashGuessStrategy.ChromaGroupVariant, "Local skins.json chroma groups");
                            if (generated < int.MaxValue) generated++;
                            if ((generated % 5000) == 0)
                            {
                                progress?.Report(engine.CreateProgress(stageName, progressOffset + generated));
                            }
                            if (engine.RemainingUnknownCount == 0) return generated;
                        }
                    }
                }
                progress?.Report(engine.CreateProgress(stageName, progressOffset + generated));
                return generated;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService.LogWarning("Hash Lab skipped skins.json chroma groups: " + exception.Message);
                return 0;
            }
        }

        internal Task<int> GuessSkinGroupsBinUsingChromas(
            HashGuessEngine engine,
            string rootDirectory,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0) =>
            GuessChromaGroupsAsync(engine, rootDirectory, cancellationToken, progress, progressOffset);

        private string LoadLocalSkinsJson(string rootDirectory, CancellationToken cancellationToken)
        {
            ulong skinsJsonHash = XxHash64Ext.Hash(RiotCatalogDefinitions.SkinsJsonPath);
            IEnumerable<string> wadPaths = Directory.EnumerateFiles(rootDirectory, "*.wad*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path.Contains("game-data", StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (string wadPath in wadPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var wad = new WadFile(wadPath);
                    if (!wad.Chunks.TryGetValue(skinsJsonHash, out WadChunk chunk)) continue;
                    using var dataOwner = wad.LoadChunkDecompressed(chunk);
                    ArraySegment<byte> data = dataOwner.DangerousGetArray();
                    if (!TryDecodeWadText(data, out string json)) continue;
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                    }
                    return json;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logService?.LogDebug($"Hash Lab could not read local skins.json from '{wadPath}': {exception.Message}");
                }
            }

            return null;
        }

        internal Task<int> GuessSkinGroupsBinLocalAsync(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0)
        {
            return Task.Run(() =>
            {
                var characters = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
                var regex = new Regex(@"^assets/characters/([^/]+)/skins/skin(\d+)/", RegexOptions.IgnoreCase);
                foreach (string path in KnownPaths)
                {
                    Match match = regex.Match(path);
                    if (!match.Success || match.Groups[1].Value.Equals("sightward", StringComparison.OrdinalIgnoreCase)) continue;
                    string character = match.Groups[1].Value.ToLowerInvariant();
                    if (!characters.TryGetValue(character, out HashSet<int> skins))
                    {
                        skins = new HashSet<int> { 0 };
                        characters[character] = skins;
                    }
                    skins.Add(int.Parse(match.Groups[2].Value));
                }
                int generated = 0;
                const string stageName = "GAME Extended: local skin groups";
                progress?.Report(engine.CreateProgress(stageName, progressOffset));

                foreach (var pair in characters.OrderBy(pair => pair.Value.Count))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var skins = pair.Value.Select(value => $"_skins_skin{value}").OrderBy(value => value, StringComparer.Ordinal).ToList();
                    for (int length = 1; length <= skins.Count; length++)
                    {
                        foreach (var combination in GetCombinations(skins, length))
                        {
                            Check(engine, $"data/{pair.Key}{string.Concat(combination)}.bin", HashGuessStrategy.ChromaGroupVariant, "Local skin groups");
                            if (generated < int.MaxValue) generated++;
                            if ((generated % 5000) == 0)
                            {
                                progress?.Report(engine.CreateProgress(stageName, progressOffset + generated));
                            }
                            if (engine.RemainingUnknownCount == 0) return generated;
                        }
                    }
                }
                progress?.Report(engine.CreateProgress(stageName, progressOffset + generated));
                return generated;
            }, cancellationToken);
        }

        internal Task<int> GuessSkinGroupsBin(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0) =>
            GuessSkinGroupsBinLocalAsync(engine, cancellationToken, progress, progressOffset);
    }
}
