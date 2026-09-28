using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
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
        private ProjectionCamera _subscribedCamera;
        private bool _isRotating;
        private bool _isPanning;
        private long _lastWalkTimestamp;
        private System.Windows.Point _lastMousePosition;
        
        // Smooth Zoom and Transition variables
        private Point3D _targetPosition;
        private Vector3D _targetLookDirection;
        private Vector3D _targetUpDirection;
        private bool _isTransitioning;
        private const double SmoothFactor = 0.15; 
        private const double TransitionThreshold = 0.05;
        internal const double MapGroundHeight = 10.0;

        public double ZoomSensitivity { get; set; } = 80.0;
        public bool IsMapGroundCollisionEnabled { get; set; }
        public double OrthographicMinWidth { get; set; } = 10.0;
        public double OrthographicMaxWidth { get; set; } = 20000.0;
        public double PerspectiveMinDistance { get; set; }
        public double PerspectiveMaxDistance { get; set; } = double.PositiveInfinity;

        /// <summary>
        /// Height of the MAP ground plane while a map scene is navigated. When set, the wheel zooms toward
        /// the terrain under the cursor, WASD travels and a double click flies to the clicked point.
        /// Null keeps the object-orbit controls unchanged.
        /// </summary>
        public double? MapNavigationGroundHeight { get; set; }

        private bool IsMapNavigation => MapNavigationGroundHeight.HasValue;
        public event EventHandler RotationStarted;
        public event EventHandler RotationEnded;

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
            
            // Initialize targets and track the active projection camera.
            if (_viewport.Camera is ProjectionCamera camera)
                SetCamera(camera);
        }

        public void SetCamera(ProjectionCamera camera)
        {
            if (_viewport == null || camera == null) return;

            if (camera.IsFrozen)
                camera = (ProjectionCamera)camera.Clone();

            // Track the exact camera instance whose Changed event we own. WPF Freezable
            // throws if a handler is removed when it was never registered on that instance.
            if (!ReferenceEquals(_subscribedCamera, camera))
            {
                if (_subscribedCamera != null)
                    _subscribedCamera.Changed -= OnCameraChanged;

                _subscribedCamera = camera;
                _subscribedCamera.Changed += OnCameraChanged;
            }

            _viewport.Camera = camera;
            _targetPosition = camera.Position;
            _targetLookDirection = camera.LookDirection;
            _targetUpDirection = camera.UpDirection;
            _isTransitioning = false;
        }

        public void Dispose()
        {
            if (_viewport != null)
            {
                CompositionTarget.Rendering -= OnRendering;
                if (_subscribedCamera != null)
                {
                    _subscribedCamera.Changed -= OnCameraChanged;
                    _subscribedCamera = null;
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
            if (_viewport?.Camera is not ProjectionCamera camera) return;
            WalkMap(camera);
            if (!_isTransitioning) return;

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

            if (IsMapNavigation && e.ChangedButton == MouseButton.Left && e.ClickCount == 2 &&
                TryFlyToGroundPoint(e.GetPosition(_inputSurface)))
            {
                e.Handled = true;
                return;
            }

            // Stop transitions if user starts interacting with any mouse gesture (like panning or zooming or rotation)
            _isTransitioning = false;

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                RotationStarted?.Invoke(this, EventArgs.Empty);
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
                    RotationEnded?.Invoke(this, EventArgs.Empty);
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

                double sensitivity = 0.25;
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

            if (IsMapNavigation)
            {
                ZoomMap(camera, e.GetPosition(_inputSurface), delta, speedMultiplier);
                e.Handled = true;
                return;
            }

            if (camera is OrthographicCamera orthographic)
            {
                double factor = Math.Pow(1.12, -delta * speedMultiplier);
                orthographic.Width = Math.Clamp(
                    orthographic.Width * factor,
                    OrthographicMinWidth,
                    OrthographicMaxWidth);
                _isTransitioning = false;
                e.Handled = true;
                return;
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

            Point3D heldTarget = _targetPosition + lookDir;
            lookDir.Normalize();

            // A bounded perspective preset behaves as a true dolly around its held target.
            // The default unbounded path stays unchanged for the main Viewer camera.
            double baseStep = Math.Clamp(currentDistance * 0.08, 5.0, 120.0);
            double step = baseStep * speedMultiplier;
            bool boundedPerspective = PerspectiveMinDistance > 0d || double.IsFinite(PerspectiveMaxDistance);
            if (boundedPerspective)
            {
                double nearest = Math.Max(0.001d, PerspectiveMinDistance);
                double farthest = double.IsFinite(PerspectiveMaxDistance)
                    ? Math.Max(nearest, PerspectiveMaxDistance)
                    : double.MaxValue;
                double nextDistance = Math.Clamp(currentDistance - delta * step, nearest, farthest);
                Vector3D nextLook = lookDir * nextDistance;
                _targetPosition = ConstrainMapPosition(heldTarget - nextLook);
                _targetLookDirection = nextLook;
                e.Handled = true;
                return;
            }

            _targetPosition = ConstrainMapPosition(
                _targetPosition + lookDir * (delta * step));
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
            var nextPosition = camera.Position + translation;

            if (IsMapGroundCollisionEnabled && nextPosition.Y < MapGroundHeight)
                nextPosition -= orthoUp * (delta.Y * sensitivity);

            camera.Position = ConstrainMapPosition(nextPosition);
        }

        /// <summary>
        /// True when a navigation key should move the map camera instead of reaching the focused
        /// control (e.g. tree type-ahead): the cursor is over the viewport and no text input has focus.
        /// </summary>
        public bool IsMapNavigationKey(Key key) =>
            key is (Key.W or Key.A or Key.S or Key.D) && IsWalkInputAvailable();

        private bool IsWalkInputAvailable() =>
            IsMapNavigation &&
            _inputSurface?.IsMouseOver == true &&
            (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0 &&
            Keyboard.FocusedElement is not (TextBoxBase or PasswordBox or System.Windows.Controls.ComboBox { IsEditable: true });

        private MapCameraPose Pose(ProjectionCamera camera) => Pose(camera, camera.Position, camera.LookDirection);

        private static MapCameraPose Pose(ProjectionCamera camera, Point3D position, Vector3D look) => new(
            position,
            look,
            camera.UpDirection,
            (camera as PerspectiveCamera)?.FieldOfView ?? 45.0,
            (camera as OrthographicCamera)?.Width ?? 0.0,
            camera is OrthographicCamera);

        private Size SurfaceSize => new(_inputSurface.ActualWidth, _inputSurface.ActualHeight);

        private void ZoomMap(ProjectionCamera camera, System.Windows.Point cursor, int delta, double speed)
        {
            double ground = MapNavigationGroundHeight.Value;
            if (camera is OrthographicCamera orthographic)
            {
                _isTransitioning = false;
                double width = Math.Clamp(
                    orthographic.Width * Math.Pow(1.12, -delta * speed),
                    OrthographicMinWidth,
                    OrthographicMaxWidth);
                if (MapCameraNavigation.TryGetRay(Pose(camera), SurfaceSize, cursor, out Point3D anchor, out _))
                {
                    camera.Position = MapCameraNavigation.OrthographicZoomPosition(
                        camera.Position, anchor, width / orthographic.Width);
                }
                orthographic.Width = width;
                SyncTargetsToCamera(camera);
                return;
            }

            // Successive wheel notches accumulate on the pending target, like the orbit zoom.
            if (!_isTransitioning)
                SyncTargetsToCamera(camera);
            MapCameraPose pose = Pose(camera, _targetPosition, _targetLookDirection);
            Point3D focus = MapCameraNavigation.TryGetGroundPoint(pose, SurfaceSize, cursor, ground, out Point3D hit)
                ? hit
                : _targetPosition + _targetLookDirection;
            Point3D next = MapCameraNavigation.Zoom(
                _targetPosition, focus, delta, speed, ground, PerspectiveMaxDistance);
            _targetPosition = next;
            _targetLookDirection = MapCameraNavigation.GroundedLook(next, _targetLookDirection, ground);
            _isTransitioning = true;
        }

        private bool TryFlyToGroundPoint(System.Windows.Point cursor)
        {
            if (_viewport?.Camera is not PerspectiveCamera camera ||
                !MapCameraNavigation.TryGetGroundPoint(Pose(camera), SurfaceSize, cursor, MapNavigationGroundHeight.Value, out Point3D hit))
            {
                return false;
            }

            Vector3D direction = camera.LookDirection;
            direction.Normalize();
            double distance = Math.Clamp(camera.LookDirection.Length, MapFlyMinimumDistance, MapFlyMaximumDistance);
            Vector3D look = direction * distance;
            FlyTo(hit - look, look, camera.UpDirection);
            return true;
        }

        private void WalkMap(ProjectionCamera camera)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = _lastWalkTimestamp == 0
                ? 0
                : Math.Min((now - _lastWalkTimestamp) / (double)Stopwatch.Frequency, MaximumWalkStep);
            if (!IsWalkInputAvailable())
            {
                _lastWalkTimestamp = 0;
                return;
            }

            double forward = (Keyboard.IsKeyDown(Key.W) ? 1 : 0) - (Keyboard.IsKeyDown(Key.S) ? 1 : 0);
            double strafe = (Keyboard.IsKeyDown(Key.D) ? 1 : 0) - (Keyboard.IsKeyDown(Key.A) ? 1 : 0);
            if (forward == 0 && strafe == 0)
            {
                _lastWalkTimestamp = 0;
                return;
            }

            _lastWalkTimestamp = now;
            double speed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? 3.0
                : Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ? 0.25
                : 1.0;
            Vector3D move = MapCameraNavigation.Walk(
                Pose(camera), forward, strafe, seconds, speed, MapNavigationGroundHeight.Value);
            if (move.LengthSquared < 1e-12)
                return;

            camera.Position += move;
            if (_isTransitioning)
                _targetPosition += move;
            else
                SyncTargetsToCamera(camera);
        }

        private void SyncTargetsToCamera(ProjectionCamera camera)
        {
            _targetPosition = camera.Position;
            _targetLookDirection = camera.LookDirection;
            _targetUpDirection = camera.UpDirection;
        }

        private const double MapFlyMinimumDistance = 400.0;
        private const double MapFlyMaximumDistance = 4000.0;
        private const double MaximumWalkStep = 0.1;

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
