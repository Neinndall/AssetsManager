using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers.Game
{
    internal sealed partial class GameHashGuesser
    {
        private const int AnimationBinFallbackCandidateBudget = 100_000;
        private static readonly Regex AnimationBinPathRegex = new(
            @"^(?:assets|data)/characters/(?<character>[^/]+)/(?:animations/(?<skin>[^/]+)|skins/(?<skin>[^/]+)(?:/animations)?(?:/[^/]+)?|themes/(?<skin>[^/]+)(?:/animations)?(?:/[^/]+)?)\.(?:bin|inibin)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly record struct AnimationFileLink(uint NameHash, ulong PathHash, string Path);

        private void GuessAnimationBinPaths(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken,
            Func<BinTree> binTreeFactory = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match context = AnimationBinPathRegex.Match(PathUtils.NormalizePath(sourcePath));
            if (!context.Success || data.Array is null || data.Count == 0) return;

            string character = context.Groups["character"].Value.ToLowerInvariant();
            string sourceSkin = context.Groups["skin"].Value.ToLowerInvariant();

            var links = new HashSet<AnimationFileLink>();
            try
            {
                BinTree tree = binTreeFactory != null
                    ? binTreeFactory()
                    : new BinTree(new MemoryStream(data.Array, data.Offset, data.Count, writable: false));
                if (tree == null) return;
                cancellationToken.ThrowIfCancellationRequested();
                foreach (AnimationFileLink link in EnumerateAnimationFileLinks(tree))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    links.Add(link);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME animation BIN link scan skipped '{sourcePath}': {exception.Message}");
                return;
            }

            foreach (AnimationFileLink link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(link.Path))
                    Check(engine, link.Path, HashGuessStrategy.AnimationBinLink, sourceWadPath, sourceChunkHash);
            }

            var unresolved = links
                .Where(link => link.PathHash != 0 && engine.UnknownHashes.Contains(link.PathHash))
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();
            if (unresolved.Count == 0) return;

            CheckIter(
                engine,
                EnumerateAnimationPaths(
                    character,
                    sourceSkin,
                    IsThemeAnimationContext(character, sourceSkin),
                    unresolved,
                    engine.UnknownHashes,
                    cancellationToken),
                HashGuessStrategy.AnimationBinLink,
                sourceWadPath,
                cancellationToken,
                sourceChunkHash);
            var remainingHashes = unresolved
                .Where(link => engine.UnknownHashes.Contains(link.PathHash))
                .Select(link => link.PathHash)
                .ToHashSet();
            if (remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;

            var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddAnimationCharacter(character);
            if (character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                AddAnimationCharacter(character[5..]);
            else if (!character.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                AddAnimationCharacter($"jade_{character}");

            var emittedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int checkedCandidates = 0;

            var namedActions = EnumerateAnimationNameCandidates(
                character,
                unresolved,
                remainingHashes,
                cancellationToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (namedActions.Count > 0)
            {
                foreach (string animationCharacter in characters)
                foreach (string skin in EnumerateAnimationSkins(animationCharacter))
                {
                    if (remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;
                    bool isTheme = IsThemeAnimationContext(animationCharacter, skin);
                    foreach (string action in namedActions)
                    foreach (string candidate in EnumerateAnimationCandidates(
                                 animationCharacter,
                                 skin,
                                 action,
                                 isTheme))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!emittedCandidates.Add(candidate)) continue;
                        ulong hash = XxHash64Ext.Hash(candidate);
                        if (remainingHashes.Remove(hash) && engine.UnknownHashes.Contains(hash))
                            Check(engine, candidate, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                    }
                }
            }

            foreach (string animationCharacter in characters)
            foreach (string skin in EnumerateAnimationSkins(animationCharacter))
            {
                if (remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;
                bool isTheme = IsThemeAnimationContext(animationCharacter, skin);
                foreach (string action in EnumerateActionsForSkin(animationCharacter, skin))
                foreach (string candidate in EnumerateAnimationCandidates(
                             animationCharacter,
                             skin,
                             action,
                             isTheme))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!emittedCandidates.Add(candidate)) continue;
                    if (checkedCandidates++ >= AnimationBinFallbackCandidateBudget ||
                        remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;

                    ulong hash = XxHash64Ext.Hash(candidate);
                    if (remainingHashes.Remove(hash) && engine.UnknownHashes.Contains(hash))
                        Check(engine, candidate, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                }
            }

            void AddAnimationCharacter(string value)
            {
                if (!string.IsNullOrWhiteSpace(value)) characters.Add(value.ToLowerInvariant());
            }

            IEnumerable<string> EnumerateAnimationSkins(string animationCharacter)
            {
                IReadOnlyList<string> knownSkins = GetChampionSkinNames(animationCharacter, cancellationToken);
                bool hasSource = !string.IsNullOrEmpty(sourceSkin) &&
                    !sourceSkin.Equals("root", StringComparison.OrdinalIgnoreCase) &&
                    !sourceSkin.Equals("shared", StringComparison.OrdinalIgnoreCase);
                // Shared base container first: highest hit density per candidate, so it
                // survives the fallback budget even when the source skin overflows it.
                if (knownSkins.Contains("base", StringComparer.OrdinalIgnoreCase) &&
                    (!hasSource || !sourceSkin.Equals("base", StringComparison.OrdinalIgnoreCase)))
                    yield return "base";
                if (hasSource)
                    yield return sourceSkin;

                foreach (string candidate in OrderAnimationContainers(sourceSkin, knownSkins))
                    if (!candidate.Equals(sourceSkin, StringComparison.OrdinalIgnoreCase) &&
                        !candidate.Equals("base", StringComparison.OrdinalIgnoreCase)) yield return candidate;
            }

            IEnumerable<string> EnumerateActionsForSkin(string animChar, string sk)
            {
                bool isTargetSkin = !string.IsNullOrEmpty(sourceSkin) &&
                                    sk.Equals(sourceSkin, StringComparison.OrdinalIgnoreCase);
                // The shared base container only gets thin per-character actions, so pooled
                // actions never reach it before the budget dies; give it the full fallback.
                if (isTargetSkin || string.IsNullOrEmpty(sourceSkin) ||
                    sk.Equals("base", StringComparison.OrdinalIgnoreCase))
                {
                    return EnumerateFallbackActions(animChar);
                }
                return GetCharacterAnimationActions(animChar, cancellationToken);
            }

            IReadOnlyList<string> EnumerateFallbackActions(string animationCharacter) =>
                Corpus.GetOrCreate($"fallback-animation-actions/{animationCharacter.ToLowerInvariant()}", _ =>
                    GetCharacterAnimationActions(animationCharacter, cancellationToken)
                        .Concat(GetGlobalAnimationActions(cancellationToken))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());

            static IEnumerable<string> EnumerateAnimationCandidates(
                string animationCharacter,
                string skin,
                string action,
                bool includeThemeLayout)
            {
                foreach (string candidate in EnumerateAnimationNameVariants(
                             animationCharacter,
                             skin,
                             action,
                             includeThemeLayout: includeThemeLayout))
                    yield return candidate;

                if (!action.Equals("recall", StringComparison.OrdinalIgnoreCase)) yield break;
                foreach (string root in AnimationRootPrefixes)
                {
                    yield return $"{root}/characters/{animationCharacter}/skins/{skin}/animations/recall.skins_{animationCharacter}_{skin}.anm";
                    if (animationCharacter.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                        yield return $"{root}/characters/{animationCharacter}/skins/{skin}/animations/recall.skins_{animationCharacter[5..]}_{skin}.anm";
                }
            }
        }

        private IReadOnlyDictionary<uint, List<string>> GetAnimationNameIndex(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("animation-name-index", knownPaths =>
            {
                var index = new Dictionary<uint, List<string>>();
                var spellSuffixes = new HashSet<(string Suffix, int Width)>();
                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = GetBasename(path);
                    if (name.Length <= 4) continue;
                    string stem = name[..^4];
                    Add(Fnv1a.HashLower(stem), stem);
                    LearnSpellSuffix(stem);
                    string compact = new(stem.Where(char.IsLetterOrDigit).ToArray());
                    if (compact.Length > 0) Add(Fnv1a.HashLower(compact), stem);

                    int sep = stem.IndexOf('_');
                    while (sep >= 0 && sep < stem.Length - 1)
                    {
                        string sub = stem[(sep + 1)..];
                        if (sub.Length >= 2)
                        {
                            Add(Fnv1a.HashLower(sub), sub);
                            LearnSpellSuffix(sub);
                            string compactSub = new(sub.Where(char.IsLetterOrDigit).ToArray());
                            if (compactSub.Length > 0) Add(Fnv1a.HashLower(compactSub), sub);
                        }
                        sep = stem.IndexOf('_', sep + 1);
                    }
                }
                foreach (var format in spellSuffixes)
                for (int slot = 1; slot <= 4; slot++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string alias = "spell" + slot.ToString("D" + format.Width, CultureInfo.InvariantCulture) + format.Suffix;
                    Add(Fnv1a.HashLower(alias), alias);
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

                void LearnSpellSuffix(string stem)
                {
                    if (!stem.StartsWith("spell", StringComparison.OrdinalIgnoreCase)) return;
                    int end = 5;
                    while (end < stem.Length && char.IsAsciiDigit(stem[end])) end++;
                    if (end - 5 is < 1 or > 2 || end >= stem.Length || stem[end] != '_' ||
                        !int.TryParse(stem.AsSpan(5, end - 5), out int slot) || slot is < 1 or > 4) return;
                    spellSuffixes.Add((stem[end..].ToLowerInvariant(), end - 5));
                }

                void Add(uint hash, string stem)
                {
                    if (!index.TryGetValue(hash, out List<string> values))
                        index.Add(hash, values = new List<string>());
                    if (!values.Contains(stem, StringComparer.OrdinalIgnoreCase)) values.Add(stem);
                }
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
            AnimationPathGroup[] groups = GetAnimationPathGroups(character, skin, includeThemeLayout);
            var hashBuffer = new ulong[groups.Max(group => group.Prefixes.Length)];

            foreach (string name in EnumerateAnimationNameCandidates(character, links, remaining, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!attemptedNames.Add(name)) continue;
                foreach (string path in MatchAnimationVariantsCore(name, skin, remaining, groups, hashBuffer, prefixes, suffixes, cancellationToken))
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
                    foreach (string path in MatchAnimationVariantsCore(name, skin, remaining, groups, hashBuffer, DefaultPrefixModifiers, DefaultSuffixModifiers, cancellationToken))
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

            var resolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AnimationFileLink link in namedLinks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string resolved = _resolveBinHash?.Invoke(link.NameHash);
                if (IsAnimationStem(resolved))
                {
                    if (Fnv1a.HashLower(resolved) == link.NameHash) resolvedNames.Add(resolved);
                    yield return resolved;
                }
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
            HashSet<string> prefixes = Corpus.GetOrCreate($"animation-reusable-prefixes/{character.ToLowerInvariant()}",
                _ => GetReusableAnimationPrefixes(sourceNames));

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

            // Clip labels can be numbered even when none of their filenames is catalogued yet.
            foreach (HashGuessCandidate candidate in GenerateNumberCandidates(
                         resolvedNames.Where(name => name.Any(char.IsAsciiDigit)).Select(name => $"animations/{name}.anm"),
                         numberLimit: 360, candidateBudget: int.MaxValue, includeCommonPadding: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stem = GetBasename(candidate.Path)[..^4];
                if (nameHashes.Contains(Fnv1a.HashLower(stem))) yield return stem;
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

        internal IReadOnlyList<string> GetAnimationNames(string character, CancellationToken cancellationToken)
        {
            var index = Corpus.GetOrCreate("animation-names-by-character", paths => BuildAnimationNamesByCharacter(paths, cancellationToken));
            return index.TryGetValue(character, out IReadOnlyList<string> names) ? names : Array.Empty<string>();
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildAnimationNamesByCharacter(
            IReadOnlyList<string> knownPaths,
            CancellationToken cancellationToken)
        {
            var namesByCharacter = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var knownAnimationPathRegex = new Regex(
                @"^(?:assets|data)/characters/(?<character>[^/]+)/(?:skins|themes)/(?<skin>[^/]+)/animations/[^/]+\.anm$",
                RegexOptions.IgnoreCase);
            for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
            {
                if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                string path = knownPaths[pathIndex];
                if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
                Match context = knownAnimationPathRegex.Match(PathUtils.NormalizePath(path));
                if (!context.Success) continue;

                string character = context.Groups["character"].Value;
                if (!namesByCharacter.TryGetValue(character, out var names))
                    namesByCharacter[character] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string name = GetBasename(path);
                if (name.Length > 0) names.Add(name);
            }

            return namesByCharacter.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
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
            bool includeThemeLayout = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnimationPathGroup[] groups = GetAnimationPathGroups(character, skin, includeThemeLayout);
            var hashBuffer = new ulong[groups.Max(group => group.Prefixes.Length)];
            foreach (string path in MatchAnimationVariantsCore(name, skin, remaining, groups, hashBuffer, prefixes, suffixes, cancellationToken))
                yield return path;
        }

        private sealed record AnimationPathGroup(string[] Prefixes, byte[][] EncodedPrefixes, int MaximumPrefixBytes);

        private AnimationPathGroup[] GetAnimationPathGroups(string character, string skin, bool includeThemeLayout) =>
            Corpus.GetOrCreate($"animation-path-prefixes/{character}/{skin}/{includeThemeLayout}", _ =>
                BuildAnimationPathPrefixes(character, skin, includeThemeLayout).Select(prefixes =>
                {
                    byte[][] encoded = prefixes.Select(Encoding.UTF8.GetBytes).ToArray();
                    return new AnimationPathGroup(prefixes, encoded, encoded.Max(value => value.Length));
                }).ToArray());

        private static IEnumerable<string> MatchAnimationVariantsCore(string name, string skin, ISet<ulong> remaining,
            AnimationPathGroup[] groups, ulong[] hashBuffer, IReadOnlyList<string> prefixes, IReadOnlyList<string> suffixes,
            CancellationToken cancellationToken)
        {
            foreach (string path in MatchName(name)) yield return path;

            string converted = Regex.Replace(name, @"skin\d+", skin, RegexOptions.IgnoreCase);
            if (converted.Equals(name, StringComparison.OrdinalIgnoreCase)) yield break;
            foreach (string path in MatchName(converted)) yield return path;

            IEnumerable<string> MatchName(string value)
            {
                string stem = value.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
                if (string.IsNullOrWhiteSpace(stem) || stem.Contains('/') || stem.Contains('\\')) yield break;
                stem = stem.ToLowerInvariant();
                foreach (AnimationPathGroup group in groups)
                foreach (string pre in prefixes ?? DefaultPrefixModifiers)
                foreach (string suf in suffixes ?? DefaultSuffixModifiers)
                foreach (string variant in ExpandAnimationStemVariants(pre + stem + suf))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    HashAnimationGroup(group, variant, hashBuffer);
                    for (int i = 0; i < group.Prefixes.Length; i++)
                    {
                        if (remaining.Count == 0) yield break;
                        if (remaining.Remove(hashBuffer[i]))
                            yield return group.Prefixes[i] + variant + ".anm";
                    }
                }
            }
        }

        private static void HashAnimationGroup(AnimationPathGroup group, string stem, Span<ulong> hashes)
        {
            int stemLength = Encoding.UTF8.GetByteCount(stem);
            int capacity = group.MaximumPrefixBytes + stemLength + 4;
            byte[] rentedStem = null, rentedPath = null;
            Span<byte> stemBytes = stemLength <= 512 ? stackalloc byte[stemLength]
                : (rentedStem = ArrayPool<byte>.Shared.Rent(stemLength)).AsSpan(0, stemLength);
            Span<byte> path = capacity <= 1024 ? stackalloc byte[capacity]
                : (rentedPath = ArrayPool<byte>.Shared.Rent(capacity)).AsSpan(0, capacity);
            try
            {
                Encoding.UTF8.GetBytes(stem, stemBytes);
                for (int i = 0; i < group.EncodedPrefixes.Length; i++)
                {
                    byte[] prefix = group.EncodedPrefixes[i];
                    prefix.CopyTo(path);
                    stemBytes.CopyTo(path[prefix.Length..]);
                    ".anm"u8.CopyTo(path[(prefix.Length + stemLength)..]);
                    hashes[i] = XxHash64.HashToUInt64(path[..(prefix.Length + stemLength + 4)]);
                }
            }
            finally
            {
                if (rentedStem != null) ArrayPool<byte>.Shared.Return(rentedStem);
                if (rentedPath != null) ArrayPool<byte>.Shared.Return(rentedPath);
            }
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
                // Folder padding and the filename's skin token are independent in shipped clips.
                if (!includeThemeLayout && !sk.Equals(paddedSkin, StringComparison.Ordinal))
                {
                    values.Add(directory + character + "_" + paddedSkin + "_");
                    values.Add(directory + paddedSkin + "_");
                }
                if (!includeThemeLayout && !sk.Equals(skin, StringComparison.Ordinal))
                {
                    values.Add(directory + character + "_" + skin + "_");
                    values.Add(directory + skin + "_");
                }
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
            for (int i = 1; i < stem.Length; i++)
            {
                if (!char.IsAsciiDigit(stem[i]) || char.IsAsciiDigit(stem[i - 1])) continue;
                if (stem[i - 1] == '_') yield return stem.Remove(i - 1, 1);
                else if (stem[i - 1] != '-') yield return stem.Insert(i, "_");
            }
            if (stem.StartsWith("crit", StringComparison.OrdinalIgnoreCase))
                yield return "attack_" + stem;
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
                foreach (BinTreeProperty path in FindProperties(pair.Value, animationFilePathNameHash))
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
    }
}
