using System.Numerics;
using System.Windows.Media.Media3D;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>Immutable camera pose and projection saved independently by each Studio scene.</summary>
    internal sealed record StudioWorkspaceCameraState(
        Point3D Position,
        Vector3D LookDirection,
        Vector3D UpDirection,
        bool Orthographic,
        double ProjectionSpan,
        StudioCameraPreset Preset)
    {
        internal static StudioWorkspaceCameraState Capture(ProjectionCamera camera, StudioCameraPreset preset) =>
            new(camera.Position, camera.LookDirection, camera.UpDirection, camera is OrthographicCamera,
                camera is OrthographicCamera orthographic ? orthographic.Width : ((PerspectiveCamera)camera).FieldOfView,
                preset);

        /// <summary>Follows a subject translation while preserving its view direction and apparent size.</summary>
        internal StudioWorkspaceCameraState Translate(Vector3 delta) => this with
        {
            Position = Position + new Vector3D(delta.X, delta.Y, delta.Z)
        };
    }
}
