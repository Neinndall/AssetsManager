using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Resources;

internal static class ViewerTextureLoadingCache
{
    internal static ViewerDecodeKey Key(MapResolvedAsset asset, string profile)
        => new(MapResolvedAssetCacheKey.From(asset), profile);

    internal static string BitmapProfile(string extension, bool viewer)
        => extension.ToLowerInvariant() is ".tex" or ".dds" ? "game-bitmap" : viewer ? "viewer-bitmap" : "file-bitmap";

    internal static long BitmapBytes(BitmapSource bitmap)
        => checked((long)bitmap.PixelWidth * bitmap.PixelHeight * 4);

    internal static BitmapSource LoadFile(string path, bool viewer = false, LogService log = null,
        MapResolvedAssetCacheKey? sourceIdentity = null)
    {
        var asset = MapResolvedAsset.FromPhysical(path, path, MapAssetOrigin.SelectedFile);
        string extension = Path.GetExtension(path);
        var key = new ViewerDecodeKey(sourceIdentity ?? MapResolvedAssetCacheKey.From(asset), BitmapProfile(extension, viewer));
        return ViewerDecodedResourceCache<BitmapSource>.Shared.Get(key, () =>
        {
            BitmapSource bitmap;
            if (viewer)
            {
                using Stream stream = File.OpenRead(path);
                bitmap = TextureUtils.LoadViewerTexture(stream, extension, log, path);
            }
            else bitmap = TextureUtils.LoadTextureFromFile(path);
            if (bitmap?.CanFreeze == true) bitmap.Freeze();
            return bitmap;
        }, BitmapBytes);
    }

    internal static Task<BitmapSource> LoadAssetAsync(MapResolvedAsset asset, MapAssetResolver resolver,
        string authoredPath, CancellationToken cancellationToken)
        => ViewerDecodedResourceCache<BitmapSource>.Shared.GetAsync(
            Key(asset, BitmapProfile(Path.GetExtension(authoredPath ?? asset.VirtualPath ?? ""), viewer: true)),
            () => Task.Run(async () =>
            {
                await using Stream stream = await resolver.OpenReadAsync(asset, CancellationToken.None).ConfigureAwait(false);
                if (stream == null) return null;
                string extension = MapTextureLoadingService.DetectTextureExtension(stream, authoredPath ?? asset.VirtualPath);
                BitmapSource bitmap = TextureUtils.LoadViewerTexture(stream, extension);
                if (bitmap?.CanFreeze == true) bitmap.Freeze();
                return bitmap;
            }), BitmapBytes, cancellationToken);

    internal static Task<CubeMapData> LoadCubeAsync(MapResolvedAsset asset, MapAssetResolver resolver,
        CancellationToken cancellationToken)
        => ViewerDecodedResourceCache<CubeMapData>.Shared.GetAsync(Key(asset, "cube"),
            () => Task.Run(async () =>
            {
                await using Stream stream = await resolver.OpenReadAsync(asset, CancellationToken.None).ConfigureAwait(false);
                return stream == null ? null : CubeMapDecoder.Decode(stream);
            }), cube => checked((long)cube.Width * cube.Height * 4 * 6), cancellationToken);
}
