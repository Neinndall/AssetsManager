using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal sealed class GameTextureFamilyIndex
    {
        private const int MaximumSuffixes = 11_000;
        private const int DirectoryCandidateBudget = 2_000_000;
        private static readonly string[] ParticleRoles = { "tx", "d", "m", "m2", "mask" };
        private readonly Dictionary<string, List<string>> _paths = new(StringComparer.Ordinal);
        private readonly Dictionary<ulong, string> _directories = new();
        private readonly string[] _suffixes;
        private readonly string[] _allPrefixes;
        private readonly (string Prefix, string Extension)[] _particleTemplates;
        private readonly ConditionalWeakTable<HashGuessEngine, ConcurrentDictionary<string, byte>> _scanned = new();

        internal GameTextureFamilyIndex(IEnumerable<string> paths, CancellationToken cancellationToken)
        {
            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            var additionalFrequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            var particleTemplates = new HashSet<(string Prefix, string Extension)>();
            foreach (string value in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = PathUtils.NormalizePath(value);
                if ((path.StartsWith("assets/characters/", StringComparison.Ordinal) && path.Contains("/particles/", StringComparison.Ordinal) ||
                     path.StartsWith("assets/maps/particles/", StringComparison.Ordinal)) &&
                    (path.EndsWith(".tex", StringComparison.Ordinal) || path.EndsWith(".dds", StringComparison.Ordinal)))
                {
                    int separator = path.LastIndexOf('_');
                    if (separator > path.LastIndexOf('/') && ParticleRoles.Contains(path[(separator + 1)..^4], StringComparer.Ordinal))
                        particleTemplates.Add((path[..(separator + 1)], path[^4..]));
                }
                bool isSkin = TryGetSkinTextureDirectory(path, out string directory);
                if (!isSkin && !TryGetAdditionalTextureDirectory(path, out directory)) continue;
                var suffixFrequencies = isSkin ? frequencies : additionalFrequencies;
                if (!_paths.TryGetValue(directory, out var family))
                    _paths[directory] = family = new List<string>();
                family.Add(path);
                _directories[XxHash64Ext.Hash(path)] = directory;
                string name = path[(directory.Length + 1)..];
                // Keep compound suffixes without treating patch decorations as naming evidence.
                if (name[..^4].Contains('.')) continue;
                int end = name.Length;
                for (int count = 0; count < 3; count++)
                {
                    int separator = name.LastIndexOf('_', end - 1);
                    if (separator <= 0) break;
                    string suffix = name[(separator + 1)..];
                    suffixFrequencies[suffix] = suffixFrequencies.GetValueOrDefault(suffix) + 1;
                    end = separator;
                }
            }
            _suffixes = frequencies.Where(pair => pair.Value >= 2)
                .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(MaximumSuffixes).Select(pair => pair.Key)
                .Concat(additionalFrequencies.Where(pair => pair.Value >= 2)
                    .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(MaximumSuffixes).Select(pair => pair.Key))
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(suffix => frequencies.GetValueOrDefault(suffix) + additionalFrequencies.GetValueOrDefault(suffix))
                .ThenBy(suffix => suffix, StringComparer.Ordinal).ToArray();

            var allPrefixes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var family in _paths.Values)
            {
                family.Sort(StringComparer.Ordinal);
                foreach (string path in family)
                {
                    int end = path.Length;
                    int dirLen = path.LastIndexOf('/');
                    for (int count = 0; count < 4; count++)
                    {
                        int separator = path.LastIndexOf('_', end - 1);
                        if (separator <= dirLen) break;
                        allPrefixes.Add(path[..(separator + 1)]);
                        end = separator;
                    }
                }
            }
            _allPrefixes = allPrefixes.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            _particleTemplates = particleTemplates.OrderBy(value => value.Prefix, StringComparer.Ordinal)
                .ThenBy(value => value.Extension, StringComparer.Ordinal).ToArray();
        }

        internal static bool HasUnresolvedContext(HashGuessEngine engine, BinTree tree, CancellationToken cancellationToken)
        {
            foreach (var obj in tree.Objects.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (obj.ClassHash != 0xff9d3409 && obj.ClassHash != 0x9b67e9f6 && obj.ClassHash != 0x27dd6361) continue;
                foreach (var property in obj.Properties.Values.SelectMany(Enumerate))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (property is BinTreeWadChunkLink link && engine.UnknownHashes.Contains(link.Value)) return true;
                }
            }
            return false;
        }
        internal void Guess(HashGuessEngine engine, BinTree tree, string sourcePath,
            string sourceWadPath, ulong sourceChunkHash, CancellationToken cancellationToken)
        {
            var directories = new HashSet<string>(StringComparer.Ordinal);
            bool hasUnknown = false;
            foreach (var obj in tree.Objects.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Texture context must come from a material, skin or gear definition.
                if (obj.ClassHash != 0xff9d3409 && obj.ClassHash != 0x9b67e9f6 && obj.ClassHash != 0x27dd6361) continue;
                var localDirectories = new HashSet<string>(StringComparer.Ordinal);
                bool localUnknown = false;
                foreach (var property in obj.Properties.Values.SelectMany(Enumerate))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (property is not BinTreeWadChunkLink link) continue;
                    if (engine.UnknownHashes.Contains(link.Value)) localUnknown = true;
                    if (_directories.TryGetValue(link.Value, out string directory))
                        localDirectories.Add(directory);
                }
                if (!localUnknown) continue;
                hasUnknown = true;
                directories.UnionWith(localDirectories);
            }
            if (!hasUnknown) return;

            // A new skin may have no resolved texture links yet, but its catalog family can still seed it.
            string normalizedSource = PathUtils.NormalizePath(sourcePath);
            if (normalizedSource.StartsWith("data/characters/", StringComparison.Ordinal) &&
                normalizedSource.Contains("/skins/", StringComparison.Ordinal) &&
                normalizedSource.EndsWith(".bin", StringComparison.Ordinal))
            {
                string directory = "assets/" + normalizedSource[5..^4];
                if (_paths.ContainsKey(directory)) directories.Add(directory);
            }

            var scanned = _scanned.GetOrCreateValue(engine);
            foreach (string directory in directories.OrderBy(value => value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!scanned.TryAdd(directory, 0)) continue;
                bool completed = false;
                try
                {
                    GuessDirectory(engine, directory, sourceWadPath, sourceChunkHash, cancellationToken);
                    completed = true;
                }
                finally
                {
                    // A cancelled pass must not suppress work on retry.
                    if (!completed) scanned.TryRemove(directory, out _);
                }
            }
        }

        private void GuessDirectory(HashGuessEngine engine, string directory, string source,
            ulong sourceHash, CancellationToken cancellationToken)
        {
            if (!_paths.TryGetValue(directory, out var paths)) return;
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                int end = path.Length;
                for (int count = 0; count < 4; count++)
                {
                    int separator = path.LastIndexOf('_', end - 1);
                    if (separator <= directory.Length) break;
                    prefixes.Add(path[..(separator + 1)]);
                    end = separator;
                }
            }
            string[] orderedPrefixes = prefixes.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            int candidates = 0;
            // Frequency-first order gives every local stem a chance before the budget is exhausted.
            foreach (string suffix in _suffixes)
            foreach (string prefix in orderedPrefixes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.RemainingUnknownCount == 0 || candidates++ >= DirectoryCandidateBudget) return;
                engine.CheckPrefixSuffix(prefix, suffix, HashGuessStrategy.BinEntry, source, sourceHash);
            }
        }

        /// <summary>
        /// Runs the learned texture build-list across texture families in frequency-ordered batches (round-robin),
        /// prioritizing high-probability suffixes across all champions before exhausting the candidate budget.
        /// </summary>
        internal long RunBuildList(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            long candidateBudget = long.MaxValue,
            Action<long> progress = null)
        {
            if (candidateBudget <= 0 || engine.RemainingUnknownCount == 0)
                return 0;

            const int suffixBatchSize = 64;
            long checkedCandidates = 0;

            // Test small particle role families before the wider learned suffix combinations.
            foreach (var template in _particleTemplates)
            foreach (string role in ParticleRoles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.RemainingUnknownCount == 0 || checkedCandidates >= candidateBudget)
                {
                    progress?.Invoke(checkedCandidates);
                    return checkedCandidates;
                }
                engine.CheckPrefixSuffix(template.Prefix, role + template.Extension,
                    HashGuessStrategy.WordlistVariant, "GAME Custom: texture build-list");
                checkedCandidates++;
                if ((checkedCandidates & 0x3fff) == 0) progress?.Invoke(checkedCandidates);
            }

            for (int suffixOffset = 0; suffixOffset < _suffixes.Length; suffixOffset += suffixBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.RemainingUnknownCount == 0 || checkedCandidates >= candidateBudget) break;

                int batchCount = Math.Min(suffixBatchSize, _suffixes.Length - suffixOffset);

                for (int i = 0; i < batchCount; i++)
                {
                    string suffix = _suffixes[suffixOffset + i];
                    foreach (string prefix in _allPrefixes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (engine.RemainingUnknownCount == 0 || checkedCandidates >= candidateBudget) break;

                        engine.CheckPrefixSuffix(
                            prefix,
                            suffix,
                            HashGuessStrategy.WordlistVariant,
                            "GAME Custom: texture build-list");

                        checkedCandidates++;
                        if ((checkedCandidates & 0x3fff) == 0) progress?.Invoke(checkedCandidates);
                    }
                    if (engine.RemainingUnknownCount == 0 || checkedCandidates >= candidateBudget) break;
                }
            }

            progress?.Invoke(checkedCandidates);
            return checkedCandidates;
        }

        private static bool TryGetSkinTextureDirectory(string path, out string directory)
        {
            directory = null;
            if (!path.StartsWith("assets/characters/", StringComparison.Ordinal) ||
                !(path.EndsWith(".tex", StringComparison.Ordinal) || path.EndsWith(".dds", StringComparison.Ordinal)))
                return false;
            int characterEnd = path.IndexOf('/', "assets/characters/".Length);
            if (characterEnd < 0 || !path.AsSpan(characterEnd).StartsWith("/skins/")) return false;
            int skinEnd = path.IndexOf('/', characterEnd + "/skins/".Length);
            if (skinEnd < 0 || path.IndexOf('/', skinEnd + 1) >= 0) return false;
            directory = path[..skinEnd];
            return true;
        }

        private static bool TryGetAdditionalTextureDirectory(string path, out string directory)
        {
            directory = null;
            if (!(path.EndsWith(".tex", StringComparison.Ordinal) || path.EndsWith(".dds", StringComparison.Ordinal))) return false;
            int separator = path.LastIndexOf('/');
            if (path.StartsWith("assets/maps/kitpieces/", StringComparison.Ordinal) && path.Contains("/textures/", StringComparison.Ordinal))
            {
                directory = path[..separator];
                return true;
            }
            if (!path.StartsWith("assets/characters/", StringComparison.Ordinal)) return false;
            int themes = path.IndexOf("/themes/", "assets/characters/".Length, StringComparison.Ordinal);
            if (themes < 0 || path.IndexOf('/', themes + "/themes/".Length) != separator) return false;
            directory = path[..separator];
            return true;
        }

        private static IEnumerable<BinTreeProperty> Enumerate(BinTreeProperty property)
        {
            yield return property;
            IEnumerable<BinTreeProperty> children = property switch
            {
                BinTreeStruct structure => structure.Properties.Values,
                BinTreeContainer container => container.Elements,
                BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
                BinTreeOptional optional when optional.Value != null => new[] { optional.Value },
                _ => Array.Empty<BinTreeProperty>()
            };
            foreach (var child in children)
                foreach (var descendant in Enumerate(child)) yield return descendant;
        }
    }
}
