using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Loading
{
    /// <summary>
    /// Resolves and decodes the base textures referenced by one map's material table.
    /// </summary>
    internal sealed class MapTextureLoadingService
    {
        internal const int PreviewTextureSize = 64;
        internal const int FullTextureSize = 1024;
        private const int MaxConcurrentLoads = 4;

        private readonly MapAssetResolver _assetResolver;
        private readonly LogService _logService;

        public MapTextureLoadingService(
            MapAssetResolver assetResolver,
            LogService logService)
        {
            _assetResolver = assetResolver;
            _logService = logService;
        }

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadPreviewAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            LoadWaveAsync(materials, projectRoot, PreviewTextureSize, cancellationToken);

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            LoadWaveAsync(materials, projectRoot, FullTextureSize, cancellationToken);

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadProgramPreviewAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            LoadProgramWaveAsync(materials, projectRoot, PreviewTextureSize, cancellationToken);

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadProgramFullAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            LoadProgramWaveAsync(materials, projectRoot, FullTextureSize, cancellationToken);

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadLightmapsPreviewAsync(
            IEnumerable<string> virtualPaths,
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            LoadVirtualPathsAsync(virtualPaths, projectRoot, PreviewTextureSize, cancellationToken);

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadLightmapsFullAsync(
            IEnumerable<string> virtualPaths,
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            LoadVirtualPathsAsync(virtualPaths, projectRoot, FullTextureSize, cancellationToken);

        private async Task<IReadOnlyDictionary<string, MapTextureImage>> LoadWaveAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            int maxTextureSize,
            CancellationToken cancellationToken)
        {
            if (materials == null || materials.Count == 0)
                return new Dictionary<string, MapTextureImage>(StringComparer.Ordinal);

            MapTextureReference[] references = materials
                .Select(material => material?.BaseTexture?.Texture)
                .Where(reference => reference?.IsEmpty == false)
                .Distinct()
                .ToArray();
            if (references.Length == 0)
                return new Dictionary<string, MapTextureImage>(StringComparer.Ordinal);

            IReadOnlyDictionary<MapTextureReference, MapResolvedAsset> assets =
                await _assetResolver.ResolveTexturesAsync(references, projectRoot, cancellationToken);
            var decoded = new ConcurrentDictionary<MapTextureReference, MapTextureImage>();
            using var gate = new SemaphoreSlim(MaxConcurrentLoads, MaxConcurrentLoads);

            Task[] loads = references.Select(async reference =>
            {
                if (!assets.TryGetValue(reference, out MapResolvedAsset asset))
                    return;

                await gate.WaitAsync(cancellationToken);
                try
                {
                    await using Stream stream = await _assetResolver.OpenReadAsync(asset, cancellationToken);
                    if (stream == null)
                        return;

                    string extension = DetectTextureExtension(stream, reference.VirtualPath ?? asset.VirtualPath);
                    IReadOnlyList<System.Windows.Media.Imaging.BitmapSource> levels = await Task.Run(
                        () => TextureUtils.LoadViewerTextureMipChain(
                            stream,
                            extension,
                            maxTextureSize),
                        cancellationToken);
                    if (levels.Count > 0)
                        decoded[reference] = new MapTextureImage(levels);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(loads);
            cancellationToken.ThrowIfCancellationRequested();

            var byMaterial = new Dictionary<string, MapTextureImage>(StringComparer.Ordinal);
            foreach (MapMaterialDefinition material in materials)
            {
                MapTextureReference reference = material?.BaseTexture?.Texture;
                if (reference != null &&
                    decoded.TryGetValue(reference, out MapTextureImage bitmap) &&
                    !string.IsNullOrWhiteSpace(material.Name))
                {
                    byMaterial[material.Name] = bitmap;
                }
            }

            int unresolved = references.Length - decoded.Count;
            if (unresolved > 0)
            {
                _logService?.LogDebug(
                    $"MAPGEO base textures unresolved or unreadable: {unresolved}/{references.Length}.");
            }

            return byMaterial;
        }

        internal static string ProgramTextureKey(string material, string texture) =>
            $"program:{material}:{texture}";

        private async Task<IReadOnlyDictionary<string, MapTextureImage>> LoadProgramWaveAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            int maxTextureSize,
            CancellationToken cancellationToken)
        {
            var requested = (materials ?? Array.Empty<MapMaterialDefinition>())
                .Where(material => material?.Program?.Passes != null && !string.IsNullOrWhiteSpace(material.Name))
                .SelectMany(material => material.Program.Passes.SelectMany(pass =>
                    (pass.Textures ?? Array.Empty<MapMaterialPassTextureData>())
                        .Where(texture => texture?.Texture?.IsEmpty == false)
                        .Select(texture => new
                        {
                            Key = ProgramTextureKey(material.Name, texture.Name),
                            texture.Texture
                        })))
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
            if (requested.Length == 0)
                return new Dictionary<string, MapTextureImage>(StringComparer.Ordinal);

            MapTextureReference[] references = requested
                .Select(item => item.Texture)
                .Distinct()
                .ToArray();
            IReadOnlyDictionary<MapTextureReference, MapResolvedAsset> assets =
                await _assetResolver.ResolveTexturesAsync(references, projectRoot, cancellationToken);
            var decoded = new ConcurrentDictionary<MapTextureReference, MapTextureImage>();
            using var gate = new SemaphoreSlim(MaxConcurrentLoads, MaxConcurrentLoads);

            Task[] loads = references.Select(async reference =>
            {
                if (!assets.TryGetValue(reference, out MapResolvedAsset asset))
                    return;

                await gate.WaitAsync(cancellationToken);
                try
                {
                    await using Stream stream = await _assetResolver.OpenReadAsync(asset, cancellationToken);
                    if (stream == null)
                        return;

                    string extension = DetectTextureExtension(stream, reference.VirtualPath ?? asset.VirtualPath);
                    IReadOnlyList<System.Windows.Media.Imaging.BitmapSource> levels = await Task.Run(
                        () => TextureUtils.LoadViewerTextureMipChain(stream, extension, maxTextureSize),
                        cancellationToken);
                    if (levels.Count > 0)
                        decoded[reference] = new MapTextureImage(levels);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(loads);
            cancellationToken.ThrowIfCancellationRequested();

            var result = new Dictionary<string, MapTextureImage>(StringComparer.Ordinal);
            foreach (var item in requested)
                if (decoded.TryGetValue(item.Texture, out MapTextureImage image))
                    result[item.Key] = image;

            int unresolved = references.Length - decoded.Count;
            if (unresolved > 0)
                _logService?.LogDebug($"MAPGEO program textures unresolved or unreadable: {unresolved}/{references.Length}.");
            return result;
        }

        private async Task<IReadOnlyDictionary<string, MapTextureImage>> LoadVirtualPathsAsync(
            IEnumerable<string> virtualPaths,
            string projectRoot,
            int maxTextureSize,
            CancellationToken cancellationToken)
        {
            MapTextureReference[] references = (virtualPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new MapTextureReference(path, 0))
                .ToArray();
            if (references.Length == 0)
                return new Dictionary<string, MapTextureImage>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyDictionary<MapTextureReference, MapResolvedAsset> assets =
                await _assetResolver.ResolveTexturesAsync(references, projectRoot, cancellationToken);
            var decoded = new ConcurrentDictionary<string, MapTextureImage>(StringComparer.OrdinalIgnoreCase);
            using var gate = new SemaphoreSlim(MaxConcurrentLoads, MaxConcurrentLoads);

            Task[] loads = references.Select(async reference =>
            {
                if (!assets.TryGetValue(reference, out MapResolvedAsset asset))
                    return;

                await gate.WaitAsync(cancellationToken);
                try
                {
                    await using Stream stream = await _assetResolver.OpenReadAsync(asset, cancellationToken);
                    if (stream == null)
                        return;

                    string extension = DetectTextureExtension(stream, reference.VirtualPath ?? asset.VirtualPath);
                    IReadOnlyList<System.Windows.Media.Imaging.BitmapSource> levels = await Task.Run(
                        () => TextureUtils.LoadViewerTextureMipChain(stream, extension, maxTextureSize),
                        cancellationToken);
                    if (levels.Count > 0)
                        decoded[reference.VirtualPath] = new MapTextureImage(levels);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(loads);
            cancellationToken.ThrowIfCancellationRequested();

            int unresolved = references.Length - decoded.Count;
            if (unresolved > 0)
                _logService?.LogDebug($"MAPGEO lightmaps unresolved or unreadable: {unresolved}/{references.Length}.");

            return new Dictionary<string, MapTextureImage>(decoded, StringComparer.OrdinalIgnoreCase);
        }

        internal static string DetectTextureExtension(Stream stream, string virtualPath)
        {
            string extension = Path.GetExtension(virtualPath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(extension))
                return extension;
            if (stream == null || !stream.CanRead)
                return ".tex";

            long position = stream.CanSeek ? stream.Position : 0;
            Span<byte> magic = stackalloc byte[4];
            int read = stream.Read(magic);
            if (stream.CanSeek)
                stream.Position = position;

            if (read >= 4)
            {
                if (magic[0] == (byte)'T' && magic[1] == (byte)'E' && magic[2] == (byte)'X' && magic[3] == 0)
                    return ".tex";
                if (magic[0] == (byte)'D' && magic[1] == (byte)'D' && magic[2] == (byte)'S' && magic[3] == (byte)' ')
                    return ".dds";
                if (magic[0] == 0x89 && magic[1] == (byte)'P' && magic[2] == (byte)'N' && magic[3] == (byte)'G')
                    return ".png";
            }

            return ".tex";
        }
    }
}
