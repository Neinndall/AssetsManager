using AssetsManager.Services.Viewer.Loading;
using LeagueToolkit.Core.Environment;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Viewer.Loading
{
    public class MapGeometryVisibilityTests
    {
        [Fact]
        public void ResolveBaseVisibility_SelectsFirstAuthoredLayer()
        {
            EnvironmentVisibility? result = MapGeometryLoadingService.ResolveBaseVisibility(new[]
            {
                EnvironmentVisibility.AllLayers,
                EnvironmentVisibility.Layer3,
                EnvironmentVisibility.Layer1 | EnvironmentVisibility.Layer4
            });

            Assert.Equal(EnvironmentVisibility.Layer1, result);
        }

        [Fact]
        public void ResolveBaseVisibility_LeavesUniversalMapsUnfiltered()
        {
            Assert.Null(MapGeometryLoadingService.ResolveBaseVisibility(new[]
            {
                EnvironmentVisibility.NoLayer,
                EnvironmentVisibility.AllLayers
            }));
        }

        [Theory]
        [InlineData(EnvironmentVisibility.NoLayer, true)]
        [InlineData(EnvironmentVisibility.AllLayers, true)]
        [InlineData(EnvironmentVisibility.Layer1, true)]
        [InlineData(EnvironmentVisibility.Layer1 | EnvironmentVisibility.Layer3, true)]
        [InlineData(EnvironmentVisibility.Layer2, false)]
        public void IsVisible_UsesResolvedBaseLayer(EnvironmentVisibility visibility, bool expected)
        {
            Assert.Equal(
                expected,
                MapGeometryLoadingService.IsVisible(visibility, EnvironmentVisibility.Layer1));
        }
    }
}
