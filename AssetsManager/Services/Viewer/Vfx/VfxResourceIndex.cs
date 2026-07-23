using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Immutable file index shared by every effect in an extracted WAD tree.
    /// Exact authored paths always win; basename fallback is deterministic.
    /// </summary>
    internal sealed class VfxResourceIndex
    {
        private const int ExtractedFileNameLimit = 240;

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".tex", ".dds", ".png", ".tga", ".scb", ".sco", ".skn", ".bin"
        };

        private readonly string _root;
        private readonly Dictionary<string, string> _byRelativePath;
        private readonly Dictionary<string, string[]> _byFileName;

        private VfxResourceIndex(string root)
        {
            _root = Path.GetFullPath(root);
            _byRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(path))) continue;

                string fullPath = Path.GetFullPath(path);
                string relativePath = Normalize(Path.GetRelativePath(_root, fullPath));
                _byRelativePath.TryAdd(relativePath, fullPath);

                string fileName = Path.GetFileName(fullPath);
                if (!byName.TryGetValue(fileName, out var paths))
                {
                    paths = new List<string>();
                    byName[fileName] = paths;
                }
                paths.Add(fullPath);
            }

            _byFileName = byName.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }

        public static VfxResourceIndex Build(string root) => new(root);

        public string Resolve(string authoredPath, IReadOnlyList<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(authoredPath)) return null;

            string normalized = Normalize(authoredPath);
            foreach (string extension in extensions)
            {
                string candidate = Normalize(Path.ChangeExtension(normalized, extension));
                if (_byRelativePath.TryGetValue(candidate, out string exact)) return exact;
            }

            string authoredDirectory = Normalize(Path.GetDirectoryName(normalized) ?? string.Empty);
            foreach (string extension in extensions)
            {
                if (!extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                string authoredFileName = Path.GetFileName(Path.ChangeExtension(normalized, extension));
                string truncated = _byRelativePath
                    .Where(pair =>
                    {
                        string indexedDirectory = Normalize(Path.GetDirectoryName(pair.Key) ?? string.Empty);
                        string indexedFileName = Path.GetFileName(pair.Key);
                        string indexedStem = Path.GetFileNameWithoutExtension(indexedFileName);
                        string authoredStem = Path.GetFileNameWithoutExtension(authoredFileName);
                        return indexedFileName.Length >= ExtractedFileNameLimit &&
                               indexedDirectory.Equals(authoredDirectory, StringComparison.OrdinalIgnoreCase) &&
                               authoredStem.StartsWith(indexedStem, StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(pair => Path.GetFileName(pair.Key).Length)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Value)
                    .FirstOrDefault();
                if (truncated != null) return truncated;
            }

            foreach (string extension in extensions)
            {
                string fileName = Path.GetFileNameWithoutExtension(normalized) + extension;
                if (!_byFileName.TryGetValue(fileName, out string[] candidates)) continue;

                return candidates
                    .OrderByDescending(path => SharedSuffixLength(
                        authoredDirectory,
                        Normalize(Path.GetDirectoryName(Path.GetRelativePath(_root, path)) ?? string.Empty)))
                    .ThenBy(path => path.Length)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .First();
            }

            return null;
        }

        private static string Normalize(string path)
        {
            string value = path.Replace('\\', '/').TrimStart('/');
            if (value.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) value = value[5..];
            if (value.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) value = value[7..];
            return value;
        }

        private static int SharedSuffixLength(string left, string right)
        {
            string[] leftParts = left.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string[] rightParts = right.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int score = 0;
            while (score < leftParts.Length && score < rightParts.Length &&
                   string.Equals(leftParts[^(1 + score)], rightParts[^(1 + score)], StringComparison.OrdinalIgnoreCase))
            {
                score++;
            }
            return score;
        }
    }
}
