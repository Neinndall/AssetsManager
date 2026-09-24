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
    internal sealed class MapTextureLoadingService : IDisposable
    {
        internal const int PreviewTextureSize = 64;
        internal const int FullTextureSize = 1024;
        private const int MaxConcurrentLoads = 4;

        private readonly MapAssetResolver _assetResolver;
        private readonly LogService _logService;
        private readonly MapTextureDecodeCache _decodeCache = new();
        private readonly SemaphoreSlim _decodeGate = new(MaxConcurrentLoads, MaxConcurrentLoads);
        private readonly CancellationTokenSource _disposeCancellation = new();

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
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null) =>
            LoadWaveAsync(materials, projectRoot, PreviewTextureSize, cancellationToken, onLoaded);

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null) =>
            LoadWaveAsync(materials, projectRoot, FullTextureSize, cancellationToken, onLoaded);

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadProgramPreviewAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null) =>
            LoadProgramWaveAsync(materials, projectRoot, PreviewTextureSize, cancellationToken, onLoaded);

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadProgramFullAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null) =>
            LoadProgramWaveAsync(materials, projectRoot, FullTextureSize, cancellationToken, onLoaded);

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadLightmapsPreviewAsync(
            IEnumerable<string> virtualPaths,
            string projectRoot,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null) =>
            LoadVirtualPathsAsync(virtualPaths, projectRoot, PreviewTextureSize, cancellationToken, onLoaded);

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadLightmapsFullAsync(
            IEnumerable<string> virtualPaths,
            string projectRoot,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null) =>
            LoadVirtualPathsAsync(virtualPaths, projectRoot, FullTextureSize, cancellationToken, onLoaded);

        private async Task<IReadOnlyDictionary<string, MapTextureImage>> LoadWaveAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            int maxTextureSize,
            CancellationToken cancellationToken,
            Action<string, MapTextureImage> onLoaded)
        {
            if (materials == null || materials.Count == 0)
                return new Dictionary<string, MapTextureImage>(StringComparer.Ordinal);

            var materialKeys = materials
                .Where(material => material?.BaseTexture?.Texture?.IsEmpty == false &&
                                   !string.IsNullOrWhiteSpace(material.Name))
                .GroupBy(material => material.BaseTexture.Texture)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(material => material.Name)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            MapTextureReference[] references = materialKeys.Keys.ToArray();
            if (references.Length == 0)
                return new Dictionary<string, MapTextureImage>(StringComparer.Ordinal);

            IReadOnlyDictionary<MapTextureReference, MapResolvedAsset> assets =
                await _assetResolver.ResolveTexturesAsync(references, projectRoot, cancellationToken);
            var decoded = new ConcurrentDictionary<MapTextureReference, MapTextureImage>();

            Task[] loads = references.Select(async reference =>
            {
                if (!assets.TryGetValue(reference, out MapResolvedAsset asset))
                    return;

                MapTextureImage image = await DecodeAsync(
                    asset,
                    reference.VirtualPath ?? asset.VirtualPath,
                    maxTextureSize,
                    cancellationToken);
                if (image == null)
                    return;

                decoded[reference] = image;
                if (onLoaded != null && materialKeys.TryGetValue(reference, out string[] keys))
                    foreach (string key in keys)
                        onLoaded(key, image);
            }).ToArray();

            await Task.WhenAll(loads);
            cancellationToken.ThrowIfCancellationRequested();

            var byMaterial = new Dictionary<string, MapTextureImage>(StringComparer.Ordinal);
            foreach ((MapTextureReference reference, string[] keys) in materialKeys)
            {
                if (!decoded.TryGetValue(reference, out MapTextureImage bitmap))
                    continue;
                foreach (string key in keys)
                    byMaterial[key] = bitmap;
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
            CancellationToken cancellationToken,
            Action<string, MapTextureImage> onLoaded)
        {
            var requested = (materials ?? Array.Empty<MapMaterialDefinition>())
                .Where(material => material?.Program?.Passes != null && !string.IsNullOrWhiteSpace(material.Name))
                .SelectMany(material => material.Program.Passes.SelectMany(pass =>
                    (pass.Textures ?? Array.Empty<GameMaterialTexture>())
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

            var keysByReference = requested
                .GroupBy(item => item.Texture)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Key)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            MapTextureReference[] references = keysByReference.Keys.ToArray();
            IReadOnlyDictionary<MapTextureReference, MapResolvedAsset> assets =
                await _assetResolver.ResolveTexturesAsync(references, projectRoot, cancellationToken);
            var decoded = new ConcurrentDictionary<MapTextureReference, MapTextureImage>();

            Task[] loads = references.Select(async reference =>
            {
                if (!assets.TryGetValue(reference, out MapResolvedAsset asset))
                    return;

                MapTextureImage image = await DecodeAsync(
                    asset,
                    reference.VirtualPath ?? asset.VirtualPath,
                    maxTextureSize,
                    cancellationToken);
                if (image == null)
                    return;

                decoded[reference] = image;
                if (onLoaded != null && keysByReference.TryGetValue(reference, out string[] keys))
                    foreach (string key in keys)
                        onLoaded(key, image);
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
            CancellationToken cancellationToken,
            Action<string, MapTextureImage> onLoaded)
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

            Task[] loads = references.Select(async reference =>
            {
                if (!assets.TryGetValue(reference, out MapResolvedAsset asset))
                    return;

                MapTextureImage image = await DecodeAsync(
                    asset,
                    reference.VirtualPath ?? asset.VirtualPath,
                    maxTextureSize,
                    cancellationToken);
                if (image == null)
                    return;

                decoded[reference.VirtualPath] = image;
                onLoaded?.Invoke(reference.VirtualPath, image);
            }).ToArray();

            await Task.WhenAll(loads);
            cancellationToken.ThrowIfCancellationRequested();

            int unresolved = references.Length - decoded.Count;
            if (unresolved > 0)
                _logService?.LogDebug($"MAPGEO lightmaps unresolved or unreadable: {unresolved}/{references.Length}.");

            return new Dictionary<string, MapTextureImage>(decoded, StringComparer.OrdinalIgnoreCase);
        }

        private Task<MapTextureImage> DecodeAsync(
            MapResolvedAsset asset,
            string virtualPath,
            int requestedWidth,
            CancellationToken cancellationToken)
        {
            // Match current LTK MAIN: once the sharpening/full decode has landed, a later preview
            // request uses those pixels immediately instead of reviving an older low-resolution decode.
            if (requestedWidth == PreviewTextureSize &&
                _decodeCache.TryGetCompleted(asset, FullTextureSize, out MapTextureImage full))
            {
                return Task.FromResult(full);
            }

            // Match useAssetTextures.sharpens(): a preview that already landed at least as wide
            // as the full ask, or below preview width because the source itself is smaller, is
            // already the whole authored texture and must not be decoded a second time.
            if (requestedWidth == FullTextureSize &&
                _decodeCache.TryGetCompleted(asset, PreviewTextureSize, out MapTextureImage preview))
            {
                if (PreviewSatisfiesFull(preview))
                    return Task.FromResult(preview);
            }

            return _decodeCache.GetOrLoadAsync(
                asset,
                requestedWidth,
                () => DecodeCoreAsync(asset, virtualPath, requestedWidth),
                cancellationToken);
        }

        internal static bool PreviewSatisfiesFull(MapTextureImage preview)
        {
            if (preview?.BaseLevel == null)
                return false;
            int landedWidth = preview.BaseLevel.PixelWidth;
            return landedWidth >= FullTextureSize || landedWidth < PreviewTextureSize;
        }

        internal Action HoldDecodedTexture(MapTextureImage image) => _decodeCache.Hold(image);

        private async Task<MapTextureImage> DecodeCoreAsync(
            MapResolvedAsset asset,
            string virtualPath,
            int requestedWidth)
        {
            CancellationToken cancellationToken = _disposeCancellation.Token;
            await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using Stream stream = await _assetResolver.OpenReadAsync(asset, cancellationToken).ConfigureAwait(false);
                if (stream == null)
                    return null;

                string extension = DetectTextureExtension(stream, virtualPath ?? asset.VirtualPath);
                IReadOnlyList<System.Windows.Media.Imaging.BitmapSource> levels = await Task.Run(
                    () => TextureUtils.LoadViewerTextureMipChain(stream, extension, requestedWidth),
                    cancellationToken).ConfigureAwait(false);
                return levels.Count > 0 ? new MapTextureImage(levels) : null;
            }
            finally
            {
                _decodeGate.Release();
            }
        }

        public void Dispose()
        {
            _disposeCancellation.Cancel();
            _decodeCache.Dispose();
            _disposeCancellation.Dispose();
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
