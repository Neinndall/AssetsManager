using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private sealed record CharacterBackdropSeed(
            MapSceneSource Source,
            MapVisibilityState Visibility,
            bool ShowParticles,
            bool ShowStructures);

        private CharacterBackdropSeed CaptureActiveBackdropSeed()
        {
            if (_mapSceneRuntime?.Scene?.Source is MapSceneSource loadedSource)
            {
                return new CharacterBackdropSeed(
                    loadedSource,
                    _mapSceneRuntime.Visibility,
                    _mapSceneRuntime.ShowParticles,
                    _mapSceneRuntime.ShowStructures);
            }

            if (_model.CharacterBackdropEnabled &&
                _model.SelectedCharacterBackdrop?.Source is MapSceneSource selectedBackdrop)
            {
                return new CharacterBackdropSeed(
                    selectedBackdrop,
                    null,
                    _model.MapParticlesVisible,
                    _model.MapStructuresVisible);
            }

            if (_model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Map &&
                _model.SelectedWorkspaceTab.Payload is MapBrowserNode { Kind: MapBrowserNodeKind.MapFile, Payload: MapSceneSource pendingMap })
            {
                return new CharacterBackdropSeed(
                    pendingMap,
                    null,
                    _model.MapParticlesVisible,
                    _model.MapStructuresVisible);
            }

            return null;
        }

        private static string SkinWorkspaceKey(VfxSkinItem skin) =>
            $"skin:{Path.GetFullPath(skin.BinPath)}";

        /// <summary>
        /// Finds or creates the Skin scene for a browser Skin. A Skin already composed into another
        /// scene reopens that scene unless the caller explicitly asks for the Skin's own tab.
        /// </summary>
        private VfxWorkspaceTab EnsureSkinWorkspaceTab(
            VfxSkinItem skin,
            bool ownTab = false,
            CharacterBackdropSeed inheritedBackdrop = null)
        {
            if (skin == null || string.IsNullOrWhiteSpace(skin.BinPath)) return null;
            string key = SkinWorkspaceKey(skin);
            VfxWorkspaceTab tab = _model.WorkspaceTabs.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (tab == null && !ownTab)
            {
                tab = _model.WorkspaceTabs.FirstOrDefault(item =>
                    item.Kind == VfxWorkspaceTabKind.Skin && item.Actors.Any(actor => actor.HasSkin(skin)));
            }

            if (tab != null)
            {
                VfxSceneActor member = tab.Actors.FirstOrDefault(actor => actor.HasSkin(skin));
                if (member != null && !ReferenceEquals(tab, _model.SelectedWorkspaceTab))
                    tab.FocusedActor = member;
            }
            else
            {
                tab = new VfxWorkspaceTab
                {
                    Key = key,
                    Kind = VfxWorkspaceTabKind.Skin
                };
                VfxSceneActor actor = CreateSceneActor(skin);
                tab.Actors.Add(actor);
                tab.FocusedActor = actor;
                if (inheritedBackdrop?.Source != null)
                {
                    string backdropKey = VfxInstallationMapCatalog.BackdropKey(inheritedBackdrop.Source);
                    if (!string.IsNullOrWhiteSpace(backdropKey))
                    {
                        tab.CharacterBackdropEnabled = true;
                        tab.CharacterBackdropKey = backdropKey;
                        tab.CharacterBackdropVisibility = inheritedBackdrop.Visibility;
                    }
                }
                _model.WorkspaceTabs.Add(tab);
                _model.NotifyWorkspaceTabsChanged();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    WorkspaceTabsScrollViewer?.ScrollToRightEnd();
                    UpdateWorkspaceTabScrollButtons();
                }));
            }

            SelectWorkspaceTab(tab);
            return tab;
        }

        private static string MapWorkspaceKey(MapSceneSource source)
        {
            string logical = VfxInstallationMapCatalog.BackdropKey(source);
            if (!string.IsNullOrWhiteSpace(logical))
                return $"map:{logical}";
            return $"map:{source?.SelectedMapFilePath ?? string.Empty}";
        }

        private static string MapWorkspaceTitle(MapSceneSource source, string fallback = null)
        {
            string title = Path.GetFileNameWithoutExtension(source?.SelectedMapFilePath);
            if (!string.IsNullOrWhiteSpace(title))
                return title;
            if (!string.IsNullOrWhiteSpace(fallback))
                return fallback;
            string path = source?.Map?.Value ?? "MAP";
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }

        private static MapBrowserNode CreateMapFileNode(MapSceneSource source, string fallbackTitle = null) =>
            new(
                MapWorkspaceTitle(source, fallbackTitle),
                MapBrowserNodeKind.MapFile,
                source?.Map?.Value,
                source);

        private VfxWorkspaceTab EnsureMapWorkspaceTab(MapBrowserNode node, bool select = true)
        {
            if (node?.Kind != MapBrowserNodeKind.MapFile || node.Payload is not MapSceneSource source)
                return null;
            string key = MapWorkspaceKey(source);
            VfxWorkspaceTab tab = _model.WorkspaceTabs.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (tab == null)
            {
                tab = new VfxWorkspaceTab
                {
                    Key = key,
                    Title = MapWorkspaceTitle(source, node.Title),
                    Subtitle = source.SelectedMapFilePath ?? node.Subtitle,
                    Kind = VfxWorkspaceTabKind.Map,
                    Payload = node
                };
                _model.WorkspaceTabs.Add(tab);
                _model.NotifyWorkspaceTabsChanged();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    WorkspaceTabsScrollViewer?.ScrollToRightEnd();
                    UpdateWorkspaceTabScrollButtons();
                }));
            }

            if (select)
                SelectWorkspaceTab(tab);
            return tab;
        }

        private MapSceneSource ResolveMapVariantSource(MapVariantData variant)
        {
            if (variant?.Map == null)
                return null;

            string wantedKey = variant.Map.Value?.Trim().ToLowerInvariant();
            VfxCharacterBackdropOption option = _model.CharacterBackdrops.FirstOrDefault(candidate =>
                string.Equals(
                    VfxInstallationMapCatalog.BackdropKey(candidate?.Source),
                    wantedKey,
                    StringComparison.OrdinalIgnoreCase));
            if (option?.Source != null)
                return option.Source;

            string selectedFile = SelectedMapFileFor(variant.Map, _model.RootPath);
            return new MapSceneSource(variant.Map, selectedFile, _model.RootPath);
        }

        private void RetargetMapWorkspaceTab(VfxWorkspaceTab tab, MapSceneSource source)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Map || source == null)
                return;

            string targetKey = MapWorkspaceKey(source);
            VfxWorkspaceTab duplicate = _model.WorkspaceTabs.FirstOrDefault(candidate =>
                !ReferenceEquals(candidate, tab) &&
                candidate.Kind == VfxWorkspaceTabKind.Map &&
                string.Equals(candidate.Key, targetKey, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                _model.WorkspaceTabs.Remove(duplicate);
                _model.NotifyWorkspaceTabsChanged();
            }

            MapBrowserNode node = CreateMapFileNode(source);
            tab.Key = targetKey;
            tab.Title = MapWorkspaceTitle(source, node.Title);
            tab.Subtitle = source.SelectedMapFilePath ?? source.Map?.Value;
            tab.Payload = node;
            _model.SelectedMapNode = node;
        }

        private void CaptureWorkspaceSelection(VfxWorkspaceTab tab)
        {
            if (tab == null) return;
            if (_dummyViewport.Camera is ProjectionCamera camera)
                tab.CameraState = VfxWorkspaceCameraState.Capture(camera, _model.PreviewCameraPreset);
            tab.CharacterEffectsEnabled = _model.CharacterEffectsEnabled;
            tab.MapEffectsEnabled = _model.MapParticlesVisible;
            tab.ShadersEnabled = _model.PreviewShaders;
            tab.StructuresVisible = _model.MapStructuresVisible;
            VfxSceneActor actor = tab.Kind == VfxWorkspaceTabKind.Skin ? tab.FocusedActor : null;
            if (actor == null)
                return;
            actor.IsPlaybackPaused = !_model.IsPlaying;

            // The tree changes SelectedSkin before PropertyChanged reaches us, while the current
            // System/Clip/Spell collections still belong to the previously focused actor. Capture
            // those live selections here instead of keying the snapshot off SelectedSkin identity.
            actor.SelectedSystemPathHash = _model.SelectedSystem?.PathHash;
            actor.SelectedAnimationFilePath = _model.SelectedAnimation?.FilePath;
            actor.SelectedAnimationGraphPathHash = _model.SelectedAnimation?.Clip?.GraphPathHash;
            actor.SelectedAnimationOwnerPathHash = _model.SelectedAnimation?.Clip?.OwnerPathHash;
            actor.ShowsBindPose = _model.SelectedAnimation?.IsBindPose == true;
            actor.SelectedSpellPathHash = _model.SelectedSpell?.PathHash;
            actor.AnimationParameter = _model.AnimationParameter;
            CaptureCharacterWorkspaceState(tab);
        }

        private void CaptureCharacterWorkspaceState(VfxWorkspaceTab tab)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Skin) return;
            tab.CharacterBackdropEnabled = _model.CharacterBackdropEnabled;
            tab.CharacterBackdropKey = VfxInstallationMapCatalog.BackdropKey(_model.SelectedCharacterBackdrop?.Source);
            if (_mapSceneIsCharacterBackdrop && _mapSceneRuntime != null)
                tab.CharacterBackdropVisibility = _mapSceneRuntime.Visibility;
            if (tab.FocusedActor != null)
                StoreFocusedPlacement(tab.FocusedActor);
        }

        private void StoreFocusedPlacement(VfxSceneActor actor)
        {
            actor.PositionX = _model.CharacterPositionX;
            actor.PositionY = _model.CharacterPositionY;
            actor.PositionZ = _model.CharacterPositionZ;
            actor.RotationX = _model.CharacterRotationX;
            actor.RotationY = _model.CharacterRotationY;
            actor.RotationZ = _model.CharacterRotationZ;
            actor.ScaleMultiplier = _model.CharacterScaleMultiplier;
        }

        /// <summary>Mirrors an actor's placement into the Inspector without marking it as a user edit.</summary>
        private void LoadFocusedPlacement(VfxSceneActor actor)
        {
            _isApplyingCharacterViewportState = true;
            try
            {
                _model.CharacterPositionX = actor.PositionX;
                _model.CharacterPositionY = actor.PositionY;
                _model.CharacterPositionZ = actor.PositionZ;
                _model.CharacterRotationX = actor.RotationX;
                _model.CharacterRotationY = actor.RotationY;
                _model.CharacterRotationZ = actor.RotationZ;
                _model.CharacterScaleMultiplier = actor.ScaleMultiplier;
            }
            finally
            {
                _isApplyingCharacterViewportState = false;
            }
        }

        private void RestoreCharacterWorkspaceState(VfxWorkspaceTab tab)
        {
            VfxSceneActor actor = tab?.Kind == VfxWorkspaceTabKind.Skin ? tab.FocusedActor : null;
            if (actor == null) return;
            LoadFocusedPlacement(actor);
            _isApplyingCharacterViewportState = true;
            try
            {
                _model.SelectedCharacterBackdrop = _model.CharacterBackdrops.FirstOrDefault(option =>
                    !string.IsNullOrWhiteSpace(tab.CharacterBackdropKey) &&
                    string.Equals(VfxInstallationMapCatalog.BackdropKey(option.Source), tab.CharacterBackdropKey, StringComparison.OrdinalIgnoreCase));
                _model.CharacterBackdropEnabled = tab.CharacterBackdropEnabled;
            }
            finally
            {
                _isApplyingCharacterViewportState = false;
            }
            ApplyCharacterPlacement();
            ApplyEffectiveCharacterSubmeshes();
            RefreshCharacterInteractionTarget();
            // Changing the focused actor keeps the scene backdrop exactly as it is.
            if (!_isSceneFocusHandover)
                EnsureCharacterBackdropRuntime(tab);
        }

        private bool TryAdoptLoadedMapAsCharacterBackdrop(VfxWorkspaceTab tab)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Skin || _mapSceneRuntime?.Scene?.Source == null)
                return false;

            string loadedKey = VfxInstallationMapCatalog.BackdropKey(_mapSceneRuntime.Scene.Source);
            if (!VfxCharacterViewportSemantics.CanAdoptLoadedBackdrop(
                    tab.CharacterBackdropEnabled,
                    tab.CharacterBackdropKey,
                    loadedKey))
            {
                return false;
            }

            ClearMapCharacterClipPreview();
            _mapSceneIsCharacterBackdrop = true;
            _mapSceneRuntime.ShowParticles = _model.MapParticlesVisible;
            _mapSceneRuntime.ShowStructures = _model.MapStructuresVisible;
            _model.HasMapPreview = true;
            PublishMapVisibilityControls(_mapSceneRuntime);
            ReplaceMapBrowserRoot(null);
            _mapGpuSceneDirty = true;

            if (tab.CharacterBackdropVisibility is MapVisibilityState wanted &&
                !wanted.Equals(_mapSceneRuntime.Visibility))
            {
                _model.SyncMapVisibility(wanted);
                _ = ApplyMapVisibilityAsync(wanted);
            }
            ApplyCharacterBackdropOrigin(_mapSceneRuntime.Scene, _mapSceneRuntime.Scene.Source);

            OpenTkControl?.InvalidateVisual();
            return true;
        }

        private void EnsureCharacterBackdropRuntime(VfxWorkspaceTab tab)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Skin ||
                !_model.CharacterBackdropEnabled ||
                _model.SelectedCharacterBackdrop?.Source == null)
            {
                if (_mapSceneIsCharacterBackdrop)
                    CancelMapLoadAndClearScene();
                return;
            }

            if (TryAdoptLoadedMapAsCharacterBackdrop(tab))
                return;

            _ = LoadCharacterBackdropAsync(_model.SelectedCharacterBackdrop);
        }

        /// <summary>
        /// Restores the focused actor's placement and its remembered System/Clip/Spell once its BIN is
        /// loaded. Returns true when an explicit selection was restored.
        /// </summary>
        private bool RestoreWorkspaceSelection(VfxWorkspaceTab tab, string loadedBinPath)
        {
            VfxSceneActor actor = tab?.Kind == VfxWorkspaceTabKind.Skin ? tab.FocusedActor : null;
            VfxSkinItem skin = actor?.Skin;
            if (skin == null ||
                !ReferenceEquals(_model.SelectedWorkspaceTab, tab) ||
                !ReferenceEquals(_model.SelectedSkin, skin) ||
                !string.Equals(Path.GetFullPath(skin.BinPath), Path.GetFullPath(loadedBinPath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Arm playback only after confirming the loaded BIN still belongs to this scene.
            // Restored Systems, Clips and Spells must retain the actor's transport state.
            _startNextPreviewPaused = actor.IsPlaybackPaused && !actor.ShowsBindPose;
            RestoreCharacterWorkspaceState(tab);

            if (actor.SelectedSystemPathHash is uint systemHash)
            {
                VfxSystemDiagnosticItem system = _model.Systems.FirstOrDefault(item => item.PathHash == systemHash);
                if (system != null)
                {
                    _model.IsRawSystemsMode = true;
                    _model.SelectedSystem = system;
                    return true;
                }
            }

            if (actor.ShowsBindPose &&
                _model.DetectedAnimations.FirstOrDefault(item => item.IsBindPose) is { } bindPose)
            {
                _model.IsAnimationMode = true;
                _model.SelectedAnimation = bindPose;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(actor.SelectedAnimationFilePath) ||
                actor.SelectedAnimationOwnerPathHash.HasValue)
            {
                AnimationClipCatalogItem animation = _model.DetectedAnimations.FirstOrDefault(item =>
                    (!actor.SelectedAnimationGraphPathHash.HasValue || item.Clip?.GraphPathHash == actor.SelectedAnimationGraphPathHash) &&
                    (!actor.SelectedAnimationOwnerPathHash.HasValue || item.Clip?.OwnerPathHash == actor.SelectedAnimationOwnerPathHash) &&
                    (string.IsNullOrWhiteSpace(actor.SelectedAnimationFilePath) ||
                     string.Equals(item.FilePath, actor.SelectedAnimationFilePath, StringComparison.OrdinalIgnoreCase)));
                if (animation != null)
                {
                    _model.IsAnimationMode = true;
                    _model.SelectedAnimation = animation;
                    if (actor.AnimationParameter.HasValue &&
                        (_model.AnimationParameter != actor.AnimationParameter || animation.HasParameterValues))
                    {
                        // A parameter change can replace an already prepared clip during adoption.
                        if (_model.AnimationParameter != actor.AnimationParameter)
                            _startNextPreviewPaused = actor.IsPlaybackPaused;
                        _model.AnimationParameter = actor.AnimationParameter;
                    }
                    return true;
                }
            }

            if (actor.SelectedSpellPathHash is uint spellHash)
            {
                VfxSpellBrowserItem spell = FindSpellByPathHash(skin.SpellItems, spellHash);
                if (spell != null)
                {
                    _model.IsAnimationMode = true;
                    _model.SelectedSpell = spell;
                    return true;
                }
            }
            return false;
        }

        private static VfxSpellBrowserItem FindSpellByPathHash(IEnumerable<object> items, uint pathHash)
        {
            foreach (object item in items ?? Array.Empty<object>())
            {
                if (item is VfxSpellBrowserItem spell && spell.PathHash == pathHash)
                    return spell;
                if (item is VfxBrowserFolder folder)
                {
                    VfxSpellBrowserItem nested = FindSpellByPathHash(folder.Children, pathHash);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private void SelectWorkspaceTab(VfxWorkspaceTab tab)
        {
            if (tab == null) return;
            VfxWorkspaceTab previous = _model.SelectedWorkspaceTab;
            if (ReferenceEquals(previous, tab)) return;
            CaptureWorkspaceSelection(previous);
            _model.SelectedWorkspaceTab = tab;
            _model.CharacterEffectsEnabled = tab.CharacterEffectsEnabled;
            _model.MapParticlesVisible = tab.MapEffectsEnabled;
            _model.PreviewShaders = tab.ShadersEnabled;
            _model.MapStructuresVisible = tab.StructuresVisible;
            RestoreWorkspaceCamera(tab);
        }

        private void ActivateWorkspaceTab(VfxWorkspaceTab tab)
        {
            if (tab == null || _isCleanedUp || ReferenceEquals(_model.SelectedWorkspaceTab, tab)) return;
            _isSwitchingWorkspaceTab = true;
            try
            {
                SelectWorkspaceTab(tab);
                switch (tab.Kind)
                {
                    case VfxWorkspaceTabKind.Skin when tab.FocusedActor?.Skin is VfxSkinItem skin:
                        _pendingWorkspaceRestoreTab = tab;
                        bool adoptedBackdrop = _mapSceneRuntime != null && TryAdoptLoadedMapAsCharacterBackdrop(tab);
                        if (!adoptedBackdrop)
                            CancelMapLoadAndClearScene();
                        _model.SelectedMapNode = null;
                        SyncSceneActorRuntimes(tab);
                        // Two scenes can focus the same Skin, so the focused actor always reloads.
                        if (!ReferenceEquals(_model.SelectedSkin, skin))
                            _model.SelectedSkin = skin;
                        else
                            BindBrowserSkin();
                        break;

                    case VfxWorkspaceTabKind.Map when tab.Payload is MapBrowserNode mapNode:
                        _pendingWorkspaceRestoreTab = null;
                        ReleaseSceneActorRuntimes();
                        if (_model.SelectedSkin != null || _championModel != null)
                        {
                            ClearLoadedSkinState();
                            _model.SelectedSkin = null;
                        }
                        _model.SelectedMapNode = mapNode;
                        HandleMapBrowserSelection(mapNode);
                        break;
                }
            }
            finally
            {
                _isSwitchingWorkspaceTab = false;
            }
        }

        private void WorkspaceTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left ||
                (sender as FrameworkElement)?.DataContext is not VfxWorkspaceTab tab)
                return;
            ActivateWorkspaceTab(tab);
            e.Handled = true;
        }

        private void CloseWorkspaceTab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not VfxWorkspaceTab tab) return;

            int index = _model.WorkspaceTabs.IndexOf(tab);
            bool wasSelected = ReferenceEquals(_model.SelectedWorkspaceTab, tab);
            if (index < 0) return;
            _model.WorkspaceTabs.RemoveAt(index);
            _model.NotifyWorkspaceTabsChanged();
            Dispatcher.BeginInvoke(new Action(UpdateWorkspaceTabScrollButtons));

            if (!wasSelected) return;
            VfxWorkspaceTab next = _model.WorkspaceTabs.Count == 0
                ? null
                : _model.WorkspaceTabs[Math.Max(0, Math.Min(index, _model.WorkspaceTabs.Count - 1))];
            if (next != null)
            {
                ActivateWorkspaceTab(next);
                return;
            }

            _isSwitchingWorkspaceTab = true;
            try
            {
                _model.SelectedWorkspaceTab = null;
                CancelMapLoadAndClearScene();
                ReleaseSceneActorRuntimes();
                ClearLoadedSkinState();
                _model.SelectedSkin = null;
                _model.SelectedMapNode = null;
                ResetCameraToConfiguredPreset();
            }
            finally
            {
                _isSwitchingWorkspaceTab = false;
            }
        }

        private void WorkspaceTabsScrollLeft_Click(object sender, RoutedEventArgs e)
            => WorkspaceTabsScrollViewer?.ScrollToHorizontalOffset(
                Math.Max(0, WorkspaceTabsScrollViewer.HorizontalOffset - 180));

        private void WorkspaceTabsScrollRight_Click(object sender, RoutedEventArgs e)
            => WorkspaceTabsScrollViewer?.ScrollToHorizontalOffset(
                WorkspaceTabsScrollViewer.HorizontalOffset + 180);

        private void WorkspaceTabsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
            => UpdateWorkspaceTabScrollButtons();

        private void UpdateWorkspaceTabScrollButtons()
        {
            if (WorkspaceTabsScrollViewer == null ||
                WorkspaceTabsScrollLeftButton == null ||
                WorkspaceTabsScrollRightButton == null)
                return;

            const double epsilon = 0.5;
            bool overflows = WorkspaceTabsScrollViewer.ScrollableWidth > epsilon;
            WorkspaceTabsScrollLeftButton.Visibility = overflows && WorkspaceTabsScrollViewer.HorizontalOffset > epsilon
                ? Visibility.Visible
                : Visibility.Collapsed;
            WorkspaceTabsScrollRightButton.Visibility = overflows &&
                                                          WorkspaceTabsScrollViewer.HorizontalOffset < WorkspaceTabsScrollViewer.ScrollableWidth - epsilon
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ClearWorkspaceTabs()
        {
            ReleaseSceneActorRuntimes();
            _model.SelectedWorkspaceTab = null;
            _model.WorkspaceTabs.Clear();
            _model.NotifyWorkspaceTabsChanged();
            Dispatcher.BeginInvoke(new Action(UpdateWorkspaceTabScrollButtons));
        }
    }
}
