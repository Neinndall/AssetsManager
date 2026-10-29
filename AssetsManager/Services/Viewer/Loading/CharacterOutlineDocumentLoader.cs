using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Loading
{
    /// <summary>Resolves inking objects outside the skin's links, keeping project BINs before installed map BINs.</summary>
    internal static class CharacterOutlineDocumentLoader
    {
        internal static async Task ExpandAsync(List<BinTree> documents, MapAssetResolver resolver,
            string projectRoot, Func<ulong, string> paths, CancellationToken cancellationToken)
        {
            if (resolver == null) return;
            var meshes = documents.FirstOrDefault()?.Objects.Values
                .Where(obj => obj.ClassHash == Fnv1a.HashLower("SkinCharacterDataProperties"))
                .Select(obj => obj.Properties.GetValueOrDefault(Fnv1a.HashLower("skinMeshProperties")) as BinTreeStruct)
                ?? Enumerable.Empty<BinTreeStruct>();
            meshes = meshes.Concat(documents.SelectMany(tree => tree.Objects.Values)
                .Select(obj => obj.Properties.GetValueOrDefault(0x639b0013u) as BinTreeStruct)
                .Where(gear => gear != null)
                .Select(gear => gear.Properties.GetValueOrDefault(Fnv1a.HashLower("skinMeshProperties")) as BinTreeStruct));
            var links = meshes.Where(mesh => mesh != null).SelectMany(mesh => CharacterOutlineReader.Links(new[] { mesh })).ToArray();
            if (links.Length == 0) return;
            var wanted = links.Select(link => link.Material == 0 ? link.Category : link.Material)
                .Where(hash => hash != 0).ToHashSet();
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<MapResolvedAsset> candidates = null;
            for (int pass = 0; pass < 2; pass++)
            {
                wanted.ExceptWith(documents.SelectMany(tree => tree.Objects.Keys));
                if (wanted.Count > 0)
                {
                    candidates ??= await Task.Run(() => resolver.OutlineCandidates(projectRoot, paths), cancellationToken);
                    for (int scope = 0; scope < 2 && wanted.Count > 0; scope++)
                    {
                        var scopedCandidates = scope == 0 ? candidates
                            : await Task.Run(() => resolver.OutlineCandidates(projectRoot, paths, true), cancellationToken);
                        foreach (var asset in scopedCandidates)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string identity = asset.PhysicalPath ?? $"{asset.WadPath}|{asset.WadPathHash}";
                            if (loaded.Contains(identity)) continue;
                            try
                            {
                                var info = new FileInfo(asset.PhysicalPath ?? asset.WadPath);
                                string key = $"{identity}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
                                if (resolver.ObjectHeaders.TryGetValue(key, out var indexed) && !indexed.Any(wanted.Contains)) continue;
                                await using var stream = await resolver.OpenReadAsync(asset, cancellationToken);
                                if (stream == null) continue;
                                if (indexed == null)
                                {
                                    indexed = await Task.Run(() => BinObjectHeaders.Read(stream), cancellationToken);
                                    resolver.ObjectHeaders[key] = indexed;
                                }
                                if (!indexed.Any(wanted.Contains)) continue;
                                stream.Position = 0;
                                var tree = await Task.Run(() => new BinTree(stream), cancellationToken);
                                documents.Add(tree);
                                loaded.Add(identity);
                                if (tree.Dependencies.Count > 0)
                                {
                                    var linked = await new BinDocumentClosureLoader(resolver).LoadAsync(asset, projectRoot,
                                        BinDocumentClosureLoader.MaximumLinkedBins, cancellationToken);
                                    documents.AddRange(linked.Skip(1));
                                }
                                wanted.ExceptWith(documents.SelectMany(document => document.Objects.Keys));
                                if (wanted.Count == 0) break;
                            }
                            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
                            {
                                // An unrelated unreadable BIN must not prevent the skin from opening.
                            }
                        }
                    }
                }
                wanted = links.Where(link => link.Material == 0).Select(link => link.Category)
                    .Select(hash => documents.Select(tree => tree.Objects.GetValueOrDefault(hash)).FirstOrDefault(obj => obj != null))
                    .Where(obj => obj != null).Select(obj => obj.Properties.GetValueOrDefault(Fnv1a.HashLower("Material")))
                    .OfType<BinTreeObjectLink>().Select(link => link.Value).ToHashSet();
            }
        }
    }
}
