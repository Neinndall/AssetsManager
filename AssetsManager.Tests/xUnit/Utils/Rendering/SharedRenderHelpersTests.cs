using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta.Properties;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Utils.Rendering
{
    /// <summary>Pins the helpers that replaced identical private copies, so the merge cannot change behaviour.</summary>
    public sealed class SharedRenderHelpersTests
    {
        [Fact]
        public void IsFiniteRejectsAnyNonFiniteComponent()
        {
            Assert.True(VectorMathUtils.IsFinite(new Vector3(1f, -2f, 0f)));
            Assert.False(VectorMathUtils.IsFinite(new Vector3(float.NaN, 0f, 0f)));
            Assert.False(VectorMathUtils.IsFinite(new Vector3(0f, float.PositiveInfinity, 0f)));
            Assert.False(VectorMathUtils.IsFinite(new Vector3(0f, 0f, float.NegativeInfinity)));
        }

        [Fact]
        public void NormalizeOrFallsBackAtOrBelowTheSquaredLengthThreshold()
        {
            Assert.Equal(Vector3.UnitX, VectorMathUtils.NormalizeOr(new Vector3(5f, 0f, 0f), Vector3.UnitY));
            Assert.Equal(Vector3.UnitY, VectorMathUtils.NormalizeOr(Vector3.Zero, Vector3.UnitY));
            // Squared length 1e-8 exactly is not above the threshold; 1e-3 is.
            Assert.Equal(Vector3.UnitZ, VectorMathUtils.NormalizeOr(new Vector3(1e-4f, 0f, 0f), Vector3.UnitZ));
            Assert.Equal(Vector3.UnitX, VectorMathUtils.NormalizeOr(new Vector3(1e-3f, 0f, 0f), Vector3.UnitZ));
        }

        [Fact]
        public void OrientationOnlyDropsScaleAndTranslationAndKeepsTheTurn()
        {
            Matrix4x4 turn = Matrix4x4.CreateRotationY(0.7f);
            Matrix4x4 placed = Matrix4x4.CreateScale(3f, 0.5f, 2f) * turn * Matrix4x4.CreateTranslation(10f, 20f, 30f);

            Matrix4x4 result = VectorMathUtils.OrientationOnly(placed);

            Assert.Equal(Vector3.Zero, result.Translation);
            Assert.True(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, turn), Vector3.TransformNormal(Vector3.UnitX, result)) < 1e-5f);
            Assert.True(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, turn), Vector3.TransformNormal(Vector3.UnitZ, result)) < 1e-5f);
        }

        [Fact]
        public void OrientationOnlyUsesTheUnitAxisForACollapsedAxis()
        {
            Matrix4x4 result = VectorMathUtils.OrientationOnly(Matrix4x4.CreateScale(0f, 2f, 2f));

            Assert.Equal(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, result));
            Assert.Equal(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitY, result));
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.04045f, 0.04045f / 12.92f)]
        [InlineData(0.5f, 0.21404114f)]
        [InlineData(1f, 1f)]
        public void SrgbChannelToLinearFollowsTheSrgbCurve(float srgb, float linear)
        {
            Assert.Equal(linear, VectorMathUtils.SrgbChannelToLinear(srgb), 5);
            Assert.Equal(new Vector3(linear), VectorMathUtils.SrgbToLinear(new Vector3(srgb)));
        }

        [Theory]
        [InlineData(ModelMaterialWrapMode.Clamp, TextureWrapMode.ClampToEdge)]
        [InlineData(ModelMaterialWrapMode.Mirror, TextureWrapMode.MirroredRepeat)]
        [InlineData(ModelMaterialWrapMode.Border, TextureWrapMode.ClampToEdge)]
        [InlineData(ModelMaterialWrapMode.Repeat, TextureWrapMode.Repeat)]
        public void ModelWrapModesMapToGl(ModelMaterialWrapMode wrap, TextureWrapMode expected) =>
            Assert.Equal(expected, GlTextureWrap.Of(wrap));

        [Fact]
        public void MapWrapModesMapToGl()
        {
            Assert.Equal(TextureWrapMode.ClampToEdge, GlTextureWrap.Of(MapTextureWrap.Clamp));
            Assert.Equal(TextureWrapMode.MirroredRepeat, GlTextureWrap.Of(MapTextureWrap.Mirror));
            Assert.Equal(TextureWrapMode.ClampToEdge, GlTextureWrap.Of(MapTextureWrap.Border));
            Assert.Equal(TextureWrapMode.Repeat, GlTextureWrap.Of(MapTextureWrap.Repeat));
        }

        [Fact]
        public void BlendFactorsMapTheEightAuthoredValuesAndFallBackOtherwise()
        {
            MapBlendFactor[] expected =
            {
                MapBlendFactor.Zero, MapBlendFactor.One, MapBlendFactor.SourceColor, MapBlendFactor.OneMinusSourceColor,
                MapBlendFactor.DestinationColor, MapBlendFactor.OneMinusDestinationColor, MapBlendFactor.SourceAlpha,
                MapBlendFactor.OneMinusSourceAlpha
            };
            for (uint value = 0; value < expected.Length; value++)
                Assert.Equal(expected[value], MaterialSwitchSemantics.BlendFactorOf(value, MapBlendFactor.One));
            Assert.Equal(MapBlendFactor.One, MaterialSwitchSemantics.BlendFactorOf(8, MapBlendFactor.One));
            Assert.Equal(MapBlendFactor.Zero, MaterialSwitchSemantics.BlendFactorOf(null, MapBlendFactor.Zero));
        }

        [Fact]
        public void SwitchesAndMacrosAreOnlyEnabledWhenSet()
        {
            var switches = new Dictionary<string, bool> { ["A"] = true, ["B"] = false };
            Assert.True(MaterialSwitchSemantics.IsEnabled(switches, "A"));
            Assert.False(MaterialSwitchSemantics.IsEnabled(switches, "B"));
            Assert.False(MaterialSwitchSemantics.IsEnabled(switches, "C"));
            Assert.False(MaterialSwitchSemantics.IsEnabled(null, "A"));

            var macros = new Dictionary<string, string> { ["ON"] = "1", ["OFF"] = "0", ["TWO"] = "2" };
            Assert.True(MaterialSwitchSemantics.IsMacroEnabled(macros, "ON"));
            Assert.False(MaterialSwitchSemantics.IsMacroEnabled(macros, "OFF"));
            Assert.False(MaterialSwitchSemantics.IsMacroEnabled(macros, "TWO"));
            Assert.False(MaterialSwitchSemantics.IsMacroEnabled(null, "ON"));
        }

        [Theory]
        [InlineData("ASSETS/Shared/Materials/black.tex", true)]
        [InlineData("assets\\shared\\materials\\flat_normal.dds", true)]
        [InlineData("ASSETS/Characters/Ahri/blank.tex", true)]
        [InlineData("ASSETS/Characters/Ahri/alpha-mask.tex", true)]
        [InlineData("ASSETS/Characters/Ahri/Skins/Base/Ahri_TX_CM.tex", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void PlaceholderTexturesAreTheEngineStandIns(string path, bool expected) =>
            Assert.Equal(expected, MaterialSwitchSemantics.IsPlaceholder(path));

        [Fact]
        public void ReadBoolAcceptsBoolAndBitBoolOnly()
        {
            Assert.True(BinPropertyReader.ReadBool(new BinTreeBool(0u, true), false));
            Assert.True(BinPropertyReader.ReadBool(new BinTreeBitBool(0u, true), false));
            Assert.False(BinPropertyReader.ReadBool(new BinTreeBool(0u, false), true));
            Assert.True(BinPropertyReader.ReadBool(new BinTreeU32(0u, 1u), true));
            Assert.False(BinPropertyReader.ReadBool(null, false));
        }

        [Fact]
        public void SameReferencesComparesEntriesByReference()
        {
            object a = new(), b = new();
            var left = new Dictionary<string, object> { ["x"] = a };
            Assert.True(VfxPlaybackRuntime.SameReferences(left, left));
            Assert.True(VfxPlaybackRuntime.SameReferences(left, new Dictionary<string, object> { ["x"] = a }));
            Assert.False(VfxPlaybackRuntime.SameReferences(left, new Dictionary<string, object> { ["x"] = b }));
            Assert.False(VfxPlaybackRuntime.SameReferences(left, new Dictionary<string, object> { ["y"] = a }));
            Assert.False(VfxPlaybackRuntime.SameReferences(left, new Dictionary<string, object>()));
        }

        [Fact]
        public void PeakIsTheLargestOfZeroTheConstantAndEveryKey()
        {
            Assert.Equal(0d, VfxDurationCalculator.Peak(VfxCurveF.Const(-3f)));
            Assert.Equal(2d, VfxDurationCalculator.Peak(VfxCurveF.Const(2f)));
            Assert.Equal(4d, VfxDurationCalculator.Peak(new VfxCurveF(1f, new[] { 0f, 1f }, new[] { 0.5f, 4f })));
        }
    }
}
