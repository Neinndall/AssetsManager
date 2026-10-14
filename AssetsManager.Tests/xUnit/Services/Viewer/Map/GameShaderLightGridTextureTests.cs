using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class GameShaderLightGridTextureTests
    {
        [Fact]
        public void AmbientCubeFillsSixLayersAndTheSunLayerIsFullyLit()
        {
            Vector3[] cube =
            {
                new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f),
                new(0.5f, 0.5f, 0.5f), new(0.25f, 0f, 0f), new(0f, 0.25f, 0f)
            };
            float[] texels = new float[GameShaderLightGridTexture.Layers * 4];

            GameShaderLightGridTexture.Fill(cube, texels);

            Assert.Equal(new[] { 1f, 0f, 0f, 1f }, texels[..4]);
            Assert.Equal(new[] { 0f, 0.25f, 0f, 1f }, texels[20..24]);
            // Layer 6 is the sun visibility the PBR shaders multiply the direct light by.
            Assert.Equal(new[] { 1f, 1f, 1f, 1f }, texels[24..28]);
        }

        [Fact]
        public void AmbientCubeWithoutALightGridComesFromThePreviewSky()
        {
            var frame = new GameShaderRuntime.Frame(Matrix4x4.Identity, Matrix4x4.Identity, Vector3.Zero, 0f, null);
            Span<Vector3> cube = stackalloc Vector3[6];

            GameShaderRuntime.ResolveAmbientCube(frame, cube);

            foreach (Vector3 face in cube)
                Assert.True(face.X > 0f && face.Y > 0f && face.Z > 0f);
        }
    }
}
