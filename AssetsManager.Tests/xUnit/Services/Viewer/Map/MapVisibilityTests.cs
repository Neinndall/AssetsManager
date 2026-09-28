using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    /// <summary>
    /// Map visibility contract modelled on Summoner's Rift base_srx: rift transformations on the
    /// primary domain, Baron pit on the secondary one and NOR/OR child controllers combining them.
    /// </summary>
    public class MapVisibilityTests
    {
        private const uint Mountain = 0x48106271;
        private const uint Infernal = 0xd1a17399;
        private const uint BaronBase = 0xf4968631;
        private const uint BaronTunnel = 0x1b3399aa;
        private const uint BaronUpgraded = 0x68fe7644;
        private const uint NotMountain = 0x0c210e40;
        private const uint NoTransformation = 0x5e652742;
        private const uint BaseOrUpgraded = 0xa7b30f31;
        private const uint HallOfLegends = 0x76c50391;
        private const uint Lily = 0xca63c5fe;

        [Fact]
        public void ParserReadsEveryControllerKindFromTheMaterialsBin()
        {
            IReadOnlyDictionary<uint, MapVisibilityControllerData> controllers =
                new MapVisibilityParser().ParseControllers(MaterialsTree());

            Assert.Equal(MapVisibilityControllerKind.PrimaryFlags, controllers[Mountain].Kind);
            Assert.Equal(4, controllers[Mountain].Mask);
            Assert.Equal(MapVisibilityControllerKind.SecondaryFlags, controllers[BaronTunnel].Kind);
            Assert.Equal(4, controllers[BaronTunnel].Mask);
            Assert.Equal(MapVisibilityControllerKind.Mutator, controllers[HallOfLegends].Kind);
            Assert.Equal("SR_Hall_Of_Legends", controllers[HallOfLegends].MutatorName);
            Assert.Equal(MapVisibilityParentMode.None, controllers[NoTransformation].ParentMode);
            Assert.Equal(new[] { Mountain, Infernal }, controllers[NoTransformation].Parents);
            Assert.Equal(MapVisibilityParentMode.Any, controllers[BaseOrUpgraded].ParentMode);
            Assert.Equal(MapVisibilityControllerKind.Named, controllers[Lily].Kind);
            Assert.True(controllers[Lily].DefaultVisible);
        }

        [Fact]
        public void ParserReadsBothDomainsAndInitialMasksFromTheMapObject()
        {
            MapVisibilityDefinitions definitions = new MapVisibilityParser().ParseDefinitions(MapTree());

            Assert.Equal(1, definitions.Primary.InitialMask);
            Assert.Equal(new[] { "Base", "Infernal", "Mountain" }, definitions.Primary.Flags.Select(flag => flag.Label));
            Assert.Equal(1, definitions.Primary.MinIndex);
            Assert.Equal(2, definitions.Primary.MaxIndex);
            Assert.Equal(1, definitions.Secondary.InitialMask);
            Assert.Equal(new[] { 1, 4, 8 }, definitions.Secondary.Flags.Select(flag => flag.Flag));
            Assert.Equal("Tunnel", definitions.Secondary.Find(2).Label);
        }

        [Fact]
        public void TransformationKeepsTheBaseBitAndSwapsPiecesThroughControllers()
        {
            MapSceneVisibility visibility = Visibility();
            MapVisibilityState opening = visibility.Opening;
            MapVisibilityState mountain = opening.WithFlags(
                MapVisibilitySemantics.TransformationFlags(visibility.Definitions.Primary, 2));

            Assert.Equal(0x05, mountain.Flags);
            // Base kit without controller stays in every transformation.
            Assert.True(MapVisibilitySemantics.IsVisible(visibility, mountain, 0x01, null));
            // Base dragon pit (NOR of every transformation) and base Krug (not Mountain) leave.
            Assert.True(MapVisibilitySemantics.IsVisible(visibility, opening, 0x01, NoTransformation));
            Assert.False(MapVisibilitySemantics.IsVisible(visibility, mountain, 0x01, NoTransformation));
            Assert.False(MapVisibilitySemantics.IsVisible(visibility, mountain, 0x01, NotMountain));
            // Mountain pieces enter, whether authored at 0x04 or at 0xff.
            Assert.False(MapVisibilitySemantics.IsVisible(visibility, opening, 0xff, Mountain));
            Assert.True(MapVisibilitySemantics.IsVisible(visibility, mountain, 0x04, Mountain));
            Assert.True(MapVisibilitySemantics.IsVisible(visibility, mountain, 0xff, Mountain));
            Assert.False(MapVisibilitySemantics.IsVisible(visibility, mountain, 0x02, Infernal));
        }

        [Fact]
        public void BaronPitStatesReplaceEachOtherInsteadOfStacking()
        {
            MapSceneVisibility visibility = Visibility();
            MapVisibilityState tunnel = visibility.Opening.WithSecondaryFlags(4);
            MapVisibilityState upgraded = visibility.Opening.WithSecondaryFlags(8);

            Assert.True(MapVisibilitySemantics.IsVisible(visibility, visibility.Opening, 0x01, BaronBase));
            Assert.False(MapVisibilitySemantics.IsVisible(visibility, visibility.Opening, 0xff, BaronTunnel));
            Assert.False(MapVisibilitySemantics.IsVisible(visibility, tunnel, 0x01, BaronBase));
            Assert.True(MapVisibilitySemantics.IsVisible(visibility, tunnel, 0xff, BaronTunnel));
            Assert.False(MapVisibilitySemantics.IsVisible(visibility, tunnel, 0xff, BaronUpgraded));
            // Pieces shared by base and upgraded pits (ParentMode Any).
            Assert.True(MapVisibilitySemantics.IsVisible(visibility, upgraded, 0xff, BaseOrUpgraded));
            Assert.False(MapVisibilitySemantics.IsVisible(visibility, tunnel, 0xff, BaseOrUpgraded));
        }

        [Fact]
        public void MutatorsAreOffUntilAppliedAndMatchWithoutCase()
        {
            MapSceneVisibility visibility = Visibility();

            Assert.False(MapVisibilitySemantics.IsVisible(visibility, visibility.Opening, 0xff, HallOfLegends));
            Assert.True(MapVisibilitySemantics.IsVisible(
                visibility,
                visibility.Opening.WithMutator("sr_hall_of_legends", true),
                0xff,
                HallOfLegends));
            Assert.Equal(new[] { "SR_Hall_Of_Legends" }, visibility.MutatorNames);
        }

        [Fact]
        public void UnknownAndCyclicControllersDoNotHangOrHide()
        {
            var controllers = new Dictionary<uint, MapVisibilityControllerData>
            {
                [1] = new(1, MapVisibilityControllerKind.Child, Parents: new uint[] { 2 }),
                [2] = new(2, MapVisibilityControllerKind.Child, Parents: new uint[] { 1 })
            };
            var visibility = new MapSceneVisibility(MapVisibilityDefinitions.Empty, controllers, MapVisibilityState.FromFlags(1));

            Assert.True(MapVisibilitySemantics.IsVisible(visibility, visibility.Opening, 0x01, 0xdeadbeef));
            Assert.True(MapVisibilitySemantics.IsVisible(visibility, visibility.Opening, 0x01, 1));
            Assert.False(MapVisibilitySemantics.IsVisible(visibility, visibility.Opening, 0x02, null));
        }

        [Fact]
        public void OpeningUsesTheMapInitialMaskAndFallsBackToGeometryHeuristic()
        {
            MapVisibilityDefinitions definitions = new MapVisibilityParser().ParseDefinitions(MapTree());

            Assert.Equal(new MapVisibilityState(1, 1), MapVisibilitySemantics.Opening(definitions, 0x40));
            Assert.Equal(new MapVisibilityState(0x40, 1), MapVisibilitySemantics.Opening(MapVisibilityDefinitions.Empty, 0x40));
        }

        [Fact]
        public void StateEqualityIgnoresMutatorOrderAndCase()
        {
            var first = new MapVisibilityState(5, 2, new[] { "MSITrophy", "SR_Hall_Of_Legends" });
            var second = new MapVisibilityState(5, 2, new[] { "sr_hall_of_legends", "msitrophy" });

            Assert.Equal(first, second);
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
            Assert.NotEqual(first, second.WithSecondaryFlags(4));
        }

        [Fact]
        public void DrawnMeshesFollowMaskAndController()
        {
            MapSceneVisibility visibility = Visibility();
            MapGeometryData geometry = Geometry(
                Mesh(0x01, 0),
                Mesh(0x01, NoTransformation),
                Mesh(0x04, Mountain),
                Mesh(0xff, BaronTunnel));
            MapVisibilityState mountain = visibility.Opening.WithFlags(0x05);

            Assert.Equal(
                new[] { geometry.Meshes[0], geometry.Meshes[1] },
                MapGeometrySemantics.DrawnMeshesFor(geometry, visibility, visibility.Opening));
            Assert.Equal(
                new[] { geometry.Meshes[0], geometry.Meshes[2] },
                MapGeometrySemantics.DrawnMeshesFor(geometry, visibility, mountain));
        }

        [Fact]
        public void InspectorOffersNamedStatesAndRequestsTheComposedState()
        {
            MapSceneVisibility visibility = Visibility();
            var model = new VfxInspectorModel();
            var requests = new List<MapVisibilityState>();
            model.MapVisibilityRequested += requests.Add;
            model.SetMapVisibility(
                visibility,
                new[] { new MapGeometryLayerData(0, 10), new MapGeometryLayerData(2, 5) },
                visibility.Opening);

            Assert.Equal(new[] { "Base", "Infernal", "Mountain" }, model.MapTransformations.Select(option => option.Label));
            Assert.Equal(new[] { "Base", "Tunnel", "Upgraded" }, model.MapSecondaryStates.Select(option => option.Label));
            Assert.Equal("1 · Base", model.MapLayers[0].Label);
            Assert.Same(model.MapTransformations[0], model.SelectedMapTransformation);
            Assert.Empty(requests);

            model.SelectedMapTransformation = model.MapTransformations[2];
            model.SelectedMapSecondaryState = model.MapSecondaryStates[1];
            model.MapMutators.Single().IsEnabled = true;

            Assert.Equal(3, requests.Count);
            Assert.Equal(new MapVisibilityState(0x05, 4, new[] { "SR_Hall_Of_Legends" }), requests[^1]);
            Assert.True(model.MapLayers[1].IsEnabled);

            model.RequestMapLayerFlags(0x04);
            Assert.True(model.SelectedMapTransformation.IsCustom);
            model.SyncMapVisibility(visibility.Opening);
            Assert.Same(model.MapTransformations[0], model.SelectedMapTransformation);
            Assert.DoesNotContain(model.MapTransformations, option => option.IsCustom);
        }

        private static MapSceneVisibility Visibility()
        {
            var parser = new MapVisibilityParser();
            MapVisibilityDefinitions definitions = parser.ParseDefinitions(MapTree());
            return new MapSceneVisibility(
                definitions,
                parser.ParseControllers(MaterialsTree()),
                MapVisibilitySemantics.Opening(definitions, 1));
        }

        private static BinTree MaterialsTree() => new(new[]
        {
            FlagController(Mountain, MapVisibilityParser.PrimaryFlagsControllerClass, MapVisibilityParser.PrimaryFlagsField, 4),
            FlagController(Infernal, MapVisibilityParser.PrimaryFlagsControllerClass, MapVisibilityParser.PrimaryFlagsField, 2),
            FlagController(BaronBase, MapVisibilityParser.SecondaryFlagsControllerClass, MapVisibilityParser.SecondaryFlagsField, 1),
            FlagController(BaronTunnel, MapVisibilityParser.SecondaryFlagsControllerClass, MapVisibilityParser.SecondaryFlagsField, 4),
            FlagController(BaronUpgraded, MapVisibilityParser.SecondaryFlagsControllerClass, MapVisibilityParser.SecondaryFlagsField, 8),
            Child(NotMountain, 3, Mountain),
            Child(NoTransformation, 3, Mountain, Infernal),
            Child(BaseOrUpgraded, 1, BaronBase, BaronUpgraded),
            new BinTreeObject(HallOfLegends, MapVisibilityParser.MutatorControllerClass, new BinTreeProperty[]
            {
                new BinTreeHash(Fnv1a.HashLower("PathHash"), HallOfLegends),
                new BinTreeString(Fnv1a.HashLower("MutatorName"), "SR_Hall_Of_Legends")
            }),
            new BinTreeObject(Lily, MapVisibilityParser.NamedControllerClass, new BinTreeProperty[]
            {
                new BinTreeHash(Fnv1a.HashLower("name"), 0x21ceb04)
            })
        }, Array.Empty<string>());

        private static BinTree MapTree() => new(new[]
        {
            new BinTreeObject(0x8333105e, MapVariantParser.MapClass, new BinTreeProperty[]
            {
                new BinTreeU8(Fnv1a.HashLower("InitialVisibilityMask"), 1),
                Domain(Fnv1a.HashLower("VisibilityFlagDefines"), 1, 2,
                    Definition(0, null), Definition(1, "Infernal"), Definition(2, "Mountain")),
                new BinTreeU8(MapVisibilityParser.SecondaryInitialMaskField, 1),
                Domain(MapVisibilityParser.SecondaryDefinitionsField, 1, 3,
                    Definition(0, "Base"), Definition(2, "Tunnel"), Definition(3, "Upgraded"))
            })
        }, Array.Empty<string>());

        private static BinTreeObject FlagController(uint hash, uint type, uint field, byte mask) =>
            new(hash, type, new BinTreeProperty[]
            {
                new BinTreeHash(Fnv1a.HashLower("PathHash"), hash),
                new BinTreeBool(Fnv1a.HashLower("DefaultVisible"), false),
                new BinTreeU8(field, mask)
            });

        private static BinTreeObject Child(uint hash, uint mode, params uint[] parents) =>
            new(hash, MapVisibilityParser.ChildControllerClass, new BinTreeProperty[]
            {
                new BinTreeHash(Fnv1a.HashLower("PathHash"), hash),
                new BinTreeUnorderedContainer(
                    Fnv1a.HashLower("Parents"),
                    BinPropertyType.ObjectLink,
                    parents.Select(parent => (BinTreeProperty)new BinTreeObjectLink(0, parent))),
                new BinTreeU32(Fnv1a.HashLower("ParentMode"), mode)
            });

        private static BinTreeEmbedded Domain(uint field, byte min, byte max, params BinTreeProperty[] definitions) =>
            new(field, Fnv1a.HashLower("MapVisibilityFlagDefinitions"), new BinTreeProperty[]
            {
                new BinTreeContainer(Fnv1a.HashLower("FlagDefinitions"), BinPropertyType.Embedded, definitions),
                new BinTreeEmbedded(Fnv1a.HashLower("FlagRange"), Fnv1a.HashLower("MapVisibilityFlagRange"), new BinTreeProperty[]
                {
                    new BinTreeU8(Fnv1a.HashLower("minIndex"), min),
                    new BinTreeU8(Fnv1a.HashLower("maxIndex"), max)
                })
            });

        private static BinTreeProperty Definition(byte bit, string publicName)
        {
            var properties = new List<BinTreeProperty>();
            if (bit != 0)
                properties.Add(new BinTreeU8(Fnv1a.HashLower("BitIndex"), bit));
            if (publicName != null)
                properties.Add(new BinTreeString(Fnv1a.HashLower("PublicName"), publicName));
            return new BinTreeEmbedded(0, Fnv1a.HashLower("MapVisibilityFlagDefinition"), properties);
        }

        private static MapGeometryMeshData Mesh(byte mask, uint controller) =>
            new(Vector3.Zero, Vector3.One, mask, 0, MapGeometryMeshFlags.None, 0, 0, default, controller, 0);

        private static MapGeometryData Geometry(params MapGeometryMeshData[] meshes) =>
            new(
                Array.Empty<Vector3>(),
                Array.Empty<Vector3>(),
                Array.Empty<Vector2>(),
                null,
                Array.Empty<uint>(),
                meshes,
                Array.Empty<MapGeometrySubmeshData>(),
                Array.Empty<string>());
    }
}
