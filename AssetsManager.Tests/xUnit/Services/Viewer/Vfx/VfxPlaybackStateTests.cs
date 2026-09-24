using System.Collections.Generic;
using AssetsManager.Views.Controls.Viewer;
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
        public void InspectorProjectIdentityAndWorkspaceTabsNotifyBindings()
        {
            var model = new VfxInspectorModel();
            var changed = new List<string>();
            model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            model.RootPath = @"C:\mods\AatroxProject";

            Assert.Equal("AatroxProject", model.ProjectName);
            Assert.Equal(@"C:\mods\AatroxProject", model.ProjectPath);
            Assert.True(model.HasProject);
            Assert.Contains(nameof(VfxInspectorModel.ProjectName), changed);
            Assert.Contains(nameof(VfxInspectorModel.ProjectPath), changed);
            Assert.Contains(nameof(VfxInspectorModel.HasProject), changed);

            var first = new VfxWorkspaceTab { Key = "skin:0", Title = "Aatrox · Skin 0", Kind = VfxWorkspaceTabKind.Skin };
            var second = new VfxWorkspaceTab { Key = "skin:1", Title = "Aatrox · Skin 1", Kind = VfxWorkspaceTabKind.Skin };
            model.WorkspaceTabs.Add(first);
            model.WorkspaceTabs.Add(second);
            model.NotifyWorkspaceTabsChanged();
            Assert.True(model.HasWorkspaceTabs);

            model.SelectedWorkspaceTab = first;
            Assert.True(first.IsSelected);
            Assert.False(second.IsSelected);

            model.SelectedWorkspaceTab = second;
            Assert.False(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.Same(second, model.SelectedWorkspaceTab);
            Assert.Contains(nameof(VfxInspectorModel.HasWorkspaceTabs), changed);
            Assert.Contains(nameof(VfxInspectorModel.SelectedWorkspaceTab), changed);
        }

        [Fact]
        public void MapWorkspaceTabIdentityCanRetargetAndNotifiesBindings()
        {
            var tab = new VfxWorkspaceTab
            {
                Key = "map:maps/mapgeometry/map11/base",
                Title = "Base",
                Subtitle = "Maps/MapGeometry/Map11/Base",
                Kind = VfxWorkspaceTabKind.Map,
                Payload = new object()
            };
            var changed = new List<string>();
            tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            object replacement = new();

            tab.Key = "map:maps/mapgeometry/map11/base_srx";
            tab.Title = "Base_SRX";
            tab.Subtitle = "Maps/MapGeometry/Map11/Base_SRX";
            tab.Payload = replacement;

            Assert.Equal("map:maps/mapgeometry/map11/base_srx", tab.Key);
            Assert.Equal("Base_SRX", tab.Title);
            Assert.Equal("Maps/MapGeometry/Map11/Base_SRX", tab.Subtitle);
            Assert.Same(replacement, tab.Payload);
            Assert.Contains(nameof(VfxWorkspaceTab.Key), changed);
            Assert.Contains(nameof(VfxWorkspaceTab.Title), changed);
            Assert.Contains(nameof(VfxWorkspaceTab.Subtitle), changed);
            Assert.Contains(nameof(VfxWorkspaceTab.Payload), changed);
        }

        [Fact]
        public void MapVariantPickerRequiresAnActiveMapPreview()
        {
            var model = new VfxInspectorModel();
            model.SetMapVariants(new[]
            {
                new MapVariantData("Default", MapPath.FromEntryPath("Maps/MapGeometry/Map11/Base")),
                new MapVariantData("SRX", MapPath.FromEntryPath("Maps/MapGeometry/Map11/Base_SRX"))
            });

            Assert.True(model.HasMultipleMapVariants);
            Assert.False(model.CanSelectMapVariant);

            model.HasMapPreview = true;
            Assert.True(model.CanSelectMapVariant);

            model.HasMapPreview = false;
            Assert.False(model.CanSelectMapVariant);
        }

        [Fact]
        public void InspectorEmitterSelectionMovesTheSelectedMarker()
        {
            var model = new VfxInspectorModel();
            var first = new VfxEmitterDiagnosticItem { Name = "First" };
            var second = new VfxEmitterDiagnosticItem { Name = "Second" };

            model.SelectedEmitter = first;

            Assert.True(first.IsSelected);
            Assert.False(second.IsSelected);
            Assert.Same(first, model.SelectedEmitter);

            model.SelectedEmitter = second;

            Assert.False(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.Same(second, model.SelectedEmitter);

            model.SelectedEmitter = null;

            Assert.False(second.IsSelected);
            Assert.False(model.HasSelectedEmitter);
        }

        [Fact]
        public void CurveKeyEditorKeepsNormalEditsBetweenNeighboringTimes()
        {
            var curve = new VfxCurveAuthoringItem { Name = "Rate", ComponentCount = 1, HasCurve = true };
            curve.Keys.Add(new VfxCurveKeyAuthoringItem
            {
                Owner = curve,
                KeyIndex = 0,
                TimeText = "0",
                XText = "1"
            });
            curve.Keys.Add(new VfxCurveKeyAuthoringItem
            {
                Owner = curve,
                KeyIndex = 1,
                TimeText = "0.5",
                XText = "2"
            });
            curve.Keys.Add(new VfxCurveKeyAuthoringItem
            {
                Owner = curve,
                KeyIndex = 2,
                TimeText = "1",
                XText = "3"
            });

            Assert.True(VfxInspectorControl.IsCurveKeyTimeWithinNeighbors(curve, 1, 0.25f));
            Assert.True(VfxInspectorControl.IsCurveKeyTimeWithinNeighbors(curve, 1, 0.75f));
            Assert.False(VfxInspectorControl.IsCurveKeyTimeWithinNeighbors(curve, 1, -0.01f));
            Assert.False(VfxInspectorControl.IsCurveKeyTimeWithinNeighbors(curve, 1, 1.01f));
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

            Assert.True(model.ShowPreviewGrid);
            Assert.False(model.ShowPreviewGround);
            Assert.False(model.ShowPreviewStage);
            Assert.Equal(1, model.PreviewDisplayCount);

            model.ShowPreviewGround = true;
            Assert.Equal(2, model.PreviewDisplayCount);
            Assert.Contains(nameof(VfxInspectorModel.ShowPreviewGround), changed);

            model.ShowPreviewStage = true;
            Assert.Equal(3, model.PreviewDisplayCount);
            Assert.Contains(nameof(VfxInspectorModel.ShowPreviewStage), changed);

            model.ShowPreviewGrid = false;
            Assert.True(model.ShowPreviewGround);
            Assert.True(model.ShowPreviewStage);
            Assert.Equal(2, model.PreviewDisplayCount);
            Assert.Contains(nameof(VfxInspectorModel.ShowPreviewGrid), changed);

            var item = new VfxEmitterDiagnosticItem { Name = "TrailDark" };
            item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            item.TrackBorderBrush = System.Windows.Media.Brushes.SlateBlue;
            Assert.Contains(nameof(VfxEmitterDiagnosticItem.TrackBorderBrush), changed);
        }
    }
}
