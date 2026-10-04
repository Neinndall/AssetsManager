using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Utils.Viewport;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private void SyncSelectedMapVariant(MapSceneSource source)
        {
            if (source?.Map == null || _model.MapVariants.Count == 0)
                return;

            MapVariantData variant = MapVariantData.ForMap(_model.MapVariants, source.Map, _model.SelectedMapVariant);
            if (variant == null || ReferenceEquals(_model.SelectedMapVariant, variant))
                return;

            _suppressMapVariantReload = true;
            try
            {
                _model.SelectedMapVariant = variant;
            }
            finally
            {
                _suppressMapVariantReload = false;
            }
        }

        private void ReloadSelectedMapVariant()
        {
            // Skins sharing the loaded container draw the same geometry; only their environment assets
            // (grass tint, reflection cube) change, so switch those instead of reloading the map.
            if (MapVariantData.Draws(_model.SelectedMapVariant, _mapSceneRuntime?.Scene?.Source?.Map))
            {
                _mapGeometryRenderer?.SetMapSkin(_model.SelectedMapVariant.Skin);
                OpenTkControl?.InvalidateVisual();
                return;
            }

            MapSceneSource source = ResolveMapVariantSource(_model.SelectedMapVariant);
            if (source == null)
                return;

            bool asCharacterBackdrop = _mapSceneIsCharacterBackdrop && _model.IsSkinWorkspace;
            if (asCharacterBackdrop && _model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin)
            {
                VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
                string key = VfxInstallationMapCatalog.BackdropKey(source);
                VfxCharacterBackdropOption option = _model.CharacterBackdrops.FirstOrDefault(candidate =>
                    string.Equals(
                        VfxInstallationMapCatalog.BackdropKey(candidate?.Source),
                        key,
                        StringComparison.OrdinalIgnoreCase));
                if (option == null)
                {
                    option = new VfxCharacterBackdropOption(
                        VfxInstallationMapCatalog.Label(
                            source,
                            projectSource: !string.IsNullOrWhiteSpace(source.SelectedMapFilePath)),
                        source);
                    _model.CharacterBackdrops.Add(option);
                }

                tab.CharacterBackdropEnabled = true;
                tab.CharacterBackdropKey = key;
                tab.CharacterBackdropVisibility = null;
                _isApplyingCharacterViewportState = true;
                try
                {
                    _model.SelectedCharacterBackdrop = option;
                    _model.CharacterBackdropEnabled = true;
                }
                finally
                {
                    _isApplyingCharacterViewportState = false;
                }
            }
            else if (_model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Map)
            {
                RetargetMapWorkspaceTab(_model.SelectedWorkspaceTab, source);
            }

            _ = LoadDetectedMapAsync(source, asCharacterBackdrop);
        }

        private void RefreshCharacterBackdrop()
        {
            if (!_model.IsSkinWorkspace || !_model.CharacterBackdropEnabled || _model.SelectedCharacterBackdrop == null)
            {
                if (_mapSceneIsCharacterBackdrop)
                    CancelMapLoadAndClearScene();
                return;
            }

            _ = LoadCharacterBackdropAsync(_model.SelectedCharacterBackdrop);
        }

        private Task LoadCharacterBackdropAsync(VfxCharacterBackdropOption option)
        {
            if (option?.Source == null || !_model.IsSkinWorkspace)
                return Task.CompletedTask;

            MapVisibilityState visibility = null;
            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
            if (tab?.Kind == VfxWorkspaceTabKind.Skin &&
                string.Equals(
                    tab.CharacterBackdropKey,
                    VfxInstallationMapCatalog.BackdropKey(option.Source),
                    StringComparison.OrdinalIgnoreCase))
            {
                visibility = tab.CharacterBackdropVisibility;
            }

            return LoadDetectedMapAsync(
                option.Source,
                asCharacterBackdrop: true,
                initialVisibility: visibility);
        }

        private void ApplyCharacterBackdropOrigin(MapSceneData scene, MapSceneSource source)
        {
            VfxSceneActor actor = FocusedActor;
            if (actor == null || !TryGetCharacterBackdropOrigin(scene, out Vector3 origin, out double? spawnYaw))
                return;

            string sourceKey = VfxInstallationMapCatalog.BackdropKey(source);
            bool hasPinnedPlacement = actor.PlacementCustomized &&
                string.Equals(actor.PlacedOnKey, sourceKey, StringComparison.OrdinalIgnoreCase);
            if (hasPinnedPlacement) return;

            var previous = new Vector3(
                (float)_model.CharacterPositionX,
                (float)_model.CharacterPositionY,
                (float)_model.CharacterPositionZ);
            bool preserveFraming = _model.SelectedWorkspaceTab?.PreserveBackdropFraming == true;
            _isApplyingCharacterViewportState = true;
            try
            {
                _model.CharacterPositionX = origin.X;
                _model.CharacterPositionY = origin.Y;
                _model.CharacterPositionZ = origin.Z;
                // A Character the map spawns always takes the game's facing; otherwise a backdrop scene keeps
                // the facing its source scene framed.
                if (spawnYaw.HasValue || !preserveFraming)
                {
                    _model.CharacterRotationX = 0d;
                    _model.CharacterRotationY = spawnYaw ?? 0d;
                    _model.CharacterRotationZ = 0d;
                }
                StoreFocusedPlacement(actor);
                actor.PlacementCustomized = false;
                actor.PlacedOnKey = sourceKey;
            }
            finally
            {
                _isApplyingCharacterViewportState = false;
            }
            ApplyCharacterPlacement();
            Vector3 displacement = origin - previous;
            ShiftSceneActorsWithAnchor(displacement, sourceKey);
            if (preserveFraming && _dummyViewport.Camera is ProjectionCamera camera)
            {
                VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
                tab.CameraState = VfxWorkspaceCameraState.Capture(camera, _model.PreviewCameraPreset).Translate(displacement);
                RestoreWorkspaceCamera(tab);
            }
        }

        /// <summary>
        /// Preview-space point where the focused Character stands on the active MAP backdrop: where the map
        /// spawns it (its map entity or the neutral camp that brings it, see <see cref="MapCharacterSpawnSemantics"/>)
        /// with that spawn's yaw in degrees, else the map origin with no yaw of its own. MAP geometry is
        /// mirrored on X by the renderer, so the authored engine transform is converted to that space.
        /// </summary>
        private bool TryGetCharacterBackdropOrigin(MapSceneData scene, out Vector3 origin, out double? spawnYaw)
        {
            origin = default;
            spawnYaw = null;
            if (!_model.IsSkinWorkspace || scene?.Geometry == null)
                return false;

            if (TryGetBackdropSpawn(scene, FocusedActor?.Skin, out origin, out double yaw))
            {
                spawnYaw = yaw;
                return true;
            }

            // Like LTK, the stand point comes from the opening state: switching map state never moves the subject.
            if (StableMapOrigin(scene) is not Vector3 engineOrigin)
                return false;
            origin = new Vector3(-engineOrigin.X, engineOrigin.Y, engineOrigin.Z);
            return true;
        }

        /// <summary>Preview-space point and yaw (degrees) where the map spawns this skin's Character, if it does.</summary>
        private static bool TryGetBackdropSpawn(MapSceneData scene, VfxSkinItem skin, out Vector3 origin, out double yaw)
        {
            origin = default;
            yaw = 0d;
            if (MapCharacterSpawnSemantics.SpawnTransform(
                    scene, MapCharacterSpawnSemantics.CharacterOfSkin(skin?.BinPath)) is not Matrix4x4 spawn)
                return false;
            origin = new Vector3(-spawn.M41, spawn.M42, spawn.M43);
            // The X mirror turns an authored yaw about Y into its negative.
            yaw = -Math.Atan2(spawn.M31, spawn.M33) * (180d / Math.PI);
            return true;
        }

        private string ActiveCharacterBackdropKey() =>
            _model.HasActiveCharacterBackdrop
                ? VfxInstallationMapCatalog.BackdropKey(_model.SelectedCharacterBackdrop?.Source)
                : null;

        /// <summary>A user placement edit pins the focused actor to the current stage.</summary>
        private void PinFocusedPlacement()
        {
            VfxSceneActor actor = FocusedActor;
            if (actor == null) return;
            actor.PlacementCustomized = true;
            actor.PlacedOnKey = ActiveCharacterBackdropKey();
        }

        private void ApplyCharacterPlacement()
        {
            if (_championModel == null || !_model.IsSkinWorkspace) return;

            _championModel.PositionX = _model.CharacterPositionX;
            _championModel.PositionY = _model.CharacterPositionY;
            _championModel.PositionZ = _model.CharacterPositionZ;
            _championModel.RotationX = _model.CharacterRotationX;
            _championModel.RotationY = _model.CharacterRotationY;
            _championModel.RotationZ = _model.CharacterRotationZ;
            _championModel.Scale = _championAuthoredScale * _model.CharacterScaleMultiplier;

            _vfxRenderer?.SetWorldTransform(CharacterVfxPlacement());
            OpenTkControl?.InvalidateVisual();
        }

        /// <summary>
        /// Where the focused Character's effects play, an inspected System included, so a System opened over a
        /// MAP backdrop plays beside the Character rather than at the map's origin. The owner scene already applies
        /// the authored skinScale to bones and attachment offsets, so only the user multiplier goes here.
        /// </summary>
        private Matrix4x4 CharacterVfxPlacement()
        {
            if (!_model.IsSkinWorkspace)
                return Matrix4x4.Identity;

            return VfxCharacterViewportSemantics.CharacterPlacementWorld(
                _model.CharacterRotationX,
                _model.CharacterRotationY,
                _model.CharacterRotationZ,
                _model.CharacterScaleMultiplier,
                _model.CharacterPositionX,
                _model.CharacterPositionY,
                _model.CharacterPositionZ);
        }

        private void RestoreWorkspaceCamera(VfxWorkspaceTab tab)
        {
            if (tab?.CameraState is not { } state || _cameraController == null)
                return;

            _suppressCameraPresetFit = true;
            try { _model.PreviewCameraPreset = state.Preset; }
            finally { _suppressCameraPresetFit = false; }
            _cameraController.MapNavigationGroundHeight = null;
            ApplyCameraDistanceLimits(CameraPresets.ForStudio(state.Preset), tab.Kind == VfxWorkspaceTabKind.Map);
            ProjectionCamera camera;
            if (state.Orthographic)
            {
                _previewOrthographicCamera.Width = state.ProjectionSpan;
                camera = _previewOrthographicCamera;
            }
            else
            {
                _previewPerspectiveCamera.FieldOfView = state.ProjectionSpan;
                camera = _previewPerspectiveCamera;
            }
            _cameraController.SetCamera(camera);
            _cameraController.SnapTo(state.Position, state.LookDirection, state.UpDirection);
        }

        private void OpenCharacterBackdropScene(VfxCharacterBackdropOption option)
        {
            VfxWorkspaceTab source = _model.SelectedWorkspaceTab;
            if (source?.Kind != VfxWorkspaceTabKind.Skin || source.FocusedActor == null || option?.Source == null)
                return;

            // Selection belongs to the destination scene; the source keeps only its open map chooser.
            _isApplyingCharacterViewportState = true;
            try
            {
                _model.SelectedCharacterBackdrop = _model.CharacterBackdrops.FirstOrDefault(candidate =>
                    source.CharacterBackdropKey != null && string.Equals(
                        VfxInstallationMapCatalog.BackdropKey(candidate.Source), source.CharacterBackdropKey,
                        StringComparison.OrdinalIgnoreCase));
            }
            finally { _isApplyingCharacterViewportState = false; }
            CaptureWorkspaceSelection(source);

            string mapKey = VfxInstallationMapCatalog.BackdropKey(option.Source);
            string key = $"{source.OriginSceneKey}|backdrop:{mapKey}";
            VfxWorkspaceTab destination = _model.WorkspaceTabs.FirstOrDefault(tab =>
                string.Equals(tab.Key, key, StringComparison.OrdinalIgnoreCase));
            if (destination == null)
            {
                destination = source.CopyForBackdrop(key, mapKey, MapWorkspaceTitle(option.Source));
                foreach (VfxSceneActor actor in destination.Actors)
                    actor.PropertyChanged += SceneActor_PropertyChanged;
                _model.WorkspaceTabs.Add(destination);
                _model.NotifyWorkspaceTabsChanged();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    WorkspaceTabsScrollViewer?.ScrollToRightEnd();
                    UpdateWorkspaceTabScrollButtons();
                }));
            }
            ActivateWorkspaceTab(destination);
        }
    }
}
