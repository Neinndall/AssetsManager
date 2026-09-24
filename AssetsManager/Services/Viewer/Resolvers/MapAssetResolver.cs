using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Resolvers
{
    /// <summary>
    /// Resolves one map asset at a time, keeping project overrides authoritative over game files.
    /// </summary>
    internal sealed class MapAssetResolver
    {
        private readonly WadContentProvider _wadContentProvider;
        private readonly AppSettings _appSettings;
        private readonly ConcurrentDictionary<string, Lazy<VfxResourceIndex>> _projectIndexes =
            new(StringComparer.OrdinalIgnoreCase);

        public MapAssetResolver(
            WadContentProvider wadContentProvider,
            AppSettings appSettings)
        {
            _wadContentProvider = wadContentProvider;
            _appSettings = appSettings;
        }

        public async Task<MapSceneAssets> ResolveSceneAssetsAsync(
            MapSceneSource source,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);

            MapResolvedAsset geometry = await ResolveGeometryAsync(source, cancellationToken);
            MapResolvedAsset materials = await ResolveMaterialsAsync(source, cancellationToken);
            return new MapSceneAssets(source, geometry, materials);
        }

        public Task<MapResolvedAsset> ResolveVirtualAsync(
            string virtualPath,
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            return ResolveReferenceAsync(
                new MapAssetReference(NormalizeVirtualPath(virtualPath), 0),
                projectRoot,
                cancellationToken);
        }

        public async Task<MapResolvedAsset> ResolveReferenceAsync(
            MapAssetReference reference,
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            if (reference?.IsEmpty != false)
                return null;

            string virtualPath = NormalizeVirtualPath(reference.VirtualPath);
            string projectFile = TryResolveProjectFile(projectRoot, virtualPath, reference.PathHash);
            if (projectFile != null)
            {
                return MapResolvedAsset.FromPhysical(
                    string.IsNullOrWhiteSpace(virtualPath) ? Path.GetFileName(projectFile) : virtualPath,
                    projectFile,
                    MapAssetOrigin.ProjectFile);
            }

            if (_wadContentProvider == null)
                return null;

            foreach (string root in GetInstallationRoots(_appSettings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(virtualPath))
                {
                    IReadOnlyDictionary<string, FileSystemNodeModel> byPath =
                        await _wadContentProvider.FindNodesByVirtualPathsAsync(
                            new[] { virtualPath },
                            root,
                            cancellationToken);
                    if (byPath.TryGetValue(virtualPath, out FileSystemNodeModel pathNode) &&
                        !string.IsNullOrWhiteSpace(pathNode.SourceWadPath))
                    {
                        return MapResolvedAsset.FromWad(
                            virtualPath,
                            pathNode.SourceWadPath,
                            reference.PathHash);
                    }
                }

                if (reference.PathHash == 0)
                    continue;

                IReadOnlyDictionary<ulong, FileSystemNodeModel> byHash =
                    await _wadContentProvider.FindNodesByPathHashesAsync(
                        new[] { reference.PathHash },
                        root,
                        cancellationToken);
                if (byHash.TryGetValue(reference.PathHash, out FileSystemNodeModel hashNode) &&
                    !string.IsNullOrWhiteSpace(hashNode.SourceWadPath))
                {
                    string resolvedPath = string.IsNullOrWhiteSpace(hashNode.VirtualPath)
                        ? reference.PathHash.ToString("x16")
                        : NormalizeVirtualPath(hashNode.VirtualPath);
                    return MapResolvedAsset.FromWad(
                        resolvedPath,
                        hashNode.SourceWadPath,
                        reference.PathHash);
                }
            }

            return null;
        }

        internal IReadOnlyList<MapResolvedAsset> ResolveLinkedProjectBins(
            string authoredPath,
            string projectRoot)
        {
            string normalized = NormalizeVirtualPath(authoredPath);
            if (string.IsNullOrWhiteSpace(normalized) ||
                string.IsNullOrWhiteSpace(projectRoot) ||
                !Directory.Exists(projectRoot))
            {
                return Array.Empty<MapResolvedAsset>();
            }

            string fullRoot = Path.GetFullPath(projectRoot);
            IReadOnlyList<string> matches = GetProjectIndex(fullRoot)
                .ResolveLinkedAll(normalized, new[] { ".bin" });
            return matches
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MapResolvedAsset.FromPhysical(
                    normalized,
                    path,
                    MapAssetOrigin.ProjectFile))
                .ToArray();
        }

        public async Task<IReadOnlyDictionary<MapAssetReference, MapResolvedAsset>> ResolveReferencesAsync(
            IEnumerable<MapAssetReference> references,
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            MapAssetReference[] requested = (references ?? Array.Empty<MapAssetReference>())
                .Where(reference => reference?.IsEmpty == false)
                .Distinct()
                .ToArray();
            if (requested.Length == 0)
                return new Dictionary<MapAssetReference, MapResolvedAsset>();

            MapTextureReference[] compatible = requested
                .Select(reference => new MapTextureReference(reference.VirtualPath, reference.PathHash))
                .ToArray();
            IReadOnlyDictionary<MapTextureReference, MapResolvedAsset> resolvedTextures =
                await ResolveTexturesAsync(compatible, projectRoot, cancellationToken);
            var resolved = new Dictionary<MapAssetReference, MapResolvedAsset>();
            foreach (MapAssetReference reference in requested)
            {
                var key = new MapTextureReference(reference.VirtualPath, reference.PathHash);
                if (resolvedTextures.TryGetValue(key, out MapResolvedAsset asset))
                    resolved[reference] = asset;
            }
            return resolved;
        }

        public async Task<IReadOnlyDictionary<MapTextureReference, MapResolvedAsset>> ResolveTexturesAsync(
            IEnumerable<MapTextureReference> references,
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            MapTextureReference[] requested = (references ?? Array.Empty<MapTextureReference>())
                .Where(reference => reference?.IsEmpty == false)
                .Distinct()
                .ToArray();
            var resolved = new Dictionary<MapTextureReference, MapResolvedAsset>();
            if (requested.Length == 0)
                return resolved;

            foreach (MapTextureReference reference in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string virtualPath = NormalizeVirtualPath(reference.VirtualPath);
                string projectFile = TryResolveProjectFile(projectRoot, virtualPath, reference.PathHash);
                if (projectFile != null)
                    resolved[reference] = MapResolvedAsset.FromPhysical(
                        string.IsNullOrWhiteSpace(virtualPath) ? Path.GetFileName(projectFile) : virtualPath,
                        projectFile,
                        MapAssetOrigin.ProjectFile);
            }

            if (_wadContentProvider == null || resolved.Count == requested.Length)
                return resolved;

            foreach (string root in GetInstallationRoots(_appSettings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MapTextureReference[] unresolved = requested
                    .Where(reference => !resolved.ContainsKey(reference))
                    .ToArray();
                if (unresolved.Length == 0)
                    break;

                string[] virtualPaths = unresolved
                    .Select(reference => NormalizeVirtualPath(reference.VirtualPath))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (virtualPaths.Length > 0)
                {
                    IReadOnlyDictionary<string, FileSystemNodeModel> byPath =
                        await _wadContentProvider.FindNodesByVirtualPathsAsync(
                            virtualPaths,
                            root,
                            cancellationToken);
                    foreach (MapTextureReference reference in unresolved)
                    {
                        string path = NormalizeVirtualPath(reference.VirtualPath);
                        if (!string.IsNullOrWhiteSpace(path) &&
                            byPath.TryGetValue(path, out FileSystemNodeModel node) &&
                            !string.IsNullOrWhiteSpace(node.SourceWadPath))
                        {
                            resolved[reference] = MapResolvedAsset.FromWad(
                                path,
                                node.SourceWadPath,
                                reference.PathHash);
                        }
                    }
                }

                ulong[] hashes = requested
                    .Where(reference => !resolved.ContainsKey(reference) && reference.PathHash != 0)
                    .Select(reference => reference.PathHash)
                    .Distinct()
                    .ToArray();
                if (hashes.Length == 0)
                    continue;

                IReadOnlyDictionary<ulong, FileSystemNodeModel> byHash =
                    await _wadContentProvider.FindNodesByPathHashesAsync(
                        hashes,
                        root,
                        cancellationToken);
                foreach (MapTextureReference reference in requested)
                {
                    if (resolved.ContainsKey(reference) || reference.PathHash == 0 ||
                        !byHash.TryGetValue(reference.PathHash, out FileSystemNodeModel node) ||
                        string.IsNullOrWhiteSpace(node.SourceWadPath))
                    {
                        continue;
                    }

                    string path = string.IsNullOrWhiteSpace(reference.VirtualPath)
                        ? reference.PathHash.ToString("x16")
                        : NormalizeVirtualPath(reference.VirtualPath);
                    resolved[reference] = MapResolvedAsset.FromWad(
                        path,
                        node.SourceWadPath,
                        reference.PathHash);
                }
            }

            return resolved;
        }

        public async Task<Stream> OpenReadAsync(
            MapResolvedAsset asset,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(asset);
            cancellationToken.ThrowIfCancellationRequested();

            if (asset.IsPhysicalFile)
            {
                return new FileStream(
                    asset.PhysicalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }

            if (_wadContentProvider == null ||
                string.IsNullOrWhiteSpace(asset.WadPath) ||
                asset.WadPathHash == 0)
            {
                return null;
            }

            var node = new FileSystemNodeModel(
                Path.GetFileName(asset.VirtualPath),
                false,
                asset.VirtualPath,
                asset.WadPath)
            {
                SourceChunkPathHash = asset.WadPathHash,
                SourceWadPath = asset.WadPath,
                Type = NodeType.VirtualFile
            };
            byte[] bytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);
            return bytes == null ? null : new MemoryStream(bytes, writable: false);
        }

        public async Task<byte[]> ReadBytesAsync(
            MapResolvedAsset asset,
            CancellationToken cancellationToken = default)
        {
            await using Stream stream = await OpenReadAsync(asset, cancellationToken);
            if (stream == null)
                return null;

            if (stream is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> buffer))
                return buffer.AsSpan(0, checked((int)stream.Length)).ToArray();

            using var output = new MemoryStream();
            await stream.CopyToAsync(output, cancellationToken);
            return output.ToArray();
        }

        internal static IReadOnlyList<string> GetInstallationRoots(AppSettings settings)
        {
            if (settings == null)
                return Array.Empty<string>();

            // Viewer resources must stay on the explicitly selected client. A PBE preview must never
            // silently resolve a missing MAP/texture/shader from LIVE (or vice versa), because that can
            // produce a scene assembled from different game builds without the user knowing it.
            string selected = settings.PreferredClient == PreferredClient.PBE
                ? settings.LolPbeDirectory
                : settings.LolLiveDirectory;

            if (string.IsNullOrWhiteSpace(selected) || !Directory.Exists(selected))
                return Array.Empty<string>();

            return new[] { Path.GetFullPath(selected) };
        }

        private Task<MapResolvedAsset> ResolveGeometryAsync(
            MapSceneSource source,
            CancellationToken cancellationToken)
        {
            string selected = ExistingFile(source.SelectedGeometryPath);
            string sibling = selected == null
                ? ExistingSibling(source.SelectedMaterialsPath, ".materials.bin", ".mapgeo")
                : null;
            return ResolveVirtualCoreAsync(
                source.Map.GeometryVirtualPath,
                source.ProjectRoot,
                selected ?? sibling,
                selected != null ? MapAssetOrigin.SelectedFile : MapAssetOrigin.ProjectFile,
                cancellationToken);
        }

        private Task<MapResolvedAsset> ResolveMaterialsAsync(
            MapSceneSource source,
            CancellationToken cancellationToken)
        {
            string selected = ExistingFile(source.SelectedMaterialsPath);
            string sibling = selected == null
                ? ExistingSibling(source.SelectedGeometryPath, ".mapgeo", ".materials.bin")
                : null;
            return ResolveVirtualCoreAsync(
                source.Map.MaterialsVirtualPath,
                source.ProjectRoot,
                selected ?? sibling,
                selected != null ? MapAssetOrigin.SelectedFile : MapAssetOrigin.ProjectFile,
                cancellationToken);
        }

        private async Task<MapResolvedAsset> ResolveVirtualCoreAsync(
            string virtualPath,
            string projectRoot,
            string explicitPhysicalPath,
            MapAssetOrigin explicitOrigin,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(virtualPath))
                return null;

            if (!string.IsNullOrWhiteSpace(explicitPhysicalPath) && File.Exists(explicitPhysicalPath))
                return MapResolvedAsset.FromPhysical(virtualPath, explicitPhysicalPath, explicitOrigin);

            string projectFile = TryResolveProjectFile(projectRoot, virtualPath);
            if (projectFile != null)
                return MapResolvedAsset.FromPhysical(virtualPath, projectFile, MapAssetOrigin.ProjectFile);

            if (_wadContentProvider == null)
                return null;

            foreach (string root in GetInstallationRoots(_appSettings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyDictionary<string, FileSystemNodeModel> found =
                    await _wadContentProvider.FindNodesByVirtualPathsAsync(
                        new[] { virtualPath },
                        root,
                        cancellationToken);
                if (found.TryGetValue(virtualPath, out FileSystemNodeModel node) &&
                    !string.IsNullOrWhiteSpace(node.SourceWadPath))
                {
                    return MapResolvedAsset.FromWad(virtualPath, node.SourceWadPath);
                }
            }

            return null;
        }

        private static string ExistingFile(string path) =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

        private static string ExistingSibling(string selectedPath, string fromSuffix, string toSuffix)
        {
            if (string.IsNullOrWhiteSpace(selectedPath) ||
                !selectedPath.EndsWith(fromSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string sibling = selectedPath[..^fromSuffix.Length] + toSuffix;
            return File.Exists(sibling) ? sibling : null;
        }

        private string TryResolveProjectFile(
            string projectRoot,
            string virtualPath,
            ulong pathHash = 0)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
                return null;

            string fullRoot = Path.GetFullPath(projectRoot);
            string rootPrefix = fullRoot
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalized = NormalizeVirtualPath(virtualPath);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                string relative = normalized.Replace('/', Path.DirectorySeparatorChar);
                string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
                if (candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                    return candidate;
            }

            // Extracted WADs keep unresolved paths at their container root as
            // <xxHash64><extension>. Project files remain authoritative over installation WADs,
            // so probe that exact extraction convention before falling back to the game install.
            ulong hash = pathHash;
            if (hash == 0 && !string.IsNullOrWhiteSpace(normalized))
                hash = XxHash64Ext.Hash(normalized.ToLowerInvariant());
            if (hash == 0)
                return null;

            string stem = hash.ToString("x16");
            return GetProjectIndex(fullRoot).Resolve(stem, Array.Empty<string>());
        }

        private VfxResourceIndex GetProjectIndex(string fullRoot) =>
            _projectIndexes.GetOrAdd(
                    Path.GetFullPath(fullRoot),
                    root => new Lazy<VfxResourceIndex>(
                        () => VfxResourceIndex.Build(root),
                        LazyThreadSafetyMode.ExecutionAndPublication))
                .Value;

        private static string NormalizeVirtualPath(string virtualPath) =>
            PathUtils.ToVirtualPath(virtualPath);
    }
}
