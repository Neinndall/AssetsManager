using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
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

        public Task<IReadOnlyDictionary<string, BitmapSource>> LoadPreviewAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            LoadWaveAsync(materials, projectRoot, PreviewTextureSize, cancellationToken);

        public Task<IReadOnlyDictionary<string, BitmapSource>> LoadFullAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            LoadWaveAsync(materials, projectRoot, FullTextureSize, cancellationToken);

        private async Task<IReadOnlyDictionary<string, BitmapSource>> LoadWaveAsync(
            IReadOnlyList<MapMaterialDefinition> materials,
            string projectRoot,
            int maxTextureSize,
            CancellationToken cancellationToken)
        {
            if (materials == null || materials.Count == 0)
                return new Dictionary<string, BitmapSource>(StringComparer.Ordinal);

            MapTextureReference[] references = materials
                .Select(material => material?.BaseTexture?.Texture)
                .Where(reference => reference?.IsEmpty == false)
                .Distinct()
                .ToArray();
            if (references.Length == 0)
                return new Dictionary<string, BitmapSource>(StringComparer.Ordinal);

            IReadOnlyDictionary<MapTextureReference, MapResolvedAsset> assets =
                await _assetResolver.ResolveTexturesAsync(references, projectRoot, cancellationToken);
            var decoded = new ConcurrentDictionary<MapTextureReference, BitmapSource>();
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
                    BitmapSource bitmap = await Task.Run(
                        () => TextureUtils.LoadViewerTexture(
                            stream,
                            extension,
                            maxTextureSize,
                            maxTextureSize),
                        cancellationToken);
                    if (bitmap != null)
                        decoded[reference] = bitmap;
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(loads);
            cancellationToken.ThrowIfCancellationRequested();

            var byMaterial = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
            foreach (MapMaterialDefinition material in materials)
            {
                MapTextureReference reference = material?.BaseTexture?.Texture;
                if (reference != null &&
                    decoded.TryGetValue(reference, out BitmapSource bitmap) &&
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
