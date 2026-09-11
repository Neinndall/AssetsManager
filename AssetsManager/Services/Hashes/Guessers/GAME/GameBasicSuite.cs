using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal sealed partial class GameHashGuesser
    {
        internal IReadOnlyList<string> GetCharacters() =>
            Corpus.GetOrCreate("characters", values => values
                .Select(path => Regex.Match(path, @"^(?:assets/|data/)?characters/([^/.]+)(?:/|$)", RegexOptions.IgnoreCase))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList());

        internal IEnumerable<HashGuessCandidate> SubstituteNumbers(int maximum = 100, int? digits = null, bool inferDigits = false) =>
            GenerateNumberCandidates(maximum, int.MaxValue, digits, inferDigits, includeCommonPadding: false);

        internal int SubstituteNumbers(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int maximum = 100,
            int? digits = null,
            Action<int> progress = null) =>
            base._SubstituteNumbers(
                engine,
                KnownPaths,
                maximum,
                digits,
                inferDigits: false,
                cancellationToken: cancellationToken,
                source: "Generated numeric variant",
                progress: progress);

        protected override bool AnchorNumberMatchesToFileName => true;

        internal IEnumerable<HashGuessCandidate> SubstituteBasicNumbers(int maximum = 100)
        {
            foreach (HashGuessCandidate candidate in SubstituteNumbers(maximum))
                yield return candidate;

            // Two-digit values above 9 are identical to their unpadded form.
            foreach (HashGuessCandidate candidate in SubstituteNumbers(Math.Min(maximum, 10), digits: 2))
                yield return candidate;
        }

        internal int CheckBasenamePrefixes(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            IEnumerable<string> prefixes = null,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            string[] values = (prefixes ?? new[] { "2x_", "2x_sd_", "4x_", "4x_sd_", "sd_", "tft_", "common_", "base_", "sru_", "icon_" })
                .Select(prefix => prefix ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            int checkedCount = 0;
            foreach (string path in KnownPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int separator = path.LastIndexOf('/');
                ReadOnlySpan<char> directory = separator >= 0 ? path.AsSpan(0, separator + 1) : ReadOnlySpan<char>.Empty;
                ReadOnlySpan<char> basename = separator >= 0 ? path.AsSpan(separator + 1) : path.AsSpan();

                foreach (string prefix in values)
                {
                    engine.CheckNormalizedParts(
                        directory,
                        prefix.AsSpan(),
                        basename,
                        HashGuessStrategy.PrefixVariant,
                        "GAME basename prefixes");
                    checkedCount++;
                    if ((checkedCount & 0x3FFF) == 0) progress?.Invoke(checkedCount);
                    if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) return checkedCount;
                }
            }

            return checkedCount;
        }

        internal int SubstituteLang(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            string source = "Generated locale variant",
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);

            string[] langs =
            {
                "ar_ae", "ar_eg", "cs_cz", "de_de", "el_gr", "en_au", "en_gb", "en_ph", "en_pl", "en_sg",
                "en_us", "es_ar", "es_es", "es_mx", "fr_fr", "hu_hu", "id_id", "it_it", "ja_jp", "ko_kr",
                "ms_my", "pl_pl", "pt_br", "ro_ro", "ru_ru", "th_th", "tr_tr", "vi_vn", "vn_vn", "zh_cn",
                "zh_my", "zh_tw"
            };
            var langsRegex = new Regex($"({string.Join("|", langs)})", RegexOptions.Compiled);

            IReadOnlyList<string> formats = KnownPaths
                .Where(path => langsRegex.IsMatch(path))
                .Select(path => langsRegex.Replace(path, "{}"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            int checkedCount = 0;
            foreach (string format in ProgressIterator(formats, value => value, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkedCount += CheckIter(
                    engine,
                    langs.Select(lang => new HashGuessCandidate(
                        format.Replace("{}", lang, StringComparison.Ordinal),
                        HashGuessStrategy.LanguageVariant)),
                    source,
                    cancellationToken);
                progress?.Invoke(checkedCount);
                if (engine.RemainingUnknownCount == 0) break;
            }

            return checkedCount;
        }

        internal int GuessFromLcuHashes(
            HashGuessEngine engine,
            HashGuesser lcuGuesser,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(lcuGuesser);
            if (lcuGuesser.Domain != HashGuessDomain.Lcu)
                throw new ArgumentException("GAME cross-domain guessing requires an LCU guesser.", nameof(lcuGuesser));
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            const string source = "GAME from LCU hashes";
            var regex = new Regex(
                @"^plugins/rcp-be-lol-game-data/global/default/((?:assets|data)/.*)\.(png|jpg|json)$",
                RegexOptions.Compiled);
            int checkedCount = 0;
            foreach (string lcuPath in lcuGuesser.KnownPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;

                Match match = regex.Match(lcuPath);
                if (!match.Success) continue;
                string path = match.Groups[1].Value;
                string extension = match.Groups[2].Value;
                string candidatePath = extension is "png" or "jpg"
                    ? $"{path}.dds"
                    : $"{path}.{extension}";

                Check(engine, candidatePath, HashGuessStrategy.CrossDomainGame, source);
                checkedCount++;
                if ((checkedCount & 0x1fff) == 0)
                {
                    progress?.Invoke(checkedCount);
                }
            }

            progress?.Invoke(checkedCount);
            return checkedCount;
        }
    }
}
