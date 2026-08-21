using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxColorSemanticsTests
    {
        [Fact]
        public void NormalizesByteEncodedValueColor()
        {
            Vector4 normalized = VfxColorSemantics.Normalize(new Vector4(216f, 216f, 216f, 255f));

            Assert.Equal(216f / 255f, normalized.X, 4);
            Assert.Equal(216f / 255f, normalized.Y, 4);
            Assert.Equal(216f / 255f, normalized.Z, 4);
            Assert.Equal(1f, normalized.W);
        }

        [Fact]
        public void InvalidColorFallsBackToWhiteIdentity()
        {
            Assert.Equal(
                Vector4.One,
                VfxColorSemantics.Normalize(new Vector4(float.NaN, 0f, 0f, 1f)));
        }

        [Fact]
        public void ResolvesColorOverLifeAsBirthColorMultiplier()
        {
            Vector4 result = VfxColorSemantics.ResolveParticle(
                new Vector4(0.8f, 0.5f, 0.25f, 1f),
                VfxCurve4.Const(new Vector4(0.5f, 0.25f, 0.4f, 0.5f)),
                0.5f);

            Assert.Equal(new Vector4(0.4f, 0.125f, 0.1f, 0.5f), result);
        }

        [Fact]
        public void ResolvesByteEncodedAnimatedColorAtParticleAge()
        {
            var curve = new VfxCurve4(
                Vector4.One,
                new[] { 0f, 1f },
                new[]
                {
                    new Vector4(255f, 128f, 0f, 255f),
                    new Vector4(0f, 0f, 0f, 0f)
                });

            Vector4 result = VfxColorSemantics.ResolveParticle(Vector4.One, curve, 0f);

            Assert.Equal(1f, result.X);
            Assert.Equal(128f / 255f, result.Y, 4);
            Assert.Equal(0f, result.Z);
            Assert.Equal(1f, result.W);
        }
    }
}
