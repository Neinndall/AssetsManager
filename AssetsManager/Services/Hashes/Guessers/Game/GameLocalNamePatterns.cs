using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Services.Hashes.Guessers.Game;

internal sealed partial class GameHashGuesser
{
    internal long GuessLocalNamePatterns(HashGuessEngine engine, string rootDirectory, bool animations,
        CancellationToken cancellationToken, long candidateBudget, Action<long> progress)
    {
        var known = HashFile.Load();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string wadPath in FindWads(rootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var wad = new WadFile(wadPath);
                if (!wad.Chunks.Keys.Any(engine.UnknownHashes.Contains)) continue;
                foreach (ulong hash in wad.Chunks.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    known.TryGetValue(hash, out string path);
                    if (path == null && engine.Matches.TryGetValue(hash, out var match)) path = match.Path;
                    if (path != null && path.Contains('/') &&
                        (animations ? path.EndsWith(".anm", StringComparison.Ordinal) :
                            path.EndsWith(".tex", StringComparison.Ordinal) || path.EndsWith(".dds", StringComparison.Ordinal)))
                        paths.Add(path);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME local naming context skipped '{wadPath}': {exception.Message}");
            }
        }
        return SubstituteLocalNameWords(engine, paths, cancellationToken, candidateBudget, progress);
    }

    internal static long SubstituteLocalNameWords(HashGuessEngine engine, IEnumerable<string> paths,
        CancellationToken cancellationToken, long candidateBudget = long.MaxValue, Action<long> progress = null)
    {
        if (engine.RemainingUnknownCount == 0 || candidateBudget <= 0) return 0;
        long checkedCandidates = 0;
        string[] variantLetters = Enumerable.Range('a', 26).Select(value => ((char)value).ToString()).ToArray();
        foreach (var family in paths.Distinct(StringComparer.Ordinal)
                     .GroupBy(path => path[..path.LastIndexOf('/')], StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A small vocabulary learned in this directory avoids unrelated global word combinations.
            string[] words = family.SelectMany(path => path[(path.LastIndexOf('/') + 1)..^4].Split('_'))
                .Where(word => word.Length is >= 2 and <= 32 && !word.Contains('.'))
                .GroupBy(word => word, StringComparer.Ordinal).OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal).Take(24).Select(group => group.Key).ToArray();
            string[] insertions = words.Select(word => word + "_").ToArray();
            string[] endings = words.Select(word => "_" + word).ToArray();
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            var suffixes = new Dictionary<string, int>(StringComparer.Ordinal);
            bool recombine = family.Key.StartsWith("assets/ux/", StringComparison.Ordinal) ||
                             family.Key.StartsWith("assets/maps/kitpieces/", StringComparison.Ordinal);
            foreach (string path in family.OrderBy(path => path, StringComparer.Ordinal))
            {
                int start = path.LastIndexOf('/') + 1, end = path.Length - 4;
                string stem = path[start..end];
                if (stem.Contains('.')) continue;
                string[] tokens = stem.Split('_');
                for (int i = 0; i + 1 < tokens.Length; i++)
                {
                    (tokens[i], tokens[i + 1]) = (tokens[i + 1], tokens[i]);
                    if (!Check(path.AsSpan(0, start), string.Join('_', tokens), path.AsSpan(end))) return Finish();
                    (tokens[i], tokens[i + 1]) = (tokens[i + 1], tokens[i]);
                }
                for (int at = start; at < end;)
                {
                    foreach (string insertion in insertions)
                        if (!Check(path.AsSpan(0, at), insertion, path.AsSpan(at))) return Finish();
                    int separator = path.IndexOf('_', at);
                    int tokenEnd = separator < 0 ? end : separator;
                    // Rare compound suffixes may never reach the global vocabulary; reuse local evidence.
                    foreach (string replacement in words)
                    {
                        if (path.AsSpan(at, tokenEnd - at).Equals(replacement, StringComparison.Ordinal)) continue;
                        if (!Check(path.AsSpan(0, at), replacement, path.AsSpan(tokenEnd))) return Finish();
                    }
                    // Number-letter names and initial 'a' variants can have uncatalogued letter siblings.
                    if (tokenEnd - at >= 2 && char.IsAsciiLetterLower(path[tokenEnd - 1]) &&
                        (path[tokenEnd - 1] == 'a' || char.IsAsciiDigit(path[tokenEnd - 2])))
                        foreach (string letter in variantLetters)
                            if (letter[0] != path[tokenEnd - 1] &&
                                !Check(path.AsSpan(0, tokenEnd - 1), letter, path.AsSpan(tokenEnd))) return Finish();
                    if (separator < 0) break;
                    at = separator + 1;
                }
                foreach (string ending in endings)
                    if (!Check(path.AsSpan(0, end), ending, path.AsSpan(end))) return Finish();
                if (!recombine) continue;
                for (int count = 0, separator = end; count < 3; count++)
                {
                    separator = path.LastIndexOf('_', separator - 1);
                    if (separator < start) break;
                    prefixes.Add(path[..(separator + 1)]);
                    string suffix = path[(separator + 1)..];
                    suffixes[suffix] = suffixes.GetValueOrDefault(suffix) + 1;
                }
            }
            string[] orderedPrefixes = prefixes.OrderBy(prefix => prefix, StringComparer.Ordinal).ToArray();
            foreach (string suffix in suffixes.OrderByDescending(pair => pair.Value)
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key))
            foreach (string prefix in orderedPrefixes)
                if (!Check(prefix, suffix, ReadOnlySpan<char>.Empty)) return Finish();
        }
        return Finish();

        bool Check(ReadOnlySpan<char> prefix, string middle, ReadOnlySpan<char> suffix)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (engine.RemainingUnknownCount == 0 || checkedCandidates >= candidateBudget) return false;
            engine.CheckNormalizedParts(prefix, middle, suffix, HashGuessStrategy.WordlistVariant,
                "GAME Custom: local filename patterns");
            if ((++checkedCandidates & 0x3fff) == 0) progress?.Invoke(checkedCandidates);
            return true;
        }
        long Finish() { progress?.Invoke(checkedCandidates); return checkedCandidates; }
    }
}
