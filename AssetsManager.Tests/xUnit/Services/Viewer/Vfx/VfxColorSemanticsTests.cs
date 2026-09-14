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
        public void PreservesHdrColorWithoutTreatingFloatsAsBytes()
        {
            Vector4 normalized = VfxColorSemantics.Normalize(new Vector4(2f, 4f, 8f, 1f));


            Assert.Equal(2f, normalized.X);
            Assert.Equal(4f, normalized.Y);
            Assert.Equal(8f, normalized.Z);
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
        public void ResolvesLinearAnimatedColorAtParticleAge()
        {
            var curve = new VfxCurve4(
                Vector4.One,
                new[] { 0f, 1f },
                new[]
                {
                    new Vector4(1f, 128f / 255f, 0f, 1f),
                    new Vector4(0f, 0f, 0f, 0f)
                });

            Vector4 result = VfxColorSemantics.ResolveParticle(Vector4.One, curve, 0f);

            Assert.Equal(1f, result.X);
            Assert.Equal(128f / 255f, result.Y, 4);
            Assert.Equal(0f, result.Z);
            Assert.Equal(1f, result.W);
        }

        [Fact]
        public void PremultipliesAddAndSubtractBlendModesOnly()
        {
            var color = new Vector4(0.5f, 0.25f, 1f, 0.5f);

            // Add (0) premultiplies RGB by alpha and sets alpha to 1
            Vector4 addResult = VfxColorSemantics.PremultiplyForAddOrSubtract(color, 0, false);
            Assert.Equal(0.25f, addResult.X);
            Assert.Equal(0.125f, addResult.Y);
            Assert.Equal(0.5f, addResult.Z);
            Assert.Equal(1f, addResult.W);

            // Subtract (2) premultiplies RGB by alpha and sets alpha to 1
            Vector4 subResult = VfxColorSemantics.PremultiplyForAddOrSubtract(color, 2, false);
            Assert.Equal(0.25f, subResult.X);
            Assert.Equal(0.125f, subResult.Y);
            Assert.Equal(0.5f, subResult.Z);
            Assert.Equal(1f, subResult.W);

            // Alpha Blend (1) preserves original color and alpha
            Vector4 alphaResult = VfxColorSemantics.PremultiplyForAddOrSubtract(color, 1, false);
            Assert.Equal(color, alphaResult);

            // Alpha Add (4) preserves original color and alpha (blend hardware modulates by SrcAlpha)
            Vector4 alphaAddResult = VfxColorSemantics.PremultiplyForAddOrSubtract(color, 4, false);
            Assert.Equal(color, alphaAddResult);

            // Distortion leaves alpha as warp mask even under Add
            Vector4 distortionResult = VfxColorSemantics.PremultiplyForAddOrSubtract(color, 0, true);
            Assert.Equal(color, distortionResult);
        }
    }
}
