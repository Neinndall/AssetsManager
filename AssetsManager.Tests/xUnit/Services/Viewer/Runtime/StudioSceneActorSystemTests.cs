using System;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Tests.xUnit.Services.Viewer.Vfx;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Runtime;

public sealed class StudioSceneActorSystemTests
{
    [Fact]
    public void ExplicitSystemStaysVisibleWithoutCharacterEffectsWhilePlayingOrPaused()
    {
        using var fixture = new Fixture();
        // Skin-driven previews obey the effects switch; explicitly opened Systems retain visibility.
        Assert.False(fixture.Runtime.ShouldRenderParticles(characterEffectsEnabled: false));
        Assert.True(fixture.Runtime.ShouldRenderParticles(characterEffectsEnabled: true));
        fixture.Session.Seek(1);
        var graph = Assert.Single(fixture.Session.Graphs);
        fixture.Runtime.AdoptPlayingSystem(fixture.Playback);

        foreach (bool playing in new[] { true, false, true })
        {
            fixture.Runtime.Advance(0.1f, playing, 1f, fixture.Actor);
            Assert.True(fixture.Runtime.ShouldRenderParticles(characterEffectsEnabled: false));
            Assert.Same(graph, Assert.Single(fixture.Session.Graphs));
            Assert.True(fixture.Session.LiveParticleCount > 0);
        }

        fixture.Session.SetSystem(null);
        Assert.False(fixture.Runtime.ShouldRenderParticles(characterEffectsEnabled: false));
        Assert.False(fixture.Runtime.ShouldRenderParticles(characterEffectsEnabled: true));
    }

    [Fact]
    public void HandoverKeepsLiveGraphsParticlesAndClockPastTheOpeningIdleDuration()
    {
        using var fixture = new Fixture();
        fixture.Session.Seek(4.25);
        var graph = Assert.Single(fixture.Session.Graphs);
        Assert.True(fixture.Session.LiveParticleCount > 0);
        var particles = graph.Root.Emitters[0].Particles.Select(particle => (particle.Age, particle.Pos)).ToArray();
        fixture.Session.SetPinnedBirthChance(0.8f);
        var rig = fixture.Session.RigSettings;
        for (int focus = 0; focus < 4; focus++)
        {
            fixture.Runtime.AdoptPlayingSystem(fixture.Playback);
            fixture.Runtime.SetGameStates(Array.Empty<string>());
            fixture.Runtime.Advance(1f, false, 2f, fixture.Actor);
            Assert.Same(graph, Assert.Single(fixture.Session.Graphs));
            Assert.Equal(4.25, fixture.Session.PlaybackTime, 5);
            Assert.Equal(particles, graph.Root.Emitters[0].Particles.Select(particle => (particle.Age, particle.Pos)).ToArray());
            Assert.Equal(rig, fixture.Session.RigSettings);
            Assert.Equal(0.8f, fixture.Session.PinnedBirthChance);
            Assert.Null(fixture.Runtime.Clip);
        }
        fixture.Runtime.Advance(0.1f, true, 2f, fixture.Actor);
        Assert.Equal(4.35, fixture.Session.PlaybackTime, 4);
        Assert.Same(graph, Assert.Single(fixture.Session.Graphs));
        Assert.Equal(fixture.Playback.Duration, fixture.Runtime.LoopDuration);
    }

    [Fact]
    public void ActorsKeepTheirOwnSystemSpeedAndPauseWhenAnotherActorChanges()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        first.Session.Seek(1);
        second.Session.Seek(2);
        first.Runtime.AdoptPlayingSystem(first.Playback with { Speed = 0.5f, FlexChannels = new(1, 2, 3) });
        second.Runtime.AdoptPlayingSystem(second.Playback with { Speed = 2f, FlexChannels = new(4, 5, 6) });
        first.Runtime.Advance(0.1f, true, 2f, first.Actor, transportTime: 9);
        second.Runtime.Advance(0.1f, true, 0.5f, second.Actor, transportTime: 0);
        Assert.Equal(1.05, first.Session.PlaybackTime, 4);
        Assert.Equal(2.2, second.Session.PlaybackTime, 4);
        second.Runtime.Advance(0.1f, false, 1f, second.Actor);
        first.Runtime.Advance(0.1f, true, 1f, first.Actor);
        Assert.Equal(1.1, first.Session.PlaybackTime, 4);
        Assert.Equal(2.2, second.Session.PlaybackTime, 4);
        Assert.Equal(new Vector3(1, 2, 3), first.Runtime.SystemPlayback.FlexChannels);
        Assert.Equal(new Vector3(4, 5, 6), second.Runtime.SystemPlayback.FlexChannels);
    }

    [Fact]
    public void InspectorResumeRequiresTheSameSystemDefinitionAndLiveSession()
    {
        using var fixture = new Fixture();
        using var other = new Fixture();
        fixture.Runtime.AdoptPlayingSystem(fixture.Playback);
        Assert.True(fixture.Runtime.CanResumeSystem(fixture.Definition.PathHash, fixture.Definition, fixture.Session));
        Assert.False(fixture.Runtime.CanResumeSystem(999, fixture.Definition, fixture.Session));
        Assert.False(fixture.Runtime.CanResumeSystem(fixture.Definition.PathHash, other.Definition, fixture.Session));
        Assert.False(fixture.Runtime.CanResumeSystem(fixture.Definition.PathHash, fixture.Definition, other.Session));
        fixture.Session.SetSystem(null);
        Assert.False(fixture.Runtime.CanResumeSystem(fixture.Definition.PathHash, fixture.Definition, fixture.Session));
    }

    [Fact]
    public void SystemKeepsItsCustomLoopAndDoesNotFallBackToIdleWhenItEnds()
    {
        using var fixture = new Fixture();
        fixture.Session.Seek(1.95);
        var graph = Assert.Single(fixture.Session.Graphs);
        fixture.Runtime.AdoptPlayingSystem(fixture.Playback with { Looping = true, LoopStart = 1, LoopEnd = 2 });
        fixture.Runtime.Advance(0.1f, true, 1f, fixture.Actor);
        Assert.Equal(1, fixture.Session.PlaybackTime, 4);
        Assert.Same(graph, Assert.Single(fixture.Session.Graphs));

        fixture.Session.Seek(1.95);
        fixture.Runtime.AdoptPlayingSystem(fixture.Playback with { Duration = 2 });
        fixture.Runtime.Advance(0.1f, true, 1f, fixture.Actor);
        Assert.True(fixture.Actor.IsPlaybackPaused);
        Assert.NotNull(fixture.Runtime.SystemPlayback);
        Assert.Same(graph, Assert.Single(fixture.Session.Graphs));
        Assert.Null(fixture.Runtime.Clip);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly VfxLoadingService _loading = new();
        internal readonly VfxSystemDefinition Definition;
        internal readonly VfxRenderSession Session;
        internal readonly StudioSceneActorRuntime Runtime;
        internal readonly StudioSceneActor Actor = new(new StudioSkinItem { OwnerName = "Evelynn", SkinIndex = 24 });
        internal readonly StudioSystemPlayback Playback;

        internal Fixture()
        {
            var emitter = VfxBeamSupportTests.Definition() with
            {
                Rate = VfxCurveF.Const(20), ParticleLifetime = VfxCurveF.Const(10), EmitterLifetime = 10,
                TexturePath = null, PrimitiveKind = VfxPrimitiveKind.CameraQuad, Beam = null
            };
            Definition = new(1, "Standalone", "Effects/Standalone", new[] { emitter });
            Session = new(loadingService: _loading);
            Session.SetSystem(new VfxSystemModel
            {
                Name = Definition.Name, Definition = Definition, TotalDuration = 20, PlaybackSeed = 123
            });
            Runtime = new(_loading, new VfxLoadingService.Bundle(), null, new SceneModel(), null,
                new AnimationService(), Session, null, null);
            Playback = new(Definition.PathHash, Session.RigDuration, 1, false, 0, Session.RigDuration,
                Vector3.Zero, Array.Empty<int>(), Array.Empty<int>());
        }

        public void Dispose()
        {
            Runtime.ReleaseCpuState();
            Session.Dispose();
            _loading.Dispose();
        }
    }
}
