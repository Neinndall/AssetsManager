using System.Collections.Generic;
using AssetsManager.Services.Viewer;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer
{
    public sealed class VfxPlaybackStateTests
    {
        [Fact]
        public void PlaybackStateNotifiesBindings()
        {
            var system = new VfxSystemModel();
            var changedProperties = new List<string>();
            system.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

            system.IsPlaying = true;
            system.Speed = 1.5;

            Assert.Contains(nameof(VfxSystemModel.IsPlaying), changedProperties);
            Assert.Contains(nameof(VfxSystemModel.Speed), changedProperties);
        }

        [Fact]
        public void SelectingVfxSystemNotifiesPanelBindings()
        {
            var panel = new ViewerPanelModel();
            var system = new VfxSystemModel { Name = "Aurora" };
            string changedProperty = null;
            panel.PropertyChanged += (_, args) => changedProperty = args.PropertyName;

            panel.SelectedVfxSystem = system;

            Assert.Same(system, panel.SelectedVfxSystem);
            Assert.Equal(nameof(ViewerPanelModel.SelectedVfxSystem), changedProperty);
        }

        [Fact]
        public void PlayingSystemGeneratesParticlesWithoutGraphicsContext()
        {
            var system = new VfxSystemModel();
            system.Emitters.Add(new VfxEmitterModel
            {
                EmissionRate = 10f,
                Lifetime = 1f
            });
            using var renderer = new GlVfxRenderer();

            renderer.SetVfxSystem(system);
            renderer.Play();
            renderer.Update(0.11f);

            Assert.Equal(1, renderer.LiveParticleCount);

            renderer.Pause();
            renderer.Update(1f);
            Assert.Equal(1, renderer.LiveParticleCount);

            renderer.Play();
            renderer.Stop();
            Assert.Equal(0, renderer.LiveParticleCount);
        }
    }
}
