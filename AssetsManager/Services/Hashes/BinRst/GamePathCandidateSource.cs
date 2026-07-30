using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes
{
    internal static class GamePathCandidateSource
    {
        internal static void Discover(
            IEnumerable<string> gameHashLines,
            InternalHashEvidenceMatcher matcher,
            string source,
            CancellationToken cancellationToken = default)
        {
            HashSet<ulong> targets = matcher.GetRemaining(InternalHashKind.BinEntries).ToHashSet();
            if (targets.Count == 0) return;

            var formsByGroup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (string line in gameHashLines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                int separator = line.IndexOf(' ');
                string path = separator >= 0 ? line[(separator + 1)..] : line;
                path = PathUtils.NormalizePath(path.Trim());
                if (path.Length < 3 || !path.Contains('/')) continue;

                AddForm(path);
                int extensionIndex = path.LastIndexOf('.');
                int slashIndex = path.LastIndexOf('/');
                if (extensionIndex > slashIndex + 1)
                    AddForm(path[..extensionIndex]);
            }

            foreach (var pair in formsByGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matches = pair.Value
                    .Select(value => (Value: value, Hash: (ulong)Fnv1a.HashLower(value)))
                    .Where(candidate => targets.Contains(candidate.Hash))
                    .GroupBy(candidate => candidate.Hash)
                    .Select(group => group.First())
                    .ToArray();
                double expected = pair.Value.Count * (double)targets.Count / 4294967296d;

                // Statistical enrichment is useful discovery evidence, never proof.
                // Require several observations and a 10x signal over random collisions.
                if (matches.Length < 3 || expected > 1 || matches.Length < expected * 10) continue;
                foreach (var match in matches)
                {
                    matcher.CheckResearchCandidate(
                        InternalHashKind.BinEntries,
                        match.Value,
                        InternalHashGuessStrategy.GamePath,
                        $"{source} [{pair.Key}]",
                        InternalHashEvidence.GamePathStatisticalMatch,
                        matches.Length,
                        expected);
                }
            }

            void AddForm(string value)
            {
                int slash = value.IndexOf('/');
                if (slash <= 0) return;
                string group = value[..slash];
                if (!formsByGroup.TryGetValue(group, out HashSet<string> forms))
                {
                    forms = new HashSet<string>(StringComparer.Ordinal);
                    formsByGroup[group] = forms;
                }
                forms.Add(value);
            }
        }
    }
}
