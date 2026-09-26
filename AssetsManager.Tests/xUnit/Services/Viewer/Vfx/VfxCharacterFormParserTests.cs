using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxCharacterFormParserTests
    {
        private static readonly uint GearLinksField = Fnv1a.HashLower("mGearSkinUpgrades");
        private static readonly uint GearDataField = Fnv1a.HashLower("mGearData");
        private static readonly uint GearDataClass = Fnv1a.HashLower("GearData");
        private static readonly uint ShowField = Fnv1a.HashLower("mCharacterSubmeshesToShow");
        private static readonly uint HideField = Fnv1a.HashLower("mCharacterSubmeshesToHide");

        [Fact]
        public void ResolvesEmbeddedUpgradeDataAndKeepsAuthoredIndicesAcrossMissingReferences()
        {
            uint firstUpgradeHash = Fnv1a.HashLower("Characters/Test/Skins/Skin0/Gear/First");
            uint missingUpgradeHash = Fnv1a.HashLower("Characters/Test/Skins/Skin0/Gear/Missing");
            uint thirdUpgradeHash = Fnv1a.HashLower("Characters/Test/Skins/Skin0/Gear/Third");
            uint visibleHash = Fnv1a.HashLower("Body_Slayer");
            uint hiddenHash = Fnv1a.HashLower("Body_Base");
            const uint resourceKey = 0x11223344;

            var idleEffect = new BinTreeEmbedded(
                0,
                Fnv1a.HashLower("SkinCharacterDataProperties_CharacterIdleEffect"),
                new BinTreeProperty[]
                {
                    new BinTreeHash(Fnv1a.HashLower("effectKey"), 0x55667788),
                    new BinTreeString(Fnv1a.HashLower("effectName"), "SlayerIdle"),
                    new BinTreeString(Fnv1a.HashLower("boneName"), "R_Hand"),
                    new BinTreeString(Fnv1a.HashLower("targetBoneName"), "Head"),
                });
            var resolver = new BinTreeStruct(
                Fnv1a.HashLower("mVFXResourceResolver"),
                Fnv1a.HashLower("ResourceResolver"),
                new BinTreeProperty[]
                {
                    new BinTreeMap(
                        Fnv1a.HashLower("resourceMap"),
                        BinPropertyType.Hash,
                        BinPropertyType.ObjectLink,
                        new[]
                        {
                            new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                                new BinTreeHash(0, resourceKey),
                                new BinTreeObjectLink(0, 0))
                        })
                });
            var skinMesh = new BinTreeEmbedded(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("simpleSkin"), "Characters/Test/skin0.skn"),
                    new BinTreeString(Fnv1a.HashLower("skeleton"), "Characters/Test/skeleton.skl"),
                    new BinTreeString(Fnv1a.HashLower("materialOverride"), "Materials/Slayer"),
                });
            BinTree primary = MakePrimary(
                new[] { firstUpgradeHash, missingUpgradeHash, thirdUpgradeHash },
                MakeUpgrade(firstUpgradeHash, GearData(
                    show: new[] { visibleHash },
                    hide: new[] { hiddenHash },
                    extra: new BinTreeProperty[]
                    {
                        resolver,
                        skinMesh,
                        new BinTreeString(Fnv1a.HashLower("mEquipAnimation"), "Animations/Equip.anm"),
                        new BinTreeContainer(
                            Fnv1a.HashLower("OverrideIdleEffects"),
                            BinPropertyType.Embedded,
                            new BinTreeProperty[] { idleEffect }),
                        new BinTreeBool(Fnv1a.HashLower("EnableOverrideIdleEffects"), true),
                    })),
                MakeUpgrade(thirdUpgradeHash, GearData(show: new[] { Fnv1a.HashLower("Body_Assassin") })));

            VfxCharacterFormDocumentData parsed = VfxCharacterFormParser.ParseDocument(primary);
            IReadOnlyList<VfxCharacterFormDefinition> forms = VfxCharacterFormParser.Resolve(
                new[] { parsed },
                hash => hash == firstUpgradeHash ? "Characters/Test/Gear_First.bin" : null);

            Assert.Equal(2, forms.Count);
            VfxCharacterFormDefinition first = forms[0];
            Assert.Equal(firstUpgradeHash, first.PathHash);
            Assert.Equal(0, first.GearIndex);
            Assert.Equal("Gear_First", first.Name);
            Assert.Contains(visibleHash, first.ShowSubmeshHashes);
            Assert.Contains(hiddenHash, first.HideSubmeshHashes);
            Assert.Equal("Characters/Test/skin0.skn", first.MeshPath);
            Assert.Equal("Characters/Test/skeleton.skl", first.SkeletonPath);
            Assert.Equal("Animations/Equip.anm", first.EquipAnimation);
            Assert.True(first.HasMaterialOverrides);
            Assert.True(first.EnableOverrideIdleEffects);
            Assert.Equal("SlayerIdle", Assert.Single(first.OverrideIdleEffects).EffectName);
            Assert.True(first.ResourceMap.ContainsKey(resourceKey));
            Assert.Equal(0u, first.ResourceMap[resourceKey]);

            Assert.Equal(thirdUpgradeHash, forms[1].PathHash);
            Assert.Equal(2, forms[1].GearIndex);
            Assert.Equal("Form 3", forms[1].Name);
        }

        [Fact]
        public void DependencyDefinitionsFillPrimaryLinksButCannotAddOrOverrideForms()
        {
            uint primaryUpgradeHash = Fnv1a.HashLower("Characters/Test/Gear/Primary");
            uint dependencyUpgradeHash = Fnv1a.HashLower("Characters/Test/Gear/Dependency");
            uint unrelatedUpgradeHash = Fnv1a.HashLower("Characters/Other/Gear/Unrelated");
            uint primaryShowHash = Fnv1a.HashLower("Body_Primary");
            uint duplicateDependencyShowHash = Fnv1a.HashLower("Body_Duplicate");
            uint dependencyShowHash = Fnv1a.HashLower("Body_Dependency");

            BinTree primary = MakePrimary(
                new[] { primaryUpgradeHash, dependencyUpgradeHash },
                MakeUpgrade(primaryUpgradeHash, GearData(show: new[] { primaryShowHash })));
            BinTree dependency = MakeTree(
                MakeUpgrade(primaryUpgradeHash, GearData(show: new[] { duplicateDependencyShowHash })),
                MakeUpgrade(dependencyUpgradeHash, GearData(show: new[] { dependencyShowHash })),
                MakeUpgrade(unrelatedUpgradeHash, GearData(show: new[] { Fnv1a.HashLower("Body_Unrelated") })));

            IReadOnlyList<VfxCharacterFormDefinition> forms = VfxCharacterFormParser.Resolve(
                new[]
                {
                    VfxCharacterFormParser.ParseDocument(primary),
                    VfxCharacterFormParser.ParseDocument(dependency),
                },
                null);

            Assert.Equal(2, forms.Count);
            Assert.Equal(new[] { primaryShowHash }, forms[0].ShowSubmeshHashes);
            Assert.Equal(new[] { dependencyShowHash }, forms[1].ShowSubmeshHashes);
            Assert.DoesNotContain(forms, form => form.PathHash == unrelatedUpgradeHash);
        }

        [Fact]
        public void PlaybackViewOverlaysGearMapAndReplacesIdleEffectsWithoutMutatingSourceBundle()
        {
            const uint key = 0x10203040;
            var baselineIdle = new VfxIdleEffectDefinition(1, "Base", "", 0, "", 0, default);
            var formIdle = new VfxIdleEffectDefinition(2, "Form", "", 0, "", 0, default);
            var source = new VfxLoadingService.Bundle();
            source.ResourceMap[key] = 0x11111111;
            source.IdleEffects.Add(baselineIdle);
            var form = new VfxCharacterFormDefinition(
                0x22222222,
                3,
                "Form",
                Array.Empty<uint>(),
                Array.Empty<uint>(),
                ResourceMap: new Dictionary<uint, uint> { [key] = 0 },
                OverrideIdleEffects: new[] { formIdle },
                EnableOverrideIdleEffects: true);

            VfxLoadingService.Bundle view = source.CreateCharacterPlaybackView(form);

            Assert.Same(source.Systems, view.Systems);
            Assert.NotSame(source.ResourceMap, view.ResourceMap);
            Assert.Equal(0u, view.ResourceMap[key]);
            Assert.Equal(0x11111111u, source.ResourceMap[key]);
            Assert.Equal("Form", Assert.Single(view.IdleEffects).EffectName);
            Assert.Equal("Base", Assert.Single(source.IdleEffects).EffectName);
            Assert.Equal(3, view.CharacterGearIndex);
        }

        private static BinTree MakePrimary(
            IReadOnlyList<uint> gearUpgradeHashes,
            params BinTreeObject[] gearUpgrades)
        {
            BinTreeProperty[] links = gearUpgradeHashes
                .Select(hash => (BinTreeProperty)new BinTreeObjectLink(0, hash))
                .ToArray();
            BinTreeObject skin = new(
                "Characters/Test/Skins/Skin0",
                "SkinCharacterDataProperties",
                new BinTreeProperty[]
                {
                    new BinTreeEmbedded(
                        Fnv1a.HashLower("skinUpgradeData"),
                        Fnv1a.HashLower("SkinUpgradeData"),
                        new BinTreeProperty[]
                        {
                            new BinTreeContainer(GearLinksField, BinPropertyType.ObjectLink, links),
                        }),
                });
            return MakeTree(new[] { skin }.Concat(gearUpgrades).ToArray());
        }

        private static BinTreeObject MakeUpgrade(uint pathHash, BinTreeStruct gearData)
            => new(
                pathHash,
                Fnv1a.HashLower("GearSkinUpgrade"),
                new BinTreeProperty[] { gearData });

        private static BinTreeStruct GearData(
            IEnumerable<uint> show = null,
            IEnumerable<uint> hide = null,
            IEnumerable<BinTreeProperty> extra = null)
        {
            var properties = new List<BinTreeProperty>();
            if (show != null)
                properties.Add(HashList(ShowField, show));
            if (hide != null)
                properties.Add(HashList(HideField, hide));
            if (extra != null)
                properties.AddRange(extra);
            return new BinTreeStruct(GearDataField, GearDataClass, properties);
        }

        private static BinTreeContainer HashList(uint fieldHash, IEnumerable<uint> hashes)
            => new(
                fieldHash,
                BinPropertyType.Hash,
                hashes.Select(hash => (BinTreeProperty)new BinTreeHash(0, hash)).ToArray());

        private static BinTree MakeTree(params BinTreeObject[] objects)
            => new(objects, Array.Empty<string>());
    }
}