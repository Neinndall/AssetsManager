using System;
using System.ComponentModel;
using System.Linq;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VfxInspectorModel.TimelineVisible))
            {
                ApplyTimelineVisibility();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedSkin))
            {
                if (_model.SelectedSkin != null && !_isSwitchingWorkspaceTab)
                {
                    VfxSkinItem skin = _model.SelectedSkin;
                    VfxWorkspaceTab current = _model.SelectedWorkspaceTab;
                    VfxSceneActor member = current?.Kind == VfxWorkspaceTabKind.Skin
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
                    VfxWorkspaceTab skinTab = EnsureSkinWorkspaceTab(
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
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedSystem))
            {
                RequestSystemInspection(_model.SelectedSystem);
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.HasStandaloneSystem))
            {
                if (!_model.HasStandaloneSystem && ChancePinPopup != null) ChancePinPopup.IsOpen = false;
                SyncChancePinControls();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedAnimation))
            {
                if (_isUpdatingCharacterForms) return;
                if (_model.SelectedAnimation != null)
                {
                    BeginExclusivePreviewSelection();
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
                        OpenTkControl?.InvalidateVisual();
                    }
                    else
                    {
                        ConfigureAnimationParameterOptions(_model.SelectedAnimation);
                        _ = PlaySelectedAnimationAsync(_model.SelectedAnimation);
                    }
                }
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedSpell))
            {
                if (_model.SelectedSpell != null)
                {
                    BeginExclusivePreviewSelection();
                    RequestSpellPreview(_model.SelectedSpell);
                }
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedMapVariant) &&
                     !_suppressMapVariantReload &&
                     _mapSceneRuntime != null &&
                     _model.SelectedMapVariant != null)
            {
                ReloadSelectedMapVariant();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.AnimationParameter) &&
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
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedCharacterForm))
            {
                if (!_isUpdatingCharacterForms)
                    ApplySelectedCharacterForm(clearManualOverrides: true, restoreTextures: true);
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.CharacterBackdropEnabled))
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
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedCharacterBackdrop))
            {
                if (!_isApplyingCharacterViewportState && _model.CharacterBackdropEnabled &&
                    _model.SelectedCharacterBackdrop is { } backdrop)
                    OpenCharacterBackdropScene(backdrop);
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.MapStructuresVisible) ||
                     e.PropertyName == nameof(VfxInspectorModel.MapParticlesVisible))
            {
                if (_mapSceneRuntime != null)
                {
                    _mapSceneRuntime.ShowStructures = _model.MapStructuresVisible;
                    _mapSceneRuntime.ShowParticles = _model.MapParticlesVisible;
                    OpenTkControl?.InvalidateVisual();
                }
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.CharacterPositionX) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterPositionY) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterPositionZ) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterRotationX) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterRotationY) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterRotationZ) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterScaleMultiplier))
            {
                if (!_isApplyingCharacterViewportState)
                {
                    PinFocusedPlacement();
                    ApplyCharacterPlacement();
                }
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.CharacterEffectsEnabled) ||
                     e.PropertyName == nameof(VfxInspectorModel.ShowCharacterArmature) ||
                     e.PropertyName == nameof(VfxInspectorModel.ShowCharacterJointNames) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterAutoRotate) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterTransformGizmoEnabled) ||
                     e.PropertyName == nameof(VfxInspectorModel.InspectorVisible) ||
                     e.PropertyName == nameof(VfxInspectorModel.IsInspectorPanelVisible))
            {
                if (e.PropertyName == nameof(VfxInspectorModel.CharacterAutoRotate))
                {
                    ApplyCharacterPlacement();
                    ApplySceneActorPlacements();
                }
                if (e.PropertyName == nameof(VfxInspectorModel.CharacterTransformGizmoEnabled))
                    RefreshCharacterInteractionTarget();
                if (e.PropertyName == nameof(VfxInspectorModel.InspectorVisible) ||
                    e.PropertyName == nameof(VfxInspectorModel.IsInspectorPanelVisible))
                {
                    if (_model.IsInspectorPanelVisible)
                        CollapseInspectorSections();
                    UpdateInspectorColumnVisibility();
                }
                OpenTkControl?.InvalidateVisual();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.PreviewCameraPreset))
            {
                if (!_suppressCameraPresetFit)
                    ApplyCameraPreset(_model.PreviewCameraPreset, refit: true);
                SavePreviewDisplayPreferences();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.ShowPreviewSky) ||
                     e.PropertyName == nameof(VfxInspectorModel.ShowPreviewGrid) ||
                     e.PropertyName == nameof(VfxInspectorModel.ShowPreviewGround) ||
                     e.PropertyName == nameof(VfxInspectorModel.ShowPreviewStage) ||
                     e.PropertyName == nameof(VfxInspectorModel.PreviewViewMode) ||
                     e.PropertyName == nameof(VfxInspectorModel.PreviewWireOverlay))
            {
                OpenTkControl?.InvalidateVisual();
                SavePreviewDisplayPreferences();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.PreviewShaders))
            {
                OpenTkControl?.InvalidateVisual();
            }
        }
    }
}
