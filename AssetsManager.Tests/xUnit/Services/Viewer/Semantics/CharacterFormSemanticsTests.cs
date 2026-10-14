using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Semantics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Semantics
{
    public sealed class CharacterFormSemanticsTests
    {
        private static CharacterFormDefinition Form(uint show, uint hide) =>
            new(1, 0, "Variant", new[] { show }, new[] { hide }, null, null, null,
                new Dictionary<uint, uint>(), Array.Empty<VfxIdleEffectDefinition>(), false);

        // Aatrox Skin33: a persistent condition on gear 1 shows Monster_Head (1) and hides Default_Head (2).
        [Fact]
        public void PersistentSubmeshConditionsSwapSubmeshesForTheirGear()
        {
            var conditions = new[]
            {
                new VfxSubmeshCondition(new GameMaterialBoolCondition(GameMaterialBoolKind.Gear, 1), new uint[] { 1 }, new uint[] { 2 })
            };
            var empowered = new CharacterFormDefinition(5, 1, "Empowered", Array.Empty<uint>(), Array.Empty<uint>());
            var sword = new CharacterFormDefinition(4, 0, "Sword", Array.Empty<uint>(), Array.Empty<uint>());

            Assert.True(CharacterFormSemantics.HiddenSubmeshes(new uint[] { 1 }, null, null, conditions).SetEquals(new uint[] { 1 }));
            Assert.True(CharacterFormSemantics.HiddenSubmeshes(new uint[] { 1 }, sword, null, conditions).SetEquals(new uint[] { 1 }));
            Assert.True(CharacterFormSemantics.HiddenSubmeshes(new uint[] { 1 }, empowered, null, conditions).SetEquals(new uint[] { 2 }));
        }

        [Fact]
        public void GearFormsChangingMeshSkeletonOrMaterialsReloadTheModel()
        {
            // Elementalist Lux (Skin07): each GearData points at another skin's SKN/SKL with material overrides.
            var owner = new VfxOwnerSceneContext(
                "ASSETS/Characters/Lux/Skins/Skin07/Lux_Skin07.skn", "ASSETS/Characters/Lux/Skins/Skin07/Lux_Skin07.skl", 1f);
            var otherMesh = new CharacterFormDataProjection(
                Array.Empty<uint>(), new uint[] { 3 },
                "ASSETS/Characters/LuxFire/Skins/Skin07/Lux_Skin07_Fire.skn",
                "ASSETS/Characters/LuxFire/Skins/Skin07/Lux_Skin07_Fire.skl",
                null, new Dictionary<uint, uint>(), Array.Empty<VfxIdleEffectDefinition>(), false, true,
                new uint[] { 7 }, 1.2f);
            var materialsOnly = otherMesh with { SkinScale = null, MeshPath = owner.MeshPath, SkeletonPath = owner.SkeletonPath };
            var skeletonOnly = materialsOnly with { HasMaterialOverrides = false, SkeletonPath = "ASSETS/Other.skl" };
            var plain = skeletonOnly with { SkeletonPath = owner.SkeletonPath };
            var document = new CharacterFormDocumentData(
                new uint?[] { 10, 20, 30, 40 },
                new Dictionary<uint, CharacterFormDataProjection>
                {
                    [10] = otherMesh, [20] = materialsOnly, [30] = skeletonOnly, [40] = plain
                });

            var forms = CharacterFormParser.Resolve(new[] { document }, null, owner);

            Assert.Equal(new[] { true, true, true, false }, forms.Select(form => form.ReloadsModel));
            Assert.Equal("Fire", forms[0].Name);
            // GearData skinScale is kept only when authored, so an absent one falls back to the skin's.
            Assert.Equal(1.2f, forms[0].SkinScale);
            Assert.Null(forms[1].SkinScale);
            Assert.Equal(forms, CharacterFormSemantics.CompatibleForms(forms, owner));
            // A reloading form starts from its own initialSubmeshToHide, then its GearData hide list applies.
            Assert.True(CharacterFormSemantics.HiddenSubmeshes(new uint[] { 2, 9 }, forms[0]).SetEquals(new uint[] { 3, 7 }));
            Assert.True(CharacterFormSemantics.HiddenSubmeshes(new uint[] { 2, 9 }, null).SetEquals(new uint[] { 2, 9 }));
        }

        [Fact]
        public void PlaybackViewAnchorsEffectsAtTheReloadedFormsSkinScale()
        {
            var bundle = new VfxLoadingService.Bundle
            {
                OwnerSceneContext = new VfxOwnerSceneContext("ASSETS/Test/Base.skn", "ASSETS/Test/Base.skl", 1f)
            };
            var reloading = CharacterFormDefinition.CreateBase() with
            {
                PathHash = 1, GearIndex = 0, ReloadsModel = true, SkinScale = 1.3f
            };

            Assert.Equal(1.3f, bundle.CreateCharacterPlaybackView(reloading).OwnerSceneContext.SkinScale);
            Assert.Equal(1f, bundle.CreateCharacterPlaybackView(reloading with { ReloadsModel = false }).OwnerSceneContext.SkinScale);
            Assert.Equal(1f, bundle.CreateCharacterPlaybackView(reloading with { SkinScale = null }).OwnerSceneContext.SkinScale);
        }

        [Fact]
        public void SwitchingFormsStartsFromSkinBaselineAndDoesNotAccumulatePreviousHides()
        {
            uint[] baseline = { 2, 9 };
            var alternate = CharacterFormSemantics.HiddenSubmeshes(baseline, Form(2, 1));
            var original = CharacterFormSemantics.HiddenSubmeshes(baseline, Form(1, 2));

            Assert.True(alternate.SetEquals(new uint[] { 1, 9 }));
            Assert.True(original.SetEquals(new uint[] { 2, 9 }));
            Assert.Equal(new uint[] { 2, 9 }, baseline);
        }

        [Fact]
        public void AnimationCuesStartFromSelectedFormAndManualVisibilityStillWins()
        {
            var baseline = CharacterFormSemantics.HiddenSubmeshes(new uint[] { 2, 9 }, Form(2, 1));
            var timeline = VfxClipCueEvaluator.BuildVisibilityTimeline(
                new AnimationClipTimedCue[]
                {
                    new AnimationSubmeshVisibilityCue(1d, 2d, new uint[] { 9 }, Array.Empty<uint>())
                }, baseline);

            Assert.True(VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 0d).SetEquals(new uint[] { 1, 9 }));
            Assert.True(VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 1.5d).SetEquals(new uint[] { 1 }));
            Assert.True(VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 2.5d).SetEquals(new uint[] { 1, 9 }));
            Assert.True(CharacterViewportSemantics.ResolveSubmeshVisibility(false, true, true));
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

            CharacterFormSemantics.RestoreAuthoredTextures(new[] { part });

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
            string actual = CharacterFormSemantics.ExtractFormTokenFromTexturePath(path);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void BaseFormDefinition_HasExpectedDefaults()
        {
            var baseDef = CharacterFormDefinition.CreateBase();
            Assert.True(baseDef.IsBase);
            Assert.Equal(-1, baseDef.GearIndex);
            Assert.Equal("Base", baseDef.Name);

            Assert.Equal("Base", CharacterFormSemantics.FormLabel(baseDef, null));
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
            var darkForm = new CharacterFormDefinition(
                PathHash: 0x918a0e13,
                GearIndex: 0,
                Name: "Dark",
                ShowSubmeshHashes: Array.Empty<uint>(),
                HideSubmeshHashes: Array.Empty<uint>());

            var darkHidden = CharacterFormSemantics.HiddenSubmeshes(initialHidden, darkForm, parts);

            // Dark and SnakeDark should be unhidden
            Assert.DoesNotContain(Fnv1a.HashLower("Dark"), darkHidden);
            Assert.DoesNotContain(Fnv1a.HashLower("SnakeDark"), darkHidden);
            // Base and SnakeBase should be hidden
            Assert.Contains(Fnv1a.HashLower("Base"), darkHidden);
            Assert.Contains(Fnv1a.HashLower("SnakeBase"), darkHidden);
            // Gold should remain hidden
            Assert.Contains(Fnv1a.HashLower("Gold"), darkHidden);

            // Form 2: Gold (GearIndex 1, empty authored lists)
            var goldForm = new CharacterFormDefinition(
                PathHash: 0x928a0fa6,
                GearIndex: 1,
                Name: "Gold",
                ShowSubmeshHashes: Array.Empty<uint>(),
                HideSubmeshHashes: Array.Empty<uint>());

            var goldHidden = CharacterFormSemantics.HiddenSubmeshes(initialHidden, goldForm, parts);

            // Gold, Emblem, and Envelopes should be unhidden
            Assert.DoesNotContain(Fnv1a.HashLower("Gold"), goldHidden);
            Assert.DoesNotContain(Fnv1a.HashLower("Emblem"), goldHidden);
            Assert.DoesNotContain(Fnv1a.HashLower("Envelopes"), goldHidden);
            // Base, Dark, and SnakeDark should be hidden
            Assert.Contains(Fnv1a.HashLower("Base"), goldHidden);
            Assert.Contains(Fnv1a.HashLower("Dark"), goldHidden);
            Assert.Contains(Fnv1a.HashLower("SnakeDark"), goldHidden);

            // Base Form: returns exactly initialHidden
            var baseForm = CharacterFormDefinition.CreateBase("Base");
            var baseHidden = CharacterFormSemantics.HiddenSubmeshes(initialHidden, baseForm, parts);
            Assert.True(baseHidden.SetEquals(initialHidden));
        }

        [Theory]
        [InlineData("Base", false)]
        [InlineData("Form 1", false)]
        [InlineData("Base", true)]
        [InlineData("Form 1", true)]
        public void KaynFormOptionsKeepAuthoredBaseGearWithoutAddingAnotherBase(string baseName, bool legacyMaterials)
        {
            using var model = new SceneModel();
            string[] names = { "Base", "Assassin", "Slayer" };
            string MaterialName(string name) => legacyMaterials
                ? name == "Base" ? "Kayn_Base_Mat" : $"Kayn_Base_{name}_Mat"
                : "Body_" + name;
            foreach (string name in names)
                model.AddPart(new ModelPart(MaterialName(name), new GeometryModel3D()));
            uint[] hashes = names.Select(name => Fnv1a.HashLower(MaterialName(name))).ToArray();
            var forms = names.Select((name, index) => new CharacterFormDefinition(
                (uint)(10 + index), index, index == 0 ? baseName : $"Form {index + 1}",
                new[] { hashes[index] }, hashes.Where(hash => hash != hashes[index]).ToArray(),
                ResourceMap: new Dictionary<uint, uint> { [1] = (uint)(20 + index) })).ToArray();

            var options = CharacterFormSemantics.FormOptions(forms, null, model);

            Assert.Equal(names, options.Select(option => option.Label));
            Assert.Equal(forms, options.Select(option => option.Definition));
            Assert.Same(forms[0], options[0].Definition);
            Assert.False(options[0].Definition.IsBase);
            Assert.True(CharacterFormSemantics.HiddenSubmeshes(hashes, options[0].Definition)
                .SetEquals(hashes.Skip(1)));
            var playback = new VfxLoadingService.Bundle().CreateCharacterPlaybackView(options[0].Definition);
            Assert.Equal(0, playback.CharacterGearIndex);
            Assert.Equal(20u, playback.ResourceMap[1]);
        }

        [Fact]
        public void SettFormOptionsAddBaseBeforeDarkAndGoldWithoutRenumberingGear()
        {
            using var model = new SceneModel();
            var forms = new[]
            {
                new CharacterFormDefinition(10, 0, "Form 1", Array.Empty<uint>(), Array.Empty<uint>()),
                new CharacterFormDefinition(20, 1, "Form 2", Array.Empty<uint>(), Array.Empty<uint>())
            };
            model.AddPart(new ModelPart("Base", new GeometryModel3D())
            {
                MaterialDefinition = ModelMaterialDefinition.TextureOnly("sett_skin66_base_tx_cm") with
                {
                    TextureSwaps = new[]
                    {
                        new GameMaterialTextureSwap("Diffuse_Texture", new[]
                        {
                            new GameMaterialTextureSwapOption("ASSETS/Characters/Sett/Skins/Skin66/sett_skin66_dark_tx_cm.tex",
                                new GameMaterialBoolCondition(GameMaterialBoolKind.Gear, 0)),
                            new GameMaterialTextureSwapOption("ASSETS/Characters/Sett/Skins/Skin66/sett_skin66_gold_tx_cm.tex",
                                new GameMaterialBoolCondition(GameMaterialBoolKind.Gear, 1))
                        })
                    }
                }
            });

            var options = CharacterFormSemantics.FormOptions(forms, null, model);

            Assert.Equal(new[] { "Base", "Dark", "Gold" }, options.Select(option => option.Label));
            Assert.True(options[0].Definition.IsBase);
            Assert.Equal(new[] { -1, 0, 1 }, options.Select(option => option.Definition.GearIndex));
            Assert.Equal(forms, options.Skip(1).Select(option => option.Definition));
        }

        [Theory]
        [InlineData("Example_Base_Mat", "Base")]
        [InlineData("Example_Base_Dark_Mat", "Dark")]
        [InlineData("Example_Base_Gold_Mat", "Gold")]
        [InlineData("Example_Baseball_Mat", "Form 1")]
        [InlineData("Example_Hair_Mat", "Form 1")]
        public void FormLabelsUseShownBodyTokensWithoutDependingOnCharacterName(string materialName, string expected)
        {
            using var model = new SceneModel();
            model.AddPart(new ModelPart(materialName, new GeometryModel3D()));
            var form = new CharacterFormDefinition(1, 0, "Form 1",
                new[] { Fnv1a.HashLower(materialName) }, Array.Empty<uint>());

            Assert.Equal(expected, CharacterFormSemantics.FormLabel(form, model));
        }

        [Fact]
        public void IncompatibleFormsDoNotCreateAnEmptyBaseSelector()
        {
            var owner = new VfxOwnerSceneContext("ASSETS/Base.skn", "ASSETS/Base.skl", 1f);
            var incompatible = new CharacterFormDefinition(1, 0, "Base", Array.Empty<uint>(),
                Array.Empty<uint>(), MeshPath: "ASSETS/Other.skn");

            Assert.Empty(CharacterFormSemantics.FormOptions(new[] { incompatible }, owner, null));
            Assert.Empty(CharacterFormSemantics.FormOptions(null, owner, null));
        }
    }
}
