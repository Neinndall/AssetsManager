using System.Collections.Generic;
using AssetsManager.Views.Models.Viewer;
using Xunit;
using AssetsManager.Services.Viewer.Vfx.Semantics;

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
        public void SkinSceneTabFollowsItsFocusedActorAndCountsExtraCharacters()
        {
            var kayn = new VfxSceneActor(new VfxSkinItem { OwnerName = "Kayn", BrowserTitle = "Base", BinPath = @"C:\p\kayn\skin0.bin" });
            var rhaast = new VfxSceneActor(new VfxSkinItem { OwnerName = "Kayn", BrowserTitle = "Rhaast", BinPath = @"C:\p\kayn\skin8.bin" });
            var tab = new VfxWorkspaceTab { Key = "skin:kayn", Kind = VfxWorkspaceTabKind.Skin };
            var changed = new List<string>();
            tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            tab.Actors.Add(kayn);
            tab.FocusedActor = kayn;
            Assert.False(tab.HasExtraActors);
            Assert.Equal("Kayn · Base", tab.Title);

            tab.Actors.Add(rhaast);
            tab.FocusedActor = rhaast;

            Assert.True(tab.HasExtraActors);
            Assert.Equal(1, tab.ExtraActorCount);
            Assert.Equal("Kayn · Rhaast", tab.Title);
            Assert.True(rhaast.IsFocused);
            Assert.False(kayn.IsFocused);
            Assert.Contains(nameof(VfxWorkspaceTab.HasExtraActors), changed);
            Assert.Contains(nameof(VfxWorkspaceTab.ExtraActorCount), changed);
            Assert.Contains(nameof(VfxWorkspaceTab.FocusedActor), changed);
        }

        [Fact]
        public void SceneActorSkinIdentityUsesTheCanonicalBinPath()
        {
            var skin = new VfxSkinItem { BinPath = @"C:\p\kayn\skins\skin0.bin" };
            var sameFile = new VfxSkinItem { BinPath = @"C:\P\Kayn\skins\..\skins\SKIN0.bin" };
            var other = new VfxSkinItem { BinPath = @"C:\p\kayn\skins\skin8.bin" };
            var actor = new VfxSceneActor(skin);

            Assert.True(actor.HasSkin(skin));
            Assert.True(actor.HasSkin(sameFile));
            Assert.False(actor.HasSkin(other));
            Assert.False(actor.HasSkin(null));
        }

        [Fact]
        public void GroupSelectionSurvivesFocusChangesAndBackdropCopies()
        {
            var first = new VfxSceneActor(new VfxSkinItem { BinPath = @"C:\p\first.bin" });
            var second = new VfxSceneActor(new VfxSkinItem { BinPath = @"C:\p\second.bin" });
            var tab = new VfxWorkspaceTab { Key = "scene", Kind = VfxWorkspaceTabKind.Skin };
            tab.Actors.Add(first);
            tab.Actors.Add(second);
            tab.FocusedActor = first;
            second.IsSelected = true;

            tab.FocusedActor = second;
            Assert.True(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.False(first.IsFocused);
            Assert.True(second.IsFocused);

            VfxWorkspaceTab copy = tab.CopyForBackdrop("backdrop", "map", "Map");
            Assert.All(copy.Actors, actor => Assert.True(actor.IsSelected));
            copy.Actors[0].IsSelected = false;
            Assert.True(first.IsSelected);
        }

        [Fact]
        public void CompatibleFormsExcludeOwnerMeshMismatchesUnlessTheyReloadTheModel()
        {
            var owner = new VfxOwnerSceneContext("ASSETS/Kayn/Skin0/Kayn.skn", "ASSETS/Kayn/Skin0/Kayn.skl", 1f);
            var shared = new VfxCharacterFormDefinition(1, 0, "Darkin", null, null, MeshPath: @"assets\kayn\skin0\kayn.skn");
            var materials = new VfxCharacterFormDefinition(2, 1, "Shadow", null, null, HasMaterialOverrides: true);
            var otherMesh = new VfxCharacterFormDefinition(3, 2, "Other", null, null, MeshPath: "ASSETS/Kayn/Skin8/Rhaast.skn");
            var otherSkeleton = new VfxCharacterFormDefinition(4, 3, "Rig", null, null, SkeletonPath: "ASSETS/Kayn/Skin8/Rhaast.skl");
            var reloading = materials with { PathHash = 5, ReloadsModel = true };

            IReadOnlyList<VfxCharacterFormDefinition> compatible = VfxCharacterFormSemantics.CompatibleForms(
                new[] { shared, materials, otherMesh, otherSkeleton, reloading, null },
                owner);

            Assert.Equal(new[] { shared, reloading }, compatible);
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
