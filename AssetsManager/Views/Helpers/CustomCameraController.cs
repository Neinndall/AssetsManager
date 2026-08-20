using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Media.Media3D;

namespace AssetsManager.Views.Helpers
{
    public class CustomCameraController : IDisposable
    {
        private Viewport3D _viewport;
        private FrameworkElement _inputSurface;
        private bool _isRotating;
        private bool _isPanning;
        private System.Windows.Point _lastMousePosition;
        
        // Smooth Zoom and Transition variables
        private Point3D _targetPosition;
        private Vector3D _targetLookDirection;
        private Vector3D _targetUpDirection;
        private bool _isTransitioning;
        private const double SmoothFactor = 0.15; 
        private const double TransitionThreshold = 0.05;
        internal const double MapGroundHeight = 10.0;
        private const double MinimumZoomDistance = 0.05;

        public double ZoomSensitivity { get; set; } = 80.0;
        public bool IsMapGroundCollisionEnabled { get; set; }

        public CustomCameraController(Viewport3D viewport, FrameworkElement inputSurface = null)
        {
            _viewport = viewport;
            _inputSurface = inputSurface ?? viewport;
            _inputSurface.PreviewMouseDown += OnPreviewMouseDown;
            _inputSurface.MouseUp += OnMouseUp;
            _inputSurface.MouseMove += OnMouseMove;
            _inputSurface.MouseWheel += OnMouseWheel;
            
            // Start the smooth update loop
            CompositionTarget.Rendering += OnRendering;
            
            // Initialize targets
            if (_viewport.Camera is ProjectionCamera camera)
            {
                if (camera.IsFrozen)
                {
                    camera = (ProjectionCamera)camera.Clone();
                    _viewport.Camera = camera;
                }
                _targetPosition = camera.Position;
                _targetLookDirection = camera.LookDirection;
                _targetUpDirection = camera.UpDirection;
                camera.Changed += OnCameraChanged;
            }
        }

        public void Dispose()
        {
            if (_viewport != null)
            {
                CompositionTarget.Rendering -= OnRendering;
                if (_viewport.Camera != null)
                {
                    _viewport.Camera.Changed -= OnCameraChanged;
                }
                _inputSurface.PreviewMouseDown -= OnPreviewMouseDown;
                _inputSurface.MouseUp -= OnMouseUp;
                _inputSurface.MouseMove -= OnMouseMove;
                _inputSurface.MouseWheel -= OnMouseWheel;
                _inputSurface = null;
                _viewport = null;
            }
        }

        private void OnCameraChanged(object sender, EventArgs e)
        {
            // Keep interpolation targets aligned with external camera changes.
            if (!_isTransitioning && _viewport?.Camera is ProjectionCamera camera)
            {
                _targetPosition = camera.Position;
                _targetLookDirection = camera.LookDirection;
                _targetUpDirection = camera.UpDirection;
            }
        }

        public void FlyTo(Vector3D lookDirection, Vector3D upDirection, double distance = 500)
        {
            lookDirection.Normalize();
            var targetPos = new Point3D(0, 0, 0) - (lookDirection * distance);
            FlyTo(targetPos, lookDirection, upDirection);
        }

        public void FlyTo(Point3D position, Vector3D lookDirection, Vector3D upDirection)
        {
            if (_viewport?.Camera is not ProjectionCamera camera) return;

            _targetPosition = ConstrainMapPosition(position);
            _targetLookDirection = lookDirection;
            _targetUpDirection = upDirection;
            _isTransitioning = true;
        }

        public void SnapTo(Point3D position, Vector3D lookDirection, Vector3D upDirection)
        {
            if (_viewport?.Camera is not ProjectionCamera camera) return;

            position = ConstrainMapPosition(position);
            _targetPosition = position;
            _targetLookDirection = lookDirection;
            _targetUpDirection = upDirection;
            _isTransitioning = false; // Instant

            camera.Position = position;
            camera.LookDirection = lookDirection;
            camera.UpDirection = upDirection;
        }

        public void Reset()
        {
            // The "Professional League Front View" coordinates
            var position = new Point3D(0.00, 2386.00, 670.00);
            var lookDirection = new Vector3D(0.00, -250.00, -650.00);
            var upDirection = new Vector3D(0.00, 1.00, 0.00);
            
            FlyTo(position, lookDirection, upDirection);
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_isTransitioning || _viewport?.Camera is not ProjectionCamera camera) return;

            // Interpolate Position
            var currentPos = camera.Position;
            var newPos = ConstrainMapPosition(
                currentPos + (_targetPosition - currentPos) * SmoothFactor);
            camera.Position = newPos;

            // Interpolate LookDirection
            var currentLook = camera.LookDirection;
            var newLook = currentLook + (_targetLookDirection - currentLook) * SmoothFactor;
            camera.LookDirection = newLook;

            // Interpolate UpDirection
            var currentUp = camera.UpDirection;
            var newUp = currentUp + (_targetUpDirection - currentUp) * SmoothFactor;
            camera.UpDirection = newUp;

            // Stop updating if we are close enough to the target
            bool posReached = (newPos - _targetPosition).Length < TransitionThreshold;
            bool lookReached = (newLook - _targetLookDirection).Length < TransitionThreshold;

            if (posReached && lookReached)
            {
                camera.Position = _targetPosition;
                camera.LookDirection = _targetLookDirection;
                camera.UpDirection = _targetUpDirection;
                _isTransitioning = false;
            }
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                e.Handled = true;
            }

            // Stop transitions if user starts interacting with any mouse gesture (like panning or zooming or rotation)
            _isTransitioning = false;

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isRotating = true;
                _lastMousePosition = e.GetPosition(_inputSurface);
                _inputSurface.Cursor = System.Windows.Input.Cursors.SizeAll;
                _inputSurface.CaptureMouse();
            }
            else if (e.RightButton == MouseButtonState.Pressed)
            {
                _isPanning = true;
                _lastMousePosition = e.GetPosition(_inputSurface);
                _inputSurface.Cursor = System.Windows.Input.Cursors.Hand;
                _inputSurface.CaptureMouse();
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Released)
            {
                if (_isRotating)
                {
                    _isRotating = false;
                    _inputSurface.Cursor = System.Windows.Input.Cursors.Arrow;
                    _inputSurface.ReleaseMouseCapture();
                }
            }
            if (e.RightButton == MouseButtonState.Released)
            {
                if (_isPanning)
                {
                    _isPanning = false;
                    _inputSurface.Cursor = System.Windows.Input.Cursors.Arrow;
                    _inputSurface.ReleaseMouseCapture();
                }
            }
        }

        private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isRotating && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentMousePosition = e.GetPosition(_inputSurface);
                var delta = new System.Windows.Point(currentMousePosition.X - _lastMousePosition.X, currentMousePosition.Y - _lastMousePosition.Y);

                double sensitivity = 0.5;
                var delta3D = new Vector3D(-delta.X * sensitivity, delta.Y * sensitivity, 0);
                Rotate(delta3D);

                _lastMousePosition = currentMousePosition;
                
                // Update target position/dirs after rotation to sync
                if (_viewport.Camera is ProjectionCamera camera)
                {
                    _targetPosition = camera.Position;
                    _targetLookDirection = camera.LookDirection;
                    _targetUpDirection = camera.UpDirection;
                }
            }
            else if (_isPanning && e.RightButton == MouseButtonState.Pressed)
            {
                var currentMousePosition = e.GetPosition(_inputSurface);
                var delta = new System.Windows.Point(currentMousePosition.X - _lastMousePosition.X, currentMousePosition.Y - _lastMousePosition.Y);

                Pan(delta);

                _lastMousePosition = currentMousePosition;

                // Update target position/dirs after panning to sync
                if (_viewport.Camera is ProjectionCamera camera)
                {
                    _targetPosition = camera.Position;
                    _targetLookDirection = camera.LookDirection;
                    _targetUpDirection = camera.UpDirection;
                }
            }
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var camera = _viewport.Camera as ProjectionCamera;
            if (camera == null) return;

            var delta = e.Delta > 0 ? 1 : -1;

            double speedMultiplier = 1.0;
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                speedMultiplier = 5.0; // Turbo Mode
            }
            else if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                speedMultiplier = 0.2; // Precision Mode
            }

            // If we weren't already transitioning, start from current
            if (!_isTransitioning)
            {
                _targetPosition = camera.Position;
                _targetLookDirection = camera.LookDirection;
                _targetUpDirection = camera.UpDirection;
                _isTransitioning = true;
            }

            var lookDir = _targetLookDirection;
            double currentDistance = lookDir.Length;
            if (!double.IsFinite(currentDistance) || currentDistance <= 0.001) return;

            lookDir.Normalize();

            // Calculate dynamic zoom step based on the logical orbit distance, not the interpolating camera.
            double baseStep = Math.Clamp(currentDistance * 0.08, 5.0, 120.0);
            double step = baseStep * speedMultiplier;
            if (delta > 0)
                step = Math.Min(step, Math.Max(currentDistance - MinimumZoomDistance, 0));

            var requestedShift = lookDir * delta * step;
            var nextPosition = ConstrainMapPosition(_targetPosition + requestedShift);
            var actualShift = nextPosition - _targetPosition;
            _targetPosition = nextPosition;
            _targetLookDirection -= actualShift;
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


            var newPosition = ConstrainMapPosition(
                transform.Transform(camera.Position - target) + target);
            var newLookDirection = target - newPosition;
            var newUpDirection = transform.Transform(up);

            camera.Position = newPosition;
            camera.LookDirection = newLookDirection;
            camera.UpDirection = newUpDirection;
        }

        private void Pan(System.Windows.Point delta)
        {
            var camera = _viewport.Camera as ProjectionCamera;
            if (camera == null) return;

            var lookDir = camera.LookDirection;
            var upDir = camera.UpDirection;

            var rightDir = Vector3D.CrossProduct(lookDir, upDir);
            rightDir.Normalize();

            var orthoUp = Vector3D.CrossProduct(rightDir, lookDir);
            orthoUp.Normalize();

            // Panning sensitivity scales naturally with camera target distance
            double distance = lookDir.Length;
            double sensitivity = distance * 0.0012;

            var translation = rightDir * (-delta.X * sensitivity) + orthoUp * (delta.Y * sensitivity);

            camera.Position = ConstrainMapPosition(camera.Position + translation);
        }

        internal static Point3D ConstrainMapPosition(Point3D position, bool collisionEnabled)
        {
            if (collisionEnabled && position.Y < MapGroundHeight)
                position.Y = MapGroundHeight;

            return position;
        }

        private Point3D ConstrainMapPosition(Point3D position) =>
            ConstrainMapPosition(position, IsMapGroundCollisionEnabled);
    }
}
