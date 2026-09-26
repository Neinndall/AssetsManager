using System.Numerics;
using AssetsManager.Shaders;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class GameShaderRuntimeSemanticsTests
    {
        [Fact]
        public void BufferTexturesRequireIntegerNearestNeutralSampling()
        {
            Assert.True(GameShaderRuntime.RequiresIntegerNeutral(GameShaderTranslator.TextureDimension.Buffer));
            Assert.False(GameShaderRuntime.RequiresIntegerNeutral(GameShaderTranslator.TextureDimension.Texture2D));
            Assert.False(GameShaderRuntime.RequiresIntegerNeutral(GameShaderTranslator.TextureDimension.Cube));
        }

        [Theory]
        [InlineData(0u, false)]
        [InlineData(16u, false)]
        [InlineData(1u, true)]
        [InlineData(8u, true)]
        [InlineData(15u, true)]
        [InlineData(31u, true)]
        public void ColorWriteMatchesViewportAllOrNothingMask(uint writeMask, bool expected)
        {
            Assert.Equal(expected, GameShaderRuntime.ColorWriteEnabled(writeMask));
        }

        [Fact]
        public void ProgramTextureKeyIncludesPassIndexWhenSpecified()
        {
            Assert.Equal("program:materialA:0:Diffuse", AssetsManager.Services.Viewer.Loading.MapTextureLoadingService.ProgramTextureKey("materialA", 0, "Diffuse"));
            Assert.Equal("program:materialA:1:Emissive", AssetsManager.Services.Viewer.Loading.MapTextureLoadingService.ProgramTextureKey("materialA", 1, "Emissive"));
            Assert.Equal("program:materialA:Diffuse", AssetsManager.Services.Viewer.Loading.MapTextureLoadingService.ProgramTextureKey("materialA", "Diffuse"));
        }

        [Fact]
        public void WriteCharacterPerDrawVertex_AmbientCubeAddsSunWhereItFalls()
        {
            var sun = new MapSunData(
                Direction: new Vector3(0f, 1f, 0f),
                Color: new Vector4(1f, 0.5f, 0f, 1f),
                Intensity: 1f,
                SkyColor: new Vector4(0.2f, 0.2f, 0.2f, 1f),
                GroundColor: new Vector4(0.1f, 0.1f, 0.1f, 1f),
                HorizonColor: new Vector4(0.4f, 0.4f, 0.4f, 1f),
                SkyScale: 1f,
                LightMapColorScale: 1f,
                FogColor: Vector4.Zero,
                FogAlternateColor: Vector4.Zero,
                FogStartEnd: Vector2.Zero,
                FogEmissiveRemap: 0f,
                FogEnabled: false);

            var frame = new GameShaderRuntime.Frame(
                Matrix4x4.Identity,
                Matrix4x4.Identity,
                Vector3.Zero,
                0f,
                sun);

            float[] data = new float[64];
            GameShaderRuntime.WriteCharacterPerDrawVertex(data, frame);

            // Face 2 is +Y: index 16 + 2 * 4 = 24
            // Lit = basis * skyScale + sun * facing
            // Basis for +Y is sky = (0.2, 0.2, 0.2)
            // Sun is (1.0, 0.5, 0.0) * 1.0 = (1.0, 0.5, 0.0)
            // Facing for +Y and direction (0, 1, 0) is dot = 1.0
            // Lit = (0.2, 0.2, 0.2) + (1.0, 0.5, 0.0) = (1.2, 0.7, 0.2)
            Assert.Equal(1.2f, data[24], precision: 4);
            Assert.Equal(0.7f, data[25], precision: 4);
            Assert.Equal(0.2f, data[26], precision: 4);

            // Face 3 is -Y: index 16 + 3 * 4 = 28
            // Basis for -Y is ground = (0.1, 0.1, 0.1)
            // Facing for -Y and direction (0, 1, 0) is dot = 0.0
            // Lit = ground * skyScale = (0.1, 0.1, 0.1)
            Assert.Equal(0.1f, data[28], precision: 4);
            Assert.Equal(0.1f, data[29], precision: 4);
            Assert.Equal(0.1f, data[30], precision: 4);

            // Face 0 is +X: index 16 + 0 * 4 = 16
            // Basis for +X is horizon = (0.4, 0.4, 0.4)
            // Facing for +X and direction (0, 1, 0) is dot = 0.0
            // Lit = horizon * skyScale = (0.4, 0.4, 0.4)
            Assert.Equal(0.4f, data[16], precision: 4);
            Assert.Equal(0.4f, data[17], precision: 4);
            Assert.Equal(0.4f, data[18], precision: 4);
        }

        [Theory]
        [InlineData(0u, Silk.NET.OpenGL.DepthFunction.Lequal)] // 0 reads as class default (Lequal) rather than Never
        [InlineData(1u, Silk.NET.OpenGL.DepthFunction.Less)]
        [InlineData(2u, Silk.NET.OpenGL.DepthFunction.Equal)]
        [InlineData(3u, Silk.NET.OpenGL.DepthFunction.Lequal)]
        [InlineData(4u, Silk.NET.OpenGL.DepthFunction.Greater)]
        [InlineData(5u, Silk.NET.OpenGL.DepthFunction.Notequal)]
        [InlineData(6u, Silk.NET.OpenGL.DepthFunction.Gequal)]
        [InlineData(7u, Silk.NET.OpenGL.DepthFunction.Always)]
        [InlineData(99u, Silk.NET.OpenGL.DepthFunction.Lequal)]
        public void ToDepth_MapsZeroToDefaultLequal(uint value, Silk.NET.OpenGL.DepthFunction expected)
        {
            Assert.Equal(expected, GameShaderRuntime.ToDepth(value));
        }
    }
}
