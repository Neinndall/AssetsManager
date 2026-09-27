using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Models.Viewer;
using WpfVector = System.Windows.Vector;

namespace AssetsManager.Services.Viewer.Interaction
{
    internal sealed class ViewportModelInteractionController : IDisposable
    {
        private enum TransformAxis
        {
            None,
            X,
            Y,
            Z
        }

        private readonly FrameworkElement _inputSurface;
        private readonly ViewportTransformGizmo _gizmoCanvas;
        private readonly Func<ProjectionCamera> _cameraProvider;
        private readonly IReadOnlyList<SceneModel> _sceneModels;
        private readonly List<SceneModel> _selectedModels = new();
        private readonly List<(SceneModel Model, Vector3 Position)> _dragStartPositions = new();
        private readonly Dictionary<TransformAxis, Point> _axisEndpoints = new();

        private SceneModel _activeModel;
        private Point _pointerDownPosition;
        private Point _originScreen;
        private TransformAxis _dragAxis;
        private double _axisWorldLength;
        private bool _pointerMoved;
        private bool _isDragging;
        private bool _isEnabled = true;

        public ViewportModelInteractionController(
            FrameworkElement inputSurface,
            ViewportTransformGizmo gizmoCanvas,
            Func<ProjectionCamera> cameraProvider,
            IReadOnlyList<SceneModel> sceneModels)
        {
            _inputSurface = inputSurface ?? throw new ArgumentNullException(nameof(inputSurface));
            _gizmoCanvas = gizmoCanvas ?? throw new ArgumentNullException(nameof(gizmoCanvas));
            _cameraProvider = cameraProvider ?? throw new ArgumentNullException(nameof(cameraProvider));
            _sceneModels = sceneModels ?? throw new ArgumentNullException(nameof(sceneModels));

            _inputSurface.PreviewMouseDown += OnPreviewMouseDown;
            _inputSurface.PreviewMouseMove += OnPreviewMouseMove;
            _inputSurface.MouseUp += OnMouseUp;
        }

        public event Action<SceneModel, ModifierKeys> SelectionRequested;

        /// <summary>
        /// World transform used for picking. Consumers that draw models in another convention (VFX
        /// Studio mirrors Characters on X) supply it so hits match what is on screen.
        /// </summary>
        public Func<SceneModel, Matrix4x4> WorldMatrixProvider { get; set; }

        /// <summary>
        /// Raised while the active selection is translated through the shared viewport gizmo.
        /// Consumers with their own placement ViewModel can mirror the SceneModel transform without
        /// duplicating the interaction controller.
        /// </summary>
        public event Action<SceneModel> TransformChanged;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                if (!value) _gizmoCanvas.Clear();
            }
        }

        public void SetSelection(IEnumerable<SceneModel> models, SceneModel activeModel)
        {
            if (!ReferenceEquals(_activeModel, activeModel))
            {
                _isDragging = false;
                _dragAxis = TransformAxis.None;
                _dragStartPositions.Clear();
                _axisEndpoints.Clear();
                _gizmoCanvas.Clear();
                if (_inputSurface.IsMouseCaptured)
                    _inputSurface.ReleaseMouseCapture();
                _inputSurface.Cursor = Cursors.Arrow;
            }
            _selectedModels.Clear();
            if (models != null)
                _selectedModels.AddRange(models.Where(model => model != null));
            _activeModel = activeModel;
            if (_activeModel == null)
                _gizmoCanvas.Clear();
        }

        public void Update(Matrix4x4 viewProjection)
        {
            ProjectionCamera camera = _cameraProvider();
            if (!_isEnabled ||
                _activeModel == null ||
                !_activeModel.IsVisible ||
                _inputSurface.ActualWidth <= 0 ||
                _inputSurface.ActualHeight <= 0 ||
                camera == null)
            {
                _gizmoCanvas.Clear();
                return;
            }

            Vector3 origin = new(
                (float)_activeModel.PositionX,
                (float)_activeModel.PositionY,
                (float)_activeModel.PositionZ);
            if (!ViewerInteractionService.TryCalculateGizmoAxisLength(
                    origin, viewProjection, _inputSurface.ActualHeight, out _axisWorldLength))
            {
                _gizmoCanvas.Clear();
                return;
            }
            if (!ViewerInteractionService.TryProject(
                    origin,
                    viewProjection,
                    _inputSurface.ActualWidth,
                    _inputSurface.ActualHeight,
                    out _originScreen))
            {
                _gizmoCanvas.Clear();
                return;
            }

            _axisEndpoints[TransformAxis.X] =
                Project(origin + Vector3.UnitX * (float)_axisWorldLength, viewProjection);
            _axisEndpoints[TransformAxis.Y] =
                Project(origin + Vector3.UnitY * (float)_axisWorldLength, viewProjection);
            _axisEndpoints[TransformAxis.Z] =
                Project(origin + Vector3.UnitZ * (float)_axisWorldLength, viewProjection);

            _gizmoCanvas.SetPoints(_originScreen,
                _axisEndpoints[TransformAxis.X],
                _axisEndpoints[TransformAxis.Y],
                _axisEndpoints[TransformAxis.Z]);
        }

        public void Dispose()
        {
            _inputSurface.PreviewMouseDown -= OnPreviewMouseDown;
            _inputSurface.PreviewMouseMove -= OnPreviewMouseMove;
            _inputSurface.MouseUp -= OnMouseUp;
            _selectedModels.Clear();
            _dragStartPositions.Clear();
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            _pointerDownPosition = e.GetPosition(_inputSurface);
            _pointerMoved = false;
            if (!_isEnabled || _activeModel == null) return;

            TransformAxis axis = HitTestAxis(_pointerDownPosition);
            if (axis == TransformAxis.None) return;

            _dragAxis = axis;
            _isDragging = true;
            _dragStartPositions.Clear();
            IEnumerable<SceneModel> targets = _selectedModels.Count > 0
                ? _selectedModels
                : new[] { _activeModel };
            foreach (SceneModel model in targets)
            {
                _dragStartPositions.Add((
                    model,
                    new Vector3(
                        (float)model.PositionX,
                        (float)model.PositionY,
                        (float)model.PositionZ)));
            }

            _inputSurface.CaptureMouse();
            _inputSurface.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            Point current = e.GetPosition(_inputSurface);
            WpfVector movement = current - _pointerDownPosition;
            if (movement.Length > 4) _pointerMoved = true;
            if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;

            Point endpoint = _axisEndpoints[_dragAxis];
            WpfVector screenAxis = endpoint - _originScreen;
            double screenLength = screenAxis.Length;
            if (screenLength <= 1) return;
            screenAxis.Normalize();

            double worldDelta = WpfVector.Multiply(movement, screenAxis) * _axisWorldLength / screenLength;
            foreach ((SceneModel model, Vector3 start) in _dragStartPositions)
            {
                switch (_dragAxis)
                {
                    case TransformAxis.X:
                        model.PositionX = start.X + worldDelta;
                        break;
                    case TransformAxis.Y:
                        model.PositionY = start.Y + worldDelta;
                        break;
                    case TransformAxis.Z:
                        model.PositionZ = start.Z + worldDelta;
                        break;
                }
            }
            TransformChanged?.Invoke(_activeModel);
            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (_isDragging)
            {
                _isDragging = false;
                _dragAxis = TransformAxis.None;
                _dragStartPositions.Clear();
                _inputSurface.ReleaseMouseCapture();
                _inputSurface.Cursor = Cursors.Arrow;
                e.Handled = true;
                return;
            }

            ProjectionCamera projectionCamera = _cameraProvider();
            if (!_isEnabled || _pointerMoved || projectionCamera is not PerspectiveCamera camera) return;

            SceneModel picked = ViewerInteractionService.PickModel(
                _sceneModels,
                e.GetPosition(_inputSurface),
                _inputSurface.ActualWidth,
                _inputSurface.ActualHeight,
                camera,
                WorldMatrixProvider);
            SelectionRequested?.Invoke(picked, Keyboard.Modifiers);
        }

        private Point Project(Vector3 point, Matrix4x4 viewProjection)
        {
            return ViewerInteractionService.TryProject(
                point,
                viewProjection,
                _inputSurface.ActualWidth,
                _inputSurface.ActualHeight,
                out Point result)
                ? result
                : _originScreen;
        }

        private TransformAxis HitTestAxis(Point point)
        {
            const double threshold = 10;
            TransformAxis closestAxis = TransformAxis.None;
            double closestDistance = threshold;
            foreach ((TransformAxis axis, Point endpoint) in _axisEndpoints)
            {
                if ((endpoint - _originScreen).Length < 15) continue;
                double distance = DistanceToSegment(point, _originScreen, endpoint);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestAxis = axis;
                }
            }
            return closestAxis;
        }

        private static double DistanceToSegment(Point point, Point start, Point end)
        {
            WpfVector segment = end - start;
            if (segment.LengthSquared <= double.Epsilon) return (point - start).Length;
            double t = Math.Clamp(
                WpfVector.Multiply(point - start, segment) / segment.LengthSquared,
                0,
                1);
            Point projection = start + segment * t;
            return (point - projection).Length;
        }


    }
}
