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
        public void GearFormsAuthoredOnAnotherSkinModelAreCompatibleModelSwaps()
        {
            // Elementalist Lux (Skin07): each GearData points at another skin's SKN/SKL with material overrides.
            var owner = new VfxOwnerSceneContext("ASSETS/Characters/Lux/Skins/Skin07/Lux_Skin07.skn", "", 1f);
            var projection = new VfxCharacterFormDataProjection(
                Array.Empty<uint>(), Array.Empty<uint>(),
                "ASSETS/Characters/LuxFire/Skins/Skin07/Lux_Skin07_Fire.skn",
                "ASSETS/Characters/LuxFire/Skins/Skin07/Lux_Skin07_Fire.skl",
                null, new Dictionary<uint, uint>(), Array.Empty<VfxIdleEffectDefinition>(), false, true);
            var sameModel = projection with { MeshPath = owner.MeshPath };
            var document = new VfxCharacterFormDocumentData(
                new uint?[] { 10, 20 },
                new Dictionary<uint, VfxCharacterFormDataProjection> { [10] = sameModel, [20] = projection });

            var forms = VfxCharacterFormParser.Resolve(new[] { document }, null, owner);

            Assert.False(forms[0].IsModelSwap);
            Assert.True(forms[1].IsModelSwap);
            Assert.Equal("Fire", forms[1].Name);
            Assert.Equal(-1, forms[1].EquippedGearIndex);
            Assert.Equal(new[] { forms[1] }, VfxCharacterFormSemantics.CompatibleForms(forms, owner));
        }

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

        [Theory]
        [InlineData("ASSETS/Characters/Sett/Skins/Skin66/sett_skin66_dark_tx_cm.tex", "Dark")]
        [InlineData("ASSETS/Characters/Sett/Skins/Skin66/sett_skin66_gold_tx_cm.tex", "Gold")]
        [InlineData("ASSETS/Characters/Sett/Skins/Skin38/sett_skin38_akana_tx_cm.tex", "Akana")]
        [InlineData("ASSETS/Characters/Sett/Skins/Skin38/sett_skin38_kanmei_tx_cm.tex", "Kanmei")]
        [InlineData("ASSETS/Characters/Lux/Skins/Skin07/lux_skin07_fire_tx_cm.tex", "Fire")]
        public void ExtractFormTokenFromTexturePath_ReturnsExpectedFormName(string path, string expected)
        {
            string actual = VfxCharacterFormOption.ExtractFormTokenFromTexturePath(path);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void BaseFormDefinition_HasExpectedDefaults()
        {
            var baseDef = VfxCharacterFormDefinition.CreateBase();
            Assert.True(baseDef.IsBase);
            Assert.Equal(-1, baseDef.GearIndex);
            Assert.Equal("Base", baseDef.Name);

            var option = new VfxCharacterFormOption(baseDef, null);
            Assert.Equal("Base", option.Label);
        }

        [Fact]
        public void Sett66SubmeshInference_UnhidesFormSubmeshesAndHidesBase()
        {
            // Sett 66 geometry parts: Persistent, SnakeBase, Base, Dark, SnakeDark, Emblem, Gold, Envelopes, SnakeW
            uint[] initialHidden =
            {
                Fnv1a.HashLower("Dark"),
                Fnv1a.HashLower("SnakeDark"),
                Fnv1a.HashLower("Gold"),
                Fnv1a.HashLower("Emblem"),
                Fnv1a.HashLower("SnakeW"),
                Fnv1a.HashLower("Envelopes")
            };

            var partNames = new[] { "Persistent", "SnakeBase", "Base", "Dark", "SnakeDark", "Emblem", "Gold", "Envelopes", "SnakeW" };
            var parts = partNames.Select(name => new ModelPart(name, new GeometryModel3D())).ToArray();

            // Form 1: Dark (GearIndex 0, empty authored lists)
            var darkForm = new VfxCharacterFormDefinition(
                PathHash: 0x918a0e13,
                GearIndex: 0,
                Name: "Dark",
                ShowSubmeshHashes: Array.Empty<uint>(),
                HideSubmeshHashes: Array.Empty<uint>());

            var darkHidden = VfxCharacterFormSemantics.HiddenSubmeshes(initialHidden, darkForm, parts);

            // Dark and SnakeDark should be unhidden
            Assert.DoesNotContain(Fnv1a.HashLower("Dark"), darkHidden);
            Assert.DoesNotContain(Fnv1a.HashLower("SnakeDark"), darkHidden);
            // Base and SnakeBase should be hidden
            Assert.Contains(Fnv1a.HashLower("Base"), darkHidden);
            Assert.Contains(Fnv1a.HashLower("SnakeBase"), darkHidden);
            // Gold should remain hidden
            Assert.Contains(Fnv1a.HashLower("Gold"), darkHidden);

            // Form 2: Gold (GearIndex 1, empty authored lists)
            var goldForm = new VfxCharacterFormDefinition(
                PathHash: 0x928a0fa6,
                GearIndex: 1,
                Name: "Gold",
                ShowSubmeshHashes: Array.Empty<uint>(),
                HideSubmeshHashes: Array.Empty<uint>());

            var goldHidden = VfxCharacterFormSemantics.HiddenSubmeshes(initialHidden, goldForm, parts);

            // Gold, Emblem, and Envelopes should be unhidden
            Assert.DoesNotContain(Fnv1a.HashLower("Gold"), goldHidden);
            Assert.DoesNotContain(Fnv1a.HashLower("Emblem"), goldHidden);
            Assert.DoesNotContain(Fnv1a.HashLower("Envelopes"), goldHidden);
            // Base, Dark, and SnakeDark should be hidden
            Assert.Contains(Fnv1a.HashLower("Base"), goldHidden);
            Assert.Contains(Fnv1a.HashLower("Dark"), goldHidden);
            Assert.Contains(Fnv1a.HashLower("SnakeDark"), goldHidden);

            // Base Form: returns exactly initialHidden
            var baseForm = VfxCharacterFormDefinition.CreateBase("Base");
            var baseHidden = VfxCharacterFormSemantics.HiddenSubmeshes(initialHidden, baseForm, parts);
            Assert.True(baseHidden.SetEquals(initialHidden));
        }
    }
}
