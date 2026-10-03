using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Utils;
using Xunit;
using Newtonsoft.Json;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class FxaaPostEffectsRendererTests
    {
        [Fact]
        public void FxaaQualityOptionsMatchInGamePreset()
        {
            Assert.Equal(0.75f, FxaaPostEffectsRenderer.DefaultSubpix);
            Assert.Equal(0.5f, FxaaPostEffectsRenderer.DefaultEdgeThreshold);
            Assert.Equal(0.0833f, FxaaPostEffectsRenderer.DefaultEdgeThresholdMin);
            Assert.Equal(
                new Vector3(0.75f, 0.5f, 0.0833f),
                FxaaPostEffectsRenderer.DefaultOptions);
        }

        [Fact]
        public void SearchStepsMatchPreset26()
        {
            Assert.Equal(11, FxaaPostEffectsRenderer.SearchSteps.Length);
            Assert.Equal(
                new float[] { 1.0f, 1.5f, 2.0f, 2.0f, 2.0f, 2.0f, 2.0f, 2.0f, 2.0f, 4.0f, 8.0f },
                FxaaPostEffectsRenderer.SearchSteps);

            Assert.Contains("#define STEPS 11", FxaaPostEffectsRenderer.FragmentShader);
            Assert.Contains(
                "float[](1.0, 1.5, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 4.0, 8.0)",
                FxaaPostEffectsRenderer.FragmentShader);
        }

        [Fact]
        public void CalculateRcpFrameCalculatesInverseDimensions()
        {
            Vector2 rcp = FxaaPostEffectsRenderer.CalculateRcpFrame(1920, 1080);
            Assert.Equal(1f / 1920f, rcp.X);
            Assert.Equal(1f / 1080f, rcp.Y);

            Vector2 clamped = FxaaPostEffectsRenderer.CalculateRcpFrame(0, -10);
            Assert.Equal(1f, clamped.X);
            Assert.Equal(1f, clamped.Y);
        }

        [Fact]
        public void FragmentShaderReadsCenterLumaFromGreenAndNeighborsFromWeightedSum()
        {
            Assert.Contains("float lumaM = rgbyM.g;", FxaaPostEffectsRenderer.FragmentShader);
            Assert.Contains("dot(rgba.rgb, vec3(0.299, 0.587, 0.114))", FxaaPostEffectsRenderer.FragmentShader);
        }

        [Theory]
        [InlineData("Off", false)]
        [InlineData("Fxaa", true)]
        [InlineData("Smaa", true)]
        public void AntiAliasingSelectionEnablesTheSelectedModeAndSurvivesReload(string choice, bool enabled)
        {
            var settings = new StudioParametersSettings { EnableFxaa = false, AntiAliasingMode = "Smaa" };
            settings.AntiAliasingSelection = choice;
            Assert.Equal(enabled, settings.EnableFxaa);
            Assert.Equal(choice, settings.AntiAliasingSelection);
            if (enabled) Assert.Equal(choice, settings.AntiAliasingMode);

            string json = JsonConvert.SerializeObject(settings);
            Assert.DoesNotContain("AntiAliasingSelection", json);
            var restored = JsonConvert.DeserializeObject<StudioParametersSettings>(json);
            Assert.Equal(choice, restored.AntiAliasingSelection);
            Assert.Equal(enabled, restored.EnableFxaa);
        }

        [Fact]
        public void LegacyDisabledAntiAliasingShowsOffAndNotifiesWhenReenabled()
        {
            var settings = JsonConvert.DeserializeObject<StudioParametersSettings>(
                "{\"EnableFxaa\":false,\"AntiAliasingMode\":\"Smaa\"}");
            Assert.Equal("Off", settings.AntiAliasingSelection);
            var changed = new System.Collections.Generic.List<string>();
            settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            settings.AntiAliasingSelection = "Smaa";
            Assert.True(settings.EnableFxaa);
            Assert.Equal("Smaa", settings.AntiAliasingSelection);
            Assert.Contains(nameof(StudioParametersSettings.AntiAliasingSelection), changed);
            settings.AntiAliasingSelection = "Off";
            Assert.False(settings.EnableFxaa);
        }

        [Fact]
        public void StudioParametersSettingsDefaultsEnableFxaaToTrue()
        {
            var settings = new StudioParametersSettings();
            Assert.True(settings.EnableFxaa);

            AppSettings defaults = AppSettings.GetDefaultSettings();
            Assert.NotNull(defaults.StudioParameters);
            Assert.True(defaults.StudioParameters.EnableFxaa);
        }
    }
}
