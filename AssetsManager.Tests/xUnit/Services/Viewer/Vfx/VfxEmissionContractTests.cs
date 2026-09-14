using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxEmissionContractTests
{
    [Fact]
    public void SingleParticleEmitsAuthoredBurstOnlyOnce()
    {
        var runtime = Create(Emitter() with { Rate = VfxCurveF.Const(4), ParticleLifetime = VfxCurveF.Const(0.2f) });
        runtime.Update(0.01f);
        Assert.Equal(4, runtime.LiveParticleCount);
        runtime.Update(0.5f);
        Assert.Equal(0, runtime.LiveParticleCount);
        Assert.True(runtime.IsComplete);
    }

    [Fact]
    public void ZeroRateStillHasOneInitialEmission()
    {
        var runtime = Create(Emitter() with { IsSingleParticle = false, Rate = VfxCurveF.Zero });
        runtime.Update(0.01f);
        Assert.Equal(1, runtime.LiveParticleCount);
        runtime.Update(0.1f);
        Assert.Equal(1, runtime.LiveParticleCount);
    }

    [Fact]
    public void DelayDoesNotExtendTheEmitterLifetime()
    {
        var emitter = Emitter() with { TimeBeforeFirstEmission = 0.5f, EmitterLifetime = 0.2f };
        var runtime = Create(emitter);
        runtime.Update(0.75f);
        Assert.Equal(0, runtime.LiveParticleCount);
        Assert.True(runtime.IsComplete);
        Assert.Equal(0, VfxDurationCalculator.Calculate(new VfxSystemDefinition(1, "test", "test", new[] { emitter })));
    }

    [Fact]
    public void EmitterBirthCurvesIncludeTimeBeforeEmission()
    {
        var runtime = Create(Emitter() with
        {
            TimeBeforeFirstEmission = 0.5f,
            EmitterLifetime = 1f,
            BirthScale = new VfxCurve3(Vector3.Zero, new[] { 0f, 1f }, new[] { Vector3.Zero, new Vector3(10) })
        });
        runtime.Update(0.5f);
        var particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
        Assert.InRange(particle.BirthSize.X, 4.99f, 5.01f);
    }

    [Fact]
    public void AuthoredOverridesTransformSpawnPositionVelocityAndSize()
    {
        var runtime = Create(Emitter() with
        {
            EmitterPosition = VfxCurve3.Const(Vector3.UnitX),
            TranslationOverride = new Vector3(10, 0, 0),
            RotationOverride = new Vector3(0, 0, 90),
            ScaleOverride = new Vector3(2),
            BirthVelocity = VfxCurve3.Const(Vector3.UnitX)
        });
        runtime.Update(0.01f);
        var particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
        Assert.Equal(10, particle.Pos.X, 4);
        Assert.InRange(particle.Pos.Y, 2, 2.03f);
        Assert.Equal(2, particle.Vel.Y, 4);
        Assert.Equal(new Vector3(2), particle.BirthSize);
    }

    [Fact]
    public void StoppedContinuousEmitterCompletesAfterItsParticlesDie()
    {
        var runtime = Create(Emitter() with { IsSingleParticle = false });
        runtime.Update(0.01f);
        runtime.IsStopped = true;
        runtime.Update(2);
        Assert.True(runtime.IsComplete);
    }

    private static VfxPlaybackRuntime Create(VfxEmitterDefinition emitter)
    {
        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(new VfxSystemDefinition(1, "test", "test", new[] { emitter }), Vector3.Zero);
        return runtime;
    }

    private static VfxEmitterDefinition Emitter() => new(
        "test", VfxCurveF.Const(1), VfxCurveF.Const(1), null, 0, 0, true, false, 1,
        VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One), null,
        null, null, null, VfxCurve3.Const(Vector3.Zero), "test.dds", Vector2.One, 1, false, false);
}
