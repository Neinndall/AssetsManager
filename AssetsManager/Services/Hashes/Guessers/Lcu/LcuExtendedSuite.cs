using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Lcu
{
    internal sealed partial class LcuHashGuesser
    {
        internal int GuessFromGameHashes(
            HashGuessEngine engine,
            HashGuesser gameGuesser,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(gameGuesser);
            if (gameGuesser.Domain != HashGuessDomain.Game)
                throw new ArgumentException("Cross-domain LCU guessing requires a GAME guesser.", nameof(gameGuesser));
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            const string basePath = "plugins/rcp-be-lol-game-data/global/default";
            const string source = "LCU from GAME hashes";
            int checkedCount = 0;

            bool CheckGamePath(string path)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) return false;
                Check(engine, path, HashGuessStrategy.CrossDomainAsset, source);
                checkedCount++;
                if ((checkedCount & 0x1fff) == 0)
                {
                    progress?.Invoke(checkedCount);
                }
                return true;
            }

            foreach (string path in gameGuesser.KnownPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;

                if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = path[..^4];
                    if (!CheckGamePath($"{basePath}/{prefix}.png")) break;
                    if (!CheckGamePath($"{basePath}/{prefix}.jpg")) break;
                }
                else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    if (!CheckGamePath($"{basePath}/{path}")) break;
                }
            }

            progress?.Invoke(checkedCount);
            return checkedCount;
        }

        // Dedicated, opt-in coverage for the v1 check_iter patterns used by CDTB tooling.
        // It deliberately stays out of Basic and Extended because its wordlist cross-product is expensive.
        internal int RunV1PathPatterns(
            HashGuessEngine engine,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IEnumerable<string> words = null,
            IEnumerable<string> locales = null)
        {
            ArgumentNullException.ThrowIfNull(engine);

            string[] wordList = (words ?? BuildWordlist())
                .Where(word => !string.IsNullOrWhiteSpace(word))
                .Select(PathUtils.NormalizePath)
                .Where(word => word.Length > 0)
                .Distinct(StringComparer.Ordinal)
                // Short, composable terms produce useful v1 names early (for example augment + list),
                // while retaining the complete CDTB word corpus for exhaustive coverage.
                .OrderBy(word => word.Length)
                .ThenBy(word => word, StringComparer.Ordinal)
                .ToArray();
            // GREP already derives installed localized paths from their WAD contents.
            // V1 guessing therefore stays on the default path unless a caller explicitly opts into locales.
            string[] localeList = (locales ?? Array.Empty<string>())
                .Where(locale => !string.IsNullOrWhiteSpace(locale))
                .Where(locale => !locale.Equals("default", StringComparison.OrdinalIgnoreCase))
                .Select(locale => locale.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyDictionary<ulong, string> knownHashes = HashFile.Load();

            const string v1Prefix = "plugins/rcp-be-lol-game-data/global/";
            const string source = "LCU v1 path patterns";
            int checkedCandidates = 0;
            int lastReported = -1;
            var progressClock = Stopwatch.StartNew();

            void Report(string phase, bool force = false)
            {
                if (!force && ((checkedCandidates & 0x3fff) != 0 || progressClock.ElapsedMilliseconds < 100)) return;
                if (lastReported == checkedCandidates) return;
                progress?.Report(engine.CreateProgress($"LCU V1 Paths: {phase}", checkedCandidates));
                lastReported = checkedCandidates;
                progressClock.Restart();
            }

            bool CheckDefaultThenLocales(string fileName, string phase)
            {
                string defaultPath = $"{v1Prefix}default/v1/{fileName}";
                checkedCandidates += CheckIter(
                    engine,
                    new[] { new HashGuessCandidate(defaultPath, HashGuessStrategy.LcuPattern) },
                    source,
                    cancellationToken);
                Report(phase);
                if (engine.RemainingUnknownCount == 0) return false;

                // A localized path is only attempted after its default counterpart is known or resolved.
                // This preserves the useful locale expansion without multiplying every word pair by all locales.
                ulong defaultHash = XxHash64Ext.Hash(PathUtils.NormalizePath(defaultPath));
                bool hasDefaultEvidence = engine.Matches.ContainsKey(defaultHash) || knownHashes.ContainsKey(defaultHash);
                if (!hasDefaultEvidence) return true;

                checkedCandidates += CheckIter(
                    engine,
                    localeList.Select(locale => new HashGuessCandidate(
                        $"{v1Prefix}{locale}/v1/{fileName}",
                        HashGuessStrategy.LcuPattern)),
                    source,
                    cancellationToken);
                Report(phase);
                return true;
            }

            Report("preparing", force: true);
            foreach (string a in wordList)
            {
                // The non-TFT paths are first because they are both broadly useful and cheap to resolve early.
                if (!CheckDefaultThenLocales($"{a}.json", "single names") ||
                    !CheckDefaultThenLocales($"tft{a}.json", "TFT single names") ||
                    !CheckDefaultThenLocales($"tft{a}s.json", "TFT plural names"))
                {
                    Report("completed", force: true);
                    return checkedCandidates;
                }

                foreach (string b in wordList)
                {
                    // These are the unique candidate sets from the 24 CDTB check_iter expressions.
                    // Because both words iterate over the full list, swapping a/b would only repeat work.
                    if (!CheckDefaultThenLocales($"{a}{b}s.json", "word pairs") ||
                        !CheckDefaultThenLocales($"{a}{b}n.json", "word pairs") ||
                        !CheckDefaultThenLocales($"{a}-{b}s.json", "word pairs") ||
                        !CheckDefaultThenLocales($"{a}-{b}.json", "word pairs") ||
                        !CheckDefaultThenLocales($"{a}{b}.json", "word pairs") ||
                        !CheckDefaultThenLocales($"{a}{b}", "word pairs") ||
                        !CheckDefaultThenLocales($"tft{a}{b}s.json", "TFT pairs") ||
                        !CheckDefaultThenLocales($"tft{a}-{b}s.json", "TFT pairs") ||
                        !CheckDefaultThenLocales($"tft{a}{b}.json", "TFT pairs") ||
                        !CheckDefaultThenLocales($"tft{a}-{b}.json", "TFT pairs") ||
                        !CheckDefaultThenLocales($"tft-{a}{b}.json", "TFT pairs"))
                    {
                        Report("completed", force: true);
                        return checkedCandidates;
                    }
                }
            }

            Report("completed", force: true);
            return checkedCandidates;
        }

    }
}
