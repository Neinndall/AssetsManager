using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HelixToolkit.Wpf;
using System.Windows.Media.Media3D;

namespace AssetsManager.Views.Helpers
{
    public class CustomCameraController : IDisposable
    {
        private HelixViewport3D _viewport;
        private bool _isRotating;
        private System.Windows.Point _lastMousePosition;
        
        // Smooth Zoom variables
        private Point3D _targetPosition;
        private bool _isZooming;
        private const double SmoothFactor = 0.15; // Adjusted for fluid feel (lower = smoother)

        public double ZoomSensitivity { get; set; } = 80.0;

        public CustomCameraController(HelixViewport3D viewport)
        {
            _viewport = viewport;
            _viewport.PreviewMouseDown += OnPreviewMouseDown;
            _viewport.MouseUp += OnMouseUp;
            _viewport.MouseMove += OnMouseMove;
            _viewport.MouseWheel += OnMouseWheel;
            
            // Start the smooth update loop
            CompositionTarget.Rendering += OnRendering;
            
            // Initialize target
            if (_viewport.Camera is ProjectionCamera camera)
            {
                _targetPosition = camera.Position;
            }
        }

        public void Dispose()
        {
            if (_viewport != null)
            {
                CompositionTarget.Rendering -= OnRendering;
                _viewport.PreviewMouseDown -= OnPreviewMouseDown;
                _viewport.MouseUp -= OnMouseUp;
                _viewport.MouseMove -= OnMouseMove;
                _viewport.MouseWheel -= OnMouseWheel;
                _viewport = null;
            }
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_isZooming || _viewport?.Camera is not ProjectionCamera camera) return;

            // Interpolate position (Lerp)
            var currentPos = camera.Position;
            var newPos = currentPos + (_targetPosition - currentPos) * SmoothFactor;

            camera.Position = newPos;

            // Stop updating if we are close enough to the target
            if ((newPos - _targetPosition).Length < 0.1)
            {
                camera.Position = _targetPosition;
                _isZooming = false;
            }
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
                
                // Stop zooming if user starts rotating to prevent jerky camera
                _isZooming = false;
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
                
                // Update target position after rotation to sync zoom target
                if (_viewport.Camera is ProjectionCamera camera)
                {
                    _targetPosition = camera.Position;
                }
            }
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var camera = _viewport.Camera as ProjectionCamera;
            if (camera == null) return;

            var delta = e.Delta > 0 ? 1 : -1;
            var lookDir = camera.LookDirection;
            lookDir.Normalize();

            double speedMultiplier = 1.0;
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                speedMultiplier = 5.0; // Turbo Mode
            }
            else if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                speedMultiplier = 0.2; // Precision Mode
            }

            // If we weren't already zooming, start from current camera position
            if (!_isZooming)
            {
                _targetPosition = camera.Position;
                _isZooming = true;
            }

            // Increment the target, not the current position
            _targetPosition += lookDir * delta * ZoomSensitivity * speedMultiplier;
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
