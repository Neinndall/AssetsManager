using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxTimingPlacementRegressionTests
{
    [Theory]
    [InlineData(4f, null, null, 1f, 3f, .5f)]
    [InlineData(8f, 4f, 2f, 1f, 2f, .5f)]
    [InlineData(null, null, null, 1f, 2f, 0f)]
    [InlineData(2f, null, null, 1f, .5f, -.25f)]
    public void EmitterPhaseUsesTheShortestIndependentWindow(float? end, float? period, float? active, float delay, float age, float expected)
    {
        var emitter = Emitter() with { IsSingleParticle = false, EmitterLifetime = end,
            TimeBeforeFirstEmission = delay, EmissionPeriod = VfxEmissionPeriod.FromAuthored(period, active) };
        Assert.Equal(expected, VfxPlaybackRuntime.EmitterPhase(emitter, age), 5);
    }

    [Theory]
    [InlineData(null, 2f, false, 2f)]
    [InlineData(100f, 2f, false, 2f)]
    [InlineData(10f, 2f, false, 10f)]
    [InlineData(100f, 2f, true, 100f)]
    [InlineData(100f, -1f, false, 100f)]
    public void SingleBurstEndRespectsTheMaterialAndEndlessExceptions(float? end, float life, bool materials, float expected)
        => Assert.Equal(expected, VfxPlaybackRuntime.EmissionEnd(Emitter() with
            { EmitterLifetime = end, ParticleLifetime = VfxCurveF.Const(life), OverridesMaterials = materials }));

    [Fact]
    public void BirthDragActsOnLocalAxesBeforeTheBirthFrameTurnsMotion()
    {
        var runtime = Create(Emitter() with { BirthVelocity = VfxCurve3.Const(new Vector3(10f, 10f, 0f)),
            BirthDrag = VfxCurve3.Const(Vector3.UnitX), RotationOverride = new Vector3(0f, 0f, 90f) });
        runtime.Update(.01f);
        runtime.Update(.1f);
        var particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
        Near(new Vector3(9f, 10f, 0f), particle.Vel);
        Near(new Vector3(-1f, .9f, 0f), particle.Pos);
    }

    [Fact]
    public void KeyedMotionReadsParticleAgeAfterADelayedBirth()
    {
        var runtime = Create(Emitter() with { EmitterLifetime = 4f, TimeBeforeFirstEmission = 1f,
            ParticleLifetime = VfxCurveF.Const(2f), AccelerationOverLife = new VfxCurve3(Vector3.Zero,
                new[] { 0f, 1f }, new[] { Vector3.Zero, new Vector3(10f, 0f, 0f) }) });
        runtime.Update(1f);
        runtime.Update(.1f);
        var particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
        Assert.Equal(.05f, particle.Vel.X, 5);
        Assert.Equal(.005f, particle.Pos.X, 5);
    }

    [Fact]
    public void NonuniformScaleIsReadAfterTheParticlesOwnTurn()
    {
        var emitter = Emitter() with { IsMeshPrimitive = true, PrimitiveKind = VfxPrimitiveKind.Mesh,
            BirthRotation = VfxCurve3.Const(new Vector3(0f, 0f, 90f)), ScaleOverride = new Vector3(2f, 3f, 4f) };
        var runtime = Create(emitter, Matrix4x4.CreateScale(5f, 7f, 11f));
        runtime.Update(.01f);
        var state = Assert.Single(runtime.Emitters);
        Assert.Equal(21f, state.Instances[3], 4);
        Assert.Equal(10f, state.Instances[4], 4);
        Assert.Equal(44f, state.Instances[18], 4);
    }

    [Fact]
    public void SharedBirthNumberDoesNotCorrelateLifetimeOrVectorChannels()
    {
        var table = new VfxProbTable(new[] { 0f, 1f }, new[] { 0f, 1f });
        var emitter = Emitter() with { Rate = VfxCurveF.Const(2f), ParticlesShareRandomValue = true,
            ParticleLifetime = new VfxCurveF(10f, null, null, new[] { table }),
            BirthVelocity = new VfxCurve3(Vector3.One, null, null, new[] { table, table, table }),
            BirthUvOffset = new VfxCurve2(Vector2.One, null, null, new[] { table, table }) };
        var runtime = Create(emitter);
        var expected = new VfxLtkRandom(7);
        float shared = expected.NextUnitFloat();
        runtime.Update(.01f);
        foreach (var particle in Assert.Single(runtime.Emitters).Particles)
        {
            Assert.Equal(shared, particle.RangeRandom);
            Assert.Equal(expected.NextUnitFloat() * 10f, particle.Life, 5);
            Near(new Vector3(expected.NextUnitFloat(), expected.NextUnitFloat(), expected.NextUnitFloat()), particle.Vel);
            Assert.Equal(new Vector2(shared), particle.BirthUvOffset);
        }
        Assert.Equal(expected.State, runtime.RandomState);
        var pinned = Create(emitter);
        pinned.SetPinnedBirthChance(.75f);
        pinned.Update(.01f);
        Assert.Equal(runtime.RandomState, pinned.RandomState);
        Assert.All(pinned.Emitters[0].Particles, particle =>
        {
            Assert.Equal(7.5f, particle.Life);
            Assert.Equal(new Vector3(.75f), particle.Vel);
        });
    }

    [Fact]
    public void IntegratedCurvesUseRunningIntegralsAndConstantsStayFixed()
    {
        var scalar = new VfxCurveF(99f, new[] { 0f, 1f }, new[] { 0f, 4f });
        Assert.InRange(VfxIntegratedValue.Sample(scalar, .5f), .5f, .5002f);
        Assert.InRange(VfxIntegratedValue.Sample(scalar, .5f, 2), 1f / 12f, .0835f);
        Assert.Equal(4f, VfxIntegratedValue.Sample(VfxCurveF.Const(4f), .5f, 2));
        Assert.Equal(0f, VfxIntegratedValue.Sample(scalar, 0f));
        Assert.Equal(2f, VfxIntegratedValue.Sample(scalar, 1f), 5);
    }

    [Fact]
    public void StaticMeshUsesAreaWeightsAndTheTwoDrawCornerBlend()
    {
        var mesh = new VfxMeshData(new float[] { 0,0,0, 2,0,0, 0,2,0, 100,0,0, 120,0,0, 100,20,0 },
            Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), new uint[] { 0,1,2, 3,4,5 });
        var sampler = new VfxStaticEmissionMeshSampler(mesh, 2f);
        var expected = new VfxLtkRandom(7);
        bool large = expected.NextUnitFloat() * 202f >= 2f;
        float u = expected.NextUnitFloat(), v = expected.NextUnitFloat();
        Vector3 a = large ? new Vector3(100f, 0f, 0f) : Vector3.Zero;
        Vector3 b = a + new Vector3(large ? 20f : 2f, 0f, 0f);
        Vector3 c = a + new Vector3(0f, large ? 20f : 2f, 0f);
        var random = new VfxLtkRandom(7);
        Assert.True(sampler.TrySample(0f, random, out var birth));
        Near(2f * (a * ((1f - u) * (1f - v)) + b * ((1f - u) * v) + c * u), birth.Position);
        Near(Vector3.UnitZ, birth.Normal);
        Assert.Equal(expected.State, random.State);
        int largeCount = 0;
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(sampler.TrySample(0f, random, out birth));
            if (birth.Position.X >= 200f) largeCount++;
        }
        Assert.InRange(largeCount, 950, 1000);
    }

    [Fact]
    public void BothEmissionSurfacesAddOffsetsAndOnlyTheStaticMeshRedirectsAcceleration()
    {
        var emitter = Emitter() with { ParticleLifetime = VfxCurveF.Const(1f),
            BirthVelocity = VfxCurve3.Const(new Vector3(3f, 0f, 0f)), BirthAcceleration = VfxCurve3.Const(new Vector3(2f, 0f, 0f)),
            SpawnShape = new VfxSpawnShape(VfxSpawnShapeKind.Legacy, VfxCurve3.Const(new Vector3(-2f, 0f, 0f)),
                new[] { Vector3.UnitZ }, new[] { VfxCurveF.Const(90f) }),
            OffsetLifetimeScaling = new Vector3(2f, 0f, 0f), OffsetLifeScalingSymmetryMode = 1,
            EmissionMesh = new VfxEmissionMeshDefinition("static.scb"),
            EmissionSurface = new VfxEmissionSurfaceDefinition(VfxEmissionSurfaceKind.Mesh, "posed.skn", null, null,
                Array.Empty<uint>(), Array.Empty<uint>(), 1f, 4, true) };
        var runtime = Create(emitter);
        runtime.SetEmissionSurfaces(new Dictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler>
        {
            [emitter] = new VfxEmitterEmissionSampler(new FixedSurface(new Vector3(10f, 0f, 0f), Vector3.UnitY),
                new FixedSurface(new Vector3(0f, 20f, 0f), Vector3.UnitZ))
        });
        runtime.Update(.01f);
        var particle = Assert.Single(runtime.Emitters[0].Particles);
        Near(new Vector3(10f, 18f, 0f), particle.Pos);
        Near(new Vector3(0f, 0f, 3f), particle.Vel);
        Near(new Vector3(-2f, 0f, 0f), particle.BirthAcceleration);
        Assert.Equal(5f, particle.Life, 4);
        var checkpoint = runtime.CaptureSnapshot();
        runtime.Update(.1f);
        var expected = runtime.Emitters[0].Particles.ToArray();
        runtime.RestoreSnapshot(checkpoint);
        runtime.Update(.1f);
        Assert.Equal(expected, runtime.Emitters[0].Particles);
    }

    [Theory]
    [InlineData(0, 0, false, 0, 1)]
    [InlineData(7, 0, false, -1, 1)]
    [InlineData(7, 0, false, 0, 2)]
    [InlineData(7, 2, false, 0, 4)]
    [InlineData(7, 1, false, 0, 8)]
    [InlineData(7, 3, false, 0, 12)]
    [InlineData(2, 0, false, 0, 4)]
    [InlineData(3, 0, false, 0, 8)]
    [InlineData(6, 0, false, 0, 0)]
    [InlineData(6, 0, true, 0, 2)]
    [InlineData(1, 1, false, 0, 0)]
    [InlineData(99, 1, false, 0, 0)]
    public void RenderPhasesHonorOverridesHudAndBothDistortionBits(byte phase, byte distortion, bool hud, int pass, int expected)
    {
        var emitter = Emitter() with { RenderState = VfxEmitterRenderState.Default with { RenderPhase = phase, RenderPass = pass },
            Distortion = new VfxDistortionDefinition(.1f, distortion, "normal.tex") };
        var actual = VfxRenderPhaseSemantics.Resolve(emitter, hud);
        int flags = (actual.Under ? 1 : 0) | (actual.Over ? 2 : 0) | (actual.EarlyDistortion ? 4 : 0) | (actual.LateDistortion ? 8 : 0);
        Assert.Equal(expected, flags);
    }

    [Fact]
    public void SimpleUnsupportedPrimitivesSimulateWithoutDrawing()
    {
        foreach (var kind in new[] { VfxPrimitiveKind.CameraUnitQuad, VfxPrimitiveKind.Ray, VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.Beam })
            Assert.False(VfxRenderPhaseSemantics.Resolve(Emitter() with { IsSimpleEmitter = true, PrimitiveKind = kind }).Draws);
    }

    [Fact]
    public void ParserConnectsEmissionMeshPlacementAndWindingFields()
    {
        var definition = Parse(new BinTreeProperty[] {
            new BinTreeBitBool(Fnv1a.HashLower("isSingleParticle"), true),
            new BinTreeString(Fnv1a.HashLower("emissionMeshName"), "mesh.scb"),
            new BinTreeF32(Fnv1a.HashLower("emissionMeshScale"), 2f),
            new BinTreeBitBool(Fnv1a.HashLower("useEmissionMeshNormalForBirth"), false),
            new BinTreeVector3(Fnv1a.HashLower("offsetLifetimeScaling"), new Vector3(1f, 2f, 3f)),
            new BinTreeU8(Fnv1a.HashLower("offsetLifeScalingSymmetryMode"), 5),
            new BinTreeBitBool(Fnv1a.HashLower("hasPostRotateOrientation"), true),
            new BinTreeVector3(Fnv1a.HashLower("postRotateOrientationAxis"), new Vector3(10f, 20f, 30f)),
            new BinTreeBitBool(0xd1ee8634, true),
            new BinTreeF32(Fnv1a.HashLower("ChanceToNotExist"), .25f) });
        Assert.Equal(new VfxEmissionMeshDefinition("mesh.scb", 2f, false), definition.EmissionMesh);
        Assert.Equal(new Vector3(1f, 2f, 3f), definition.OffsetLifetimeScaling);
        Assert.Equal(5, definition.OffsetLifeScalingSymmetryMode);
        Assert.Equal(new Vector3(10f, 20f, 30f), definition.PostRotateOrientation);
        Assert.True(definition.RenderState.FlipWinding);
        Assert.Equal(.25f, definition.ChanceToNotExist);
        Assert.Equal(VfxCullReason.NoRate, Parse(Array.Empty<BinTreeProperty>()).Culled);
        Assert.True(Parse(Array.Empty<BinTreeProperty>()).Disabled);
    }

    [Fact]
    public void LargeBurstIsCappedEvenWithVariableStartAndTheRootKeepsItsSharedCapacity()
    {
        var runtime = Create(Emitter() with { Rate = VfxCurveF.Const(5000f), HasVariableStartTime = true });
        runtime.Update(.01f);
        Assert.Equal(1000, runtime.LiveParticleCount);
        Assert.Equal(32768, runtime.ParticleCapacity);
    }

    [Fact]
    public void CameraTravelAlignmentDoesNotReplaceTheChildsInheritedTurn()
    {
        var parentEmitter = Emitter() with { IsDirectionOriented = true,
            BirthRotation = VfxCurve3.Const(new Vector3(0f, 0f, 90f)), BirthVelocity = VfxCurve3.Const(Vector3.UnitX) };
        var (parent, child) = Family(parentEmitter, Emitter());
        var graph = new VfxPlaybackGraphRuntime(parent, Matrix4x4.Identity, 7,
            new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child }, new Dictionary<uint, uint>(),
            (definition, placement, seed) => {
                var runtime = new VfxPlaybackRuntime(seed); runtime.SetSystem(definition, placement); return runtime;
            });
        graph.Update(.01f);
        graph.Update(.1f);
        var runtime = Assert.Single(graph.Runtimes, item => item != graph.Root);
        Near(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitX, runtime.CaptureSnapshot().WorldTransform));
        var state = Assert.Single(graph.Root.Emitters);
        Near(Vector3.UnitX, new Vector3(state.Instances[12], state.Instances[13], state.Instances[14]));
    }

    [Fact]
    public void LoadedEmissionMeshesReachRootChildrenAndSnapshotReplay()
    {
        string folder = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxTiming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllLines(Path.Combine(folder, "birth.sco"), new[] {
                "[ObjectBegin]", "Name= birth", "CentralPoint= 0 0 0", "Verts= 3",
                "10 0 0", "12 0 0", "10 2 0", "Faces= 1", "3 0 1 2 material 0 0 1 0 0 1" });
            var emitter = Emitter() with { EmissionMesh = new VfxEmissionMeshDefinition("birth.sco"),
                BirthAcceleration = VfxCurve3.Const(Vector3.UnitX) };
            var (parent, child) = Family(emitter, emitter);
            using var loading = new VfxLoadingService();
            var graph = loading.PreparePlaybackGraph(parent,
                new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child },
                new Dictionary<uint, uint>(), folder, Matrix4x4.Identity, 7, null);
            graph.Update(.01f);
            graph.Update(.01f);
            foreach (var runtime in graph.Runtimes)
            {
                var particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
                Assert.InRange(particle.LocalPosition.X, 10f, 12f);
                Near(Vector3.UnitZ, particle.BirthAcceleration);
            }
            Assert.Equal(2, graph.Runtimes.Count);
            var checkpoint = graph.CaptureSnapshot();
            graph.Update(.1f);
            var expected = graph.Runtimes.Select(runtime => runtime.Emitters[0].Instances.ToArray()).ToArray();
            graph.RestoreSnapshot(checkpoint);
            graph.Update(.1f);
            Assert.Equal(expected.Length, graph.Runtimes.Count);
            for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], graph.Runtimes[i].Emitters[0].Instances);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ChildAppearanceUsesTheDriverClockAndReplaysItsFlicker()
    {
        var table = new VfxProbTable(new[] { 0f, 1f }, new[] { 0f, 1f });
        var emitter = Emitter() with { ColorOverLife = new VfxCurve4(Vector4.One, null, null, new[] { table }) };
        var (parent, child) = Family(emitter, emitter);
        var graph = new VfxPlaybackGraphRuntime(parent, Matrix4x4.Identity, 7,
            new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child }, new Dictionary<uint, uint>(),
            (definition, placement, seed) => {
                var runtime = new VfxPlaybackRuntime(seed); runtime.SetSystem(definition, placement); return runtime;
            });
        graph.Update(.2f);
        graph.Update(.1f);
        var rootState = graph.Root.Emitters[0];
        var childState = graph.Runtimes[1].Emitters[0];
        Assert.Equal(.3f, childState.RenderTime, 5);
        Assert.Equal(rootState.Instances[5], childState.Instances[5]);
        var checkpoint = graph.CaptureSnapshot();
        graph.Update(.1f);
        float expected = childState.Instances[5];
        graph.RestoreSnapshot(checkpoint);
        graph.Update(.1f);
        Assert.Equal(expected, graph.Runtimes[1].Emitters[0].Instances[5]);
    }

    [Theory]
    [InlineData(VfxPrimitiveKind.CameraQuad, 2f, 2f)]
    [InlineData(VfxPrimitiveKind.Ray, 3f, 4f)]
    [InlineData(VfxPrimitiveKind.CameraTrail, 3f, 4f)]
    [InlineData(VfxPrimitiveKind.PlanarProjection, 3f, 4f)]
    public void UniformScaleOnlyReachesQuadsAndMeshes(VfxPrimitiveKind kind, float y, float z)
    {
        var runtime = Create(Emitter() with { PrimitiveKind = kind, IsUniformScale = true,
            BirthScale = VfxCurve3.Const(new Vector3(2f, 3f, 4f)) });
        runtime.Update(.01f);
        Near(new Vector3(2f, y, z), Assert.Single(runtime.Emitters[0].Particles).BirthSize);
    }

    [Fact]
    public void PreviewDurationUsesAbsoluteEndsIndependentActiveWindowsAndDynamicMaxima()
    {
        var emitter = Emitter() with { IsSingleParticle = false, EmitterLifetime = 4f, TimeBeforeFirstEmission = 1f,
            ParticleLifetime = new VfxCurveF(100f, new[] { 0f, 1f }, new[] { 1f, 2f }) };
        var system = new VfxSystemDefinition(1, "span", "span", new[] { emitter });
        Assert.Equal(6d, VfxDurationCalculator.SystemSpan(system));
        Assert.Equal(6d, VfxDurationCalculator.Calculate(system));
        emitter = emitter with { EmissionPeriod = VfxEmissionPeriod.FromAuthored(null, 2f) };
        Assert.Equal(4d, VfxDurationCalculator.SystemSpan(system with { Emitters = new[] { emitter } }));
        emitter = emitter with { IsSingleParticle = true, TimeBeforeFirstEmission = 3f, EmitterLifetime = null };
        Assert.Equal(0d, VfxDurationCalculator.Calculate(system with { Emitters = new[] { emitter } }));
    }

    private static (VfxSystemDefinition Parent, VfxSystemDefinition Child) Family(VfxEmitterDefinition parent, VfxEmitterDefinition child)
    {
        var set = new VfxChildParticleSetDefinition(new[] { new VfxChildSystemReference("child", 2, 0) }, false,
            VfxCurveF.Zero, VfxCurve3.Const(Vector3.Zero), 0);
        return (new VfxSystemDefinition(1, "parent", "parent", new[] { parent with { ChildParticleSet = set } }),
            new VfxSystemDefinition(2, "child", "child", new[] { child }));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(99, 3)]
    public void UnknownLingerPoliciesDisableLinger(byte authored, byte expected)
    {
        var definition = Parse(new BinTreeProperty[] {
            new BinTreeBitBool(Fnv1a.HashLower("isSingleParticle"), true),
            new BinTreeU8(Fnv1a.HashLower("particleLingerType"), authored) });
        Assert.Equal(expected, definition.ParticleLingerType);
        if (expected != 3) return;
        var runtime = Create(definition with { ParticleLifetime = VfxCurveF.Const(10f), ParticleLinger = 0f });
        runtime.Update(.01f); runtime.IsStopped = true; runtime.Update(.1f);
        Assert.Equal(10f, Assert.Single(runtime.Emitters[0].Particles).Life);
    }

    [Fact]
    public void MissingFirstProbabilityTableDisablesEveryChannelTable()
    {
        var table = new BinTreeStruct(0, Fnv1a.HashLower("VfxProbabilityTableData"), new BinTreeProperty[] {
            new BinTreeF32(Fnv1a.HashLower("singleValue"), 2f) });
        var empty = new BinTreeStruct(0, 0, Array.Empty<BinTreeProperty>());
        var noFirst = new BinTreeContainer(Fnv1a.HashLower("probabilityTables"), BinPropertyType.Struct,
            new BinTreeProperty[] { empty, table });
        var definition = new BinTreeStruct(0, Fnv1a.HashLower("ValueVector3"), new BinTreeProperty[] {
            new BinTreeVector3(Fnv1a.HashLower("constantValue"), Vector3.One),
            new BinTreeStruct(Fnv1a.HashLower("dynamics"), Fnv1a.HashLower("VfxAnimatedVector3fVariableData"),
                new BinTreeProperty[] { noFirst }) });
        // Empty class hashes represent null struct slots on the BIN wire.
        var parsed = Parse(new BinTreeProperty[] { new BinTreeBitBool(Fnv1a.HashLower("isSingleParticle"), true),
            new BinTreeStruct(Fnv1a.HashLower("birthVelocity"), definition.ClassHash, definition.Properties.Values) });
        Assert.Null(parsed.BirthVelocity.Value.Prob);
        var runtime = Create(parsed); runtime.Update(.01f);
        Near(Vector3.One, Assert.Single(runtime.Emitters[0].Particles).Vel);
    }

    [Fact]
    public void ChildExistenceConsumesTheStreamAfterChildSelection()
    {
        var childEmitter = Emitter() with { ChanceToNotExist = .5f };
        var (parent, child) = Family(Emitter(), childEmitter);
        var table = new VfxProbTable(null, null, 1f);
        var set = parent.Emitters[0].ChildParticleSet with { Children = new[] {
            new VfxChildSystemReference("child", 2, 0), new VfxChildSystemReference("child", 2, 0) },
            Probability = new VfxCurveF(1f, null, null, new[] { table }) };
        parent = parent with { Emitters = new[] { parent.Emitters[0] with { ChildParticleSet = set } } };
        for (int seed = 1; seed <= 32; seed++)
        {
            var graph = new VfxPlaybackGraphRuntime(parent, Matrix4x4.Identity, seed,
                new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child }, new Dictionary<uint, uint>(),
                (definition, placement, childSeed) => {
                    var runtime = new VfxPlaybackRuntime(childSeed); runtime.SetSystem(definition, placement); return runtime;
                });
            graph.Update(.01f);
            var childRuntime = graph.Runtimes[1];
            var random = VfxLtkRandom.FromState(childRuntime.InitialRandomState);
            bool absent = random.NextUnitFloat() < .5f;
            Assert.Equal(absent, childRuntime.Emitters[0].Absent);
            Assert.Equal(random.State, childRuntime.RandomState);
        }
    }

    [Fact]
    public void LegacyScrollUsesTheBirthRampAndBothLayersInheritTheBaseEmitterScroll()
    {
        var fields = new BinTreeProperty[] {
            new BinTreeBitBool(Fnv1a.HashLower("isSingleParticle"), true),
            new BinTreeBitBool(Fnv1a.HashLower("uvScrollClamp"), true),
            new BinTreeVector2(Fnv1a.HashLower("emitterUvScrollRate"), new Vector2(.2f, .3f)),
            new BinTreeStruct(Fnv1a.HashLower("LegacySimple"), Fnv1a.HashLower("VfxEmitterLegacySimple"),
                new BinTreeProperty[] { new BinTreeVector2(Fnv1a.HashLower("uvScrollRate"), new Vector2(.4f, .5f)) }),
            new BinTreeStruct(Fnv1a.HashLower("textureMult"), Fnv1a.HashLower("VfxTextureMultDefinitionData"),
                new BinTreeProperty[] { new BinTreeVector2(Fnv1a.HashLower("emitterUvScrollRateMult"), new Vector2(.8f, .9f)) }) };
        var simple = Parse(fields, simple: true);
        Assert.False(simple.RenderState.ClampUvScroll);
        Assert.Equal(new Vector2(.4f, .5f), simple.BirthUvScrollRateCurve.Value.Constant);
        Assert.Equal(new Vector2(.2f, .3f), simple.EmitterUvScrollRate);
        Assert.Equal(simple.EmitterUvScrollRate, simple.TextureMultEmitterUvScrollRate);
        var complex = Parse(fields);
        Assert.True(complex.RenderState.ClampUvScroll);
        Assert.Equal(complex.EmitterUvScrollRate, complex.TextureMultEmitterUvScrollRate);
    }

    [Fact]
    public void CullReasonsFollowTheDefinitionGatesAndKeepExplicitDisableSeparate()
    {
        var fields = new BinTreeProperty[] { new BinTreeU8(Fnv1a.HashLower("importance"), 4),
            new BinTreeU8(Fnv1a.HashLower("colorblindVisibility"), 2) };
        Assert.Equal(VfxCullReason.NoRate, Parse(fields).Culled);
        Assert.Equal(VfxCullReason.Importance, Parse(fields.Append(
            new BinTreeBitBool(Fnv1a.HashLower("isSingleParticle"), true)).ToArray()).Culled);
        var disabled = Parse(fields.Append(new BinTreeBitBool(Fnv1a.HashLower("disabled"), true)).ToArray());
        Assert.True(disabled.Disabled);
        Assert.Equal(VfxCullReason.None, disabled.Culled);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void ZeroLengthPeriodWithoutAnActiveTimeCountsNoFurtherBirths(bool variable, int count)
    {
        var runtime = Create(Emitter() with { IsSingleParticle = false, HasVariableStartTime = variable,
            Rate = VfxCurveF.Const(10f), EmissionPeriod = VfxEmissionPeriod.FromAuthored(0f, null) });
        runtime.Update(.01f); runtime.Update(.1f);
        Assert.Equal(count, runtime.LiveParticleCount);
    }

    private sealed record FixedSurface(Vector3 Position, Vector3 Normal) : IVfxEmissionSurfaceSampler
    {
        public bool TrySample(float time, VfxLtkRandom rng, out VfxSurfaceBirth birth)
        { birth = new(Position, Normal); return true; }
    }

    private static VfxEmitterDefinition Parse(BinTreeProperty[] fields, bool simple = false)
    {
        var emitter = new BinTreeStruct(0, Fnv1a.HashLower("VfxEmitterDefinitionData"), fields);
        var system = new BinTreeObject("Effects/Regression", "VfxSystemDefinitionData", new BinTreeProperty[] {
            new BinTreeContainer(Fnv1a.HashLower(simple ? "simpleEmitterDefinitionData" : "complexEmitterDefinitionData"), BinPropertyType.Struct, new BinTreeProperty[] { emitter }) });
        using var stream = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(stream);
        return Assert.Single(Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);
    }

    private static void Near(Vector3 expected, Vector3 actual)
    { Assert.Equal(expected.X, actual.X, 4); Assert.Equal(expected.Y, actual.Y, 4); Assert.Equal(expected.Z, actual.Z, 4); }

    private static VfxPlaybackRuntime Create(VfxEmitterDefinition emitter, Matrix4x4? definitionTransform = null)
    {
        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(new VfxSystemDefinition(1, "test", "test", new[] { emitter }, Transform: definitionTransform), Vector3.Zero);
        return runtime;
    }

    private static VfxEmitterDefinition Emitter() => new(
        "test", VfxCurveF.Const(1f), VfxCurveF.Const(10f), null, 0, 0, true, false, 1,
        VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One), null,
        null, null, null, VfxCurve3.Const(Vector3.Zero), "test.dds", Vector2.One, 1, false, false);
}
