using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
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
}
