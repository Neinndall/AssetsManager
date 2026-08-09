using AssetsManager.Services.Viewer.Loading;
using LeagueToolkit.Core.Environment;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Viewer.Loading
{
    public class MapGeometryVisibilityTests
    {
        [Fact]
        public void IsDefaultVisibility_OnlyAcceptsMeshesVisibleOnEveryLayer()
        {
            Assert.True(MapGeometryLoadingService.IsDefaultVisibility(EnvironmentVisibility.AllLayers));
            Assert.False(MapGeometryLoadingService.IsDefaultVisibility(EnvironmentVisibility.NoLayer));
            Assert.False(MapGeometryLoadingService.IsDefaultVisibility(EnvironmentVisibility.Layer1));
            Assert.False(MapGeometryLoadingService.IsDefaultVisibility(
                EnvironmentVisibility.Layer1 | EnvironmentVisibility.Layer3));
        }
    }
}
