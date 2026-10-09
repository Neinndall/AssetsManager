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

public sealed class MapVisibilityInspectorTests
{
    [Fact]
    public void ListingOmitsExistingMapControlsAndKeepsAdditionalDependencies()
    {
        var scene = Scene(new(1, MapVisibilityControllerKind.Named),
            new(2, MapVisibilityControllerKind.Mutator, MutatorName: "MSITrophy"),
            new(3, MapVisibilityControllerKind.SecondaryFlags, Mask: 4),
            new(4, MapVisibilityControllerKind.Terrain, Mask: 64),
            new(5, MapVisibilityControllerKind.Terrain, Mask: 2),
            new(6, MapVisibilityControllerKind.Child, Parents: new uint[] { 1, 9, 4 }, ParentMode: MapVisibilityParentMode.None),
            new(7, MapVisibilityControllerKind.Child, Parents: new uint[] { 6 }),
            new(8, MapVisibilityControllerKind.Named, StageMask: 4),
            new(9, MapVisibilityControllerKind.Driven),
            new(10, MapVisibilityControllerKind.PrimaryFlags, Mask: 1),
            new(11, MapVisibilityControllerKind.Named, TerrainMask: 2),
            new(12, MapVisibilityControllerKind.Child, Parents: new uint[] { 4 }),
            new(13, MapVisibilityControllerKind.Child, Parents: new uint[] { 2 }),
            new(14, MapVisibilityControllerKind.Terrain),
            new(15, MapVisibilityControllerKind.SecondaryFlags));
        var rows = MapControllerListing.Build(scene, null);
        Assert.Equal(new uint[] { 1, 6, 7, 9, 6, 7, 14, 15 }, rows.Select(r => r.Controller.PathHash));
        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2, 0, 0 }, rows.Select(r => r.Depth));
        Assert.Equal(new[] { "", "−", "+", "", "−", "+", "", "" }, rows.Select(r => r.Relation));
    }

    [Fact]
    public void MapsWithOnlyExistingVisibilityControlsDoNotOfferTheAdditionalSection()
    {
        var scene = Scene(new(1, MapVisibilityControllerKind.Terrain, Mask: 4),
            new(2, MapVisibilityControllerKind.Child, Parents: new uint[] { 1, 3 }),
            new(3, MapVisibilityControllerKind.Child, Parents: new uint[] { 2 }));
        var model = new StudioModel();
        model.SetMapVisibility(scene, Array.Empty<MapGeometryLayerData>(), scene.Opening);
        Assert.Empty(MapControllerListing.Build(scene, null));
        Assert.Empty(model.OtherMapControllers);
        Assert.False(model.HasOtherMapControllers);
    }

    [Fact]
    public void OrphansAndDisconnectedCyclesRemainListedWithoutInfiniteNesting()
    {
        var scene = Scene(new(1, MapVisibilityControllerKind.Child, Parents: new uint[] { 99 }),
            new(2, MapVisibilityControllerKind.Child, Parents: new uint[] { 3 }),
            new(3, MapVisibilityControllerKind.Child, Parents: new uint[] { 2 }),
            new(4, MapVisibilityControllerKind.Child, Parents: new uint[] { 4 }));
        var rows = MapControllerListing.Build(scene, null);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, rows.Select(row => row.Controller.PathHash));
        Assert.Equal(new uint[] { 1, 2, 4 }, rows.Where(row => row.Depth == 0).Select(row => row.Controller.PathHash));
    }

    [Fact]
    public void MeshUsesCountEveryMeshAndDeduplicateItsMaterialNames()
    {
        var geometry = Geometry(Mesh(1, 0, 2), Mesh(1, 0, 1), Mesh(2, 0, 0), Mesh(0, 0, 2));
        var rows = MapControllerListing.Build(Scene(
            new(1, MapVisibilityControllerKind.Named), new(2, MapVisibilityControllerKind.Named)), geometry);
        Assert.Equal(2, rows[0].MeshCount);
        Assert.Equal(new[] { "Maps/A", "Maps/B" }, rows[0].Materials);
        Assert.Equal(1, rows[1].MeshCount);
        Assert.Empty(rows[1].Materials);
    }

    [Fact]
    public void ParserPrefersAuthoredNameThenEntryNameThenHash()
    {
        uint name = Fnv1a.HashLower("ControllerName");
        var tree = new BinTree(new[]
        {
            new BinTreeObject(1, MapVisibilityParser.NamedControllerClass, new BinTreeProperty[]
            {
                new BinTreeHash(Fnv1a.HashLower("name"), name),
                new BinTreeU8(MapVisibilityParser.SecondaryFlagsField, 4)
            }),
            new BinTreeObject(2, MapVisibilityParser.NamedControllerClass, Array.Empty<BinTreeProperty>()),
            new BinTreeObject(3, MapVisibilityParser.NamedControllerClass, Array.Empty<BinTreeProperty>())
        }, Array.Empty<string>());
        var controllers = new MapVisibilityParser().ParseControllers(tree,
            hash => hash == name ? "Baron_Tunnel" : hash.ToString("x8"),
            hash => hash == 2 ? "Maps/Test/Controller" : $"0x{hash:x8}");
        Assert.Equal("Baron_Tunnel", controllers[1].Name);
        Assert.Equal(4, controllers[1].StageMask);
        Assert.Equal("Maps/Test/Controller", controllers[2].Name);
        Assert.Null(controllers[3].Name);
        var scene = new MapSceneVisibility(MapVisibilityDefinitions.Empty, controllers, MapVisibilityState.FromFlags(1));
        Assert.Equal(new uint[] { 2, 3 }, MapControllerListing.Build(scene, null).Select(row => row.Controller.PathHash));
    }

    [Fact]
    public void InspectorToggleRecomputesEveryChildCopyAndRemovingOverrideRestoresDefaults()
    {
        var scene = Scene(new(1, MapVisibilityControllerKind.Named, DefaultVisible: false, Name: "First"),
            new(2, MapVisibilityControllerKind.Named, DefaultVisible: false),
            new(3, MapVisibilityControllerKind.Child, Parents: new uint[] { 1, 2 }, ParentMode: MapVisibilityParentMode.Any));
        var model = new StudioModel();
        var requests = new List<MapVisibilityState>();
        model.MapVisibilityRequested += requests.Add;
        model.SetMapVisibility(scene, Array.Empty<MapGeometryLayerData>(), scene.Opening);
        var rows = model.OtherMapControllers.ToArray();
        var root = rows.Single(row => row.PathHash == 1);
        var children = rows.Where(row => row.PathHash == 3).ToArray();
        Assert.Equal(2, children.Length);
        Assert.Empty(requests);
        Assert.All(children, row => Assert.False(row.IsVisible));
        root.IsVisible = true;
        Assert.Single(requests);
        Assert.True(root.IsOverridden);
        Assert.True(model.AreOtherMapControllersCustomized);
        Assert.All(children, row => Assert.True(row.IsVisible));
        children[0].IsVisible = false;
        Assert.Single(requests);
        Assert.True(children[0].IsVisible);
        root.IsVisible = false;
        Assert.Equal(2, requests.Count);
        Assert.Empty(model.MapVisibility.ControllerOverrides);
        Assert.False(model.AreOtherMapControllersCustomized);
        Assert.All(children, row => Assert.False(row.IsVisible));
    }

    [Fact]
    public void ResetOfAdditionalControllersPreservesTransformationPitAndDecorations()
    {
        var opening = new MapVisibilityState(1, 1);
        var scene = Scene(new(1, MapVisibilityControllerKind.Named, DefaultVisible: false),
            new(2, MapVisibilityControllerKind.SecondaryFlags, Mask: 4, DefaultVisible: false),
            new(3, MapVisibilityControllerKind.Mutator, MutatorName: "MSITrophy"));
        var model = new StudioModel();
        var requests = new List<MapVisibilityState>();
        model.MapVisibilityRequested += requests.Add;
        model.SetMapVisibility(scene, new[] { new MapGeometryLayerData(0, 2), new MapGeometryLayerData(2, 3) }, opening);
        var root = Assert.Single(model.OtherMapControllers);
        root.IsVisible = true;
        model.RequestMapLayerFlags(4);
        model.SelectedMapSecondaryState = model.MapSecondaryStates.Single();
        model.MapMutators.Single().IsEnabled = true;
        Assert.True(root.IsVisible);
        Assert.True(root.IsOverridden);
        Assert.True(model.MapVisibility.HasSecondaryOverride);
        Assert.Equal(4, model.MapVisibility.Flags);
        var beforeReset = model.MapVisibility;
        model.ResetOtherMapControllers();
        Assert.Equal(beforeReset.Flags, requests.Last().Flags);
        Assert.Equal(beforeReset.SecondaryFlags, requests.Last().SecondaryFlags);
        Assert.Equal(beforeReset.Mutators, requests.Last().Mutators);
        Assert.False(root.IsVisible);
        Assert.False(root.IsOverridden);
        Assert.True(model.MapVisibility.HasSecondaryOverride);
        Assert.True(model.MapVisibility.HasMutator("MSITrophy"));
        Assert.False(model.AreOtherMapControllersCustomized);
        int count = requests.Count;
        model.ResetOtherMapControllers();
        model.SyncMapVisibility(opening);
        Assert.Equal(count, requests.Count);
    }

    [Fact]
    public void ReplacingOrClosingSceneDropsOldControllerControlsAndRejectsStaleEdits()
    {
        var first = Scene(new MapVisibilityControllerData(1, MapVisibilityControllerKind.Named, DefaultVisible: false));
        var second = Scene(new MapVisibilityControllerData(2, MapVisibilityControllerKind.Named));
        var model = new StudioModel();
        int requests = 0;
        model.MapVisibilityRequested += _ => requests++;
        model.SetMapVisibility(first, Array.Empty<MapGeometryLayerData>(), first.Opening);
        var old = Assert.Single(model.OtherMapControllers);
        model.SetMapVisibility(second, Array.Empty<MapGeometryLayerData>(), second.Opening);
        Assert.Equal(2u, Assert.Single(model.OtherMapControllers).PathHash);
        old.IsVisible = true;
        Assert.Equal(0, requests);
        model.ClearMapVisibility();
        Assert.False(model.HasOtherMapControllers);
        Assert.False(model.AreOtherMapControllersCustomized);
        model.ResetOtherMapControllers();
        Assert.Equal(0, requests);
    }

    private static MapSceneVisibility Scene(params MapVisibilityControllerData[] controllers)
        => new(MapVisibilityDefinitions.Empty, controllers.ToDictionary(controller => controller.PathHash), MapVisibilityState.FromFlags(1));
    private static MapGeometryMeshData Mesh(uint controller, int start, int count)
        => new(Vector3.Zero, Vector3.One, 255, 0, MapGeometryMeshFlags.None, start, count, default, controller, 0);
    private static MapGeometryData Geometry(params MapGeometryMeshData[] meshes)
        => new(Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector2>(), null, Array.Empty<uint>(), meshes,
            new[] { new MapGeometrySubmeshData(0, 3, 0), new MapGeometrySubmeshData(3, 3, 1) }, new[] { "Maps/A", "Maps/B" });
}
