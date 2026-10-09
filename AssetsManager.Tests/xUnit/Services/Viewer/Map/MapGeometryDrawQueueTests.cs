using System;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map;

public sealed class MapGeometryDrawQueueTests
{
    [Fact]
    public void CameraDistanceOutranksMaterialsAndBlendedGroupsReverseTheMeshAndSubmeshOrder()
    {
        var geometry = Geometry(Mesh(1000), Mesh(2000), Mesh(1000));
        var groups = new[] { Group(1, 0, 0), Group(0, 1, 1), Group(0, 2, 2), Group(2, 3, 3) };
        var plan = new MapGeometryRenderer.DrawPlan(Array.Empty<MapGeometryRenderer.BoundMaterial>(), groups, groups);
        var queue = new MapGeometryDrawQueue();
        var projection = Matrix4x4.CreateScale(0.0001f);
        queue.Prepare(geometry, plan, projection, Vector3.Zero);
        Assert.Equal(new[] { 1, 2, 3, 0 }, queue.Opaque.Select(g => g.Order));
        Assert.Equal(new[] { 0, 3, 2, 1 }, queue.Transparent.Select(g => g.Order));

        queue.Prepare(geometry, plan, projection, new Vector3(-3000, 0, 0));
        Assert.Equal(0, queue.Opaque[0].Order);
        Assert.Equal(0, queue.Transparent[^1].Order);
    }

    [Theory]
    [InlineData(0, true)] [InlineData(150, true)] [InlineData(153, false)]
    [InlineData(-153, false)]
    public void FrustumUsesMirroredMapSpaceAndKeepsTheDeformationMargin(float x, bool visible)
    {
        var geometry = Geometry(Mesh(x));
        var groups = new[] { Group(0, 0, 0) };
        var plan = new MapGeometryRenderer.DrawPlan(Array.Empty<MapGeometryRenderer.BoundMaterial>(), groups, groups);
        var queue = new MapGeometryDrawQueue();
        queue.Prepare(geometry, plan, Matrix4x4.Identity, Vector3.Zero);
        Assert.Equal(visible ? 1 : 0, queue.Opaque.Count);
        Assert.Equal(visible ? 1 : 0, queue.Transparent.Count);
    }

    [Fact]
    public void PerspectiveCullingRejectsBehindAndBeyondTheCameraAndUpdatesAfterMoving()
    {
        var front = Mesh(500) with { Min = new Vector3(-10, -10, -1000), Max = new Vector3(10, 10, -900) };
        var behind = front with { Min = new Vector3(-10, -10, 1000), Max = new Vector3(10, 10, 1100) };
        var outside = front with { Min = new Vector3(9000, -10, -1000), Max = new Vector3(9100, 10, -900) };
        var geometry = Geometry(front, behind, outside);
        var groups = new[] { Group(0, 0, 0), Group(1, 1, 1), Group(2, 2, 2) };
        var plan = new MapGeometryRenderer.DrawPlan(Array.Empty<MapGeometryRenderer.BoundMaterial>(), groups, groups);
        var queue = new MapGeometryDrawQueue();
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 1, 5000);
        queue.Prepare(geometry, plan, projection, Vector3.Zero);
        Assert.Equal(0, Assert.Single(queue.Opaque).MeshIndex);
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY);
        queue.Prepare(geometry, plan, view * projection, Vector3.Zero);
        Assert.Equal(1, Assert.Single(queue.Opaque).MeshIndex);
    }

    [Fact]
    public void VisibilityChangesReuseQueuesAndOnlyRetainGroupsFromTheNewPlan()
    {
        var geometry = Geometry(Mesh(0), Mesh(10));
        var groups = new[] { Group(0, 0, 0), Group(1, 1, 1) };
        var plan = new MapGeometryRenderer.DrawPlan(Array.Empty<MapGeometryRenderer.BoundMaterial>(), groups, groups);
        var queue = new MapGeometryDrawQueue();
        var retained = queue.Opaque;
        queue.Prepare(geometry, plan, Matrix4x4.Identity, Vector3.Zero);
        queue.Prepare(geometry, plan with { OpaqueGroups = new[] { groups[1] }, TransparentGroups = Array.Empty<MapGeometryRenderer.DrawGroup>() },
            Matrix4x4.Identity, Vector3.Zero);
        Assert.Same(retained, queue.Opaque);
        Assert.Equal(1, Assert.Single(queue.Opaque).MeshIndex);
        Assert.Empty(queue.Transparent);
    }

    [Fact]
    public void CharacterReachIncludesAnimationSlackAndPlacementScaleAndMirror()
    {
        var reach = MapCharacterSemantics.PoseReach(new[] { new Vector3(9, 0, 0), new Vector3(11, 0, 0) });
        Assert.Equal(new Vector3(10, 0, 0), reach.Center);
        Assert.Equal(2f, reach.Radius);
        var world = MapCharacterSemantics.WorldTransform(Matrix4x4.CreateTranslation(20, 0, 0), 2);
        // Mirrored placement (-20) and mirrored/scaled center (-20) put the sphere at -40.
        Assert.False(MapCharacterSemantics.PoseInView(reach.Center, reach.Radius, world, Matrix4x4.Identity));
        Assert.True(MapCharacterSemantics.PoseInView(reach.Center, reach.Radius,
            world * Matrix4x4.CreateTranslation(36, 0, 0), Matrix4x4.Identity));
        Assert.False(MapCharacterSemantics.PoseInView(reach.Center, reach.Radius,
            world * Matrix4x4.CreateTranslation(34, 0, 0), Matrix4x4.Identity));
        Assert.True(MapCharacterSemantics.PoseInView(Vector3.Zero, float.PositiveInfinity, world, Matrix4x4.Identity));
    }

    [Fact]
    public void CharacterCullingUpdatesWithTheCameraAndKeepsIntersectingSpheres()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 1, 5000);
        Assert.True(MapCharacterSemantics.PoseInView(Vector3.Zero, 20,
            Matrix4x4.CreateTranslation(0, 0, -1000), projection));
        Assert.False(MapCharacterSemantics.PoseInView(Vector3.Zero, 20,
            Matrix4x4.CreateTranslation(0, 0, 1000), projection));
        var reversed = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY) * projection;
        Assert.True(MapCharacterSemantics.PoseInView(Vector3.Zero, 20,
            Matrix4x4.CreateTranslation(0, 0, 1000), reversed));
        Assert.True(MapCharacterSemantics.PoseInView(Vector3.Zero, 2,
            Matrix4x4.CreateTranslation(2, 0, 0), Matrix4x4.Identity));
        Assert.False(MapCharacterSemantics.PoseInView(Vector3.Zero, 2,
            Matrix4x4.CreateTranslation(4, 0, 0), Matrix4x4.Identity));
    }

    private static MapGeometryRenderer.DrawGroup Group(int mesh, int order, int material)
        => new(order * 3, 3, material, mesh, order);
    private static MapGeometryMeshData Mesh(float x)
        => new(new Vector3(x, 0, 0), new Vector3(x + 1, 1, 1), 255, 0, MapGeometryMeshFlags.None, 0, 0, default, 0, 0);
    private static MapGeometryData Geometry(params MapGeometryMeshData[] meshes)
        => new(Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector2>(), null,
            Array.Empty<uint>(), meshes, Array.Empty<MapGeometrySubmeshData>(), Array.Empty<string>());
}
