using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxSystemBoundsTests
{
    [Fact]
    public void StillRigKeepsChampionSizedDefinitionFrame()
    {
        VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(System(), VfxRigPreset.Still);

        Assert.Equal(new Vector3(-100f, 0f, -100f), bounds.Min);
        Assert.Equal(new Vector3(100f, 200f, 100f), bounds.Max);
    }

    [Fact]
    public void MissileRigFramesBothEndsOfItsAuthoredFlight()
    {
        VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(System(), VfxRigPreset.Missile);

        Assert.Equal(-700f, bounds.Min.X, precision: 4);
        Assert.Equal(700f, bounds.Max.X, precision: 4);
        Assert.Equal(0f, bounds.Min.Y, precision: 4);
        Assert.Equal(200f, bounds.Max.Y, precision: 4);
    }

    [Fact]
    public void RootEmitterShapeAndTranslationExpandDefinitionBounds()
    {
        VfxEmitterDefinition emitter = Emitter() with
        {
            TranslationOverride = new Vector3(300f, 0f, 0f),
            SpawnShape = new VfxSpawnShape(
                VfxSpawnShapeKind.Box,
                VfxCurve3.Const(Vector3.Zero),
                Array.Empty<Vector3>(),
                Array.Empty<VfxCurveF>(),
                Size: new Vector3(50f, 10f, 20f),
                Flags: 1)
        };

        VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(System(emitter), VfxRigPreset.Still);

        Assert.Equal(-100f, bounds.Min.X, precision: 4);
        Assert.Equal(350f, bounds.Max.X, precision: 4);
    }

    [Fact]
    public void DisabledEmitterDoesNotExpandDefinitionBounds()
    {
        VfxEmitterDefinition disabled = Emitter() with
        {
            Disabled = true,
            SpawnShape = new VfxSpawnShape(
                VfxSpawnShapeKind.Sphere,
                VfxCurve3.Const(Vector3.Zero),
                Array.Empty<Vector3>(),
                Array.Empty<VfxCurveF>(),
                Radius: 900f)
        };

        VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(System(disabled), VfxRigPreset.Still);

        Assert.Equal(new Vector3(-100f, 0f, -100f), bounds.Min);
        Assert.Equal(new Vector3(100f, 200f, 100f), bounds.Max);
    }

    [Fact]
    public void RootEmitterShapeStartsAtTheRigHeight()
    {
        VfxEmitterDefinition emitter = Emitter() with
        {
            SpawnShape = new VfxSpawnShape(
                VfxSpawnShapeKind.Point,
                VfxCurve3.Const(new Vector3(0f, 300f, 0f)),
                Array.Empty<Vector3>(),
                Array.Empty<VfxCurveF>())
        };

        VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(System(emitter), VfxRigPreset.Still);

        Assert.Equal(408f, bounds.Max.Y, precision: 4);
    }

    [Fact]
    public void SystemTransformMovesRigAndEmitterBoundsTogether()
    {
        VfxEmitterDefinition emitter = Emitter() with
        {
            SpawnShape = new VfxSpawnShape(
                VfxSpawnShapeKind.Point,
                VfxCurve3.Const(new Vector3(250f, 0f, 0f)),
                Array.Empty<Vector3>(),
                Array.Empty<VfxCurveF>())
        };
        VfxSystemDefinition system = System(emitter) with
        {
            Transform = Matrix4x4.CreateTranslation(50f, 0f, 0f)
        };

        VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(system, VfxRigPreset.Still);

        Assert.Equal(-50f, bounds.Min.X, precision: 4);
        Assert.Equal(308f, bounds.Max.X, precision: 4);
    }

    [Fact]
    public void SphereShapeUsesItsAuthoredRadiusAroundTheEmitterOffset()
    {
        VfxEmitterDefinition emitter = Emitter() with
        {
            TranslationOverride = new Vector3(25f, 0f, 0f),
            SpawnShape = new VfxSpawnShape(
                VfxSpawnShapeKind.Sphere,
                VfxCurve3.Const(Vector3.Zero),
                Array.Empty<Vector3>(),
                Array.Empty<VfxCurveF>(),
                Radius: 400f)
        };

        VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(System(emitter), VfxRigPreset.Still);

        Assert.Equal(-375f, bounds.Min.X, precision: 4);
        Assert.Equal(425f, bounds.Max.X, precision: 4);
    }

    [Fact]
    public void CylinderShapeRunsUpwardFromTheEmitterRatherThanAroundItsMiddle()
    {
        VfxEmitterDefinition emitter = Emitter() with
        {
            SpawnShape = new VfxSpawnShape(
                VfxSpawnShapeKind.Cylinder,
                VfxCurve3.Const(Vector3.Zero),
                Array.Empty<Vector3>(),
                Array.Empty<VfxCurveF>(),
                Radius: 25f,
                Height: 350f)
        };

        VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(System(emitter), VfxRigPreset.Still);

        Assert.Equal(450f, bounds.Max.Y, precision: 4);
        Assert.Equal(-100f, bounds.Min.X, precision: 4);
        Assert.Equal(100f, bounds.Max.X, precision: 4);
    }

    [Fact]
    public void LegacyShapeAddsItsOffsetAndBirthTranslationAtOpeningPhase()
    {
        VfxEmitterDefinition emitter = Emitter() with
        {
            SpawnShape = new VfxSpawnShape(
                VfxSpawnShapeKind.Legacy,
                VfxCurve3.Const(new Vector3(20f, 0f, 0f)),
                Array.Empty<Vector3>(),
                Array.Empty<VfxCurveF>(),
                BirthTranslation: VfxCurve3.Const(new Vector3(30f, 0f, 0f)))
        };

        VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(System(emitter), VfxRigPreset.Still);

        Assert.Equal(100f, bounds.Max.X, precision: 4);
        Assert.Equal(-100f, bounds.Min.X, precision: 4);
    }

    [Fact]
    public void ChildDefinitionsDoNotExpandTheRootDefinitionBounds()
    {
        VfxEmitterDefinition hugeChildEmitter = Emitter() with
        {
            SpawnShape = new VfxSpawnShape(
                VfxSpawnShapeKind.Sphere,
                VfxCurve3.Const(Vector3.Zero),
                Array.Empty<Vector3>(),
                Array.Empty<VfxCurveF>(),
                Radius: 5000f)
        };
        VfxSystemDefinition child = new(2u, "child", "child", new[] { hugeChildEmitter });
        VfxEmitterDefinition root = Emitter() with
        {
            ChildParticleSet = new VfxChildParticleSetDefinition(
                new[] { new VfxChildSystemReference("child", child.PathHash, 0u) },
                false,
                VfxCurveF.Zero,
                VfxCurve3.Const(Vector3.Zero),
                0,
                Array.Empty<string>())
        };

        VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(System(root), VfxRigPreset.Still);

        Assert.Equal(new Vector3(-100f, 0f, -100f), bounds.Min);
        Assert.Equal(new Vector3(100f, 200f, 100f), bounds.Max);
    }

    [Fact]
    public void RigGroundUsesTheOpeningStopWithoutRigHeight()
    {
        Vector3 ground = VfxSystemBounds.Ground(System(), VfxRigSettings.ForPreset(VfxRigPreset.Missile));

        // AssetsManager renders directly in engine/OpenGL space; LTK mirrors X only while
        // crossing into Three.js, so the equivalent opening ground remains at engine -X here.
        Assert.Equal(new Vector3(-600f, 0f, 0f), ground);
    }

    [Fact]
    public void PerspectiveFrameUsesLtkBoundingSphereAndMargin()
    {
        var bounds = new VfxDefinitionBounds(
            new Vector3(-100f, 0f, -100f),
            new Vector3(100f, 200f, 100f));

        VfxCameraFrame frame = VfxSystemBounds.FramePerspective(
            bounds,
            45f,
            1f,
            Vector3.UnitZ);

        float radius = new Vector3(200f, 200f, 200f).Length() * 0.5f;
        float expectedDistance = radius / MathF.Sin(22.5f * MathF.PI / 180f) * 1.15f;
        Assert.Equal(new Vector3(0f, 100f, 0f), frame.Target);
        Assert.Equal(0f, frame.Position.X, precision: 4);
        Assert.Equal(100f, frame.Position.Y, precision: 4);
        Assert.Equal(expectedDistance, frame.Position.Z, precision: 3);
    }

    private static VfxSystemDefinition System(params VfxEmitterDefinition[] emitters)
        => new(1u, "bounds", "bounds", emitters);

    private static VfxEmitterDefinition Emitter()
        => new(
            "emitter",
            VfxCurveF.Const(1f),
            VfxCurveF.Const(1f),
            1f,
            0f,
            0f,
            false,
            false,
            0,
            VfxCurve3.Const(Vector3.One),
            null,
            VfxCurve4.Const(Vector4.One),
            null,
            null,
            null,
            null,
            VfxCurve3.Const(Vector3.Zero),
            string.Empty,
            Vector2.One,
            1,
            false,
            false);
}
