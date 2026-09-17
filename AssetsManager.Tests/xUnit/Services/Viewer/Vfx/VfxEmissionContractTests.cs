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
        Assert.InRange(particle.Pos.X, -0.0001f, 0.0001f);
        Assert.InRange(particle.Pos.Y, 21.999f, 22.001f);
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

    [Fact]
    public void BirthAccelerationIsNotIntegratedLikeLtk()
    {
        var runtime = Create(Emitter() with
        {
            ParticleLifetime = VfxCurveF.Const(10f),
            BirthAcceleration = VfxCurve3.Const(new Vector3(100f, 0f, 0f))
        });

        runtime.Update(0.01f);
        Vector3 bornAt = Assert.Single(Assert.Single(runtime.Emitters).Particles).Pos;
        runtime.Update(0.1f);

        var particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
        Assert.Equal(Vector3.Zero, particle.Vel);
        Assert.Equal(bornAt, particle.Pos);
    }

    [Fact]
    public void RateIsPeriodDoesNotReinterpretEmissionRateLikeLtk()
    {
        var runtime = Create(Emitter() with
        {
            IsSingleParticle = false,
            Rate = VfxCurveF.Const(2f),
            ParticleLifetime = VfxCurveF.Const(10f),
            RateIsPeriod = true
        });

        runtime.Update(1.01f);

        Assert.Equal(2, runtime.LiveParticleCount);
    }

    [Fact]
    public void EmitterIsLoopFlagDoesNotRestartFiniteEmissionLikeLtk()
    {
        var runtime = Create(Emitter() with
        {
            IsSingleParticle = false,
            Rate = VfxCurveF.Const(10f),
            EmitterLifetime = 0.1f,
            ParticleLifetime = VfxCurveF.Const(10f),
            IsLoop = true
        });

        runtime.Update(0.51f);

        Assert.Equal(1, runtime.LiveParticleCount);
    }

    [Fact]
    public void SingleParticleBurstUsesLtkUint16Wrap()
    {
        var runtime = Create(Emitter() with
        {
            Rate = VfxCurveF.Const(65536f),
            ParticleLifetime = VfxCurveF.Const(10f)
        });

        runtime.Update(0.01f);

        Assert.Equal(1, runtime.LiveParticleCount);
    }

    [Fact]
    public void ParticleCapacityIsSharedAcrossTheWholeSystemLikeLtk()
    {
        VfxEmitterDefinition first = Emitter() with
        {
            Name = "first",
            Rate = VfxCurveF.Const(20000f),
            ParticleLifetime = VfxCurveF.Const(10f)
        };
        VfxEmitterDefinition second = first with { Name = "second" };
        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(new VfxSystemDefinition(1, "test", "test", new[] { first, second }), Vector3.Zero);

        runtime.Update(0.01f);

        Assert.Equal(32768, runtime.LiveParticleCount);
        Assert.Equal(20000, runtime.Emitters[0].Particles.Count);
        Assert.Equal(12768, runtime.Emitters[1].Particles.Count);
    }

    [Fact]
    public void FullPoolDropsBurstInsteadOfRetryingItLaterLikeLtk()
    {
        VfxEmitterDefinition fill = Emitter() with
        {
            Name = "fill",
            Rate = VfxCurveF.Const(32768f),
            ParticleLifetime = VfxCurveF.Const(0.2f)
        };
        VfxEmitterDefinition blocked = Emitter() with
        {
            Name = "blocked",
            Rate = VfxCurveF.Const(1f),
            ParticleLifetime = VfxCurveF.Const(10f)
        };
        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(new VfxSystemDefinition(1, "test", "test", new[] { fill, blocked }), Vector3.Zero);

        runtime.Update(0.01f);
        Assert.Equal(32768, runtime.LiveParticleCount);
        Assert.Empty(runtime.Emitters[1].Particles);

        runtime.Update(0.5f);

        Assert.Equal(0, runtime.LiveParticleCount);
        Assert.Empty(runtime.Emitters[1].Particles);
    }

    [Fact]
    public void AllExistingParticlesRetireBeforeAnyEmitterConsumesFreedPoolSlots()
    {
        VfxEmitterDefinition growing = Emitter() with
        {
            Name = "growing",
            IsSingleParticle = false,
            Rate = VfxCurveF.Const(100f),
            ParticleLifetime = VfxCurveF.Const(10f)
        };
        VfxEmitterDefinition expiringFill = Emitter() with
        {
            Name = "expiring",
            Rate = VfxCurveF.Const(32767f),
            ParticleLifetime = VfxCurveF.Const(0.05f)
        };
        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(new VfxSystemDefinition(1, "test", "test", new[] { growing, expiringFill }), Vector3.Zero);

        runtime.Update(0.01f);
        Assert.Equal(32768, runtime.LiveParticleCount);

        runtime.Update(0.1f);

        Assert.Equal(11, runtime.LiveParticleCount);
        Assert.Equal(11, runtime.Emitters[0].Particles.Count);
        Assert.Empty(runtime.Emitters[1].Particles);
    }

    [Fact]
    public void BuildUpUsesLtkPrerollClockAndKeepsVisibleTimelineAtZero()
    {
        VfxEmitterDefinition emitter = Emitter() with
        {
            ParticleLifetime = VfxCurveF.Const(10f)
        };
        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(
            new VfxSystemDefinition(1, "test", "test", new[] { emitter }, BuildUpTime: 0.1f),
            Vector3.Zero);

        runtime.WarmUp();

        Assert.Equal(0f, runtime.CurrentTime);
        VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
        Assert.InRange(state.Age, 0.0999f, 0.1001f);
        VfxPlaybackRuntime.Particle particle = Assert.Single(state.Particles);
        Assert.InRange(particle.Age, 0.0832f, 0.0835f);
        Assert.Equal(1, runtime.LiveParticleCount);

        runtime.WarmUp();
        Assert.InRange(state.Age, 0.0999f, 0.1001f);
    }

    [Fact]
    public void ZeroLifetimeParticleReadsAtEndOfLifeLikeLtk()
    {
        var runtime = Create(Emitter() with { ParticleLifetime = VfxCurveF.Zero });

        runtime.Update(0.01f);

        VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
        Assert.Equal(1, state.InstanceCount);
        Assert.Equal(1f, state.Instances[11]);
    }

    [Fact]
    public void BindWeightIsNotClampedLikeLtk()
    {
        var runtime = Create(Emitter() with
        {
            ParticleLifetime = VfxCurveF.Const(10f),
            BindWeight = VfxCurveF.Const(2f)
        });
        runtime.Update(0.01f);

        runtime.SetTransform(Matrix4x4.CreateTranslation(10f, 0f, 0f));
        runtime.Update(0.1f);

        VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
        Assert.Equal(20f, particle.Pos.X, precision: 4);
    }

    [Fact]
    public void LingerRotationUsesParticleAgeRatherThanEmitterLingerProgress()
    {
        var lingerRotation = new VfxCurve3(
            Vector3.Zero,
            new[] { 0f, 1f },
            new[] { Vector3.Zero, Vector3.UnitX });
        var runtime = Create(Emitter() with
        {
            ParticleLifetime = VfxCurveF.Const(2f),
            EmitterLifetime = 0.1f,
            EmitterLinger = 0f,
            ParticleLinger = 10f,
            ParticleLingerType = 0,
            IsRotationEnabled = true,
            RotationOverLife = VfxCurve3.Const(Vector3.Zero),
            Linger = new VfxLingerDefinition(Rotation: lingerRotation)
        });
        runtime.Update(0.01f);
        runtime.IsStopped = true;

        runtime.Update(0.5f);

        VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
        Assert.InRange(particle.BirthRotation.X, 0.075f, 0.082f);
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
