using System;
using System.ComponentModel;
using System.Linq;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(StudioModel.IsProjectFilesVisible) or nameof(StudioModel.RootPath))
                UpdateProjectFiles();
            if (e.PropertyName == nameof(StudioWorkspaceTab.IsAnimationSyncEnabled))
            {
                SynchronizeStudioCatalogs();
                _ = SynchronizeStudioPlaybackAsync();
            }
            else if (e.PropertyName == nameof(StudioWorkspaceTab.IsAnimationPlaybackSyncEnabled))
            {
                SynchronizeStudioTransport();
                _ = SynchronizeStudioPlaybackAsync();
            }
            else if (e.PropertyName == nameof(StudioWorkspaceTab.IsMeshSyncEnabled) &&
                     _model.SelectedWorkspaceTab.IsMeshSyncEnabled && _championModel != null)
            {
                foreach (ModelPart part in _championModel.Parts) SynchronizeStudioPart(part, textures: false);
            }
            else if (e.PropertyName == nameof(StudioWorkspaceTab.IsTextureSyncEnabled) &&
                     _model.SelectedWorkspaceTab.IsTextureSyncEnabled && _championModel != null)
            {
                foreach (ModelPart part in _championModel.Parts) SynchronizeStudioPart(part, textures: true);
            }
            else if (e.PropertyName == nameof(StudioModel.IsPlaying))
                SynchronizeStudioTransport();
            if (e.PropertyName == nameof(StudioModel.IsChromaLibraryVisible))
            {
                _discardNextSimulationDelta = true;
                SetRenderLoopRunning(_isActive && IsVisible);
            }
            if (e.PropertyName == nameof(StudioModel.TimelineVisible))
            {
                ApplyTimelineVisibility();
            }
            else if (e.PropertyName == nameof(StudioModel.SelectedSkin))
            {
                if (_model.SelectedSkin != null && !_isSwitchingWorkspaceTab)
                {
                    StudioSkinItem skin = _model.SelectedSkin;
                    StudioWorkspaceTab current = _model.SelectedWorkspaceTab;
                    StudioSceneActor member = current?.Kind == StudioWorkspaceTabKind.Skin
                        ? current.Actors.FirstOrDefault(actor => actor.HasSkin(skin))
                        : null;
                    bool staysInScene = member != null &&
                        (!_openSkinInOwnTab ||
                         string.Equals(current.Key, SkinWorkspaceKey(skin), StringComparison.OrdinalIgnoreCase));
                    if (staysInScene && !ReferenceEquals(member, current.FocusedActor))
                    {
                        // A Skin already composed into the active scene only takes the focus.
                        FocusSceneActor(member);
                        return;
                    }

                    CharacterBackdropSeed inheritedBackdrop = CaptureActiveBackdropSeed();
                    StudioWorkspaceTab skinTab = EnsureSkinWorkspaceTab(
                        skin,
                        ownTab: _openSkinInOwnTab,
                        inheritedBackdrop: inheritedBackdrop);
                    _pendingWorkspaceRestoreTab = skinTab;
                    bool adoptedBackdrop = _mapSceneRuntime != null && TryAdoptLoadedMapAsCharacterBackdrop(skinTab);
                    if (!adoptedBackdrop)
                        CancelMapLoadAndClearScene();
                    SyncSceneActorRuntimes(skinTab);
                }
                BindBrowserSkin();
            }
            else if (e.PropertyName == nameof(StudioModel.SelectedSystem))
            {
                RequestSystemInspection(_model.SelectedSystem);
            }
            else if (e.PropertyName == nameof(StudioModel.HasStandaloneSystem))
            {
                if (!_model.HasStandaloneSystem && StudioTimelineView.ChancePinPopup != null) StudioTimelineView.ChancePinPopup.IsOpen = false;
                SyncChancePinControls();
            }
            else if (e.PropertyName == nameof(StudioModel.SelectedAnimation))
            {
                if (_isUpdatingCharacterForms) return;
                if (_model.SelectedAnimation != null)
                {
                    BeginExclusivePreviewSelection(preserveCharacterPose: !_model.SelectedAnimation.IsBindPose);
                    if (_model.SelectedAnimation.IsBindPose)
                    {
                        _animationClipCancellation?.Cancel();
                        _activeAnimationClip = null;
                        ResetChampionToBindPose();
                        _model.StatusText = "Character in Bind pose (T-Pose).";
                        _model.TotalDuration = 0d;
                        _model.CurrentTime = 0d;
                        _model.IsPlaying = false;
                        _vfxRenderer?.SetSystem(null);
                        _ = SynchronizeStudioPlaybackAsync();
                        StudioViewportView.OpenTkControl?.InvalidateVisual();
                    }
                    else
                    {
                        ConfigureAnimationParameterOptions(_model.SelectedAnimation);
                        _ = PlaySelectedAnimationAsync(_model.SelectedAnimation);
                    }
                }
            }
            else if (e.PropertyName == nameof(StudioModel.SelectedSpell))
            {
                if (_model.SelectedSpell != null)
                {
                    BeginExclusivePreviewSelection();
                    RequestSpellPreview(_model.SelectedSpell);
                }
            }
            else if (e.PropertyName == nameof(StudioModel.SelectedMapVariant) &&
                     !_suppressMapVariantReload &&
                     _mapSceneRuntime != null &&
                     _model.SelectedMapVariant != null)
            {
                ReloadSelectedMapVariant();
            }
            else if (e.PropertyName == nameof(StudioModel.AnimationParameter) &&
                     !_isUpdatingAnimationParameter &&
                     _model.AnimationParameter.HasValue)
            {
                if (_model.SelectedMapNode?.Kind == MapBrowserNodeKind.Clip &&
                    _model.SelectedMapNode.Payload is MapCharacterClipSelection mapClip)
                {
                    _ = PlayMapCharacterClipAsync(mapClip, preservePlayhead: true);
                }
                else if (_model.SelectedAnimation != null)
                {
                    RebuildAnimationsForParameter(_model.AnimationParameter.Value);
                }
            }
            else if (e.PropertyName == nameof(StudioModel.SelectedCharacterForm))
            {
                if (!_isUpdatingCharacterForms)
                    ApplySelectedCharacterForm(clearManualOverrides: true, restoreTextures: true);
            }
            else if (e.PropertyName == nameof(StudioModel.CharacterBackdropEnabled))
            {
                if (!_isApplyingCharacterViewportState)
                {
                    // Enabling the chooser must not load a remembered or default map.
                    _isApplyingCharacterViewportState = true;
                    try { _model.SelectedCharacterBackdrop = null; }
                    finally { _isApplyingCharacterViewportState = false; }
                    RefreshCharacterBackdrop();
                }
            }
            else if (e.PropertyName == nameof(StudioModel.SelectedCharacterBackdrop))
            {
                if (!_isApplyingCharacterViewportState && _model.CharacterBackdropEnabled &&
                    _model.SelectedCharacterBackdrop is { } backdrop)
                    OpenCharacterBackdropScene(backdrop);
            }
            else if (e.PropertyName == nameof(StudioModel.MapStructuresVisible) ||
                     e.PropertyName == nameof(StudioModel.MapParticlesVisible))
            {
                if (_mapSceneRuntime != null)
                {
                    _mapSceneRuntime.ShowStructures = _model.MapStructuresVisible;
                    _mapSceneRuntime.ShowParticles = _model.MapParticlesVisible;
                    StudioViewportView.OpenTkControl?.InvalidateVisual();
                }
            }
            else if (e.PropertyName == nameof(StudioModel.CharacterPositionX) ||
                     e.PropertyName == nameof(StudioModel.CharacterPositionY) ||
                     e.PropertyName == nameof(StudioModel.CharacterPositionZ) ||
                     e.PropertyName == nameof(StudioModel.CharacterRotationX) ||
                     e.PropertyName == nameof(StudioModel.CharacterRotationY) ||
                     e.PropertyName == nameof(StudioModel.CharacterRotationZ) ||
                     e.PropertyName == nameof(StudioModel.CharacterScaleMultiplier))
            {
                if (!_isApplyingCharacterViewportState)
                {
                    PinFocusedPlacement();
                    ApplyCharacterPlacement();
                }
            }
            else if (e.PropertyName == nameof(StudioModel.CharacterEffectsEnabled) ||
                     e.PropertyName == nameof(StudioModel.ShowCharacterArmature) ||
                     e.PropertyName == nameof(StudioModel.ShowCharacterJointNames) ||
                     e.PropertyName == nameof(StudioModel.CharacterAutoRotate) ||
                     e.PropertyName == nameof(StudioModel.CharacterTransformGizmoEnabled) ||
                     e.PropertyName == nameof(StudioModel.InspectorVisible) ||
                     e.PropertyName == nameof(StudioModel.IsInspectorPanelVisible))
            {
                if (e.PropertyName == nameof(StudioModel.CharacterAutoRotate))
                {
                    ApplyCharacterPlacement();
                    ApplySceneActorPlacements();
                }
                if (e.PropertyName == nameof(StudioModel.CharacterTransformGizmoEnabled))
                    RefreshCharacterInteractionTarget();
                if (e.PropertyName == nameof(StudioModel.InspectorVisible) ||
                    e.PropertyName == nameof(StudioModel.IsInspectorPanelVisible))
                {
                    if (_model.IsInspectorPanelVisible)
                        CollapseInspectorSections();
                    UpdateInspectorColumnVisibility();
                }
                StudioViewportView.OpenTkControl?.InvalidateVisual();
            }
            else if (e.PropertyName == nameof(StudioModel.PreviewCameraPreset))
            {
                if (!_suppressCameraPresetFit)
                    ApplyCameraPreset(_model.PreviewCameraPreset, refit: true);
                SavePreviewDisplayPreferences();
            }
            else if (e.PropertyName == nameof(StudioModel.ShowPreviewSky) ||
                     e.PropertyName == nameof(StudioModel.ShowPreviewGrid) ||
                     e.PropertyName == nameof(StudioModel.ShowPreviewGround) ||
                     e.PropertyName == nameof(StudioModel.ShowPreviewStage) ||
                     e.PropertyName == nameof(StudioModel.PreviewViewMode) ||
                     e.PropertyName == nameof(StudioModel.PreviewWireOverlay))
            {
                StudioViewportView.OpenTkControl?.InvalidateVisual();
                SavePreviewDisplayPreferences();
            }
            else if (e.PropertyName == nameof(StudioModel.PreviewShaders))
            {
                StudioViewportView.OpenTkControl?.InvalidateVisual();
            }
        }
    }
}
