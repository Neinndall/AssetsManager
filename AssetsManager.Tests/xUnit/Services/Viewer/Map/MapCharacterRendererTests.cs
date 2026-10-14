using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapCharacterRendererTests
    {
        [Fact]
        public void WorldMatrixCombinesCharacterMirrorScaleWithConjugatedPlacement()
        {
            Matrix4x4 authored = Matrix4x4.CreateTranslation(10f, 20f, 30f);

            Matrix4x4 world = MapCharacterRenderer.CreateWorldMatrix(authored, 2f);

            Assert.Equal(-2f, world.M11);
            Assert.Equal(2f, world.M22);
            Assert.Equal(2f, world.M33);
            Assert.Equal(new Vector3(-10f, 20f, 30f), new Vector3(world.M41, world.M42, world.M43));
            Assert.True(world.GetDeterminant() < 0f);
        }

        [Theory]
        [InlineData(0.25f, 2f, 0.5f)]
        [InlineData(-0.25f, 2f, 0.5f)]
        [InlineData(1.25f, 2f, 0.5f)]
        [InlineData(float.NaN, 2f, 0f)]
        public void UvScrollUsesAbsoluteClockFoldedIntoOneTile(float rate, float time, float expected)
        {
            Assert.Equal(expected, MapCharacterRenderer.ScrollAt(rate, time), 5);
        }

        [Fact]
        public void GameProgramTexturesStayRawWhileFallbackBaseTexturesUseSrgb()
        {
            Assert.Equal(InternalFormat.Srgb8Alpha8, MapCharacterRenderer.BaseTextureInternalFormat);
            Assert.Equal(InternalFormat.Rgba8, MapCharacterRenderer.ProgramTextureInternalFormat);
        }

        [Fact]
        public void MissingNormalsAreComputedFromTriangles()
        {
            Vector3[] positions =
            {
                new(0f, 0f, 0f),
                new(1f, 0f, 0f),
                new(0f, 0f, 1f)
            };

            Vector3[] normals = MapCharacterRenderer.ComputeNormals(positions, new uint[] { 0, 1, 2 });

            Assert.Equal(3, normals.Length);
            Assert.All(normals, normal => Assert.Equal(new Vector3(0f, -1f, 0f), normal));
        }

        [Fact]
        public void InfluencePaletteUsesInfluenceSlotsRatherThanJointOrder()
        {
            Matrix4x4 joint0 = Matrix4x4.CreateTranslation(1f, 0f, 0f);
            Matrix4x4 joint1 = Matrix4x4.CreateTranslation(2f, 0f, 0f);
            var palette = new Matrix4x4[4];

            MapCharacterRenderer.FillInfluencePalette(
                palette,
                new[] { joint0, joint1 },
                new[] { 1, 0, -1 });

            Assert.Equal(joint1, palette[0]);
            Assert.Equal(joint0, palette[1]);
            Assert.Equal(Matrix4x4.Identity, palette[2]);
            Assert.Equal(Matrix4x4.Identity, palette[3]);
        }
    }
}
