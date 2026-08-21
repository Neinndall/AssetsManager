using System.Collections.Generic;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
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
        public void SelectingModelNotifiesPanelBindings()
        {
            var panel = new ViewerPanelModel();
            var model = new SceneModel { Name = "Aurora" };
            string changedProperty = null;
            panel.PropertyChanged += (_, args) => changedProperty = args.PropertyName;

            panel.SelectedModel = model;

            Assert.Same(model, panel.SelectedModel);
            Assert.Equal(nameof(ViewerPanelModel.HasSelectedModel), changedProperty);
        }
    }
}
