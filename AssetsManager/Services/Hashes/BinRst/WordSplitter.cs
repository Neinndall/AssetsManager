using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AssetsManager.Services.Hashes
{
    /// <summary>
    /// Splits an identifier into capitalized words. Alternatives are tried
    /// left to right and the acronym branch wins:
    ///   1. A capital run closes the word if no lowercase follows (so the last
    ///      capital belongs to the next word: AIGenericCommon -> AI Generic
    ///      Common); a digit tail and, only after digits, lowercase/digits are
    ///      annexed (IX3dShadingModel -> IX3d Shading Model).
    ///   2. A capital plus lowercase/digit tail (Detection2, Vector3).
    ///   3. A lowercase run (catches camelCase: abilityHaste).
    ///   4. A bare digit run.
    /// Separators do not match, which is what splits (Obj_InfoPoint).
    /// Hungarian prefixes (whole-lowercase first word of length &lt;= prefixMax)
    /// are dropped: mCoefficient -> Coefficient, id -> nothing.
    /// </summary>
    internal static class WordSplitter
    {
        private static readonly Regex WordRegex = new(
            @"[A-Z]+(?![a-z])(?:[0-9]+[a-z0-9]*)?|[A-Z][a-z0-9]*|[a-z][a-z0-9]*|[0-9]+",
            RegexOptions.Compiled);

        private static readonly Regex AcronymRunRegex = new(@"[A-Z]{2,}", RegexOptions.Compiled);
        private static readonly Regex PascalRegex = new(@"^[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled);
        private static readonly Regex NotationRegex = new(@"^[a-z][A-Z][A-Za-z0-9]*$", RegexOptions.Compiled);

        // Acronyms that attestation, not convention, put in the tables.
        private static readonly HashSet<string> AcronymExempt = new(StringComparer.Ordinal)
        {
            "MapSSAO", "MapSSAORenderer", "MapSSAOSettings", "GroupID", "ParamsCC"
        };

        private static readonly Dictionary<string, string> BrandAcronyms = new(StringComparer.Ordinal)
        {
            ["LoL"] = "Lol"
        };

        /// <summary>Splits a name into capitalized words.</summary>
        internal static IEnumerable<string> Split(string name, int prefixMax = 2)
        {
            if (string.IsNullOrEmpty(name)) yield break;
            var words = new List<string>();
            foreach (Match match in WordRegex.Matches(name))
            {
                string word = match.Value;
                if (word.Length == 0) continue;
                bool isAllLower = IsAllLower(word);
                if (words.Count == 0 && isAllLower && word.Length <= prefixMax) continue;
                if (isAllLower) word = char.ToUpperInvariant(word[0]) + word[1..];
                words.Add(word);
            }
            foreach (string word in words) yield return word;
        }

        internal static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length == 0 || name.Length > 128) return false;
            if (AcronymExempt.Contains(name)) return true;
            if (BrandAcronyms.TryGetValue(name, out string brand)) return IsValidName(brand);
            if (!PascalRegex.IsMatch(name) && !NotationRegex.IsMatch(name)) return false;

            // "No word is capitalized past its first letter": a leftover capital
            // run is an acronym and makes the name invalid (UI -> Ui, TFT -> Tft).
            // An initial interface I does not count (IX3dShadingModel -> X3d).
            string body = name[0] == 'I' && name.Length > 1 && char.IsUpper(name[1])
                ? name[1..]
                : name;
            int runIndex = body.IndexOfAnyCapRun();
            return runIndex < 0;
        }

        /// <summary>Case-only repair of acronym spellings (idempotent; the FNV
        /// hash does not move). UIElement -> UiElement.</summary>
        internal static string FoldAcronyms(string name)
        {
            if (string.IsNullOrEmpty(name) || AcronymExempt.Contains(name)) return name;
            if (BrandAcronyms.TryGetValue(name, out string brand)) return brand;
            if (PascalRegex.IsMatch(name) || NotationRegex.IsMatch(name))
            {
                string body = name[0] == 'I' && name.Length > 1 && char.IsUpper(name[1]) ? name[1..] : name;
                if (body.IndexOfAnyCapRun() < 0) return name;
            }
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsUpper(chars[i])) continue;
                int runStart = i;
                while (i + 1 < chars.Length && char.IsUpper(chars[i + 1])) i++;
                if (i - runStart < 1) continue;
                bool followedByLower = i + 1 < chars.Length && char.IsLower(chars[i + 1]);
                int runEnd = followedByLower ? i : i + 1;
                if (runEnd - runStart <= 1) continue;
                // Keep the first capital (word start), fold the rest.
                for (int k = runStart + 1; k < runEnd && k < chars.Length; k++)
                    chars[k] = char.ToLowerInvariant(chars[k]);
                i = runEnd - 1;
            }
            return new string(chars);
        }

        private static bool IsAllLower(string word)
        {
            foreach (char c in word)
                if (!(char.IsLower(c) || char.IsDigit(c))) return false;
            return true;
        }
    }

    internal static class WordSplitterExtensions
    {
        internal static int IndexOfAnyCapRun(this string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsUpper(value[i])) continue;
                int j = i;
                while (j + 1 < value.Length && char.IsUpper(value[j + 1])) j++;
                if (j - i >= 1) return i;
                i = j;
            }
            return -1;
        }
    }
}
