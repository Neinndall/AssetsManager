using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Explorer;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Resolvers
{
    /// <summary>
    /// Resolves one map asset at a time, keeping project overrides authoritative over game files.
    /// </summary>
    internal sealed class MapAssetResolver
    {
        private readonly WadContentProvider _wadContentProvider;
        private readonly AppSettings _appSettings;

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
            if (geometry == null)
                return new MapSceneAssets(source, null, null);

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
            if (!string.IsNullOrWhiteSpace(virtualPath))
            {
                string projectFile = TryResolveProjectFile(projectRoot, virtualPath);
                if (projectFile != null)
                {
                    return MapResolvedAsset.FromPhysical(
                        virtualPath,
                        projectFile,
                        MapAssetOrigin.ProjectFile);
                }
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
                if (string.IsNullOrWhiteSpace(reference.VirtualPath))
                    continue;

                string projectFile = TryResolveProjectFile(projectRoot, reference.VirtualPath);
                if (projectFile != null)
                    resolved[reference] = MapResolvedAsset.FromPhysical(
                        reference.VirtualPath,
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

            string preferred = settings.PreferredClient == PreferredClient.PBE
                ? settings.LolPbeDirectory
                : settings.LolLiveDirectory;
            string alternate = settings.PreferredClient == PreferredClient.PBE
                ? settings.LolLiveDirectory
                : settings.LolPbeDirectory;

            return new[] { preferred, alternate }
                .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private Task<MapResolvedAsset> ResolveGeometryAsync(
            MapSceneSource source,
            CancellationToken cancellationToken)
        {
            string selected = File.Exists(source.SelectedGeometryPath)
                ? source.SelectedGeometryPath
                : null;
            return ResolveVirtualCoreAsync(
                source.Map.GeometryVirtualPath,
                source.ProjectRoot,
                selected,
                MapAssetOrigin.SelectedFile,
                cancellationToken);
        }

        private Task<MapResolvedAsset> ResolveMaterialsAsync(
            MapSceneSource source,
            CancellationToken cancellationToken)
        {
            string sibling = string.IsNullOrWhiteSpace(source.SelectedGeometryPath)
                ? null
                : Path.ChangeExtension(source.SelectedGeometryPath, ".materials.bin");
            if (!File.Exists(sibling))
                sibling = null;

            return ResolveVirtualCoreAsync(
                source.Map.MaterialsVirtualPath,
                source.ProjectRoot,
                sibling,
                MapAssetOrigin.ProjectFile,
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

        private static string TryResolveProjectFile(string projectRoot, string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
                return null;

            string relative = virtualPath.Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(projectRoot, relative));
            string root = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return null;

            return File.Exists(candidate) ? candidate : null;
        }

        private static string NormalizeVirtualPath(string virtualPath) =>
            PathUtils.ToVirtualPath(virtualPath);
    }
}
