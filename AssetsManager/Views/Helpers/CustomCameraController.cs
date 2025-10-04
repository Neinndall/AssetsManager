using HelixToolkit.Wpf;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;

namespace AssetsManager.Views.Helpers
{
    public class CustomCameraController
    {
        private readonly HelixViewport3D _viewport;
        private bool _isRotating;
        private System.Windows.Point _lastMousePosition;

        public CustomCameraController(HelixViewport3D viewport)
        {
            _viewport = viewport;
            _viewport.PreviewMouseDown += OnPreviewMouseDown;
            _viewport.MouseUp += OnMouseUp;
            _viewport.MouseMove += OnMouseMove;
            _viewport.MouseWheel += OnMouseWheel;
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                e.Handled = true;
            }
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isRotating = true;
                _lastMousePosition = e.GetPosition(_viewport);
                _viewport.Cursor = System.Windows.Input.Cursors.SizeAll;
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Released)
            {
                _isRotating = false;
                _viewport.Cursor = System.Windows.Input.Cursors.Arrow;
            }
        }

        private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isRotating && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentMousePosition = e.GetPosition(_viewport);
                var delta = new System.Windows.Point(currentMousePosition.X - _lastMousePosition.X, currentMousePosition.Y - _lastMousePosition.Y);

                double sensitivity = 0.5;
                var delta3D = new Vector3D(-delta.X * sensitivity, delta.Y * sensitivity, 0);
                Rotate(delta3D);

                _lastMousePosition = currentMousePosition;
            }
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var camera = _viewport.Camera as ProjectionCamera;
            if (camera == null) return;

            var delta = e.Delta > 0 ? 1 : -1;
            var lookDir = camera.LookDirection;
            lookDir.Normalize();

            camera.Position += lookDir * delta * 10; // Adjust sensitivity
        }

        private void Rotate(Vector3D delta)
        {
            var camera = _viewport.Camera as ProjectionCamera;
            if (camera == null) return;

            var target = camera.Position + camera.LookDirection;
            var up = camera.UpDirection;

            var transform = new Transform3DGroup();
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), delta.X)));
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(Vector3D.CrossProduct(up, -camera.LookDirection), -delta.Y)));


            var newPosition = transform.Transform(camera.Position - target) + target;
            var newLookDirection = target - newPosition;
            var newUpDirection = transform.Transform(up);

            camera.Position = newPosition;
            camera.LookDirection = newLookDirection;
            camera.UpDirection = newUpDirection;
        }
    }
}
