using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetsManager.Services.Hashes
{
    /// <summary>
    /// Restores Riot casing for names built from lowercase game paths. FNV-1a hashes are
    /// case-insensitive, so casing only affects how the resolved name reads in the catalog.
    /// </summary>
    internal sealed class BinPathCasing
    {
        private readonly Dictionary<string, string> _segments;
        // Casing of numbered segment stems (Skin, Tier, Map...), for numbers the catalog has not seen yet.
        private readonly Dictionary<string, string> _numberedStems;

        private BinPathCasing(Dictionary<string, string> segments, Dictionary<string, string> numberedStems)
        {
            _segments = segments;
            _numberedStems = numberedStems;
        }

        internal static BinPathCasing Empty { get; } = new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        internal static BinPathCasing FromKnownNames(IEnumerable<string> names)
        {
            var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var stemCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
                foreach (string segment in name.Split('/'))
                {
                    if (segment.Length == 0 || !segment.Any(char.IsUpper)) continue;
                    Count(counts, segment);
                    int digits = NumberedStemLength(segment);
                    if (digits > 0) Count(stemCounts, segment[..digits]);
                }
            return new BinPathCasing(MostFrequent(counts), MostFrequent(stemCounts));

            static void Count(Dictionary<string, Dictionary<string, int>> table, string spelling)
            {
                if (!table.TryGetValue(spelling, out var spellings))
                    table[spelling] = spellings = new Dictionary<string, int>(StringComparer.Ordinal);
                spellings[spelling] = spellings.GetValueOrDefault(spelling) + 1;
            }

            static Dictionary<string, string> MostFrequent(Dictionary<string, Dictionary<string, int>> table)
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, spellings) in table)
                    result[key] = spellings.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
                return result;
            }
        }

        // Length of the alphabetic stem of "Skin305"-like segments, or 0.
        private static int NumberedStemLength(string segment)
        {
            int end = segment.Length;
            while (end > 0 && char.IsAsciiDigit(segment[end - 1])) end--;
            return end > 0 && end < segment.Length && segment.Take(end).All(char.IsLetter) ? end : 0;
        }

        /// <summary>
        /// Recases each all-lowercase segment from the catalog, or from a string of the same
        /// object that spells it (e.g. championSkinName for a skin folder).
        /// </summary>
        internal string Recase(string path, IReadOnlyList<string> hints = null)
        {
            string[] parts = path.Split('/');
            for (int index = 0; index < parts.Length; index++)
            {
                string segment = parts[index];
                if (segment.Length == 0 || segment.Any(char.IsUpper)) continue;
                if (_segments.TryGetValue(segment, out string spelling))
                {
                    parts[index] = spelling;
                    continue;
                }
                int stem = NumberedStemLength(segment);
                if (stem > 0 && _numberedStems.TryGetValue(segment[..stem], out string stemSpelling))
                {
                    parts[index] = stemSpelling + segment[stem..];
                    continue;
                }
                if (hints == null) continue;
                // A later segment of the same path often spells an earlier folder (Jade_PoppyQ under jade_poppy).
                foreach (string hint in hints.Concat(parts.Where(part => part.Any(char.IsUpper))))
                {
                    int at = hint.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) continue;
                    string spelled = hint.Substring(at, segment.Length);
                    if (!spelled.Any(char.IsUpper)) continue;
                    parts[index] = spelled;
                    break;
                }
            }
            return string.Join('/', parts);
        }
    }
}
