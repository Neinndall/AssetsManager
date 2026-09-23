using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Services.Viewer.Loading
{
    /// <summary>
    /// Opens one BIN and the linked BIN documents reachable from it, preserving project files over
    /// installed WAD assets and bounding the linked walk like the reference viewport.
    /// </summary>
    internal sealed class BinDocumentClosureLoader
    {
        private readonly MapAssetResolver _assetResolver;

        internal BinDocumentClosureLoader(MapAssetResolver assetResolver)
        {
            _assetResolver = assetResolver ?? throw new ArgumentNullException(nameof(assetResolver));
        }

        internal async Task<List<BinTree>> LoadAsync(
            MapResolvedAsset primary,
            string projectRoot,
            int maximumLinkedBins,
            CancellationToken cancellationToken,
            Action<MapResolvedAsset, Exception> onReadFailure = null)
        {
            var result = new List<BinTree>();
            if (primary == null)
                return result;

            string root = ResolveProjectRoot(primary, projectRoot);
            string primaryIdentity = AssetIdentity(primary);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<MapResolvedAsset>();
            int openedLinkedBins = 0;
            int linkedLimit = Math.Max(0, maximumLinkedBins);
            pending.Enqueue(primary);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MapResolvedAsset asset = pending.Dequeue();
                string identity = AssetIdentity(asset);
                if (!seen.Add(identity))
                    continue;

                bool isPrimary = string.Equals(identity, primaryIdentity, StringComparison.OrdinalIgnoreCase);
                if (!isPrimary)
                {
                    if (openedLinkedBins >= linkedLimit)
                        break;
                    // The linked-open budget counts attempted documents, including unreadable ones.
                    openedLinkedBins++;
                }

                BinTree tree;
                try
                {
                    await using Stream stream = await _assetResolver.OpenReadAsync(asset, cancellationToken);
                    tree = stream == null ? null : new BinTree(stream);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    onReadFailure?.Invoke(asset, ex);
                    continue;
                }

                if (tree == null)
                    continue;
                result.Add(tree);

                string[] dependencies = tree.Dependencies
                    .Where(dependency => !string.IsNullOrWhiteSpace(dependency))
                    .ToArray();
                if (dependencies.Length == 0)
                    continue;

                var unresolved = new List<MapAssetReference>(dependencies.Length);
                foreach (string dependency in dependencies)
                {
                    IReadOnlyList<MapResolvedAsset> projectMatches =
                        _assetResolver.ResolveLinkedProjectBins(dependency, root);
                    if (projectMatches.Count > 0)
                    {
                        foreach (MapResolvedAsset resolved in projectMatches)
                        {
                            if (!seen.Contains(AssetIdentity(resolved)))
                                pending.Enqueue(resolved);
                        }
                        continue;
                    }

                    unresolved.Add(new MapAssetReference(dependency, 0));
                }

                if (unresolved.Count == 0)
                    continue;

                IReadOnlyDictionary<MapAssetReference, MapResolvedAsset> resolvedDependencies =
                    await _assetResolver.ResolveReferencesAsync(
                        unresolved,
                        root,
                        cancellationToken);
                foreach (MapAssetReference reference in unresolved)
                {
                    if (resolvedDependencies.TryGetValue(reference, out MapResolvedAsset resolved) &&
                        !seen.Contains(AssetIdentity(resolved)))
                    {
                        pending.Enqueue(resolved);
                    }
                }
            }

            return result;
        }

        internal static string ResolveProjectRoot(MapResolvedAsset primary, string projectRoot)
        {
            if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
                return Path.GetFullPath(projectRoot);
            if (primary?.IsPhysicalFile != true || string.IsNullOrWhiteSpace(primary.PhysicalPath))
                return projectRoot;

            string fullPath = Path.GetFullPath(primary.PhysicalPath)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string marker = $"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}";
            int dataIndex = fullPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (dataIndex >= 0)
                return fullPath[..dataIndex];
            return Path.GetDirectoryName(fullPath);
        }

        private static string AssetIdentity(MapResolvedAsset asset)
        {
            if (asset?.IsPhysicalFile == true)
                return $"file:{Path.GetFullPath(asset.PhysicalPath)}";
            return $"wad:{asset?.WadPath}|{asset?.WadPathHash:x16}";
        }
    }
}
