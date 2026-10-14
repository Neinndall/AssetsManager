using System;
using System.Collections.Generic;
using System.Threading;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes
{
    internal static class GamePathCandidateSource
    {
        // Skins enumerate skin00..skin49 and sets set01..set29; cap generated variants.
        private const int VariantBudget = 5_000_000;

        internal static void Discover(
            IEnumerable<string> gameHashLines,
            InternalHashEvidenceMatcher matcher,
            string source,
            CancellationToken cancellationToken = default)
        {
            // A GAME/WAD path is not evidence that the same string is a BIN
            // entry or a BIN hash.  The two catalogs use different hashing
            // domains, and a 32-bit FNV hit against an internal unknown can be
            // a coincidental or merely unrelated value from a BIN.  Only the
            // RST domain is safe here because its keys are themselves derived
            // from arbitrary shipped paths.
            if (matcher.GetRemainingCount(InternalHashKind.RstXxh3) == 0 &&
                matcher.GetRemainingCount(InternalHashKind.RstXxh64) == 0) return;
            int generatedVariants = 0;
            var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in gameHashLines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                int separator = line.IndexOf(' ');
                string path = separator >= 0 ? line[(separator + 1)..] : line;
                path = InternalHashEvidenceMatcher.NormalizeCandidate(path);
                if (path.Length < 3 || !path.Contains('/')) continue;

                Consider(path, includeTruncatedRst: true);

                int extensionIndex = path.LastIndexOf('.');
                if (extensionIndex > path.LastIndexOf('/') + 1)
                {
                    Consider(path[..extensionIndex], includeTruncatedRst: true);
                    if (generatedVariants < VariantBudget)
                        ConsiderVariants(path[..extensionIndex]);
                }
                if (generatedVariants < VariantBudget)
                    ConsiderVariants(path);

                // Parent directories are real shipped paths too.
                int dirEnd = path.Length;
                while ((dirEnd = path.LastIndexOf('/', dirEnd - 1)) > 0)
                    Consider(path[..dirEnd], includeTruncatedRst: true);
            }

            void Consider(string value, bool includeTruncatedRst)
            {
                // Known paths are real string-table strings; resolve RST targets too.
                // Synthetic variants keep only full 64-bit checks (a truncated
                // prefix match cannot prove a derived string).
                value = InternalHashEvidenceMatcher.NormalizeCandidate(value);
                if (!seenCandidates.Add(value)) return;
                matcher.Check(
                    value,
                    InternalHashGuessStrategy.GamePath,
                    source,
                    includeTruncatedRst: includeTruncatedRst);
            }

            // Sibling skin/set enumeration is retained for RST keys: the generated
            // strings are still checked against their actual XXH targets below. It
            // never promotes the generated path into a BIN catalog.
            void ConsiderVariants(string value)
            {
                foreach ((string marker, int min, int max) in new[] { ("skins/skin", 0, 49), ("sets/set", 1, 29) })
                {
                    int markerIndex = value.IndexOf(marker, StringComparison.Ordinal);
                    if (markerIndex < 0) continue;
                    int digitsStart = markerIndex + marker.Length;
                    int digitsEnd = digitsStart;
                    while (digitsEnd < value.Length && char.IsDigit(value[digitsEnd])) digitsEnd++;
                    if (digitsEnd == digitsStart) continue;
                    string segment = $"{marker[(marker.LastIndexOf('/') + 1)..]}{value[digitsStart..digitsEnd]}";
                    for (int number = min; number <= max; number++)
                    {
                        generatedVariants++;
                        if (generatedVariants > VariantBudget) return;
                        string replacement = $"{segment[..^value[digitsStart..digitsEnd].Length]}{number:D2}";
                        Consider(value.Replace(segment, replacement), includeTruncatedRst: false);
                    }
                }
            }
        }
    }
}
