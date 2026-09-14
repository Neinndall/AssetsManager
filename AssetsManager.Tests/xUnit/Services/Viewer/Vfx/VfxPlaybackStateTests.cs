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

        [Fact]
        public void EmitterDiagnosticItemNotifiesSoloMuteAndVisibilityEvents()
        {
            var item = new VfxEmitterDiagnosticItem { Name = "Sparks" };
            var changed = new List<string>();
            item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            int visibilityChangedCount = 0;
            item.OnVisibilityStateChanged += _ => visibilityChangedCount++;

            item.IsSolo = true;
            item.IsMuted = true;
            item.PrimitiveKindName = "MESH";

            Assert.True(item.IsSolo);
            Assert.True(item.IsMuted);
            Assert.Equal("MESH", item.PrimitiveKindName);
            Assert.Contains(nameof(VfxEmitterDiagnosticItem.IsSolo), changed);
            Assert.Contains(nameof(VfxEmitterDiagnosticItem.IsMuted), changed);
            Assert.Contains(nameof(VfxEmitterDiagnosticItem.PrimitiveKindName), changed);
            Assert.Equal(2, visibilityChangedCount);
        }

        [Fact]
        public void InspectorModelSoloMuteAndMeshPropertiesNotifyBindings()
        {
            var model = new VfxInspectorModel();
            var changed = new List<string>();
            model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            model.HasAnySolo = true;
            model.IsAllMuted = true;
            model.ShowChampionMesh = false;
            model.HasChampionMesh = true;

            Assert.True(model.HasAnySolo);
            Assert.True(model.IsAllMuted);
            Assert.False(model.ShowChampionMesh);
            Assert.True(model.HasChampionMesh);

            Assert.Contains(nameof(VfxInspectorModel.HasAnySolo), changed);
            Assert.Contains(nameof(VfxInspectorModel.IsAllMuted), changed);
            Assert.Contains(nameof(VfxInspectorModel.ShowChampionMesh), changed);
            Assert.Contains(nameof(VfxInspectorModel.HasChampionMesh), changed);
        }

        [Fact]
        public void InspectorModelEmitterFilterAndPreviewPropertiesNotifyBindings()
        {
            var model = new VfxInspectorModel();
            var changed = new List<string>();
            model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            model.EmitterFilterText = "Trail";

            Assert.Equal("Trail", model.EmitterFilterText);
            Assert.True(model.HasEmitterFilter);
            Assert.Contains(nameof(VfxInspectorModel.EmitterFilterText), changed);
            Assert.Contains(nameof(VfxInspectorModel.HasEmitterFilter), changed);

            var item = new VfxEmitterDiagnosticItem { Name = "TrailDark" };
            item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            item.TrackBorderBrush = System.Windows.Media.Brushes.SlateBlue;
            Assert.Contains(nameof(VfxEmitterDiagnosticItem.TrackBorderBrush), changed);
        }
    }
}
