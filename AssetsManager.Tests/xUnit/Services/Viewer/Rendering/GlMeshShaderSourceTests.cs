using AssetsManager.Services.Viewer.Rendering.Core;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class GlMeshShaderSourceTests
    {
        [Fact]
        public void Fragment_UsesSrgbCharacterMaterialPath()
        {
            Assert.Contains("uniform int uMaterialSrgb;", GlMeshShaderSource.Fragment);
            Assert.Contains("vec4 texColor = readBaseTexture(materialUv);", GlMeshShaderSource.Fragment);
            Assert.Contains("? srgbToLinear(uColorTint.rgb)", GlMeshShaderSource.Fragment);
            Assert.Contains("finalColor = linearToSrgb(finalColor);", GlMeshShaderSource.Fragment);
        }

        [Fact]
        public void Fragment_ReusesDecodedBaseTextureForFlowSampling()
        {
            Assert.Contains("vec3 flowColor = readBaseTexture(flowUv).rgb", GlMeshShaderSource.Fragment);
            Assert.DoesNotContain("vec3 flowColor = texture(uTex, flowUv).rgb", GlMeshShaderSource.Fragment);
        }
    }
}
