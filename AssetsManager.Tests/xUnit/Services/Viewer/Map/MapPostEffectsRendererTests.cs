using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapPostEffectsRendererTests
    {
        [Fact]
        public void DrawsAnythingOnlyWhenMapScreenEffectsNeedASecondPass()
        {
            Assert.False(MapPostEffectsRenderer.DrawsAnything(null, null));

            var effects = new MapPostEffectsData(
                new MapFogData(true, Vector4.One, 5000f, 8000f, 1f),
                new MapFogData(false, Vector4.One, 300f, -100f, 1f),
                new MapDepthOfFieldData(false, 2000f, 800f, 10f));
            Assert.True(MapPostEffectsRenderer.DrawsAnything(effects, null));

            var ssao = new MapSsaoData(0, 25f, 3f, 1f, 1f, 0.5f, true);
            Assert.True(MapPostEffectsRenderer.DrawsAnything(null, ssao));
            Assert.False(MapPostEffectsRenderer.DrawsAnything(
                null,
                ssao with { Intensity = 0f }));
        }

        [Fact]
        public void FullscreenTriangleCoversTheWholeFramebufferUvRange()
        {
            Assert.Contains("vUv = corner;", MapPostEffectsRenderer.FullscreenVertex);
            Assert.DoesNotContain("vUv = corner * 0.5", MapPostEffectsRenderer.FullscreenVertex);
            Assert.Contains("gl_Position = vec4(corner * 2.0 - 1.0", MapPostEffectsRenderer.FullscreenVertex);
        }

        [Fact]
        public void SsaoMatchesTheLtkKernelAndEdgeAwareTwoPassBlurShape()
        {
            Assert.Contains("uniform vec4 uKernel[8];", MapPostEffectsRenderer.OcclusionFragment);
            Assert.Contains("pow(abs(open), uPower)", MapPostEffectsRenderer.OcclusionFragment);
            Assert.Contains(
                "float[5](0.125, 0.25, 0.25, 0.25, 0.125)",
                MapPostEffectsRenderer.BlurFragment);
            Assert.Contains("uEdgeAware != 0", MapPostEffectsRenderer.BlurFragment);
        }

        [Fact]
        public void FinalPassAppliesOcclusionThenDofThenHeightAndDepthFog()
        {
            string source = MapPostEffectsRenderer.PostFragment;
            int shade = source.IndexOf("vec3 color = shadedAt(vUv);", System.StringComparison.Ordinal);
            int focus = source.IndexOf("color = blurred", System.StringComparison.Ordinal);
            int height = source.IndexOf("color = mix(color, uHeightFogColor", System.StringComparison.Ordinal);
            int depth = source.IndexOf("color = mix(color, uDepthFogColor", System.StringComparison.Ordinal);

            Assert.True(shade >= 0 && focus > shade && height > focus && depth > height);
            Assert.Contains("#define TAPS 64", source);
            Assert.Contains("depth < 1.0", source);
        }
    }
}
