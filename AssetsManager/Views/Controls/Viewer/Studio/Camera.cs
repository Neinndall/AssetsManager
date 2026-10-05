using System;
using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Utils.Rendering;
using AssetsManager.Utils.Viewport;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        public void ResetCamera()
        {
            if (TryGetStandaloneMapCenter(out _))
            {
                SnapMapCamera(_mapSceneRuntime?.Scene);
                return;
            }

            ApplyCameraPreset(_model.PreviewCameraPreset, refit: true);
        }

        public void ResetCameraToConfiguredPreset()
        {
            StudioCameraPreset targetPreset = StudioCameraPreset.Orbit;
            if (AppSettings?.Studio?.CameraPreset != null &&
                Enum.TryParse(AppSettings.Studio.CameraPreset, ignoreCase: true, out StudioCameraPreset configuredPreset))
            {
                targetPreset = configuredPreset;
            }

            _model.PreviewCameraPreset = targetPreset;
            ApplyCameraPreset(targetPreset, refit: true);
        }

        private void CameraController_RotationStarted(object sender, EventArgs e)
        {
            if (_model.PreviewCameraPreset == StudioCameraPreset.Orbit) return;

            // The reference keeps the projection that started the drag. Mark the camera as
            // free Orbit immediately, but defer an orthographic-to-perspective swap until release.
            _deferOrbitProjectionSwap = _dummyViewport.Camera is OrthographicCamera;
            _suppressCameraPresetFit = true;
            try
            {
                _model.PreviewCameraPreset = StudioCameraPreset.Orbit;
                ApplyCameraDistanceLimits(CameraPresets.ForStudio(StudioCameraPreset.Orbit), TryGetStandaloneMapCenter(out _));
            }
            finally
            {
                _suppressCameraPresetFit = false;
            }
        }

        private void CameraController_RotationEnded(object sender, EventArgs e)
        {
            if (!_deferOrbitProjectionSwap ||
                _cameraController == null ||
                _dummyViewport.Camera is not OrthographicCamera orthographic)
            {
                _deferOrbitProjectionSwap = false;
                return;
            }

            _deferOrbitProjectionSwap = false;
            Point3D target = orthographic.Position + orthographic.LookDirection;
            Vector3D fromTarget = orthographic.Position - target;
            if (fromTarget.Length > 0.001d)
                fromTarget.Normalize();
            else
                fromTarget = new Vector3D(0d, 0d, 1d);

            float aspect = OpenTkControl.ActualHeight > 0d
                ? (float)Math.Max(1d, OpenTkControl.ActualWidth) / (float)OpenTkControl.ActualHeight
                : 1f;
            double reach = CameraPresets.ReachOfOrthographicWidth(
                (float)Math.Max(1d, orthographic.Width),
                aspect);
            Point3D position = target + fromTarget * reach;

            // Carry the exact target, orientation and visible span into the perspective camera.
            _previewPerspectiveCamera.FieldOfView = CameraPresets.OrbitFieldOfView;
            _previewPerspectiveCamera.Position = position;
            _previewPerspectiveCamera.LookDirection = target - position;
            _previewPerspectiveCamera.UpDirection = orthographic.UpDirection;
            _cameraController.SetCamera(_previewPerspectiveCamera);
        }

        /// <summary>
        /// Zoom limits of a camera stand. A standalone MAP frees the reach to the whole map unless the stand
        /// bounds it: the Game camera keeps its game reach there too.
        /// </summary>
        private void ApplyCameraDistanceLimits(CameraStand stand, bool standaloneMap)
        {
            bool mapReach = standaloneMap && stand.Farthest == null;
            _cameraController.PerspectiveMinDistance = mapReach ? 10d : stand.Nearest ?? (standaloneMap ? 1000d : 0d);
            _cameraController.PerspectiveMaxDistance = mapReach ? 50000d : stand.Farthest ?? double.PositiveInfinity;
        }

        private void ApplyCameraPreset(StudioCameraPreset preset, bool refit)
        {
            if (_cameraController == null) return;

            CameraStand stand = CameraPresets.ForStudio(preset);
            bool isStandaloneMap = TryGetStandaloneMapCenter(out _);
            ApplyCameraDistanceLimits(stand, isStandaloneMap);

            ProjectionCamera camera;
            if (stand.Orthographic)
            {
                camera = _previewOrthographicCamera;
            }
            else
            {
                _previewPerspectiveCamera.FieldOfView = stand.FieldOfView;
                camera = _previewPerspectiveCamera;
            }

            _cameraController.SetCamera(camera);
            if (refit)
                FrameCurrentPreview(stand);
        }

        private void FrameCurrentPreview(CameraStand stand)
        {
            if (_cameraController == null) return;

            if (stand.Farthest is float gameReach)
            {
                Vector3 target = CurrentPreviewGround();
                Vector3 position = target + stand.Direction * gameReach;
                _cameraController.SnapTo(
                    new Point3D(position.X, position.Y, position.Z),
                    new Vector3D(target.X - position.X, target.Y - position.Y, target.Z - position.Z),
                    new Vector3D(stand.Up.X, stand.Up.Y, stand.Up.Z));
                return;
            }

            if (TryGetStandaloneMapCenter(out _) &&
                _model.PreviewCameraPreset == StudioCameraPreset.Orbit)
            {
                var mapOrbitStand = new CameraStand(
                    Vector3.Normalize(new Vector3(280f, 150f, 400f)),
                    Vector3.UnitY,
                    stand.FieldOfView,
                    Orthographic: false);
                FramePreviewBounds(CurrentPreviewBounds(), mapOrbitStand);
                return;
            }

            FramePreviewBounds(CurrentPreviewBounds(), stand);
        }

        private bool TryGetStandaloneMapCenter(out Vector3 center)
        {
            center = Vector3.Zero;
            if (_mapSceneIsCharacterBackdrop || _mapSceneRuntime?.Scene?.Geometry == null)
                return false;

            if (StableMapOrigin(_mapSceneRuntime.Scene) is not Vector3 engineOrigin)
                return false;

            center = new Vector3(-engineOrigin.X, engineOrigin.Y + 300f, engineOrigin.Z);
            return true;
        }

        private static VfxDefinitionBounds MapFrameBounds(Vector3 center)
        {
            return new VfxDefinitionBounds(
                new Vector3(center.X - 1500f, center.Y - 300f, center.Z - 1500f),
                new Vector3(center.X + 1500f, center.Y + 300f, center.Z + 1500f));
        }

        private VfxDefinitionBounds CurrentPreviewBounds()
        {
            if (_model.IsSkinWorkspace)
            {
                VfxDefinitionBounds characters = SceneCharacterBounds();
                if (IsFiniteBounds(characters)) return characters;
            }

            if (_model.IsRawSystemsMode && _model.SelectedSystem?.Definition is { } definition)
            {
                VfxRigSettings settings = _vfxRenderer?.RigSettings ??
                    VfxRigSettings.ForPreset(_model.RigPreset);
                return VfxSystemBounds.Calculate(definition, settings);
            }

            if (TryGetStandaloneMapCenter(out Vector3 mapCenter))
            {
                return MapFrameBounds(mapCenter);
            }

            return DefaultPreviewBounds();
        }

        private static bool IsFiniteBounds(VfxDefinitionBounds bounds)
        {
            return VectorMathUtils.IsFinite(bounds.Min) && VectorMathUtils.IsFinite(bounds.Max) &&
                   bounds.Max.X >= bounds.Min.X &&
                   bounds.Max.Y >= bounds.Min.Y &&
                   bounds.Max.Z >= bounds.Min.Z &&
                   (bounds.Max - bounds.Min).LengthSquared() > 1e-8f;
        }

        private static VfxDefinitionBounds CharacterPreviewBounds(SceneModel model)
        {
            if (model == null) return default;
            Rect3D local = ViewerInteractionService.GetLocalBounds(model);
            if (local.IsEmpty) return default;

            Matrix4x4 world = GlMeshRenderer.CreateWorldMatrix(model, mirrorCharacterX: true);
            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);
            double[] xs = { local.X, local.X + local.SizeX };
            double[] ys = { local.Y, local.Y + local.SizeY };
            double[] zs = { local.Z, local.Z + local.SizeZ };
            foreach (double x in xs)
            foreach (double y in ys)
            foreach (double z in zs)
            {
                Vector3 point = Vector3.Transform(new Vector3((float)x, (float)y, (float)z), world);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            return new VfxDefinitionBounds(min, max);
        }

        private Vector3 CurrentPreviewGround()
        {
            if (TryGetStandaloneMapCenter(out Vector3 mapCenter))
            {
                return mapCenter;
            }

            if (_model.IsRawSystemsMode && _model.SelectedSystem?.Definition is { } definition)
            {
                VfxRigSettings settings = _vfxRenderer?.RigSettings ??
                    VfxRigSettings.ForPreset(_model.RigPreset);
                return VfxSystemBounds.Ground(definition, settings);
            }

            return Vector3.Zero;
        }

        private void FramePreviewBounds(VfxDefinitionBounds bounds, CameraStand stand)
        {
            if (_cameraController == null) return;

            var up = new Vector3D(stand.Up.X, stand.Up.Y, stand.Up.Z);
            if (stand.Orthographic)
            {
                VfxOrthographicFrame frame = VfxSystemBounds.FrameOrthographic(
                    bounds,
                    (float)Math.Max(1d, OpenTkControl.ActualWidth),
                    (float)Math.Max(1d, OpenTkControl.ActualHeight),
                    stand.Direction);
                _previewOrthographicCamera.Width = Math.Max(1d, frame.Width);
                var position = new Point3D(frame.Position.X, frame.Position.Y, frame.Position.Z);
                var target = new Point3D(frame.Target.X, frame.Target.Y, frame.Target.Z);
                _cameraController.SnapTo(position, target - position, up);
                return;
            }

            float aspect = OpenTkControl.ActualHeight > 0
                ? (float)Math.Max(1d, OpenTkControl.ActualWidth) / (float)OpenTkControl.ActualHeight
                : 1f;
            VfxCameraFrame perspectiveFrame = VfxSystemBounds.FramePerspective(
                bounds,
                stand.FieldOfView,
                aspect,
                stand.Direction);
            var perspectivePosition = new Point3D(
                perspectiveFrame.Position.X,
                perspectiveFrame.Position.Y,
                perspectiveFrame.Position.Z);
            var perspectiveTarget = new Point3D(
                perspectiveFrame.Target.X,
                perspectiveFrame.Target.Y,
                perspectiveFrame.Target.Z);
            _cameraController.SnapTo(
                perspectivePosition,
                perspectiveTarget - perspectivePosition,
                up);
        }

        private static VfxDefinitionBounds DefaultPreviewBounds()
        {
            float reach = VfxSystemBounds.StandingReach;
            return new VfxDefinitionBounds(
                new Vector3(-reach, 0f, -reach),
                new Vector3(reach, VfxRigMotion.ChampionHeight, reach));
        }

    }
}
