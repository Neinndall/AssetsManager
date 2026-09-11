using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal sealed partial class GameHashGuesser : HashGuesser
    {
        private static readonly string[] ShaderExtensions = { ".ps_2_0", ".ps_3_0", ".vs_2_0", ".vs_3_0", ".ps", ".vs", ".cs" };
        private static readonly string[] ShaderVariants = { ".dx11", ".dx9", ".dx9sm3", ".glsl", ".metal", "-dx11", "-metal" };
        private readonly ConditionalWeakTable<HashGuessEngine, ConcurrentDictionary<string, byte>> _scannedWadCharacters = new();

        private const int MaxCustomBuildListWords = 50_000;
        private const int MaxCustomBinWords = 20_000;
        private const int MaxCustomDataBinWords = 20_000;
        private const int MaxCustomSwordlistWords = 20_000;
        private const int MaxCustomDdsWords = 20_000;
        private const int MaxCustomTexWords = 20_000;
        private const int EsportsBannerSingleCandidateBudget = 2_000_000;
        private const int EsportsBannerCompoundCandidateBudget = 10_000_000;
        private const int EsportsBannerDoubleCandidateBudget = 2_000_000;
        private const int EsportsBannerInsertionCandidateBudget = 750_000;
        private const int EsportsBannerDoubleWordLimit = 96;
        private const int AnimationBinFallbackCandidateBudget = 100_000;
        private static readonly Regex AnimationBinPathRegex = new(
            @"^(?:assets|data)/characters/(?<character>[^/]+)/(?:animations/(?<skin>[^/]+)|skins/(?<skin>[^/]+)(?:/animations)?(?:/[^/]+)?|themes/(?<skin>[^/]+)(?:/animations)?(?:/[^/]+)?)\.(?:bin|inibin)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly record struct AnimationFileLink(uint NameHash, ulong PathHash, string Path);
        private readonly LogService _logService;
        private readonly Func<uint, string> _resolveBinHash;

        internal GameHashGuesser(HashFile hashFile, LogService logService = null, Func<uint, string> resolveBinHash = null)
            : base(hashFile, "*.wad.client")
        {
            if (hashFile.Domain != HashGuessDomain.Game) throw new ArgumentException("GAME guesser requires a GAME hash file.", nameof(hashFile));
            _logService = logService;
            _resolveBinHash = resolveBinHash;
        }

        internal GameHashGuesser() : this(new HashFile(HashGuessDomain.Game, Array.Empty<string>())) { }

        internal override IReadOnlyList<string> BuildWordlist() =>
            Corpus.GetOrCreate("wordlist", HashGuessEngine.BuildWordlist);

        internal IReadOnlyList<string> BuildSwordlist() =>
            Corpus.GetOrCreate(
                "swordlist",
                values => HashGuessEngine.BuildWordlist(
                    values
                        .Where(path => path.Contains(".bin", StringComparison.Ordinal))
                        .Select(GetBasename)));

        internal IEnumerable<HashGuessCandidate> GuessFromBinEntryBasenames(IEnumerable<string> binEntryPaths)
        {
            var basenames = binEntryPaths.Select(Path.GetFileName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var extensions = KnownPaths.Select(Path.GetExtension)
                .Where(extension => extension.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(extension => !Regex.IsMatch(extension, @"(?:glsl|dx9|dx9sm3|dx11|metal)_", RegexOptions.IgnoreCase))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();

            foreach (string extension in extensions)
            foreach (string basename in basenames)
                yield return new HashGuessCandidate(basename + extension, HashGuessStrategy.CrossDomainGame);
        }

        private const int MaxGlobalAnimationWords = 100_000;

        private static readonly string[] BaseAnimationActions =
        {
            "idle", "idle1", "idle2", "run", "run_fast", "walk",
            "attack1", "attack2", "attack3", "crit",
            "spell1", "spell2", "spell3", "spell4",
            "death", "recall", "dance", "taunt", "laugh", "joke",
            "channel", "spawn", "signature_move"
        };

        private IReadOnlyList<string> GetGlobalAnimationActions(CancellationToken cancellationToken = default)
        {
            return Corpus.GetOrCreate("global-animation-actions", knownPaths =>
            {
                var actionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var animRegex = new Regex(
                    @"^(?:assets|data)/characters/(?<char>[^/]+)/(?:(?:skins/(?<skin>[^/]+)|themes/(?<theme>[^/]+))/animations/|animations/)(?<file>[^/]+)\.anm$",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);

                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ||
                        !path.Contains("/characters/", StringComparison.OrdinalIgnoreCase) ||
                        !path.Contains("/animations/", StringComparison.OrdinalIgnoreCase)) continue;

                    string basename = GetBasename(path);
                    if (basename.Length <= 4) continue;
                    string stem = basename[..^4].ToLowerInvariant();

                    int dotIdx = stem.IndexOf('.');
                    if (dotIdx > 0) stem = stem[..dotIdx];

                    if (stem.Length < 2 || stem.Length > 100) continue;
                    if (stem.All(char.IsDigit) || (stem.Length == 16 && stem.All(c => char.IsAsciiHexDigitLower(c)))) continue;

                    bool isValid = true;
                    for (int i = 0; i < stem.Length; i++)
                    {
                        char c = stem[i];
                        if (!char.IsAsciiLetterOrDigit(c) && c != '_') { isValid = false; break; }
                    }
                    if (!isValid) continue;

                    AddCount(stem);

                    Match m = animRegex.Match(path);
                    if (m.Success)
                    {
                        string charName = m.Groups["char"].Value.ToLowerInvariant();
                        string container = (m.Groups["skin"].Success ? m.Groups["skin"].Value : (m.Groups["theme"].Success ? m.Groups["theme"].Value : null))?.ToLowerInvariant();

                        if (!string.IsNullOrEmpty(container) && stem.StartsWith($"{charName}_{container}_", StringComparison.OrdinalIgnoreCase))
                        {
                            string action = stem[(charName.Length + container.Length + 2)..];
                            if (action.Length >= 2) AddCount(action);
                        }
                        else if (stem.StartsWith($"{charName}_", StringComparison.OrdinalIgnoreCase))
                        {
                            string action = stem[(charName.Length + 1)..];
                            if (action.Length >= 2) AddCount(action);
                        }
                        else if (!string.IsNullOrEmpty(container) && stem.StartsWith($"{container}_", StringComparison.OrdinalIgnoreCase))
                        {
                            string action = stem[(container.Length + 1)..];
                            if (action.Length >= 2) AddCount(action);
                        }
                    }

                    int sep = stem.IndexOf('_');
                    while (sep >= 0 && sep < stem.Length - 1)
                    {
                        string sub = stem[(sep + 1)..];
                        if (sub.Length >= 2 && sub.Length <= 100 && !sub.All(char.IsDigit))
                            AddCount(sub);
                        sep = stem.IndexOf('_', sep + 1);
                    }
                }

                foreach (string baseAction in BaseAnimationActions)
                {
                    if (!actionCounts.ContainsKey(baseAction))
                        actionCounts[baseAction] = 1;
                }

                return actionCounts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxGlobalAnimationWords)
                    .Select(kv => kv.Key)
                    .ToList();

                void AddCount(string action)
                {
                    if (actionCounts.TryGetValue(action, out int count))
                    {
                        if (count < int.MaxValue) actionCounts[action] = count + 1;
                    }
                    else
                    {
                        actionCounts[action] = 1;
                    }
                }
            });
        }

        private IReadOnlyList<string> GetExpandedGlobalAnimationActions(CancellationToken cancellationToken = default)
        {
            return Corpus.GetOrCreate("expanded-global-animation-actions", _ =>
            {
                IReadOnlyList<string> actions = GetGlobalAnimationActions(cancellationToken);
                var expandedActions = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);
                foreach (string action in actions)
                {
                    foreach (Range range in action.AsSpan().Split('_'))
                    {
                        ReadOnlySpan<char> word = action.AsSpan(range);
                        if (word.Length >= 2 && word.ContainsAnyExceptInRange('0', '9'))
                            expandedActions.Add(word.ToString());
                    }
                }

                return expandedActions.OrderBy(action => action, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private IReadOnlyList<byte[]> GetExpandedGlobalAnimationActionBytes(CancellationToken cancellationToken = default)
        {
            return Corpus.GetOrCreate(
                "expanded-global-animation-action-bytes",
                _ => GetExpandedGlobalAnimationActions(cancellationToken).Select(Encoding.UTF8.GetBytes).ToList());
        }

        private IReadOnlyList<string> GetCharacterAnimationActions(string character)
        {
            return Corpus.GetOrCreate($"champion-animation-actions/{character.ToLowerInvariant()}", knownPaths =>
            {
                var actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string baseChar = character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? character[5..]
                    : (character.StartsWith("tft_", StringComparison.OrdinalIgnoreCase) || (character.StartsWith("tft", StringComparison.OrdinalIgnoreCase) && character.Length > 5 && character.Contains('_'))) ? character[(character.IndexOf('_') + 1)..]
                    : (character.StartsWith("cherry_", StringComparison.OrdinalIgnoreCase) || character.StartsWith("strawberry_", StringComparison.OrdinalIgnoreCase) || character.StartsWith("crepe_", StringComparison.OrdinalIgnoreCase) || character.StartsWith("ruby_", StringComparison.OrdinalIgnoreCase)) ? character[(character.IndexOf('_') + 1)..]
                    : character.Equals("oriannaball", StringComparison.OrdinalIgnoreCase) ? "orianna"
                    : character.Equals("tibbers", StringComparison.OrdinalIgnoreCase) ? "annie"
                    : character.Equals("heimergarrison", StringComparison.OrdinalIgnoreCase) ? "heimerdinger"
                    : character.Equals("quinnvalor", StringComparison.OrdinalIgnoreCase) ? "quinn"
                    : character.Equals("yorickghoul", StringComparison.OrdinalIgnoreCase) ? "yorick"
                    : character.Equals("kalistaspawn", StringComparison.OrdinalIgnoreCase) ? "kalista"
                    : character.Equals("malzaharvoidling", StringComparison.OrdinalIgnoreCase) ? "malzahar"
                    : null;

                string charPrefix = $"assets/characters/{character}/";
                string dataCharPrefix = $"data/characters/{character}/";
                string basePrefix = baseChar != null ? $"assets/characters/{baseChar}/" : null;
                string dataBasePrefix = baseChar != null ? $"data/characters/{baseChar}/" : null;

                for (int i = 0; i < knownPaths.Count; i++)
                {
                    string path = knownPaths[i];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;

                    string rel = null;
                    if (path.StartsWith(charPrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[charPrefix.Length..];
                    else if (path.StartsWith(dataCharPrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[dataCharPrefix.Length..];
                    else if (basePrefix != null && path.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[basePrefix.Length..];
                    else if (dataBasePrefix != null && path.StartsWith(dataBasePrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[dataBasePrefix.Length..];

                    if (string.IsNullOrEmpty(rel)) continue;

                    string basename = GetBasename(path);
                    string stem = basename.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? basename[..^4] : basename;
                    if (stem.Length == 0 || stem.Length > 50) continue;

                    // Versioned variants (attack1.pie_c_11_15) only bloat the list and starve
                    // later skins under the fallback budget; the clean action covers them and
                    // structural generators rebuild versioned names, mirroring the global list.
                    int dotIndex = stem.IndexOf('.');
                    if (dotIndex > 0) stem = stem[..dotIndex];
                    if (stem.Length == 0) continue;

                    actions.Add(stem);

                    if (stem.StartsWith(character + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        string sub = stem[(character.Length + 1)..];
                        if (sub.Length > 0) actions.Add(sub);
                    }
                    if (baseChar != null && stem.StartsWith(baseChar + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        string sub = stem[(baseChar.Length + 1)..];
                        if (sub.Length > 0) actions.Add(sub);
                    }

                    Match containerMatch = Regex.Match(
                        rel,
                        @"^(?:skins|themes)/(?<container>[^/]+)/animations/",
                        RegexOptions.IgnoreCase);
                    if (containerMatch.Success)
                    {
                        string container = containerMatch.Groups["container"].Value;
                        AddAnimationActionAfterPrefix($"{character}_{container}_");
                        if (baseChar != null) AddAnimationActionAfterPrefix($"{baseChar}_{container}_");
                    }

                    Match skinToken = Regex.Match(stem, @"(?:^|_)skin\d+_(?<action>.+)$", RegexOptions.IgnoreCase);
                    if (skinToken.Success) actions.Add(skinToken.Groups["action"].Value);

                    void AddAnimationActionAfterPrefix(string prefix)
                    {
                        if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
                        string action = stem[prefix.Length..];
                        if (action.Length > 0) actions.Add(action);
                    }
                }

                if (actions.Count == 0)
                {
                    foreach (string globalAction in GetGlobalAnimationActions())
                        actions.Add(globalAction);
                }

                return actions.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private static bool IsShaderReference(StringBuilder value)
        {
            foreach (string extension in ShaderExtensions)
                if (EndsWith(value, extension)) return true;
            return false;
        }

        private static bool EndsWith(StringBuilder value, string suffix)
        {
            if (value.Length < suffix.Length) return false;
            int offset = value.Length - suffix.Length;
            for (int index = 0; index < suffix.Length; index++)
                if (char.ToLowerInvariant(value[offset + index]) != suffix[index]) return false;
            return true;
        }

        internal int GuessEsportsBanners(
            HashGuessEngine engine,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            const string bannerPathPrefix = "assets/esports/sponsoredbanners/";
            IReadOnlyList<string> paths = Corpus.GetOrCreate(
                "esports-banner-paths",
                values => values
                    .Where(path => path.StartsWith(bannerPathPrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList());
            IReadOnlyList<string> wordlist = Corpus.GetOrCreate(
                "esports-banner-wordlist",
                _ => HashGuessEngine.BuildBasenameWordlist(paths, minimumLength: 2, maximumLength: 32));
            IReadOnlyList<string> compoundWords = Corpus.GetOrCreate(
                "esports-banner-compound-wordlist",
                _ =>
                {
                    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (string path in paths)
                    {
                        string basename = GetBasename(path);
                        int extension = basename.LastIndexOf('.');
                        string stem = extension > 0 ? basename[..extension] : basename;
                        string[] tokens = stem
                            .Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(IsBannerToken)
                            .ToArray();

                        for (int index = 0; index + 1 < tokens.Length; index++)
                        {
                            AddWord($"{tokens[index]}_{tokens[index + 1]}");
                            AddWord($"{tokens[index]}-{tokens[index + 1]}");
                        }
                    }

                    return counts
                        .OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => pair.Key.ToLowerInvariant())
                        .ToList();

                    void AddWord(string value)
                    {
                        if (!IsCompound(value)) return;
                        counts.TryGetValue(value, out int current);
                        counts[value] = current + 1;
                    }

                    bool IsCompound(string value) =>
                        !string.IsNullOrWhiteSpace(value) &&
                        value.Length <= 48 &&
                        value.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries).All(IsBannerToken);

                    bool IsBannerToken(string value) =>
                        value.Length >= 2 &&
                        value.Length <= 32 &&
                        value.All(char.IsLetterOrDigit);
                });
            IReadOnlyList<string> doubleWords = Corpus.GetOrCreate(
                "esports-banner-double-words",
                _ => compoundWords
                    .SelectMany(word => word.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                    .Concat(wordlist)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(EsportsBannerDoubleWordLimit)
                    .ToList());

            return RunBannerBasenameAttack(
                engine,
                paths,
                wordlist,
                compoundWords,
                doubleWords,
                progress,
                cancellationToken);
        }

        private int RunBannerBasenameAttack(
            HashGuessEngine engine,
            IReadOnlyList<string> paths,
            IReadOnlyList<string> wordlist,
            IReadOnlyList<string> compoundWords,
            IReadOnlyList<string> doubleWords,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            if (paths.Count == 0 || engine.RemainingUnknownCount == 0) return 0;

            int checkedCandidates = 0;
            var passes = new[]
            {
                (Words: wordlist, OldCount: 1, NewCount: 1, Budget: EsportsBannerSingleCandidateBudget, Source: "GAME Banner: vocabulary"),
                (Words: compoundWords, OldCount: 2, NewCount: 1, Budget: EsportsBannerCompoundCandidateBudget, Source: "GAME Banner: compound names"),
                (Words: compoundWords, OldCount: 1, NewCount: 1, Budget: EsportsBannerCompoundCandidateBudget, Source: "GAME Banner: compound variants"),
                (Words: doubleWords, OldCount: 2, NewCount: 2, Budget: EsportsBannerDoubleCandidateBudget, Source: "GAME Banner: double-word variants")
            };
            foreach (var pass in passes)
            {
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
                int progressOffset = checkedCandidates;
                progress?.Report(engine.CreateProgress(pass.Source, progressOffset));
                checkedCandidates += _SubstituteBasenameWords(
                    engine,
                    paths,
                    pass.Words,
                    pass.OldCount,
                    pass.NewCount,
                    cancellationToken,
                    pass.Budget,
                    pass.Source,
                    count => progress?.Report(engine.CreateProgress(pass.Source, progressOffset + count)),
                    HashGuessStrategy.BannerVariant);
            }
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            int insertionProgressOffset = checkedCandidates;
            const string insertionStage = "GAME Banner: basename insertion";
            progress?.Report(engine.CreateProgress(insertionStage, insertionProgressOffset));
            checkedCandidates += _AddBasenameWord(
                engine,
                paths,
                wordlist.Take(EsportsBannerDoubleWordLimit),
                cancellationToken,
                EsportsBannerInsertionCandidateBudget,
                insertionStage,
                count => progress?.Report(engine.CreateProgress(insertionStage, insertionProgressOffset + count)),
                HashGuessStrategy.BannerVariant);
            return checkedCandidates;
        }

        private IReadOnlyDictionary<string, IReadOnlyList<string>> GetChampionSkinMap(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("all-champion-skin-maps", knownPaths =>
            {
                var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) &&
                        !path.StartsWith("data/characters/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int skinsIdx = path.IndexOf("/skins/", StringComparison.OrdinalIgnoreCase);
                    if (skinsIdx <= 0) continue;

                    string champ = path.Substring(path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) ? 18 : 16, skinsIdx - (path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) ? 18 : 16)).ToLowerInvariant();
                    string rel = path.Substring(skinsIdx + 7);
                    int slash = rel.IndexOf('/');
                    string skin = slash > 0 ? rel[..slash] : rel;
                    if (skin.Length is >= 3 and <= 35 && !skin.Contains('.'))
                    {
                        if (!map.TryGetValue(champ, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base" };
                            map[champ] = set;
                        }
                        set.Add(skin);
                    }
                }

                return (IReadOnlyDictionary<string, IReadOnlyList<string>>)map.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (IReadOnlyList<string>)kvp.Value.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            });
        }

        private IReadOnlyList<string> GetChampionSkinNames(string character, CancellationToken cancellationToken)
        {
            var map = GetChampionSkinMap(cancellationToken);
            string baseChar = character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? character[5..] : character;
            
            var skins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base" };
            if (map.TryGetValue(character, out var directSkins))
            {
                for (int i = 0; i < directSkins.Count; i++) skins.Add(directSkins[i]);
            }
            if (baseChar != character && map.TryGetValue(baseChar, out var baseSkins))
            {
                for (int i = 0; i < baseSkins.Count; i++) skins.Add(baseSkins[i]);
            }

            // Add attested skin numbers + padding
            int maxAttested = 0;
            foreach (var s in skins)
            {
                if (s.StartsWith("skin", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(s[4..], out int num))
                {
                    if (num > maxAttested) maxAttested = num;
                }
            }

            int limit = Math.Max(maxAttested + 15, 85);
            if (character.Equals("sightward", StringComparison.OrdinalIgnoreCase)) limit = 500;
            for (int i = 0; i <= limit; i++)
            {
                skins.Add($"skin{i}");
                if (i <= 9) skins.Add($"skin{i:D2}");
            }

            for (int i = 300; i <= 350; i++) skins.Add($"skin{i}");

            return skins.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<ulong> EnumerateChunkLinks(BinTree tree)
        {
            var roots = tree.Objects.Values.SelectMany(obj => obj.Properties.Values)
                .Concat(tree.DataOverrides.Select(ovr => ovr.Property));
            foreach (BinTreeProperty root in roots)
            {
                foreach (BinTreeProperty prop in EnumerateAllProperties(root))
                {
                    if (prop is BinTreeWadChunkLink link && link.Value != 0)
                        yield return link.Value;
                }
            }
        }

        private static IEnumerable<BinTreeProperty> EnumerateAllProperties(BinTreeProperty property)
        {
            if (property == null) yield break;
            yield return property;

            IEnumerable<BinTreeProperty> children = property switch
            {
                BinTreeStruct structure => structure.Properties.Values,
                BinTreeOptional optional when optional.Value != null => new[] { optional.Value },
                BinTreeContainer container => container.Elements,
                BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
                _ => Array.Empty<BinTreeProperty>()
            };
            foreach (BinTreeProperty child in children)
            foreach (BinTreeProperty descendant in EnumerateAllProperties(child))
                yield return descendant;
        }

        private IReadOnlyDictionary<uint, List<string>> GetAnimationNameIndex(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("animation-name-index", knownPaths =>
            {
                var index = new Dictionary<uint, List<string>>();
                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = GetBasename(path);
                    if (name.Length <= 4) continue;
                    string stem = name[..^4];
                    Add(Fnv1a.HashLower(stem), stem);
                    string compact = new(stem.Where(char.IsLetterOrDigit).ToArray());
                    if (compact.Length > 0) Add(Fnv1a.HashLower(compact), stem);

                    int sep = stem.IndexOf('_');
                    while (sep >= 0 && sep < stem.Length - 1)
                    {
                        string sub = stem[(sep + 1)..];
                        if (sub.Length >= 2)
                        {
                            Add(Fnv1a.HashLower(sub), sub);
                            string compactSub = new(sub.Where(char.IsLetterOrDigit).ToArray());
                            if (compactSub.Length > 0) Add(Fnv1a.HashLower(compactSub), sub);
                        }
                        sep = stem.IndexOf('_', sep + 1);
                    }
                }
                if (index.Count == 0)
                {
                    foreach (string baseAction in BaseAnimationActions)
                    {
                        Add(Fnv1a.HashLower(baseAction), baseAction);
                        string compact = new(baseAction.Where(char.IsLetterOrDigit).ToArray());
                        if (compact.Length > 0) Add(Fnv1a.HashLower(compact), baseAction);
                    }
                }

                return index;

                void Add(uint hash, string stem)
                {
                    if (!index.TryGetValue(hash, out List<string> values))
                        index.Add(hash, values = new List<string>());
                    if (!values.Contains(stem, StringComparer.OrdinalIgnoreCase)) values.Add(stem);
                }
            });
        }

        private void GuessImageAutoAtlasPaths(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Array is null || data.Count == 0) return;
            if (!ImageAutoAtlas.IsAtlas(data.AsSpan()) || !ImageAutoAtlas.TryRead(data.Array[data.Offset..(data.Offset + data.Count)], out ImageAutoAtlas atlas))
                return;

            IReadOnlyDictionary<ulong, string> knownDict = Corpus.GetOrCreate("known-hashes-dict", _ => HashFile.Load());

            // Ensure any sprite hash not in HashFile is marked unknown in engine
            bool hasUnresolvedSprites = false;
            foreach (var sprite in atlas.Sprites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.UnknownHashes.Contains(sprite.SpriteHash) || !knownDict.ContainsKey(sprite.SpriteHash))
                {
                    engine.EnsureUnknown(sprite.SpriteHash);
                    hasUnresolvedSprites = true;
                }
            }
            if (!hasUnresolvedSprites) return;

            var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(sourcePath) && !sourcePath.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(PathUtils.NormalizePath(sourcePath));
                if (!string.IsNullOrEmpty(dir))
                    candidateDirs.Add(PathUtils.NormalizeSeparators(dir));
            }

            if (candidateDirs.Count == 0 && atlas.TextureHashes.Count > 0)
            {
                var texDirIndex = GetTextureHashToDirectoryIndex();
                foreach (ulong texHash in atlas.TextureHashes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (texDirIndex.TryGetValue(texHash, out string dir))
                        candidateDirs.Add(dir);
                }
            }

            if (candidateDirs.Count == 0)
            {
                foreach (string dir in GetAllKnownAtlasDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    candidateDirs.Add(dir);
                }
            }

            IReadOnlyList<string> candidatePatterns = GetAutoAtlasCandidatePatterns();
            foreach (string baseDir in candidateDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string pattern in candidatePatterns)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Check(engine, $"{baseDir}/{pattern}", HashGuessStrategy.AtlasReference, sourceWadPath, sourceChunkHash);

                    // If all sprites in this atlas are resolved, stop immediately
                    bool stillHasUnresolved = false;
                    foreach (var sprite in atlas.Sprites)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (engine.UnknownHashes.Contains(sprite.SpriteHash))
                        {
                            stillHasUnresolved = true;
                            break;
                        }
                    }
                    if (!stillHasUnresolved || engine.RemainingUnknownCount == 0) break;
                }

                bool anyRemaining = false;
                foreach (var sprite in atlas.Sprites)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (engine.UnknownHashes.Contains(sprite.SpriteHash))
                    {
                        anyRemaining = true;
                        break;
                    }
                }
                if (!anyRemaining || engine.RemainingUnknownCount == 0) break;
            }
        }

        private IReadOnlyDictionary<ulong, string> GetTextureHashToDirectoryIndex()
        {
            return Corpus.GetOrCreate("texture-hash-to-dir-index", knownPaths =>
            {
                var dict = new Dictionary<ulong, string>();
                foreach (string path in knownPaths)
                {
                    if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    {
                        string norm = PathUtils.NormalizePath(path);
                        string dir = PathUtils.NormalizeSeparators(Path.GetDirectoryName(norm));
                        if (!string.IsNullOrEmpty(dir))
                            dict[XxHash64Ext.Hash(norm)] = dir;
                    }
                }
                return dict;
            });
        }

        private IReadOnlyList<string> GetAllKnownAtlasDirectories()
        {
            return Corpus.GetOrCreate("all-known-atlas-directories", knownPaths =>
            {
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in knownPaths)
                {
                    if (path.EndsWith("atlas_info.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        string dir = Path.GetDirectoryName(PathUtils.NormalizePath(path));
                        if (!string.IsNullOrEmpty(dir))
                            dirs.Add(PathUtils.NormalizeSeparators(dir));
                    }
                }
                return dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private IReadOnlyList<string> GetAutoAtlasCandidatePatterns()
        {
            return Corpus.GetOrCreate("autoatlas-candidate-patterns-v4", knownPaths =>
            {
                var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string path in knownPaths)
                {
                    string norm = PathUtils.NormalizePath(path);

                    // Extract relative filenames from any known autoatlas folders
                    int autoAtlasIdx = norm.IndexOf("/autoatlas/", StringComparison.OrdinalIgnoreCase);
                    if (autoAtlasIdx >= 0)
                    {
                        string sub = norm[(autoAtlasIdx + "/autoatlas/".Length)..];
                        int firstSlash = sub.IndexOf('/');
                        if (firstSlash >= 0 && firstSlash < sub.Length - 1)
                        {
                            string relFile = sub[(firstSlash + 1)..];
                            if (!string.IsNullOrWhiteSpace(relFile) && !relFile.StartsWith("atlas_", StringComparison.OrdinalIgnoreCase))
                                patterns.Add(relFile);
                        }
                        else
                        {
                            string fileName = Path.GetFileName(norm);
                            if (!string.IsNullOrWhiteSpace(fileName) && !fileName.StartsWith("atlas_", StringComparison.OrdinalIgnoreCase))
                                patterns.Add(fileName);
                        }
                    }
                    else if (norm.Contains("/icons2d/", StringComparison.OrdinalIgnoreCase) ||
                             norm.StartsWith("ux/", StringComparison.OrdinalIgnoreCase) ||
                             norm.StartsWith("clientstates/", StringComparison.OrdinalIgnoreCase))
                    {
                        string fileName = Path.GetFileName(norm);
                        if (!string.IsNullOrWhiteSpace(fileName) && !fileName.StartsWith("atlas_", StringComparison.OrdinalIgnoreCase))
                        {
                            patterns.Add(fileName);
                            string stem = Path.GetFileNameWithoutExtension(fileName);
                            if (!string.IsNullOrWhiteSpace(stem) && stem.Length <= 100)
                            {
                                patterns.Add($"{stem}.png");
                                patterns.Add($"{stem}.dds");
                                patterns.Add($"{stem}.tex");
                            }
                        }
                    }
                }

                return patterns.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private IEnumerable<string> EnumerateAnimationPaths(
            string character,
            string skin,
            bool includeThemeLayout,
            IReadOnlyList<AnimationFileLink> links,
            IReadOnlyCollection<ulong> unknownHashes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = links
                .Where(link => link.PathHash != 0 && unknownHashes.Contains(link.PathHash))
                .Select(link => link.PathHash)
                .ToHashSet();
            if (remaining.Count == 0) yield break;

            var attemptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var (prefixes, suffixes) = GetDynamicAnimationAffixes(cancellationToken);

            foreach (string name in EnumerateAnimationNameCandidates(character, links, remaining, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!attemptedNames.Add(name)) continue;
                foreach (string path in MatchAnimationVariants(name, character, skin, remaining, prefixes, suffixes, includeThemeLayout))
                    yield return path;
                if (remaining.Count == 0) yield break;
            }

            if (remaining.Count == 0) yield break;

            IReadOnlyList<string> sourceNames = GetAnimationNames(character, cancellationToken);

            foreach (HashGuessCandidate candidate in GenerateNumberCandidates(
                         sourceNames.Where(name => name.Any(char.IsDigit)).Select(name => $"animations/{name}"),
                         numberLimit: 360,
                         candidateBudget: int.MaxValue,
                         digits: null,
                         inferDigits: false,
                         includeCommonPadding: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string name in ExpandNumberedAnimationNames(GetBasename(candidate.Path)))
                {
                    if (!attemptedNames.Add(name)) continue;
                    foreach (string path in MatchAnimationVariants(name, character, skin, remaining, DefaultPrefixModifiers, DefaultSuffixModifiers, includeThemeLayout))
                        yield return path;
                    if (remaining.Count == 0) yield break;
                }
            }
        }

        private IEnumerable<string> EnumerateAnimationNameCandidates(
            string character,
            IReadOnlyList<AnimationFileLink> links,
            IReadOnlySet<ulong> targetHashes,
            CancellationToken cancellationToken)
        {
            var namedLinks = links
                .Where(link => targetHashes.Contains(link.PathHash) && link.NameHash != 0)
                .ToList();
            if (namedLinks.Count == 0) yield break;

            foreach (AnimationFileLink link in namedLinks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string resolved = _resolveBinHash?.Invoke(link.NameHash);
                if (IsAnimationStem(resolved)) yield return resolved;
            }
            if (targetHashes.Count == 0) yield break;

            var nameHashes = namedLinks
                .Where(link => targetHashes.Contains(link.PathHash))
                .Select(link => link.NameHash)
                .ToHashSet();
            if (nameHashes.Count == 0) yield break;

            IReadOnlyDictionary<uint, List<string>> namesByHash = GetAnimationNameIndex(cancellationToken);
            foreach (AnimationFileLink link in namedLinks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!targetHashes.Contains(link.PathHash) || !namesByHash.TryGetValue(link.NameHash, out List<string> names)) continue;
                foreach (string name in names)
                    yield return name;
            }
            if (targetHashes.Count == 0) yield break;

            IReadOnlyList<string> sourceNames = GetAnimationNames(character, cancellationToken);
            HashSet<string> prefixes = GetReusableAnimationPrefixes(sourceNames);

            foreach (string name in sourceNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stem = name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
                if (nameHashes.Contains(Fnv1a.HashLower(stem))) yield return stem;
                foreach (string prefix in prefixes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string prefixed = prefix + "_" + stem;
                    if (nameHashes.Contains(Fnv1a.HashLower(prefixed))) yield return prefixed;
                }
            }

        }

        private static HashSet<string> GetReusableAnimationPrefixes(IEnumerable<string> names)
        {
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            for (int separator = name.IndexOf('_'); separator > 0; separator = name.IndexOf('_', separator + 1))
            {
                string prefix = name[..separator];
                if (prefix.Length <= 24 && prefix.All(character => char.IsLetterOrDigit(character) || character == '_'))
                    prefixes.Add(prefix);
            }
            return prefixes;
        }

        private static bool IsAnimationStem(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.Contains('/') &&
            !value.Contains('\\') &&
            !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        private IReadOnlyList<string> GetAnimationNames(string character, CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate(
                $"animation-names/{character.ToLowerInvariant()}",
                paths => BuildAnimationNames(paths, character, cancellationToken));
        }

        private static IReadOnlyList<string> BuildAnimationNames(
            IReadOnlyList<string> knownPaths,
            string character,
            CancellationToken cancellationToken)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var knownAnimationPathRegex = new Regex(
                @"^(?:assets|data)/characters/(?<character>[^/]+)/(?:skins|themes)/(?<skin>[^/]+)/animations/[^/]+\.anm$",
                RegexOptions.IgnoreCase);
            for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
            {
                if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                string path = knownPaths[pathIndex];
                if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
                Match context = knownAnimationPathRegex.Match(PathUtils.NormalizePath(path));
                if (!context.Success || !context.Groups["character"].Value.Equals(character, StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = GetBasename(path);
                if (name.Length > 0) names.Add(name);
            }

            return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private (IReadOnlyList<string> Prefixes, IReadOnlyList<string> Suffixes) GetDynamicAnimationAffixes(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("dynamic-animation-affixes", knownPaths =>
            {
                var prefixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var suffixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
                    string stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    int first = stem.IndexOf('_');
                    if (first > 0 && first < stem.Length - 1 && path.Contains($"/{stem[..first]}/", StringComparison.OrdinalIgnoreCase))
                        stem = stem[(first + 1)..];

                    first = stem.IndexOf('_');
                    if (first > 0 && first <= 16)
                        prefixes[stem[..(first + 1)]] = prefixes.GetValueOrDefault(stem[..(first + 1)]) + 1;

                    int last = stem.LastIndexOf('_');
                    if (last >= 0 && last < stem.Length - 1 && (stem.Length - last) <= 16)
                        suffixes[stem[last..]] = suffixes.GetValueOrDefault(stem[last..]) + 1;
                }

                static IReadOnlyList<string> Top(Dictionary<string, int> dict) =>
                    new[] { "" }.Concat(dict.OrderByDescending(kv => kv.Value).Take(10).Select(kv => kv.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                return (Top(prefixes), Top(suffixes));
            });
        }

        internal IEnumerable<string> MatchAnimationVariants(
            string name,
            string character,
            string skin,
            ISet<ulong> remaining,
            IReadOnlyList<string> prefixes = null,
            IReadOnlyList<string> suffixes = null,
            bool includeThemeLayout = false)
        {
            string[][] pathPrefixes = Corpus.GetOrCreate($"animation-path-prefixes/{character}/{skin}/{includeThemeLayout}", _ =>
                BuildAnimationPathPrefixes(character, skin, includeThemeLayout));
            foreach (string path in MatchName(name)) yield return path;

            string converted = Regex.Replace(name, @"skin\d+", skin, RegexOptions.IgnoreCase);
            if (converted.Equals(name, StringComparison.OrdinalIgnoreCase)) yield break;
            foreach (string path in MatchName(converted)) yield return path;

            IEnumerable<string> MatchName(string value)
            {
                string stem = value.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
                if (string.IsNullOrWhiteSpace(stem) || stem.Contains('/') || stem.Contains('\\')) yield break;
                stem = stem.ToLowerInvariant();
                foreach (string[] group in pathPrefixes)
                foreach (string pre in prefixes ?? DefaultPrefixModifiers)
                foreach (string suf in suffixes ?? DefaultSuffixModifiers)
                foreach (string variant in ExpandAnimationStemVariants(pre + stem + suf))
                foreach (string prefix in group)
                {
                    if (remaining.Count == 0) yield break;
                    if (remaining.Remove(HashAnimationPath(prefix, variant)))
                        yield return prefix + variant + ".anm";
                }
            }
        }

        private static ulong HashAnimationPath(string prefix, string stem)
        {
            int length = prefix.Length + stem.Length + 4;
            Span<char> path = length <= 512 ? stackalloc char[length] : new char[length];
            prefix.AsSpan().CopyTo(path);
            stem.AsSpan().CopyTo(path[prefix.Length..]);
            ".anm".AsSpan().CopyTo(path[(prefix.Length + stem.Length)..]);
            return XxHash64Ext.Hash(path);
        }

        private static string[][] BuildAnimationPathPrefixes(string character, string skin, bool includeThemeLayout)
        {
            string paddedSkin = skin.Length == 5 && skin.StartsWith("skin", StringComparison.OrdinalIgnoreCase) && char.IsDigit(skin[4])
                ? "skin0" + skin[4..]
                : skin.Length == 6 && skin.StartsWith("skin0", StringComparison.OrdinalIgnoreCase) && char.IsDigit(skin[5])
                    ? "skin" + skin[5..] : skin;
            string[] skins = string.Equals(skin, paddedSkin, StringComparison.OrdinalIgnoreCase) ? new[] { skin } : new[] { skin, paddedSkin };
            return skins.Select(sk => AnimationRootPrefixes.SelectMany(root =>
            {
                string directory = $"{root}/characters/{character}/{(includeThemeLayout ? "themes" : "skins")}/{sk}/animations/";
                var values = new List<string> { directory, directory + character + "_", directory + character + "_" + sk + "_", directory + sk + "_" };
                if (!includeThemeLayout && character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(directory + character[5..] + "_");
                    values.Add(directory + character[5..] + "_" + sk + "_");
                }
                return values;
            }).ToArray()).ToArray();
        }

        internal static IEnumerable<string> EnumerateAnimationNameVariants(
            string character,
            string skin,
            string name,
            IReadOnlyList<string> prefixModifiers = null,
            IReadOnlyList<string> suffixModifiers = null,
            bool includeThemeLayout = false)
        {
            string stem = name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            if (string.IsNullOrWhiteSpace(stem) || stem.Contains('/') || stem.Contains('\\')) yield break;
            stem = stem.ToLowerInvariant();

            foreach (string[] group in BuildAnimationPathPrefixes(character, skin, includeThemeLayout))
            foreach (string pre in prefixModifiers ?? DefaultPrefixModifiers)
            foreach (string suf in suffixModifiers ?? DefaultSuffixModifiers)
            foreach (string s in ExpandAnimationStemVariants(pre + stem + suf))
            foreach (string prefix in group)
                yield return prefix + s + ".anm";
        }

        private static IEnumerable<string> ExpandNumberedAnimationNames(string name)
        {
            yield return name;
            Match match = Regex.Match(name, @"^(?<stem>.*?)(?:_)?(?<number>\d+)\.anm$", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out int number) || number > 99) yield break;
            string stem = match.Groups["stem"].Value + number.ToString("D2", CultureInfo.InvariantCulture);
            yield return stem + ".anm";
            yield return stem + "a.anm";
            yield return stem + "b.anm";
        }

        private static IEnumerable<string> ExpandAnimationStemVariants(string stem)
        {
            yield return stem;
            if (stem.Contains("variant", StringComparison.OrdinalIgnoreCase))
                yield return stem.Replace("variant", "varient", StringComparison.OrdinalIgnoreCase);
            if (stem.Contains("spawn", StringComparison.OrdinalIgnoreCase))
                yield return stem.Replace("spawn", "spwan", StringComparison.OrdinalIgnoreCase);
            if (stem.Contains("_in", StringComparison.OrdinalIgnoreCase))
                yield return stem.Replace("_in", "in", StringComparison.OrdinalIgnoreCase);
            if (stem.Contains("_out", StringComparison.OrdinalIgnoreCase))
                yield return stem.Replace("_out", "out", StringComparison.OrdinalIgnoreCase);
            if (stem.Contains("_cycle", StringComparison.OrdinalIgnoreCase))
                yield return stem.Replace("_cycle", "cycle", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsThemeAnimationContext(string character, string container)
        {
            return character.StartsWith("pet", StringComparison.OrdinalIgnoreCase) &&
                   !container.Equals("root", StringComparison.OrdinalIgnoreCase) &&
                   !container.Equals("shared", StringComparison.OrdinalIgnoreCase) &&
                   !(container.StartsWith("skin", StringComparison.OrdinalIgnoreCase) &&
                     container.Length > 4 && container.Skip(4).All(char.IsDigit));
        }

        internal static IEnumerable<string> OrderAnimationContainers(string sourceContainer, IEnumerable<string> containers)
        {
            int sourceNumber = -1;
            Match sourceMatch = Regex.Match(sourceContainer ?? string.Empty, @"^skin0*(\d+)$", RegexOptions.IgnoreCase);
            if (sourceMatch.Success) int.TryParse(sourceMatch.Groups[1].Value, out sourceNumber);

            return containers
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(container => AnimationContainerDistance(container))
                .ThenBy(container => container, StringComparer.OrdinalIgnoreCase);

            int AnimationContainerDistance(string container)
            {
                // The shared base container holds the fallback actions, so it stays
                // ahead of numbered skins (the caller also tries it before the source).
                if (container.Equals("base", StringComparison.OrdinalIgnoreCase)) return -1;
                if (sourceNumber < 0) return int.MaxValue;
                Match match = Regex.Match(container, @"^skin0*(\d+)$", RegexOptions.IgnoreCase);
                return match.Success && int.TryParse(match.Groups[1].Value, out int number)
                    ? Math.Abs(number - sourceNumber)
                    : int.MaxValue;
            }
        }

        private static readonly string[] DefaultPrefixModifiers = { "" };
        private static readonly string[] DefaultSuffixModifiers = { "" };

        private static readonly string[] AnimationRootPrefixes = { "assets", "data" };

        private static string GetBasename(string path)
        {
            int separator = path.LastIndexOf('/');
            return separator >= 0 ? path[(separator + 1)..] : path;
        }

        private static IEnumerable<AnimationFileLink> EnumerateAnimationFileLinks(BinTree tree)
        {
            uint clipDataMapNameHash = Fnv1a.HashLower("mClipDataMap");
            uint animationFilePathNameHash = Fnv1a.HashLower("mAnimationFilePath");

            var roots = tree.Objects.Values.SelectMany(obj => obj.Properties.Values)
                .Concat(tree.DataOverrides.Select(ovr => ovr.Property));
            var namedPathHashes = new HashSet<ulong>();
            var namedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (BinTreeMap map in roots
                         .SelectMany(root => FindProperties(root, clipDataMapNameHash))
                         .OfType<BinTreeMap>())
            foreach (var pair in map)
            {
                uint nameHash = pair.Key is BinTreeHash hash ? hash.Value : 0;
                foreach (BinTreeProperty path in FindProperties(pair.Value, animationFilePathNameHash).Take(1))
                {
                    if (path is BinTreeWadChunkLink link && link.Value != 0)
                    {
                        namedPathHashes.Add(link.Value);
                        yield return new AnimationFileLink(nameHash, link.Value, null);
                    }
                    else if (path is BinTreeString text && !string.IsNullOrWhiteSpace(text.Value))
                    {
                        string normalized = PathUtils.NormalizePath(text.Value);
                        namedPaths.Add(normalized);
                        yield return new AnimationFileLink(nameHash, 0, normalized);
                    }
                }
            }

            // Some current hash-only BINs store AnimationClip records outside
            // mClipDataMap. Keep the same property evidence, but do not turn
            // every arbitrary WadChunkLink into an animation candidate.
            foreach (BinTreeProperty root in roots)
            foreach (BinTreeProperty path in FindProperties(root, animationFilePathNameHash))
            {
                if (path is BinTreeWadChunkLink link && link.Value != 0 &&
                    !namedPathHashes.Contains(link.Value))
                    yield return new AnimationFileLink(0, link.Value, null);
                else if (path is BinTreeString text && !string.IsNullOrWhiteSpace(text.Value) &&
                         !namedPaths.Contains(PathUtils.NormalizePath(text.Value)))
                    yield return new AnimationFileLink(0, 0, PathUtils.NormalizePath(text.Value));
            }
        }

        private static IEnumerable<BinTreeProperty> FindProperties(BinTreeProperty property, uint nameHash)
        {
            if (property == null) yield break;
            if (property.NameHash == nameHash)
            {
                yield return property;
                yield break;
            }

            IEnumerable<BinTreeProperty> children = property switch
            {
                BinTreeStruct structure => structure.Properties.Values,
                BinTreeOptional optional when optional.Value != null => new[] { optional.Value },
                BinTreeContainer container => container.Elements,
                BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
                _ => Array.Empty<BinTreeProperty>()
            };
            foreach (BinTreeProperty child in children)
            foreach (BinTreeProperty match in FindProperties(child, nameHash))
                yield return match;
        }

        internal int GrepFile(
            HashGuessEngine engine,
            string path = null,
            byte[] data = null,
            string source = "GAME grep file")
        {
            if (!string.IsNullOrWhiteSpace(path)) data = File.ReadAllBytes(path);
            else if (data == null) throw new ArgumentException("Either path or data must be provided.");
            int checkedCandidates = 0;
            foreach (HashGuessCandidate candidate in GrepFile(data, CancellationToken.None))
            {
                Check(engine, candidate.Path, candidate.Strategy, string.IsNullOrWhiteSpace(path) ? source : path);
                checkedCandidates++;
                if (engine.RemainingUnknownCount == 0) break;
            }
            return checkedCandidates;
        }

        private static IEnumerable<HashGuessCandidate> ExtractLuaManifestCandidates(
            ArraySegment<byte> data,
            CancellationToken cancellationToken)
        {
            string[] luaExtensions = { "luabin64", "preload" };
            string[] luaCharacterPrefixes = { "", "spells/", "scripts/", "npcscripts", "npcscripts/" };
            string[] sharedScriptDirectories =
            {
                "data/spells", "data/spells/modules", "data/scripts", "data/shared/scripts",
                "data/shared/scripts/aicomponents", "data/shared/spells", "data/shared/npcscripts",
                "data/shared/tft/common", "data/shared/tft/items", "data/shared/tft/traits",
                "data/shared/spells/practicetool", "data/items", "data/items/spells",
                "data/items/spells/modules", "data/buildingblocks", "data/shared/gamemodes"
            };
            string[] luaCommonPaths =
            {
                "data/spells", "data/spells/modules", "data/scripts", "data/shared/scripts",
                "data/shared/scripts/aicomponents", "data/shared/spells", "data/shared/npcscripts",
                "data/shared/tft/common", "data/shared/tft/items", "data/shared/tft/traits",
                "data/shared/spells/practicetool", "data/items", "data/items/spells",
                "data/items/spells/modules", "data/buildingblocks", "data/shared/gamemodes",
                "data/shared/spells/cheat"
            };

            using var stream = new MemoryStream(data.Array, data.Offset, data.Count, false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), true);
            if (stream.Length < 8) yield break;

            reader.ReadBytes(4);
            uint characterCount = ReadManifestCount(reader);
            for (uint characterIndex = 0; characterIndex < characterCount; characterIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string character = ReadManifestString(reader).ToLowerInvariant();
                uint childCount = ReadManifestCount(reader);
                for (uint childIndex = 0; childIndex < childCount; childIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = ReadManifestString(reader).ToLowerInvariant();
                    foreach (string prefix in luaCharacterPrefixes)
                    foreach (string extension in luaExtensions)
                        yield return new HashGuessCandidate(
                            $"data/characters/{character}/{prefix}{name}.{extension}",
                            HashGuessStrategy.LuaManifest);
                }
            }

            uint sharedCount = ReadManifestCount(reader);
            var sharedNames = new List<string>((int)Math.Min(sharedCount, 100_000));
            for (uint sharedIndex = 0; sharedIndex < sharedCount; sharedIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sharedNames.Add(ReadManifestString(reader).ToLowerInvariant());
            }

            uint hashCount = ReadManifestCount(reader);
            long hashBytes = checked((long)hashCount * sizeof(ulong));
            if (hashBytes > stream.Length - stream.Position)
                throw new InvalidDataException("Lua manifest hash table exceeds the available data.");

            var hashMap = new Dictionary<ulong, uint>((int)Math.Min(hashCount, 100_000));
            for (uint i = 0; i < hashCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong entry = reader.ReadUInt64();
                uint dirIndex = (uint)(entry & 0x1F);
                ulong xxh3Truncated = entry >> 5;
                hashMap[xxh3Truncated] = dirIndex;
            }

            foreach (string name in sharedNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                ulong nameHashTruncated = XxHash3.HashToUInt64(nameBytes) >> 5;
                if (hashMap.TryGetValue(nameHashTruncated, out uint dirIndex) && dirIndex < (uint)sharedScriptDirectories.Length)
                {
                    string dir = sharedScriptDirectories[dirIndex];
                    foreach (string extension in luaExtensions)
                    {
                        yield return new HashGuessCandidate($"{dir}/{name}.{extension}", HashGuessStrategy.LuaManifest);
                    }
                }
                else
                {
                    // Fallback for stripped scripts (Cheat* or Map scripts)
                    if (name.StartsWith("cheat", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string extension in luaExtensions)
                            yield return new HashGuessCandidate($"data/shared/spells/cheat/{name}.{extension}", HashGuessStrategy.LuaManifest);
                    }
                    else
                    {
                        foreach (string prefix in luaCommonPaths)
                        foreach (string extension in luaExtensions)
                            yield return new HashGuessCandidate($"{prefix}/{name}.{extension}", HashGuessStrategy.LuaManifest);

                        for (int map = 0; map < 1500; map++)
                        foreach (string prefix in new[] { string.Empty, "mutators/" })
                            yield return new HashGuessCandidate(
                                $"levels/map{map}/scripts/{prefix}{name}.luabin64",
                                HashGuessStrategy.LuaManifest);
                    }
                }
            }
        }

        private static uint ReadManifestCount(BinaryReader reader)
        {
            const uint maximumCount = 1_000_000;
            if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(uint))
                throw new EndOfStreamException("Unexpected end of Lua manifest count.");
            uint count = reader.ReadUInt32();
            if (count > maximumCount)
                throw new InvalidDataException($"Lua manifest count {count} exceeds the safety limit.");
            return count;
        }

        private static string ReadManifestString(BinaryReader reader)
        {
            const uint maximumLength = 16_384;
            uint length = ReadManifestCount(reader);
            if (length > maximumLength || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException($"Lua manifest string length {length} is invalid.");
            byte[] bytes = reader.ReadBytes((int)length);
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        private static IEnumerable<HashGuessCandidate> GrepFile(
            ArraySegment<byte> data,
            CancellationToken cancellationToken)
        {
            if (data.Array is null || data.Count == 0) yield break;

            string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
            var paths = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match match in Regex.Matches(text, @"(?:ASSETS|Common|DATA|DATA_SOON|DATA_Soon|Gameplay|Global|LEVELS|Loadouts|UX|UIAutoAtlas)/[0-9a-zA-Z_. /-]+"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawPath = match.Value;
                string path = rawPath.ToLowerInvariant().Replace("data_soon/", "data/", StringComparison.Ordinal);
                paths.Add(path);

                int pos = match.Index;
                if (pos >= 2)
                {
                    int n = ByteAt(data, pos - 2) | (ByteAt(data, pos - 1) << 8);
                    if (n == 0 && pos >= 4)
                    {
                        n = ByteAt(data, pos - 4) | (ByteAt(data, pos - 3) << 8) |
                            (ByteAt(data, pos - 2) << 16) | (ByteAt(data, pos - 1) << 24);
                    }

                    if (n > 0 && n < rawPath.Length)
                    {
                        string shortened = rawPath[..n].ToLowerInvariant().Replace("data_soon/", "data/", StringComparison.Ordinal);
                        paths.Add(shortened);
                    }
                }
            }

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (HashGuessCandidate candidate in ExpandGrepFilePath(path, HashGuessStrategy.EmbeddedPathGrep))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (emitted.Add(candidate.Path)) yield return candidate;
                }
            }
        }

        private static IEnumerable<HashGuessCandidate> ExpandGrepFilePath(string path, HashGuessStrategy strategy)
        {
            if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                string prefix = path[..^4];
                yield return new HashGuessCandidate(path, strategy);
                yield return new HashGuessCandidate(prefix + ".luabin", HashGuessStrategy.LuaVariant);
                yield return new HashGuessCandidate(prefix + ".luabin64", HashGuessStrategy.LuaVariant);
                yield return new HashGuessCandidate(prefix + ".preload", HashGuessStrategy.LuaVariant);
                yield break;
            }

            yield return new HashGuessCandidate(path, strategy);
        }

        private static bool IsAscii(string value)
        {
            foreach (char character in value)
                if (character > 0x7F) return false;
            return true;
        }

        private static byte ByteAt(ArraySegment<byte> data, int index) => data.Array[data.Offset + index];

        private static IEnumerable<IReadOnlyList<T>> GetCombinations<T>(IReadOnlyList<T> values, int length)
        {
            if (length <= 0 || values.Count < length) yield break;
            if (length == 1)
            {
                for (int i = 0; i < values.Count; i++)
                    yield return new[] { values[i] };
                yield break;
            }

            int[] indices = new int[length];
            for (int i = 0; i < length; i++)
                indices[i] = i;

            while (true)
            {
                T[] result = new T[length];
                for (int i = 0; i < length; i++)
                    result[i] = values[indices[i]];
                yield return result;

                int pos = length - 1;
                while (pos >= 0 && indices[pos] == values.Count - length + pos)
                    pos--;

                if (pos < 0) break;

                indices[pos]++;
                for (int i = pos + 1; i < length; i++)
                    indices[i] = indices[i - 1] + 1;
            }
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
