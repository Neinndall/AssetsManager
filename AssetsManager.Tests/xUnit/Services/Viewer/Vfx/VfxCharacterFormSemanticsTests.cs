using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxCharacterFormSemanticsTests
    {
        private static VfxCharacterFormDefinition Form(uint show, uint hide) =>
            new(1, 0, "Variant", new[] { show }, new[] { hide }, null, null, null,
                new Dictionary<uint, uint>(), Array.Empty<VfxIdleEffectDefinition>(), false);

        [Fact]
        public void SwitchingFormsStartsFromSkinBaselineAndDoesNotAccumulatePreviousHides()
        {
            uint[] baseline = { 2, 9 };
            var alternate = VfxCharacterFormSemantics.HiddenSubmeshes(baseline, Form(2, 1));
            var original = VfxCharacterFormSemantics.HiddenSubmeshes(baseline, Form(1, 2));

            Assert.True(alternate.SetEquals(new uint[] { 1, 9 }));
            Assert.True(original.SetEquals(new uint[] { 2, 9 }));
            Assert.Equal(new uint[] { 2, 9 }, baseline);
        }

        [Fact]
        public void AnimationCuesStartFromSelectedFormAndManualVisibilityStillWins()
        {
            var baseline = VfxCharacterFormSemantics.HiddenSubmeshes(new uint[] { 2, 9 }, Form(2, 1));
            var timeline = VfxClipCueEvaluator.BuildVisibilityTimeline(
                new AnimationClipTimedCue[]
                {
                    new AnimationSubmeshVisibilityCue(1d, 2d, new uint[] { 9 }, Array.Empty<uint>())
                }, baseline);

            Assert.True(VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 0d).SetEquals(new uint[] { 1, 9 }));
            Assert.True(VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 1.5d).SetEquals(new uint[] { 1 }));
            Assert.True(VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 2.5d).SetEquals(new uint[] { 1, 9 }));
            Assert.True(VfxCharacterViewportSemantics.ResolveSubmeshVisibility(false, true, true));
        }

        [Fact]
        public void RestoringTexturesKeepsAuthoredMaterialsAndShaderPrograms()
        {
            ModelMaterialDefinition authored = ModelMaterialDefinition.TextureOnly("assassin");
            using var part = new ModelPart("Body_Assassin", new GeometryModel3D())
            {
                MaterialDefinition = authored,
                SelectedTextureName = "base"
            };

            VfxCharacterFormSemantics.RestoreAuthoredTextures(new[] { part });

            Assert.Equal("assassin", part.SelectedTextureName);
            Assert.Same(authored, part.MaterialDefinition);
        }

        [Theory]
        [InlineData("EquippedGearParametricUpdater", true)]
        [InlineData("MoveSpeedParametricUpdater", false)]
        public void ParserIdentifiesOnlyAuthoredGearUpdaters(string updaterClass, bool expected)
        {
            var clip = new BinTreeStruct(0, Fnv1a.HashLower("ParametricClipData"),
                new BinTreeProperty[]
                {
                    new BinTreeStruct(Fnv1a.HashLower("Updater"), Fnv1a.HashLower(updaterClass),
                        Array.Empty<BinTreeProperty>())
                });
            var map = new BinTreeMap(Fnv1a.HashLower("mClipDataMap"), BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 123), clip) });
            var graph = new BinTreeObject("Graphs/Test", "AnimationGraphData", new BinTreeProperty[] { map });
            var tree = new BinTree(new[] { graph }, Array.Empty<string>());

            AnimationClipDefinition parsed = Assert.Single(Assert.Single(
                VfxGraphParser.ParseDocument(tree).AnimationGraphs).Clips);

            Assert.Equal(expected, parsed.UsesEquippedGearParameter);
        }
    }
}
