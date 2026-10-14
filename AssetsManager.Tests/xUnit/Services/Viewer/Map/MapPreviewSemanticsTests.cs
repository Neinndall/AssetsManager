using System.Numerics;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapPreviewSemanticsTests
    {
        [Fact]
        public void OwnSunExposesNormalizedPreviewShares()
        {
            MapSunData authored = Sun(intensity: 1f, skyScale: 1.5f);

            MapSunPreviewOverride own = MapPreviewSemantics.OwnSun(authored);

            Assert.Equal(0.4f, own.Strength, 4);
            Assert.Equal(0.6f, own.Ambient, 4);
        }

        [Fact]
        public void SunOverridePreservesAuthoredTotalHorizonFogAndLightmap()
        {
            MapSunData authored = Sun(intensity: 1f, skyScale: 1.5f) with
            {
                HorizonColor = new Vector4(0.2f, 0.3f, 0.4f, 1f),
                LightMapColorScale = 1.7f,
                FogEnabled = true,
                FogColor = new Vector4(0.1f, 0.2f, 0.3f, 1f)
            };
            var preview = new MapSunPreviewOverride(
                Vector3.UnitY,
                new Vector4(0.9f, 0.8f, 0.7f, 1f),
                0.9f,
                new Vector4(0.6f, 0.5f, 0.4f, 1f),
                new Vector4(0.3f, 0.2f, 0.1f, 1f),
                0.2f);

            MapSunData effective = MapPreviewSemantics.EffectiveSun(authored, preview);

            Assert.Equal(2.25f, effective.Intensity, 4);
            Assert.Equal(0.5f, effective.SkyScale, 4);
            Assert.Equal(authored.HorizonColor, effective.HorizonColor);
            Assert.Equal(authored.LightMapColorScale, effective.LightMapColorScale);
            Assert.Equal(authored.FogEnabled, effective.FogEnabled);
            Assert.Equal(authored.FogColor, effective.FogColor);
        }

        [Fact]
        public void SunOverrideWithoutAuthoredSunUsesRiftTotalAndKeepsDefaultCarryFields()
        {
            var preview = new MapSunPreviewOverride(
                Vector3.UnitY,
                Vector4.One,
                0.25f,
                Vector4.One,
                Vector4.One,
                0.75f);

            MapSunData effective = MapPreviewSemantics.EffectiveSun(null, preview);

            Assert.Equal(0.5f, effective.Intensity, 4);
            Assert.Equal(1.5f, effective.SkyScale, 4);
            Assert.Equal(Vector4.One, effective.HorizonColor);
            Assert.Equal(1f, effective.LightMapColorScale);
            Assert.False(effective.FogEnabled);
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(90f, 0f)]
        [InlineData(-90f, 45f)]
        [InlineData(135f, 30f)]
        public void SunAnglesRoundTrip(float azimuth, float elevation)
        {
            Vector3 direction = MapPreviewSemantics.SunDirection(azimuth, elevation);
            (float actualAzimuth, float actualElevation) = MapPreviewSemantics.SunAngles(direction);

            Assert.Equal(azimuth, actualAzimuth, 3);
            Assert.Equal(elevation, actualElevation, 3);
        }

        [Fact]
        public void ExplicitlyDisabledSsaoDiffersFromResetToAuthored()
        {
            var authored = new MapSsaoData(1, 40f, 2f, 3f, 0.8f, 0.5f, true);
            MapSsaoPreviewOverride own = MapPreviewSemantics.OwnSsao(authored);

            Assert.Same(authored, MapPreviewSemantics.EffectiveSsao(authored, null));
            Assert.Null(MapPreviewSemantics.EffectiveSsao(
                authored,
                new MapSsaoPreviewOverride(false, own.Settings)));
            Assert.Equal(own.Settings, MapPreviewSemantics.EffectiveSsao(authored, own));
        }

        [Fact]
        public void PostResetReturnsAuthoredWhileOverrideCanExplicitlyDisableEverything()
        {
            MapPostEffectsData authored = new(
                new MapFogData(true, Vector4.One, 10f, 20f, 0.5f),
                new MapFogData(false, Vector4.Zero, 30f, -10f, 1f),
                new MapDepthOfFieldData(true, 100f, 200f, 5f));

            Assert.Same(authored, MapPreviewSemantics.EffectivePostEffects(authored, null, false));
            Assert.Equal(
                MapPreviewSemantics.NoPostEffects,
                MapPreviewSemantics.EffectivePostEffects(authored, MapPreviewSemantics.NoPostEffects, true));
        }

        private static MapSunData Sun(float intensity, float skyScale) => new(
            new Vector3(-0.25f, 0.75f, -0.05f),
            Vector4.One,
            intensity,
            Vector4.One,
            Vector4.One,
            new Vector4(0.4f, 0.4f, 0.4f, 1f),
            skyScale,
            1f,
            false,
            Vector4.Zero,
            Vector4.Zero,
            Vector2.Zero,
            0f);
    }
}
