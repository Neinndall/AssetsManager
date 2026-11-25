using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers.Game;

internal sealed partial class GameHashGuesser
{
    internal long GuessMaterialTextureRoles(HashGuessEngine engine, string rootDirectory, CancellationToken cancellationToken,
        long candidateBudget = long.MaxValue, Action<long> progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(rootDirectory) || candidateBudget <= 0 || engine.RemainingUnknownCount == 0) return 0;
        var known = HashFile.Load();
        var visited = new HashSet<(ulong, ulong)>();
        long checkedCandidates = 0;
        uint textureField = Fnv1a.HashLower("texturePath");
        uint textureNameField = Fnv1a.HashLower("TextureName");
        foreach (string wadPath in FindWads(rootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checkedCandidates >= candidateBudget || engine.RemainingUnknownCount == 0) break;
            try
            {
                using var wad = OpenWad(wadPath);
                if (!wad.Chunks.Keys.Any(engine.UnknownHashes.Contains)) continue;
                foreach (var chunk in wad.Chunks.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (checkedCandidates >= candidateBudget || engine.RemainingUnknownCount == 0) break;
                    if (chunk.Compression == WadChunkCompression.Satellite || !known.TryGetValue(chunk.PathHash, out string path) ||
                        !GameMaterialTextureIndex.IsSkinBin(path) || visited.Contains((chunk.PathHash, chunk.Checksum))) continue;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        if (!FileTypeDetector.IsPropertyBin(data.Span)) continue;
                        // Reject irrelevant BINs before constructing their full object graphs.
                        // Parsed properties still validate every accepted reference and sampler role.
                        if (!GameBinLinkProbe.ContainsPendingLink(data.Span, textureField, engine.UnknownHashes, cancellationToken) &&
                            !GameBinLinkProbe.ContainsPendingLink(data.Span, textureNameField, engine.UnknownHashes, cancellationToken)) continue;
                        var bytes = data.DangerousGetArray();
                        using var stream = new MemoryStream(bytes.Array, bytes.Offset, bytes.Count, false);
                        var tree = new BinTree(stream);
                        visited.Add((chunk.PathHash, chunk.Checksum));
                        if (!GameMaterialTextureIndex.HasUnresolvedSamplerContext(engine, tree, path, cancellationToken) &&
                            !GameMaterialTextureIndex.HasUnresolvedDynamicMaterialContext(engine, tree, path, cancellationToken)) continue;
                        var index = GetMaterialTextureIndex(cancellationToken);
                        checkedCandidates += index.Guess(engine, tree, path, wadPath,
                            chunk.PathHash, cancellationToken, candidateBudget - checkedCandidates);
                        checkedCandidates += index.GuessSharedParticles(engine, tree, path, wadPath, chunk.PathHash,
                            cancellationToken, candidateBudget - checkedCandidates);
                        checkedCandidates += index.GuessDynamicMaterialOptions(engine, tree, path, wadPath, chunk.PathHash,
                            cancellationToken, candidateBudget - checkedCandidates);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logService?.LogDebug($"GAME material texture context skipped '{path}': {exception.Message}");
                    }
                }
                progress?.Invoke(checkedCandidates);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME material texture context skipped '{wadPath}': {exception.Message}");
            }
        }
        progress?.Invoke(checkedCandidates);
        return checkedCandidates;
    }

    private GameMaterialTextureIndex GetMaterialTextureIndex(CancellationToken cancellationToken) =>
        Corpus.GetOrCreate("game-material-texture-roles", paths => new GameMaterialTextureIndex(paths, cancellationToken));

    private static readonly Regex BinPropertyWordRegex = new(
        @"[A-Z]+(?=[A-Z][a-z]|\b)|[A-Z]?[a-z]+|[0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Derives glued compound texture names (e.g. eyesgradient, headbase, visoralpha) using only words from the owning skin BIN's properties.
    private void GuessBinPropertyGluedTextures(
        HashGuessEngine engine,
        BinTree tree,
        string sourcePath,
        string sourceWadPath,
        ulong sourceChunkHash,
        CancellationToken cancellationToken)
    {
        if (tree == null || engine.RemainingUnknownCount == 0 || !GameMaterialTextureIndex.IsSkinBin(sourcePath)) return;

        // Pass 1: find pending links without allocating strings or running regexes on BINs that have no unresolved links.
        var pendingTargets = new HashSet<ulong>();
        void CollectPendingLinks(BinTreeProperty property)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (property)
            {
                case BinTreeWadChunkLink link:
                    if (engine.UnknownHashes.Contains(link.Value))
                        pendingTargets.Add(link.Value);
                    break;
                case BinTreeStruct structure:
                    foreach (var child in structure.Properties.Values) CollectPendingLinks(child);
                    break;
                case BinTreeContainer container:
                    foreach (var element in container.Elements) CollectPendingLinks(element);
                    break;
                case BinTreeOptional optional when optional.Value != null:
                    CollectPendingLinks(optional.Value);
                    break;
                case BinTreeMap map:
                    foreach (var pair in map)
                    {
                        CollectPendingLinks(pair.Key);
                        CollectPendingLinks(pair.Value);
                    }
                    break;
            }
        }

        foreach (var obj in tree.Objects.Values)
            foreach (var property in obj.Properties.Values)
                CollectPendingLinks(property);

        if (pendingTargets.Count == 0) return;

        // Pass 2: only for skin BINs with pending links, collect non-particle sibling textures and BIN property words.
        var known = HashFile.Load();
        var knownTextures = new HashSet<string>(StringComparer.Ordinal);
        var binWords = new HashSet<string>(StringComparer.Ordinal);

        void CollectContext(BinTreeProperty property)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (property)
            {
                case BinTreeWadChunkLink link:
                    if (!pendingTargets.Contains(link.Value))
                    {
                        if (!known.TryGetValue(link.Value, out string resolved) &&
                            engine.Matches.TryGetValue(link.Value, out var match))
                            resolved = match.Path;
                        if (resolved != null &&
                            (resolved.EndsWith(".tex", StringComparison.Ordinal) || resolved.EndsWith(".dds", StringComparison.Ordinal)) &&
                            !resolved.StartsWith("assets/shared/materials/", StringComparison.Ordinal) &&
                            !resolved.Contains("/particles/", StringComparison.Ordinal) &&
                            resolved.Contains('/'))
                        {
                            knownTextures.Add(resolved);
                        }
                    }
                    break;
                case BinTreeString text when !string.IsNullOrEmpty(text.Value) && text.Value.Length <= 80 && !text.Value.Contains('/'):
                    foreach (Match m in BinPropertyWordRegex.Matches(text.Value))
                    {
                        string word = m.Value.ToLowerInvariant();
                        if (word.Length is >= 3 and <= 16 && word.All(char.IsAsciiLetterLower))
                            binWords.Add(word);
                    }
                    break;
                case BinTreeStruct structure:
                    foreach (var child in structure.Properties.Values) CollectContext(child);
                    break;
                case BinTreeContainer container:
                    foreach (var element in container.Elements) CollectContext(element);
                    break;
                case BinTreeOptional optional when optional.Value != null:
                    CollectContext(optional.Value);
                    break;
                case BinTreeMap map:
                    foreach (var pair in map)
                    {
                        CollectContext(pair.Key);
                        CollectContext(pair.Value);
                    }
                    break;
            }
        }

        foreach (var obj in tree.Objects.Values)
            foreach (var property in obj.Properties.Values)
                CollectContext(property);

        if (knownTextures.Count == 0 || binWords.Count == 0) return;

        string[] words = binWords.OrderBy(word => word, StringComparer.Ordinal).Take(200).ToArray();
        foreach (string path in knownTextures.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (pendingTargets.Count == 0 || engine.RemainingUnknownCount == 0) return;
            int slash = path.LastIndexOf('/');
            string directory = path[..(slash + 1)];
            string extension = path[^4..];
            string stem = path[(slash + 1)..^4];
            if (stem.Contains('.')) continue;

            string[] tokens = stem.Split('_');
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token.Length < 3 || !token.All(char.IsAsciiLetterLower)) continue;
                string before = i == 0 ? directory : directory + string.Join('_', tokens[..i]) + "_";
                string after = i == tokens.Length - 1 ? extension : "_" + string.Join('_', tokens[(i + 1)..]) + extension;

                foreach (string word in words)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(token, word, StringComparison.Ordinal)) continue;
                    if (engine.CheckNormalizedParts(before, token + word, after, HashGuessStrategy.BinLinkSibling,
                        sourceWadPath, sourceChunkHash, expectedHashes: pendingTargets))
                    {
                        pendingTargets.RemoveWhere(hash => !engine.UnknownHashes.Contains(hash));
                        if (pendingTargets.Count == 0) return;
                    }
                }

                if (token.Length >= 7)
                {
                    int maxPrefix = Math.Min(8, token.Length - 3);
                    for (int prefixLen = 3; prefixLen <= maxPrefix; prefixLen++)
                    {
                        string head = token[..prefixLen];
                        string tail = token[prefixLen..];
                        foreach (string word in words)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (string.Equals(word, tail, StringComparison.Ordinal)) continue;
                            if (engine.CheckNormalizedParts(before, head + word, after, HashGuessStrategy.BinLinkSibling,
                                sourceWadPath, sourceChunkHash, expectedHashes: pendingTargets))
                            {
                                pendingTargets.RemoveWhere(hash => !engine.UnknownHashes.Contains(hash));
                                if (pendingTargets.Count == 0) return;
                            }
                        }
                    }
                }
            }
        }
    }
}
