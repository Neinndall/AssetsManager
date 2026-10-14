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

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map;

public sealed class MapVisibilityContractTests
{
    [Theory]
    [InlineData(0, 0, false)] [InlineData(0, 1, false)] [InlineData(0, 3, true)]
    [InlineData(1, 0, false)] [InlineData(1, 1, true)] [InlineData(1, 3, true)]
    [InlineData(2, 0, false)] [InlineData(2, 1, true)] [InlineData(2, 3, false)]
    [InlineData(3, 0, true)] [InlineData(3, 1, false)] [InlineData(3, 3, false)]
    [InlineData(4, 0, false)] [InlineData(4, 1, false)]
    public void ParentModesCountVisibleParents(uint mode, int flags, bool expected)
    {
        var scene = Scene(new(1, MapVisibilityControllerKind.PrimaryFlags, Mask: 1),
            new(2, MapVisibilityControllerKind.PrimaryFlags, Mask: 2),
            new(3, MapVisibilityControllerKind.Child, Parents: new uint[] { 1, 2 }, ParentMode: (MapVisibilityParentMode)mode));
        Assert.Equal(expected, MapVisibilitySemantics.IsControllerVisible(scene, MapVisibilityState.FromFlags(flags), 3));
    }

    [Theory]
    [InlineData(0, true)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, true)] [InlineData(4, false)]
    public void EmptyParentsUseTheModePredicate(uint mode, bool expected)
        => Assert.Equal(expected, MapVisibilitySemantics.IsControllerVisible(
            Scene(new MapVisibilityControllerData(3, MapVisibilityControllerKind.Child, ParentMode: (MapVisibilityParentMode)mode)),
            MapVisibilityState.FromFlags(1), 3));

    [Theory]
    [InlineData(true, 0, true)] [InlineData(false, 0, false)] [InlineData(false, 4, true)]
    public void TerrainUsesDefaultVisibleOrItsActiveLayers(bool initial, int flags, bool expected)
        => Assert.Equal(expected, MapVisibilitySemantics.IsControllerVisible(
            Scene(new MapVisibilityControllerData(1, MapVisibilityControllerKind.Terrain, Mask: 4, DefaultVisible: initial)),
            MapVisibilityState.FromFlags(flags), 1));

    [Fact]
    public void StagesUseDefaultsUntilAnExplicitPreviewStateReplacesThem()
    {
        var scene = Scene(new(1, MapVisibilityControllerKind.SecondaryFlags, Mask: 1, DefaultVisible: false),
            new(2, MapVisibilityControllerKind.SecondaryFlags, Mask: 4, DefaultVisible: true));
        Assert.False(MapVisibilitySemantics.IsControllerVisible(scene, scene.Opening, 1));
        Assert.True(MapVisibilitySemantics.IsControllerVisible(scene, scene.Opening, 2));
        var chosen = scene.Opening.WithSecondaryFlags(1);
        Assert.True(MapVisibilitySemantics.IsControllerVisible(scene, chosen, 1));
        Assert.False(MapVisibilitySemantics.IsControllerVisible(scene, chosen, 2));
    }

    [Fact]
    public void ControllerReplacesMaskAcrossGeometryCharactersAndParticles()
    {
        var scene = Scene(new MapVisibilityControllerData(1, MapVisibilityControllerKind.Named, DefaultVisible: true));
        var placement = Placement(4, 1);
        var particle = new MapParticleData(placement, 0x123, false, false);
        var character = new MapCharacterData(placement, "Skin", 100, null);
        var mesh = Mesh(4, 1);
        Assert.True(MapGeometrySemantics.IsDrawn(mesh, scene, scene.Opening));
        Assert.Single(MapParticleSemantics.PlayedFor(new[] { particle }, scene, scene.Opening));
        Assert.Single(MapCharacterSemantics.StoodFor(new[] { character }, scene, scene.Opening));
        var hidden = scene.Opening.WithControllerOverride(1, false);
        Assert.False(MapGeometrySemantics.IsDrawn(mesh, scene, hidden));
        Assert.Empty(MapParticleSemantics.PlayedFor(new[] { particle }, scene, hidden));
        Assert.Empty(MapCharacterSemantics.StoodFor(new[] { character }, scene, hidden));
    }

    [Fact]
    public void AllLayerMaskNeedsNoActiveFlagAndMissingLinksDifferByConsumer()
    {
        var scene = Scene();
        Assert.True(MapVisibilitySemantics.IsVisible(scene, MapVisibilityState.FromFlags(0), 255, null));
        Assert.False(MapVisibilitySemantics.IsVisible(scene, scene.Opening, 255, 99));
        Assert.True(MapVisibilitySemantics.IsMeshVisible(scene, scene.Opening, 1, 99));
        Assert.False(MapVisibilitySemantics.IsMeshVisible(scene, scene.Opening, 4, 99));
        Assert.True(Placement(255, null).IsVisibleForFlags(0));
    }

    [Fact]
    public void MissingParentsCountAsInvisibleAndDeepAcyclicGraphsRemainValid()
    {
        var controllers = new List<MapVisibilityControllerData> { new(1, MapVisibilityControllerKind.Named) };
        for (uint hash = 2; hash <= 70; hash++)
            controllers.Add(new(hash, MapVisibilityControllerKind.Child, Parents: new[] { hash - 1 }));
        controllers.Add(new(71, MapVisibilityControllerKind.Child, Parents: new uint[] { 1, 99 }));
        controllers.Add(new(72, MapVisibilityControllerKind.Child, Parents: new uint[] { 0 }));
        var scene = Scene(controllers.ToArray());
        Assert.True(MapVisibilitySemantics.IsControllerVisible(scene, scene.Opening, 70));
        Assert.False(MapVisibilitySemantics.IsControllerVisible(scene, scene.Opening, 71));
        Assert.False(MapVisibilitySemantics.IsControllerVisible(scene, scene.Opening, 72));
    }

    [Fact]
    public void OverridesAffectChildrenAndPersistThroughStateEditsWithoutRetainingDefaults()
    {
        var scene = Scene(new(1, MapVisibilityControllerKind.Named, DefaultVisible: false),
            new(2, MapVisibilityControllerKind.Child, Parents: new uint[] { 1 }));
        var enabled = MapVisibilitySemantics.WithControllerState(scene, scene.Opening, 1, true);
        Assert.True(MapVisibilitySemantics.IsControllerVisible(scene, enabled, 2));
        var edited = enabled.WithSecondaryFlags(4).WithFlags(8).WithMutator("MSITrophy", true);
        Assert.True(edited.ControllerOverrides[1]);
        Assert.True(edited.HasSecondaryOverride);
        Assert.NotEqual(enabled, scene.Opening);
        Assert.Equal(enabled, new MapVisibilityState(1, controllerOverrides: new Dictionary<uint, bool> { [1] = true }));
        Assert.Empty(MapVisibilitySemantics.WithControllerState(scene, enabled, 1, false).ControllerOverrides);
    }

    [Fact]
    public void CyclicNegationUsesOneGraphEvaluationRegardlessOfConsumerLookupOrder()
    {
        var scene = Scene(new(1, MapVisibilityControllerKind.Child, Parents: new uint[] { 2 }, ParentMode: MapVisibilityParentMode.None),
            new(2, MapVisibilityControllerKind.Child, Parents: new uint[] { 1 }));
        Assert.False(MapVisibilitySemantics.IsControllerVisible(scene, scene.Opening, 2));
        Assert.True(MapVisibilitySemantics.IsControllerVisible(scene, scene.Opening, 1));
        Assert.False(MapVisibilitySemantics.IsControllerVisible(scene, scene.Opening, 2));
        var overridden = scene.Opening.WithControllerOverride(1, true);
        Assert.True(MapVisibilitySemantics.IsControllerVisible(scene, overridden, 2));
    }

    [Fact]
    public void EventSelectionStillRequiresItsLayerAndNeverStartsTransitions()
    {
        var scene = Scene(new MapVisibilityControllerData(1, MapVisibilityControllerKind.Named));
        var controlled = new MapParticleData(Placement(4, 1), 1, false, false);
        var disabled = controlled with { StartDisabled = true };
        var transitional = controlled with { Transitional = true };
        var unknown = new MapParticleData(Placement(1, 99), 1, false, false);
        Assert.Single(MapParticleSemantics.PlayedFor(new[] { controlled }, scene, scene.Opening));
        Assert.Empty(MapParticleSemantics.PlayedFor(new[] { disabled, transitional }, scene, scene.Opening, events: true));
        Assert.Equal(new[] { disabled }, MapParticleSemantics.PlayedFor(new[] { disabled, transitional }, scene,
            scene.Opening.WithFlags(4), picked: new HashSet<string> { MapOutlineSemantics.ItemId(1, 1) }));
        Assert.Single(MapParticleSemantics.PlayedFor(new[] { unknown }, scene, scene.Opening, events: true));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void SerializedBinPreservesControllerKindsAndParentModes(byte mode)
    {
        var objects = new[] {
            new BinTreeObject(1, MapVisibilityParser.LogicDriverControllerClass, Array.Empty<BinTreeProperty>()),
            new BinTreeObject(2, MapVisibilityParser.ProviderControllerClass, Array.Empty<BinTreeProperty>()),
            new BinTreeObject(3, MapVisibilityParser.ChildControllerClass, new BinTreeProperty[] {
                new BinTreeU8(Fnv1a.HashLower("ParentMode"), mode),
                new BinTreeContainer(Fnv1a.HashLower("Parents"), BinPropertyType.ObjectLink,
                    new BinTreeProperty[] { new BinTreeObjectLink(0, 0) }) }),
            new BinTreeObject(4, MapVisibilityParser.VisFlagsControllerClass, new BinTreeProperty[] {
                new BinTreeU16(Fnv1a.HashLower("VisFlags"), 4) }) };
        using var bytes = new System.IO.MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(bytes);
        bytes.Position = 0;
        var parsed = new MapVisibilityParser().ParseControllers(new BinTree(bytes));
        Assert.Equal(MapVisibilityControllerKind.Driven, parsed[1].Kind);
        Assert.Equal(MapVisibilityControllerKind.Driven, parsed[2].Kind);
        Assert.Equal(mode <= 3 ? MapVisibilityControllerKind.Child : MapVisibilityControllerKind.Driven, parsed[3].Kind);
        Assert.Equal(new uint[] { 0 }, parsed[3].Parents);
        Assert.Equal(4, parsed[4].Mask);
        Assert.False(MapVisibilitySemantics.IsControllerVisible(Scene(parsed.Values.ToArray()), MapVisibilityState.FromFlags(1), 1));
    }

    [Theory]
    [InlineData(0xe07edfa4u)]
    [InlineData(MapVisibilityParser.PrimaryFlagsControllerClass)]
    [InlineData(MapVisibilityParser.SecondaryFlagsControllerClass)]
    public void NamedControllerFamilyReadsTerrainBitsAndAuthoredDefaults(uint type)
    {
        var entry = new BinTreeObject(7, type, new BinTreeProperty[]
        {
            new BinTreeBool(Fnv1a.HashLower("DefaultVisible"), false),
            new BinTreeU32(MapVisibilityParser.PrimaryFlagsField, 4)
        });
        var parsed = new MapVisibilityParser().ParseControllers(new BinTree(new[] { entry }, Array.Empty<string>()));
        var scene = Scene(parsed.Values.ToArray());
        Assert.False(MapVisibilitySemantics.IsControllerVisible(scene, MapVisibilityState.FromFlags(1), 7));
        Assert.True(MapVisibilitySemantics.IsControllerVisible(scene, MapVisibilityState.FromFlags(4), 7));
    }

    [Fact]
    public void OutOfRangeControllerMasksDoNotWrapIntoVisibleLayers()
    {
        var entry = new BinTreeObject(7, MapVisibilityParser.VisFlagsControllerClass,
            new BinTreeProperty[] { new BinTreeU16(Fnv1a.HashLower("VisFlags"), 257) });
        var parsed = new MapVisibilityParser().ParseControllers(new BinTree(new[] { entry }, Array.Empty<string>()));
        Assert.Equal(0, parsed[7].Mask);
        Assert.False(MapVisibilitySemantics.IsControllerVisible(Scene(parsed.Values.ToArray()), MapVisibilityState.FromFlags(1), 7));
    }

    private static MapSceneVisibility Scene(params MapVisibilityControllerData[] controllers)
        => new(MapVisibilityDefinitions.Empty, controllers.ToDictionary(c => c.PathHash), MapVisibilityState.FromFlags(1));
    private static MapPlaceableData Placement(byte mask, uint? controller)
        => new(1, 1, 0, "placement", Matrix4x4.Identity, mask, controller, null);
    private static MapGeometryMeshData Mesh(byte mask, uint controller)
        => new(Vector3.Zero, Vector3.One, mask, 0, MapGeometryMeshFlags.None, 0, 0, default, controller, 0);
}
