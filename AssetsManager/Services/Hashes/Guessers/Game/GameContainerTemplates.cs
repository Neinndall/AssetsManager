using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Game
{
    internal sealed partial class GameHashGuesser
    {
        internal IEnumerable<HashGuessCandidate> GenerateContainerTemplateCandidates(
            CancellationToken cancellationToken = default)
        {
            var directoryPattern = new Regex(
                @"^(?:assets|data)/characters/(?<character>[^/]+)/(?<kind>skins|themes)/(?<container>[^/]+)/",
                RegexOptions.CultureInvariant);
            var contexts = new Dictionary<(string Character, string Kind), ContainerTemplates>();
            foreach (string path in KnownPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Match directory = directoryPattern.Match(path);
                if (!directory.Success) continue;
                string kind = directory.Groups["kind"].Value;
                string container = directory.Groups["container"].Value;
                if (kind == "skins" && container != "base" &&
                    !Regex.IsMatch(container, @"^skin\d+$", RegexOptions.CultureInvariant)) continue;
                var key = (directory.Groups["character"].Value, kind);
                if (!contexts.TryGetValue(key, out ContainerTemplates context))
                    contexts[key] = context = new ContainerTemplates();
                context.Containers.Add(container);

                int start = directory.Groups["container"].Index;
                string prefix = path[..start];
                string tail = path[(start + container.Length)..];
                // Correlate only complete tokens; borrowed skins and patch decorations remain literal.
                string pattern = @"(?<![a-z0-9])" + Regex.Escape(container) + @"(?![a-z0-9])";
                int decoration = tail.IndexOf('.', tail.LastIndexOf('/') + 1);
                if (decoration < 0) decoration = tail.Length;
                string template = prefix + "{container}" + Regex.Replace(
                    tail[..decoration], pattern, "{container}", RegexOptions.CultureInvariant) + tail[decoration..];
                context.Formats.Add(template);
            }

            foreach (var pair in contexts.OrderBy(value => value.Key.Character, StringComparer.Ordinal)
                         .ThenBy(value => value.Key.Kind, StringComparer.Ordinal))
            foreach (string format in pair.Value.Formats.OrderBy(value => value, StringComparer.Ordinal))
            foreach (string container in pair.Value.Containers.OrderBy(value => value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new HashGuessCandidate(
                    format.Replace("{container}", container, StringComparison.Ordinal),
                    HashGuessStrategy.SkinNumberVariant);
            }
        }


        private sealed class ContainerTemplates
        {
            internal HashSet<string> Containers { get; } = new(StringComparer.Ordinal);
            internal HashSet<string> Formats { get; } = new(StringComparer.Ordinal);
        }
    }
}
