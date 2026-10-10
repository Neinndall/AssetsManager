using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers.Game;

internal sealed partial class GameHashGuesser
{
    internal long GuessBinReferencedAnimations(HashGuessEngine engine, string rootDirectory,
        CancellationToken cancellationToken, long candidateBudget = long.MaxValue, Action<long> progress = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || candidateBudget <= 0 || engine.RemainingUnknownCount == 0) return 0;
        var containers = FindPendingAnimationContainers(rootDirectory, engine, cancellationToken);
        return SubstituteReferencedAnimationSuffixes(engine, containers, cancellationToken, candidateBudget, progress);
    }

    private HashSet<(string Character, string Container)> FindPendingAnimationContainers(
        string rootDirectory, HashGuessEngine engine, CancellationToken cancellationToken)
    {
        var containers = new HashSet<(string, string)>();
        var visited = new HashSet<(ulong Hash, ulong Checksum)>();
        var known = HashFile.Load();
        byte[] animationField = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(animationField, Fnv1a.HashLower("mAnimationFilePath"));
        foreach (string wadPath in FindWads(rootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var wad = new WadFile(wadPath);
                if (!wad.Chunks.Keys.Any(engine.UnknownHashes.Contains)) continue;
                foreach (var (hash, chunk) in wad.Chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (chunk.Compression == WadChunkCompression.Satellite || !known.TryGetValue(hash, out string path) ||
                        !path.StartsWith("data/characters/", StringComparison.OrdinalIgnoreCase) ||
                        !path.Contains("/animations/", StringComparison.OrdinalIgnoreCase) ||
                        !path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || visited.Contains((hash, chunk.Checksum))) continue;
                    string[] parts = path.ToLowerInvariant().Split('/');
                    if (parts.Length != 5 || parts[3] != "animations") continue;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        visited.Add((hash, chunk.Checksum));
                        // Reject irrelevant BINs cheaply; a parsed property still verifies every accepted link.
                        var remaining = data.Span;
                        bool pending = false;
                        while (remaining.Length >= 13)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int offset = remaining.IndexOf(animationField);
                            if (offset < 0 || remaining.Length - offset < 13) break;
                            if (engine.UnknownHashes.Contains(BinaryPrimitives.ReadUInt64LittleEndian(remaining.Slice(offset + 5, 8))))
                            {
                                pending = true;
                                break;
                            }
                            remaining = remaining[(offset + 1)..];
                        }
                        if (!pending) continue;
                        var bytes = data.DangerousGetArray();
                        using var stream = new MemoryStream(bytes.Array, bytes.Offset, bytes.Count, false);
                        var tree = new BinTree(stream);
                        if (EnumerateAnimationFileLinks(tree).Any(link => engine.UnknownHashes.Contains(link.PathHash)))
                            containers.Add((parts[2], parts[4][..^4]));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logService?.LogDebug($"GAME animation context skipped '{path}': {exception.Message}");
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME animation context skipped '{wadPath}': {exception.Message}");
            }
        }
        return containers;
    }

    internal long SubstituteReferencedAnimationSuffixes(HashGuessEngine engine,
        IReadOnlySet<(string Character, string Container)> containers, CancellationToken cancellationToken,
        long candidateBudget = long.MaxValue, Action<long> progress = null)
    {
        if (containers.Count == 0 || candidateBudget <= 0 || engine.RemainingUnknownCount == 0) return 0;
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in KnownPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
            string normalized = path.ToLowerInvariant();
            int slash = normalized.LastIndexOf('/');
            if (slash < 0) continue;
            string[] words = normalized[(slash + 1)..^4].Split('_');
            for (int count = 1; count < Math.Min(5, words.Length); count++)
                suffixes.Add(string.Join('_', words[^count..]) + ".anm");
            string[] folders = normalized[..slash].Split('/');
            if (folders.Length < 5 || folders[1] != "characters") continue;
            bool active = containers.Any(container => container.Character == folders[2] &&
                (folders.Skip(3).Contains(container.Container) ||
                 container.Container == "base" && folders.Skip(3).Contains("skin0")));
            if (!active) continue;
            for (int count = 1; count < Math.Min(4, words.Length); count++)
                prefixes.Add(normalized[..(slash + 1)] + string.Join('_', words[..count]) + "_");
        }
        string[] orderedPrefixes = prefixes.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        long checkedCandidates = 0;
        foreach (string suffix in suffixes.OrderBy(value => value, StringComparer.Ordinal))
        foreach (string prefix in orderedPrefixes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checkedCandidates >= candidateBudget || engine.RemainingUnknownCount == 0)
            {
                progress?.Invoke(checkedCandidates);
                return checkedCandidates;
            }
            engine.CheckPrefixSuffix(prefix, suffix, HashGuessStrategy.WordlistVariant,
                "GAME Custom: BIN-referenced animation suffixes");
            if ((++checkedCandidates & 0x3fff) == 0) progress?.Invoke(checkedCandidates);
        }
        long suffixCheckedCandidates = checkedCandidates;
        checkedCandidates += SubstituteReferencedAnimationNames(engine, containers, cancellationToken,
            candidateBudget - checkedCandidates, count => progress?.Invoke(suffixCheckedCandidates + count));
        progress?.Invoke(checkedCandidates);
        return checkedCandidates;
    }

    internal long SubstituteReferencedAnimationNames(HashGuessEngine engine,
        IReadOnlySet<(string Character, string Container)> containers, CancellationToken cancellationToken,
        long candidateBudget = long.MaxValue, Action<long> progress = null)
    {
        if (containers.Count == 0 || candidateBudget <= 0 || engine.RemainingUnknownCount == 0) return 0;
        var orderedContainers = containers.OrderBy(value => value.Character, StringComparer.Ordinal)
            .ThenBy(value => value.Container, StringComparer.Ordinal).ToArray();
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (character, container) in orderedContainers)
        {
            string[] names = ContainerNames(container);
            foreach (string name in names)
            foreach (string folder in new[] { "skins", "themes" })
            foreach (string stem in names.SelectMany(value => new[] { value + "_", character + "_" + value + "_" })
                         .Prepend(character + "_").Prepend(string.Empty))
                prefixes.Add($"assets/characters/{character}/{folder}/{name}/animations/{stem}");
        }
        long checkedCandidates = 0;
        var actions = GetGlobalAnimationActions(cancellationToken);
        foreach (string prefix in prefixes.OrderBy(value => value, StringComparer.Ordinal))
        foreach (string action in actions)
            if (!Check(prefix, action, ".anm")) return Finish();

        var characters = containers.Select(value => value.Character).ToHashSet(StringComparer.Ordinal);
        var pathsByCharacter = KnownPaths.Where(path => path.StartsWith("assets/characters/", StringComparison.Ordinal)
                && path.EndsWith(".anm", StringComparison.Ordinal) && characters.Contains(path.Split('/')[2]))
            .GroupBy(path => path.Split('/')[2], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        string[] numbers = Enumerable.Range(0, 33).SelectMany(number => new[]
            { number.ToString(CultureInfo.InvariantCulture), number.ToString("D2", CultureInfo.InvariantCulture) })
            .Distinct(StringComparer.Ordinal).SelectMany(value => new[] { value, "_" + value }).ToArray();
        var skinPattern = new Regex(@"skin\d+", RegexOptions.CultureInvariant);
        var numberPattern = new Regex(@"\d+", RegexOptions.CultureInvariant);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        // The second round can combine two counters after the first round resolves a new seed.
        for (int pass = 0; pass < 2; pass++)
        foreach (var (character, container) in orderedContainers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var discovered = engine.Matches.Values.Where(match => match.Path.StartsWith($"assets/characters/{character}/", StringComparison.Ordinal)
                && match.Path.EndsWith(".anm", StringComparison.Ordinal)).Select(match => match.Path).ToArray();
            var seeds = pathsByCharacter.GetValueOrDefault(character, Array.Empty<string>()).Concat(discovered).Distinct(StringComparer.Ordinal);
            foreach (string seed in seeds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] parts = seed.Split('/');
                if (parts.Length < 6 || parts[3] is not ("skins" or "themes")) continue;
                foreach (string folderContainer in ContainerNames(container))
                foreach (string filenameContainer in ContainerNames(container))
                {
                    string relative = skinPattern.Replace(string.Join('/', parts.Skip(5)), filenameContainer);
                    string path = $"assets/characters/{character}/{parts[3]}/{folderContainer}/{relative}";
                    if (!visited.Add(path)) continue;
                    int start = path.LastIndexOf('/') + 1, end = path.Length - 4;
                    string stem = path[start..end];
                    foreach (Match digit in numberPattern.Matches(stem))
                    {
                        if (digit.Index >= 4 && stem.AsSpan(digit.Index - 4, 4).SequenceEqual("skin")) continue;
                        foreach (string number in numbers)
                            if (!Check(path.AsSpan(0, start + digit.Index), number, path.AsSpan(start + digit.Index + digit.Length))) return Finish();
                    }
                    foreach (string number in numbers)
                        if (!Check(path.AsSpan(0, end), number, ".anm")) return Finish();
                }
            }
        }
        var localSeeds = pathsByCharacter.Values.SelectMany(paths => paths)
            .Concat(engine.Matches.Values.Where(match => match.Path.EndsWith(".anm", StringComparison.Ordinal) &&
                match.Path.StartsWith("assets/characters/", StringComparison.Ordinal) && characters.Contains(match.Path.Split('/')[2]))
                .Select(match => match.Path)).ToArray();
        long numericCheckedCandidates = checkedCandidates;
        checkedCandidates += SubstituteLocalNameWords(engine, localSeeds, cancellationToken, candidateBudget - checkedCandidates,
            count => progress?.Invoke(numericCheckedCandidates + count));
        return Finish();

        bool Check(ReadOnlySpan<char> prefix, string middle, ReadOnlySpan<char> suffix)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (engine.RemainingUnknownCount == 0 || checkedCandidates >= candidateBudget) return false;
            engine.CheckNormalizedParts(prefix, middle, suffix, HashGuessStrategy.WordlistVariant,
                "GAME Custom: BIN-referenced animation names");
            if ((++checkedCandidates & 0x3fff) == 0) progress?.Invoke(checkedCandidates);
            return true;
        }
        long Finish() { progress?.Invoke(checkedCandidates); return checkedCandidates; }
        static string[] ContainerNames(string container) => container.StartsWith("skin", StringComparison.Ordinal) &&
            int.TryParse(container.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? new[] { container, "skin" + number.ToString("D2", CultureInfo.InvariantCulture) }.Distinct(StringComparer.Ordinal).ToArray()
                : new[] { container };
    }
}
