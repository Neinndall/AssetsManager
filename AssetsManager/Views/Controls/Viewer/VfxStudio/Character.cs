using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private void RefreshCharacterInteractionTarget()
        {
            // Every visible Character can be picked to take the focus; only the focused one is moved.
            _characterInteractionModels.Clear();
            SceneModel focused = _championModel != null && _model.IsSkinWorkspace && IsFocusedActorVisible
                ? _championModel
                : null;
            if (focused != null)
                _characterInteractionModels.Add(focused);
            if (_model.IsSkinWorkspace)
            {
                foreach ((VfxSceneActor actor, VfxSceneActorRuntime runtime) in _sceneActorRuntimes)
                {
                    if (actor.IsVisible)
                        _characterInteractionModels.Add(runtime.Model);
                }
            }

            if (_characterInteractionController == null)
                return;

            _characterInteractionController.IsEnabled =
                _model.IsSkinWorkspace && _model.CharacterTransformGizmoEnabled;
            _characterInteractionController.SetSelection(
                focused == null ? Array.Empty<SceneModel>() : new[] { focused },
                focused);
        }

        private void CharacterInteraction_TransformChanged(SceneModel model)
        {
            if (_isApplyingCharacterViewportState ||
                model == null ||
                !ReferenceEquals(model, _championModel) ||
                !_model.IsSkinWorkspace)
            {
                return;
            }

            _isApplyingCharacterViewportState = true;
            try
            {
                _model.CharacterPositionX = model.PositionX;
                _model.CharacterPositionY = model.PositionY;
                _model.CharacterPositionZ = model.PositionZ;
                PinFocusedPlacement();
            }
            finally
            {
                _isApplyingCharacterViewportState = false;
            }

            // The shared gizmo owns the SceneModel translation. Re-applying through the Studio placement
            // path keeps attached clip/idle VFX and the actor placement in the same world frame.
            ApplyCharacterPlacement();
        }

        private void AdvanceCharacterAutoRotate(float deltaSeconds)
        {
            if (!_model.IsSkinWorkspace || !_model.CharacterAutoRotate || deltaSeconds <= 0f ||
                (_championModel == null && _sceneActorRuntimes.Count == 0))
            {
                return;
            }

            // Match the normal Viewer: one calm 30-degree/second orbit. This is a transient layer over
            // the user's authored placement, so disabling Auto Rotate restores the exact manual yaw.
            _characterAutoRotateDegrees = VfxCharacterViewportSemantics.AdvanceAutoRotation(
                _characterAutoRotateDegrees,
                deltaSeconds);
            ApplyCharacterPlacement();
        }

        private void ClearCharacterArmatureOverlay()
        {
            _characterArmatureLines.Clear();
            _characterJointLabels.Clear();
            CharacterArmatureCanvas?.Children.Clear();
        }

        private void UpdateCharacterArmatureOverlay(Matrix4x4 viewProjection)
        {
            if (CharacterArmatureCanvas == null ||
                !_model.IsSkinWorkspace ||
                !_model.ShowCharacterArmature ||
                !IsFocusedActorVisible ||
                _championAwaitingFirstPose ||
                _championModel?.Skeleton?.Joints == null ||
                _championModel.Skeleton.Joints.Count == 0)
            {
                if (CharacterArmatureCanvas != null)
                    CharacterArmatureCanvas.Visibility = Visibility.Collapsed;
                return;
            }

            var skeleton = _championModel.Skeleton;
            IReadOnlyList<Matrix4x4> pose =
                _championModel.CurrentAnimation != null &&
                _championAnimationService?.WorldBoneTransforms?.Count == skeleton.Joints.Count
                    ? _championAnimationService.WorldBoneTransforms
                    : EnsureChampionBindWorldTransforms(skeleton);
            if (pose == null || pose.Count != skeleton.Joints.Count)
            {
                CharacterArmatureCanvas.Visibility = Visibility.Collapsed;
                return;
            }

            CharacterArmatureCanvas.Visibility = Visibility.Visible;
            Matrix4x4 modelWorld = GlMeshRenderer.CreateWorldMatrix(_championModel, mirrorCharacterX: true);
            double width = Math.Max(1d, OpenTkControl.ActualWidth);
            double height = Math.Max(1d, OpenTkControl.ActualHeight);
            int lineIndex = 0;
            int labelIndex = 0;

            for (int index = 0; index < skeleton.Joints.Count; index++)
            {
                Matrix4x4 bone = pose[index];
                Vector3 world = Vector3.Transform(new Vector3(bone.M41, bone.M42, bone.M43), modelWorld);
                bool childVisible = TryProjectToViewport(world, viewProjection, width, height, out Point child);

                int parentIndex = (int)skeleton.Joints[index].ParentId;
                if (parentIndex >= 0 && parentIndex < skeleton.Joints.Count)
                {
                    Matrix4x4 parentBone = pose[parentIndex];
                    Vector3 parentWorld = Vector3.Transform(
                        new Vector3(parentBone.M41, parentBone.M42, parentBone.M43),
                        modelWorld);
                    bool parentVisible = TryProjectToViewport(parentWorld, viewProjection, width, height, out Point parent);
                    System.Windows.Shapes.Line line = EnsureCharacterArmatureLine(lineIndex++);
                    if (childVisible && parentVisible)
                    {
                        line.X1 = parent.X;
                        line.Y1 = parent.Y;
                        line.X2 = child.X;
                        line.Y2 = child.Y;
                        line.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        line.Visibility = Visibility.Collapsed;
                    }
                }

                if (_model.ShowCharacterJointNames)
                {
                    TextBlock label = EnsureCharacterJointLabel(labelIndex++);
                    if (childVisible)
                    {
                        label.Text = skeleton.Joints[index].Name ?? $"joint {index}";
                        Canvas.SetLeft(label, child.X + 4d);
                        Canvas.SetTop(label, child.Y - 8d);
                        label.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        label.Visibility = Visibility.Collapsed;
                    }
                }
            }

            for (int index = lineIndex; index < _characterArmatureLines.Count; index++)
                _characterArmatureLines[index].Visibility = Visibility.Collapsed;
            for (int index = labelIndex; index < _characterJointLabels.Count; index++)
                _characterJointLabels[index].Visibility = Visibility.Collapsed;
        }

        private IReadOnlyList<Matrix4x4> EnsureChampionBindWorldTransforms(LeagueToolkit.Core.Animation.RigResource skeleton)
        {
            if (!ReferenceEquals(_championBindSkeleton, skeleton) ||
                _championBindWorldTransforms.Length != skeleton.Joints.Count)
            {
                _championBindSkeleton = skeleton;
                _championBindBoneTransformProvider = AnimationService.CreateBindBoneTransformProvider(skeleton);
                _championBindSkinningMatrices = AnimationService.CreateBindSkinningMatrices(skeleton);
                _championBindWorldTransforms = AnimationService.CreateBindWorldTransforms(skeleton);
            }
            return _championBindWorldTransforms;
        }

        private System.Windows.Shapes.Line EnsureCharacterArmatureLine(int index)
        {
            while (_characterArmatureLines.Count <= index)
            {
                var line = new System.Windows.Shapes.Line
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                    StrokeThickness = 1.25,
                    Opacity = 0.9,
                    SnapsToDevicePixels = true
                };
                _characterArmatureLines.Add(line);
                CharacterArmatureCanvas.Children.Add(line);
            }
            return _characterArmatureLines[index];
        }

        private TextBlock EnsureCharacterJointLabel(int index)
        {
            while (_characterJointLabels.Count <= index)
            {
                var label = new TextBlock
                {
                    FontSize = 8.5,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromRgb(224, 242, 254)),
                    Background = new SolidColorBrush(Color.FromArgb(160, 12, 16, 24)),
                    Padding = new Thickness(2, 0, 2, 0),
                    IsHitTestVisible = false
                };
                _characterJointLabels.Add(label);
                CharacterArmatureCanvas.Children.Add(label);
            }
            return _characterJointLabels[index];
        }

        private static bool TryProjectToViewport(
            Vector3 world,
            Matrix4x4 viewProjection,
            double width,
            double height,
            out Point point)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
            if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y) || !float.IsFinite(clip.W) ||
                clip.W <= 1e-5f)
            {
                point = default;
                return false;
            }

            float x = clip.X / clip.W;
            float y = clip.Y / clip.W;
            if (x < -1.15f || x > 1.15f || y < -1.15f || y > 1.15f)
            {
                point = default;
                return false;
            }

            point = new Point((x * 0.5d + 0.5d) * width, (1d - (y * 0.5d + 0.5d)) * height);
            return true;
        }

        private GridLength _savedInspectorWidth = new GridLength(330, GridUnitType.Pixel);

        private void ResetCharacterPlacement_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ResetFocusedPlacement(position: true, rotation: true, scale: true, pin: false);
        }

        private void ResetCharacterPosition_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ResetFocusedPlacement(position: true, rotation: false, scale: false, pin: true);
        }

        private void ResetCharacterRotation_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ResetFocusedPlacement(position: false, rotation: true, scale: false, pin: true);
        }

        private void ResetCharacterScale_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ResetFocusedPlacement(position: false, rotation: false, scale: true, pin: true);
        }

        /// <summary>
        /// Resets parts of the focused actor placement. Position returns to the stage origin: the
        /// active MAP backdrop origin, or the preview origin without one. A full reset unpins the actor
        /// so a later backdrop can re-anchor it; partial resets keep it pinned to the current stage.
        /// </summary>
        private void ResetFocusedPlacement(bool position, bool rotation, bool scale, bool pin)
        {
            VfxSceneActor actor = FocusedActor;
            if (actor == null) return;

            // A map-spawned Character resets to its spawn transform, facing included (Rift camps, drakes).
            double? spawnYaw = null;
            Vector3 stageOrigin = _model.HasActiveCharacterBackdrop &&
                TryGetCharacterBackdropOrigin(_mapSceneRuntime?.Scene, out Vector3 backdropOrigin, out spawnYaw)
                    ? backdropOrigin
                    : Vector3.Zero;
            _isApplyingCharacterViewportState = true;
            try
            {
                if (position)
                {
                    _model.CharacterPositionX = stageOrigin.X;
                    _model.CharacterPositionY = stageOrigin.Y;
                    _model.CharacterPositionZ = stageOrigin.Z;
                }
                if (rotation)
                {
                    _model.CharacterRotationX = 0d;
                    _model.CharacterRotationY = spawnYaw ?? 0d;
                    _model.CharacterRotationZ = 0d;
                }
                if (scale)
                    _model.CharacterScaleMultiplier = 1d;
                actor.PlacementCustomized = pin;
                actor.PlacedOnKey = ActiveCharacterBackdropKey();
            }
            finally
            {
                _isApplyingCharacterViewportState = false;
            }
            ApplyCharacterPlacement();
        }

        private long _previewShowClosedTicks;
        private long _previewViewModeClosedTicks;
        private long _previewCameraClosedTicks;
        private long _rigMenuClosedTicks;
        private long _chancePinClosedTicks;
        private long _timelineOptionsClosedTicks;

        private async void TryLoadChampionModelAsync(string searchDir)
        {
            if (string.IsNullOrEmpty(searchDir)) return;
            if (_championModel != null && ReferenceEquals(_championBundle, _activeBundle))
            {
                TryPlayPendingSpell();
                return;
            }
            int generation = ++_championLoadGeneration;
            var bundle = _activeBundle;
            try
            {
                string authored = _activeBundle?.OwnerSceneContext?.MeshPath;
                string sknPath = ResolveSknPath(authored, searchDir);

                if (!string.IsNullOrEmpty(sknPath) && File.Exists(sknPath) && SknLoadingService != null)
                {
                    var loaded = await SknLoadingService.LoadModelWithSkinBin(
                        sknPath,
                        bundle?.PrimaryBinPath,
                        searchDir);
                    if (generation != _championLoadGeneration || !ReferenceEquals(bundle, _activeBundle) || _isCleanedUp)
                    {
                        loaded?.Dispose();
                        return;
                    }
                    if (loaded != null)
                    {
                        InstallChampionModel(loaded, bundle, sknPath, searchDir, startPreview: true);
                        return;
                    }
                }
                _model.HasChampionMesh = false;
            }
            catch (Exception ex)
            {
                LogService?.LogDebug($"Champion mesh not loaded: {ex.Message}");
                _model.HasChampionMesh = false;
            }
        }

        /// <summary>
        /// Makes a loaded Character the focused owner mesh: authored scale and submeshes, skeleton,
        /// GPU skinning, forms and placement. With startPreview the Skin opens on its restored or
        /// opening clip; scene actor adoption restores its own selection afterwards instead.
        /// </summary>
        private void InstallChampionModel(
            SceneModel loaded,
            VfxLoadingService.Bundle bundle,
            string sknPath,
            string searchDir,
            bool startPreview,
            string authoredSkeletonPath = null,
            uint formPathHash = 0,
            float? formSkinScale = null)
        {
            var oldModel = _championModel;
            _championModel = loaded;
            _championBundle = bundle;
            _championSknPath = sknPath;
            _championSearchDir = searchDir;
            _championFormPathHash = formPathHash;
            _championAwaitingFirstPose = true;
            RefreshCharacterInteractionTarget();
            // Keep the owner mesh and its joint anchors in authored skinScale space. User
            // placement is an outer multiplier so attached VFX do not receive skinScale twice.
            // A reloaded form's GearData skinScale replaces the skin's when authored.
            _championAuthoredScale = formSkinScale is > 0f
                ? formSkinScale.Value
                : bundle?.OwnerSceneContext is { SkinScale: > 0f } owner
                    ? owner.SkinScale
                    : 1d;
            _championModel.Scale = _championAuthoredScale;
            // The selected form owns the baseline: a reloaded form hides its own submeshes, not the owner's.
            ApplyOwnerSubmeshVisibility(GetCharacterFormHiddenSubmeshes());
            if (oldModel != null && !ReferenceEquals(oldModel, loaded))
            {
                _championMeshRenderer?.QueueRelease(oldModel);
                oldModel.Dispose();
            }
            _model.HasChampionMesh = true;
            RebuildCharacterSubmeshOptions();
            RebuildCharacterGameStates();

            // Ensure skeleton is loaded; a form's authored SKL replaces the one found beside the SKN.
            if ((_championModel.Skeleton == null || authoredSkeletonPath != null) && !string.IsNullOrEmpty(sknPath))
            {
                string sklPath = ResolveSklPath(
                    authoredSkeletonPath ?? bundle?.OwnerSceneContext?.SkeletonPath, sknPath, searchDir);
                if (!string.IsNullOrEmpty(sklPath) && File.Exists(sklPath))
                {
                    using var sklStream = File.OpenRead(sklPath);
                    _championModel.Skeleton = new LeagueToolkit.Core.Animation.RigResource(sklStream);
                }
            }

            _model.HasCharacterSkeleton = _championModel.Skeleton?.Joints?.Count > 0;

            if (_championModel.GpuSkinningData == null &&
                _championModel.Skeleton != null &&
                _championModel.SkinnedMesh != null)
            {
                _championModel.GpuSkinningData = GpuSkinningData.TryCreate(
                    _championModel.Skeleton,
                    _championModel.SkinnedMesh,
                    _championModel.Parts,
                    out string skinningFailure);
                if (_championModel.GpuSkinningData == null)
                {
                    _model.LogMessages.Add(
                        $"[CHAMPION MESH] GPU skinning unavailable: {skinningFailure ?? "Unsupported skin data."}");
                }
            }

            int boneCount = _championModel.Skeleton?.Joints?.Count ?? 0;
            ApplyCharacterPlacement();
            _model.LogMessages.Add($"[CHAMPION MESH] Model loaded for VFX studio: {Path.GetFileName(sknPath)} (Skeleton: {(boneCount > 0 ? $"{boneCount} bones" : "None")})");
            InvalidateChampionBindPose();
            if (_model.SelectedSystem != null && _model.SelectedAnimation == null && _model.SelectedSpell == null)
                ApplyChampionBindPose();

            // The catalog may already be available from BIN load. Rebuild only when needed.
            if (_model.DetectedAnimations.Count == 0)
                BindAnimationCatalog(searchDir);
            if (!startPreview)
                return;

            // A restored explicit System/Clip/Spell keeps ownership; otherwise the Skin opens
            // on its first playable Idle, matching the authored AnimationGraph order.
            if (_model.SelectedAnimation != null)
            {
                _ = PlaySelectedAnimationAsync(_model.SelectedAnimation);
            }
            else if (_model.SelectedSystem == null && _model.SelectedSpell == null)
            {
                TrySelectOpeningSkinAnimation();
            }
            else
            {
                TryPlayPendingSpell();
            }
        }

        private void ApplyOwnerSubmeshVisibility(IEnumerable<uint> hiddenHashes)
        {
            _characterAuthoredHiddenSubmeshes.Clear();
            foreach (uint hash in hiddenHashes ?? Array.Empty<uint>())
                _characterAuthoredHiddenSubmeshes.Add(hash);
            ApplyEffectiveCharacterSubmeshes();
        }

        private void ApplyEffectiveCharacterSubmeshes()
        {
            if (_championModel == null) return;
            VfxSceneActor actor = FocusedActor;
            var effectiveHidden = new HashSet<uint>();
            foreach (ModelPart part in _championModel.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Name)) continue;
                uint hash = Fnv1a.HashLower(part.Name);
                bool inheritedVisible = !_characterAuthoredHiddenSubmeshes.Contains(hash);
                bool manualVisible = false;
                bool overridden = actor != null && actor.SubmeshOverrides.TryGetValue(hash, out manualVisible);
                bool visible = VfxCharacterViewportSemantics.ResolveSubmeshVisibility(
                    inheritedVisible,
                    overridden,
                    manualVisible);
                if (part.IsVisible != visible) part.IsVisible = visible;
                if (!visible) effectiveHidden.Add(hash);

                VfxCharacterSubmeshOption option = _model.CharacterSubmeshes.FirstOrDefault(item => item.NameHash == hash);
                option?.Sync(visible, overridden);
            }
            _vfxRenderer?.SetOwnerHiddenSubmeshes(effectiveHidden);
        }

        private void RebuildCharacterSubmeshOptions()
        {
            foreach (VfxCharacterSubmeshOption existing in _model.CharacterSubmeshes)
            {
                existing.VisibilityChanged -= CharacterSubmesh_VisibilityChanged;
                existing.TextureChanged -= CharacterSubmesh_TextureChanged;
                existing.Detach();
            }
            _model.CharacterSubmeshes.Clear();

            if (_championModel != null)
            {
                foreach (ModelPart part in _championModel.Parts)
                {
                    if (string.IsNullOrWhiteSpace(part?.Name)) continue;
                    var option = new VfxCharacterSubmeshOption(part.Name, part.IsVisible, part);
                    option.VisibilityChanged += CharacterSubmesh_VisibilityChanged;
                    option.TextureChanged += CharacterSubmesh_TextureChanged;
                    _model.CharacterSubmeshes.Add(option);
                }
            }
            RebuildCharacterFormOptions();
            _model.NotifyCharacterCollectionsChanged();
            ApplyEffectiveCharacterSubmeshes();
        }

        private void CharacterSubmesh_VisibilityChanged(object sender, EventArgs e)
        {
            if (sender is not VfxCharacterSubmeshOption option || FocusedActor is not VfxSceneActor actor)
                return;
            actor.SubmeshOverrides[option.NameHash] = option.IsVisible;
            ApplyEffectiveCharacterSubmeshes();
            OpenTkControl?.InvalidateVisual();
        }

        private void CharacterSubmesh_TextureChanged(object sender, EventArgs e)
        {
            OpenTkControl?.InvalidateVisual();
        }

        private void ResetCharacterSubmeshOverrides_Click(object sender, RoutedEventArgs e)
        {
            if (FocusedActor is not VfxSceneActor actor) return;
            actor.SubmeshOverrides.Clear();
            if (_championModel != null)
                VfxCharacterFormSemantics.RestoreAuthoredTextures(_championModel.Parts);
            ApplyEffectiveCharacterSubmeshes();
            OpenTkControl?.InvalidateVisual();
        }

        private void ResetChampionToBindPose()
        {
            _vfxRenderer?.SetBoneTransformSampler(null);
            if (_championModel == null)
            {
                _vfxRenderer?.SetOwnerSkinningMatrices(null);
                _vfxRenderer?.UpdateBoneTransforms(null);
                return;
            }

            _championModel.CurrentAnimation = null;
            _championModel.AnimationTime = 0d;
            _championModel.IsAnimationPaused = true;
            ApplyChampionBindPose();
        }

        private void ApplyChampionBindPose()
        {
            var skeleton = _championModel?.Skeleton;
            if (skeleton?.Joints == null || skeleton.Joints.Count == 0)
            {
                if (_championModel != null) _championModel.SkinningMatrices = null;
                _vfxRenderer?.SetOwnerSkinningMatrices(null);
                _vfxRenderer?.UpdateBoneTransforms(null);
                return;
            }

            if (!ReferenceEquals(_championBindSkeleton, skeleton) || _championBindBoneTransformProvider == null)
            {
                _championBindSkeleton = skeleton;
                _championBindBoneTransformProvider = AnimationService.CreateBindBoneTransformProvider(skeleton);
                _championBindSkinningMatrices = AnimationService.CreateBindSkinningMatrices(skeleton);
                _championBindWorldTransforms = AnimationService.CreateBindWorldTransforms(skeleton);
            }

            _championModel.SkinningMatrices = _championBindSkinningMatrices;
            _vfxRenderer?.SetOwnerSkinningMatrices(_championBindSkinningMatrices);
            _vfxRenderer?.UpdateBoneTransforms(_championBindBoneTransformProvider);
        }

        private void InvalidateChampionBindPose()
        {
            _championBindSkeleton = null;
            _championBindBoneTransformProvider = null;
            _championBindSkinningMatrices = Array.Empty<Matrix4x4>();
            _championBindWorldTransforms = Array.Empty<Matrix4x4>();
        }

        private string ResolveSknPath(string authoredPath, string searchDir)
            => VfxLoadingService?.ResolveAssetPath(authoredPath, searchDir, ".skn");
    }
}
