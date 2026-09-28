using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Services.Viewer.Loading
{
    internal sealed class MapLightGridLoadingService
    {
        private readonly MapAssetResolver _resolver;
        private readonly LogService _log;

        internal MapLightGridLoadingService(MapAssetResolver resolver, LogService log)
        {
            _resolver = resolver;
            _log = log;
        }

        internal async Task<MapLightGridData> LoadAsync(BinTree materials, MapSceneSource source,
            CancellationToken cancellationToken)
        {
            string path = MapLightGridParser.FindPath(materials, source.Map);
            if (string.IsNullOrWhiteSpace(path)) return null;
            MapResolvedAsset asset = await _resolver.ResolveVirtualAsync(path, source.ProjectRoot, cancellationToken);
            if (asset == null) return null;
            await using Stream stream = await _resolver.OpenReadAsync(asset, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (stream == null) return null;
            float rmaIntensityScale = MapLightGridParser.FindRmaIntensityScale(materials, source.Map)
                                      ?? MapLightGridData.DefaultRmaIntensityScale;
            try { return MapLightGridParser.Decode(stream, rmaIntensityScale); }
            catch (IOException ex)
            {
                _log?.LogDebug($"Map light grid unavailable: {path}: {ex.Message}");
                return null;
            }
        }
    }
}
