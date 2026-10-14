using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxBeamSupportTests
{
    [Fact]
    public void SegmentSourcesKeepTheSharedRibBudgetAndMeshBeamsUseTheMeshBudget()
    {
        var segment = Definition() with { PrimitiveKind = VfxPrimitiveKind.CameraSegmentBeam };
        Assert.Equal(512, VfxOpenGlRenderer.ResolveEmitterDrawCount(segment, 0, 600));
        Assert.Equal(0, VfxOpenGlRenderer.ResolveEmitterDrawCount(segment, 500, 13));
        Assert.Equal(12, VfxOpenGlRenderer.ResolveEmitterDrawCount(segment, 500, 12));
        Assert.Equal(512, VfxOpenGlRenderer.ResolveEmitterDrawCount(Definition() with { MeshPath = "beam.scb" }, 0, 600));
        Assert.Equal(256, VfxOpenGlRenderer.ResolveEmitterDrawCount(Definition(), 0, 600));
        Assert.False((segment with { MeshPath = "beam.scb" }).DrawsAsMesh);
        Assert.True((segment with { MeshPath = "beam.scb" }).SuppressesBeamRibbon);
    }

    [Fact]
    public void ChildBeamSourcesInheritTheParentTargetAtBirthAndAfterRetargeting()
    {
        var child = new VfxSystemDefinition(2, "child", "child", new[] { Definition() });
        var set = new VfxChildParticleSetDefinition(new[] { new VfxChildSystemReference("child", 2, 0) }, false,
            VfxCurveF.Zero, VfxCurve3.Const(Vector3.Zero), 0);
        var root = new VfxSystemDefinition(1, "root", "root", new[] { Definition() with { ChildParticleSet = set } });
        var graph = new VfxPlaybackGraphRuntime(root, Matrix4x4.Identity, 7,
            new Dictionary<uint, VfxSystemDefinition> { [1] = root, [2] = child }, new Dictionary<uint, uint>(),
            (definition, transform, seed) => { var runtime = new VfxPlaybackRuntime(seed); runtime.SetSystem(definition, transform); return runtime; });
        Vector3 target = new(40, 10, 90);
        graph.SetTarget(target);
        graph.Update(0.01f);
        Assert.True(graph.Runtimes.Count > 1);
        Assert.All(graph.Runtimes, runtime => Assert.All(runtime.Emitters, emitter => Assert.Equal(target, emitter.SystemTarget)));
        var snapshot = graph.CaptureSnapshot();
        target = new Vector3(-20, 0, 30);
        graph.SetTarget(target);
        graph.Update(0.01f);
        Assert.All(graph.Runtimes, runtime => Assert.All(runtime.Emitters, emitter => Assert.Equal(target, emitter.SystemTarget)));
        graph.RestoreSnapshot(snapshot);
        graph.Update(0.01f);
        Assert.All(graph.Runtimes, runtime => Assert.All(runtime.Emitters, emitter => Assert.Equal(new Vector3(40, 10, 90), emitter.SystemTarget)));
    }

    [Fact]
    public void SpawnOrdinalsArePerEmitterWrapAndSurviveSnapshots()
    {
        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(new VfxSystemDefinition(1, "beam", "beam", new[] { Definition(), Definition() }), Vector3.Zero);
        runtime.Emitters[0].Spawned = ushort.MaxValue;
        runtime.Update(0.01f);
        Assert.Equal(new ushort[] { ushort.MaxValue, 0, 1 }, runtime.Emitters[0].Particles.Select(p => p.Ordinal));
        Assert.Equal(new ushort[] { 0, 1, 2 }, runtime.Emitters[1].Particles.Select(p => p.Ordinal));
        var snapshot = runtime.CaptureSnapshot();
        runtime.Emitters[0].Spawned = 100;
        runtime.Emitters[0].Particles.Clear();
        runtime.RestoreSnapshot(snapshot);
        Assert.Equal((ushort)2, runtime.Emitters[0].Spawned);
        Assert.Equal((ushort)3, runtime.Emitters[1].Spawned);
        Assert.Equal(new ushort[] { ushort.MaxValue, 0, 1 }, runtime.Emitters[0].Particles.Select(p => p.Ordinal));
        runtime.Reset();
        Assert.All(runtime.Emitters, emitter => Assert.Equal((ushort)0, emitter.Spawned));
    }

    [Theory]
    [InlineData(0, 0, 10)]
    [InlineData(3, 0, 0)]
    [InlineData(0, 3, 0)]
    [InlineData(0, -3, 0)]
    public void BeamMeshFrameFacesFromTargetToSourceAndKeepsAnOrthonormalBasis(float x, float y, float z)
    {
        var state = new VfxPlaybackRuntime.EmitterState { Def = Definition() with { MeshPath = "mesh.scb" },
            SystemOrigin = new Vector3(x, y, z), SystemTarget = Vector3.Zero, MeshDepth = 2 };
        Assert.True(state.Def.DrawsAsMesh);
        Assert.True(VfxBeamGeometry.TryMeshPlacement(state, out var position, out var basis, out float stretch));
        var direction = new Vector3(x, y, z);
        Assert.Equal(direction / 2, position);
        Assert.Equal(direction.Length() / 2, stretch, 5);
        Vector3 forward = Vector3.TransformNormal(Vector3.UnitZ, basis);
        Vector3 right = Vector3.TransformNormal(Vector3.UnitX, basis);
        Vector3 up = Vector3.TransformNormal(Vector3.UnitY, basis);
        Assert.True(Vector3.Distance(Vector3.Normalize(direction), forward) < 1e-5f);
        Assert.Equal(1f, right.Length(), 5);
        Assert.Equal(1f, up.Length(), 5);
        Assert.Equal(0f, Vector3.Dot(right, forward), 5);
        Assert.Equal(1f, basis.GetDeterminant(), 5);
    }

    [Fact]
    public void MeshPlacementRejectsFlatGeometryAndCoincidentEnds()
    {
        var state = new VfxPlaybackRuntime.EmitterState { Def = Definition(), SystemTarget = Vector3.UnitZ };
        Assert.False(VfxBeamGeometry.TryMeshPlacement(state, out _, out _, out _));
        state.MeshDepth = 2; state.SystemTarget = Vector3.Zero;
        Assert.False(VfxBeamGeometry.TryMeshPlacement(state, out _, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SegmentStripUsesBirthOrdinalsPositionUvQuirksAndDescendingSerials(int trailMode)
    {
        var def = Definition() with { PrimitiveKind = VfxPrimitiveKind.CameraSegmentBeam,
            Beam = Definition().Beam with { TrailMode = trailMode }, TextureMultPath = "mult.tex",
            UvTransformCenter = Vector2.Zero, ColorLookUpTypeX = 0, ColorLookUpTypeY = 0,
            ColorLookUpScales = new Vector2(0.5f, 0.125f) };
        var state = new VfxPlaybackRuntime.EmitterState { Def = def, SystemOrigin = Vector3.Zero,
            SystemTarget = new Vector3(100, 0, 0), InstanceCount = 3, Instances = new float[3 * VfxPlaybackRuntime.InstanceStride] };
        for (int i = 0; i < 3; i++)
        {
            int at = i * VfxPlaybackRuntime.InstanceStride;
            state.Instances[at + 3] = 4;
            state.Instances[at + 5] = state.Instances[at + 6] = state.Instances[at + 7] = state.Instances[at + 8] = 1;
            state.Instances[at + 19] = 0.25f; state.Instances[at + 20] = 0.5f;
            state.Instances[at + 21] = state.Instances[at + 22] = 3;
            state.Instances[at + 23] = 1;
            state.Particles.Add(new VfxPlaybackRuntime.Particle { Serial = (uint)i, Ordinal = (ushort)i,
                TrailTiling = new Vector3(50, 0, 0) });
        }
        var geometry = new VfxBeamGeometry();
        Assert.Equal(12, geometry.Build(state, new Vector3(0, 0, 100)));
        int stride = VfxBeamGeometry.VertexStride;
        // Triangle expansion starts at the second rib and retains the far rib as its second vertex.
        Assert.Equal(50, geometry.Vertices[2]);
        Assert.Equal(100, geometry.Vertices[stride + 2]);
        Assert.Equal(2, geometry.Vertices[3]);
        Assert.Equal(0.25f, geometry.Vertices[0]);
        Assert.Equal(trailMode == 0 ? 0.5f : 1.5f, geometry.Vertices[1]);
        Assert.Equal(0.5f, geometry.Vertices[17]);
        Assert.Equal(0.125f, geometry.Vertices[18]);
        Assert.Equal(0, geometry.Vertices[26]);
        // Compaction cannot move a surviving rib to a different ordinal share.
        state.Particles.RemoveAt(1);
        Array.Copy(state.Instances, 2 * VfxPlaybackRuntime.InstanceStride, state.Instances, VfxPlaybackRuntime.InstanceStride, VfxPlaybackRuntime.InstanceStride);
        state.InstanceCount = 2;
        Assert.Equal(6, geometry.Build(state, new Vector3(0, 0, 100)));
        Assert.Equal(0, geometry.Vertices[2]);
        Assert.Equal(100, geometry.Vertices[stride + 2]);
    }

    [Fact]
    public void DefaultAndAuthoredTargetsFaceForwardAndBeamReachIgnoresAnimatedOffAxisEmitters()
    {
        var effect = Definition() with { Beam = null, EmitterPosition = VfxCurve3.Const(new Vector3(0, 0, 1200)) };
        var system = new VfxSystemDefinition(1, "rig", "rig", new[] { Definition(), effect });
        Assert.Equal(1200f, VfxSystemRigResolver.BeamReach(system));
        Assert.Null(VfxSystemRigResolver.BeamReach(system with { Emitters = new[] { Definition(), effect with
            { EmitterPosition = VfxCurve3.Const(new Vector3(20, 0, 1200)) } } }));
        var settings = VfxRigSettings.ForPreset(VfxRigPreset.Still);
        Assert.Equal(new Vector3(0, 0, 600), VfxRigMotion.Evaluate(settings, 0, 1).Target);
        var aimed = VfxRigMotion.Evaluate(settings with { Target = new Vector3(500, 20, 0), Height = 10 }, 0, 1);
        Assert.Equal(new Vector3(500, 30, 0), aimed.Target);
        Assert.Equal(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitZ, aimed.Transform));
        var bone = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(1, 2, 3);
        Assert.True(Vector3.Distance(new Vector3(601, 2, 3), VfxRenderSession.AheadOf(bone)) < 0.001f);
    }

    internal static VfxEmitterDefinition Definition() => new("beam", VfxCurveF.Const(3), VfxCurveF.Const(1),
        null, 0, 0, true, false, 3, VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One), null,
        null, null, null, VfxCurve3.Const(Vector3.Zero), "base.tex", Vector2.One, 1, false, false,
        PrimitiveKind: VfxPrimitiveKind.Beam, Beam: new VfxBeamDefinition(0, 0, 3, VfxCurve3.Const(Vector3.One),
            VfxCurve4.Const(Vector4.One), false, Vector3.Zero, Vector3.Zero));
}
