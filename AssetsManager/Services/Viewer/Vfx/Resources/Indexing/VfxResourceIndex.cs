using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Utils;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Resources
{
    /// <summary>
    /// Immutable file index shared by every effect in an extracted WAD tree.
    /// Named assets resolve by their authored virtual path or WAD hash; BIN-only truncated
    /// extraction names are handled separately without falling back to unrelated basenames.
    /// </summary>
    internal sealed class VfxResourceIndex
    {
        private const int ExtractedFileNameLimit = 240;

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".tex", ".dds", ".png", ".tga", ".scb", ".sco", ".tmesh", ".gmesh", ".skn", ".skl", ".anm", ".bin"
        };

        private readonly string _root;
        private readonly Dictionary<string, string> _byRelativePath;
        private readonly Dictionary<string, string> _byHash;
        private readonly Dictionary<string, string[]> _byFileName;

        private VfxResourceIndex(string root)
        {
            _root = Path.GetFullPath(root);
            _byRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(path))) continue;

                string fullPath = Path.GetFullPath(path);
                string relativePath = Normalize(Path.GetRelativePath(_root, fullPath));
                _byRelativePath.TryAdd(relativePath, fullPath);

                string ext = Path.GetExtension(fullPath);
                string fileName = Path.GetFileName(fullPath);
                if (!byName.TryGetValue(fileName, out var paths))
                {
                    paths = new List<string>();
                    byName[fileName] = paths;
                }
                paths.Add(fullPath);

                // Index by XxHash64 of the virtual relative path (e.g. assets/characters/lulu/...)
                ulong hash = XxHash64Ext.Hash(relativePath.ToLowerInvariant());
                string hashKey = hash.ToString("x16");
                _byHash.TryAdd(hashKey, fullPath);
                _byHash.TryAdd(hashKey + ext, fullPath);

                if (!relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
                    !relativePath.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                {
                    ulong assetsHash = XxHash64Ext.Hash(("assets/" + relativePath).ToLowerInvariant());
                    string assetsHashKey = assetsHash.ToString("x16");
                    _byHash.TryAdd(assetsHashKey, fullPath);
                    _byHash.TryAdd(assetsHashKey + ext, fullPath);
                }

                // If the file on disk was extracted with a 16-hex or 8-hex hash name, also index it
                string stem = Path.GetFileNameWithoutExtension(fullPath);
                if ((stem.Length == 16 || stem.Length == 8) && ulong.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                {
                    string stemLower = stem.ToLowerInvariant();
                    _byHash.TryAdd(stemLower, fullPath);
                    _byHash.TryAdd(stemLower + ext, fullPath);
                }
            }

            _byFileName = byName.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }

        public static VfxResourceIndex Build(string root) => new(root);

        public string Resolve(string authoredPath, IReadOnlyList<string> extensions)
            => ResolveAll(authoredPath, extensions).FirstOrDefault();

        /// <summary>
        /// Resolves a declared BIN dependency by authored path.
        /// Truncated extraction names remain supported; unrelated basename matches do not.
        /// </summary>
        public IReadOnlyList<string> ResolveLinkedAll(string authoredPath, IReadOnlyList<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(authoredPath)) return Array.Empty<string>();

            string normalized = Normalize(authoredPath);
            IReadOnlyList<string> exact = ResolveExact(normalized, extensions);
            if (exact.Count > 0) return exact;

            return ResolveTruncated(normalized, extensions);
        }

        public IReadOnlyList<string> ResolveAll(string authoredPath, IReadOnlyList<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(authoredPath)) return Array.Empty<string>();

            string normalized = Normalize(authoredPath);
            IReadOnlyList<string> exact = ResolveExact(normalized, extensions);
            if (exact.Count > 0) return exact;

            IReadOnlyList<string> truncated = ResolveTruncated(normalized, extensions);
            if (truncated.Count > 0) return truncated;

            // LTK's NamedAsset lookup is exact: a missing authored virtual path remains
            // unresolved instead of borrowing a same-named file from another directory.
            return Array.Empty<string>();
        }

        private IReadOnlyList<string> ResolveExact(string normalized, IReadOnlyList<string> extensions)
        {
            foreach (string extension in extensions)
            {
                string candidate = Normalize(Path.ChangeExtension(normalized, extension));
                if (_byRelativePath.TryGetValue(candidate, out string exact)) return new[] { exact };
            }

            // Authored path is a 16-hex or 8-hex hash string (e.g. "aa5a8ee2e6b5d8c4.anm" or "0xaa5a8ee2e6b5d8c4")
            string authoredStem = Path.GetFileNameWithoutExtension(normalized);
            if (authoredStem.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                authoredStem = authoredStem[2..];
            if ((authoredStem.Length == 16 || authoredStem.Length == 8) &&
                ulong.TryParse(authoredStem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                string hexLower = authoredStem.ToLowerInvariant();
                if (_byHash.TryGetValue(hexLower, out string matched)) return new[] { matched };
                foreach (string extension in extensions)
                {
                    if (_byHash.TryGetValue(hexLower + extension, out string matchedWithExt))
                        return new[] { matchedWithExt };
                    if (_byFileName.TryGetValue(hexLower + extension, out string[] matchedByName) && matchedByName.Length > 0)
                        return matchedByName;
                }
            }

            // Unresolved WAD entries keep the hash of their full virtual path, including DATA/ASSETS.
            foreach (string sourceExtension in extensions)
            {
                string virtualPath = Path.ChangeExtension(normalized, sourceExtension).ToLowerInvariant();
                string hash = XxHash64Ext.Hash(virtualPath).ToString("x16");
                if (_byHash.TryGetValue(hash, out string hashedMatch))
                    return new[] { hashedMatch };
                foreach (string storedExtension in extensions)
                {
                    if (_byFileName.TryGetValue(hash + storedExtension, out string[] hashed))
                        return hashed;
                }
            }

            return Array.Empty<string>();
        }

        private IReadOnlyList<string> ResolveTruncated(string normalized, IReadOnlyList<string> extensions)
        {
            string authoredDirectory = Normalize(Path.GetDirectoryName(normalized) ?? string.Empty);
            foreach (string extension in extensions)
            {
                if (!extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                string authoredFileName = Path.GetFileName(Path.ChangeExtension(normalized, extension));
                string authoredStem = Path.GetFileNameWithoutExtension(authoredFileName);
                string[] truncated = _byRelativePath
                    .Where(pair =>
                    {
                        string indexedDirectory = Normalize(Path.GetDirectoryName(pair.Key) ?? string.Empty);
                        string indexedFileName = Path.GetFileName(pair.Key);
                        string indexedStem = StripCollisionSuffix(Path.GetFileNameWithoutExtension(indexedFileName));
                        return (indexedFileName.Length >= ExtractedFileNameLimit ||
                                HasCollisionSuffix(Path.GetFileNameWithoutExtension(indexedFileName))) &&
                               indexedDirectory.Equals(authoredDirectory, StringComparison.OrdinalIgnoreCase) &&
                               authoredStem.StartsWith(indexedStem, StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(pair => HasCollisionSuffix(Path.GetFileNameWithoutExtension(pair.Key)) ? 1 : 0)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Value)
                    .ToArray();
                if (truncated.Length > 0) return truncated;
            }

            return Array.Empty<string>();
        }

        private static bool HasCollisionSuffix(string stem)
        {
            int open = stem.LastIndexOf(" (", StringComparison.Ordinal);
            return open >= 0 && stem.EndsWith(')') &&
                   int.TryParse(stem.AsSpan(open + 2, stem.Length - open - 3), out _);
        }

        private static string StripCollisionSuffix(string stem)
        {
            int open = stem.LastIndexOf(" (", StringComparison.Ordinal);
            return open >= 0 && stem.EndsWith(')') &&
                   int.TryParse(stem.AsSpan(open + 2, stem.Length - open - 3), out _)
                ? stem[..open]
                : stem;
        }

        private static string Normalize(string path)
        {
            return PathUtils.NormalizeSeparators(path).TrimStart('/');
        }

    }
}
