using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;

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
            HashSet<ulong> entryTargets = matcher.GetRemaining(InternalHashKind.BinEntries).ToHashSet();
            HashSet<ulong> hashTargets = matcher.GetRemaining(InternalHashKind.BinHashes).ToHashSet();
            if (entryTargets.Count == 0 && hashTargets.Count == 0 &&
                matcher.GetRemainingCount(InternalHashKind.RstXxh3) == 0 &&
                matcher.GetRemainingCount(InternalHashKind.RstXxh64) == 0) return;
            int generatedVariants = 0;

            foreach (string line in gameHashLines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                int separator = line.IndexOf(' ');
                string path = separator >= 0 ? line[(separator + 1)..] : line;
                path = InternalHashEvidenceMatcher.NormalizeCandidate(path);
                if (path.Length < 3 || !path.Contains('/')) continue;

                // Exact known path: entry name only (asset paths are never entry keys).
                Consider(path, checkHashes: false, includeTruncatedRst: true);

                int extensionIndex = path.LastIndexOf('.');
                int slashIndex = path.LastIndexOf('/');
                if (extensionIndex > slashIndex + 1)
                {
                    // Extension-stripped known path: entry name and file hash alike.
                    Consider(path[..extensionIndex], checkHashes: true, includeTruncatedRst: true);
                    if (generatedVariants < VariantBudget)
                        ConsiderVariants(path[..extensionIndex], checkHashes: true);
                }
                if (generatedVariants < VariantBudget)
                    ConsiderVariants(path, checkHashes: false);

                // Parent directories are real shipped paths too; check them both.
                int dirEnd = path.Length;
                while ((dirEnd = path.LastIndexOf('/', dirEnd - 1)) > 0)
                    Consider(path[..dirEnd], checkHashes: true, includeTruncatedRst: true);
            }

            void Consider(string value, bool checkHashes, bool includeTruncatedRst)
            {
                uint hash = Fnv1a.HashLower(value);
                if (entryTargets.Remove(hash))
                {
                    matcher.CheckResearchCandidate(
                        InternalHashKind.BinEntries,
                        value,
                        InternalHashGuessStrategy.GamePath,
                        source,
                        InternalHashEvidence.GamePathExactMatch,
                        verified: true);
                }
                if (checkHashes && hashTargets.Count > 0 && hashTargets.Remove(hash))
                {
                    matcher.CheckResearchCandidate(
                        InternalHashKind.BinHashes,
                        value,
                        InternalHashGuessStrategy.GamePath,
                        source,
                        InternalHashEvidence.GamePathExactMatch,
                        verified: true);
                }
                // Known paths are real string-table strings; resolve RST targets too.
                // Synthetic variants keep only full 64-bit XXH64 checks (a 38-bit
                // prefix match cannot prove a derived string).
                matcher.Check(
                    value,
                    InternalHashGuessStrategy.GamePath,
                    source,
                    includeTruncatedRst: includeTruncatedRst);
            }

            // Sibling skin/set enumeration: every variant is an exact FNV1a hit on a
            // real catalog template, so any match is proof of the entry name.
            // The skin/set digits renumber everywhere in the path, so the directory
            // and the basename (aatrox_skin03.skn -> aatrox_skin17.skn) both follow.
            void ConsiderVariants(string value, bool checkHashes)
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
                        Consider(value.Replace(segment, replacement), checkHashes, includeTruncatedRst: false);
                    }
                }
            }
        }
    }
}
