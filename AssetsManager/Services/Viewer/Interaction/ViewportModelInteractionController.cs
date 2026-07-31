using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
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
        private readonly Canvas _gizmoCanvas;
        private readonly Line _xAxis;
        private readonly Line _yAxis;
        private readonly Line _zAxis;
        private readonly Ellipse _originMarker;
        private readonly Func<PerspectiveCamera> _cameraProvider;
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
            Canvas gizmoCanvas,
            Line xAxis,
            Line yAxis,
            Line zAxis,
            Ellipse originMarker,
            Func<PerspectiveCamera> cameraProvider,
            IReadOnlyList<SceneModel> sceneModels)
        {
            _inputSurface = inputSurface ?? throw new ArgumentNullException(nameof(inputSurface));
            _gizmoCanvas = gizmoCanvas ?? throw new ArgumentNullException(nameof(gizmoCanvas));
            _xAxis = xAxis ?? throw new ArgumentNullException(nameof(xAxis));
            _yAxis = yAxis ?? throw new ArgumentNullException(nameof(yAxis));
            _zAxis = zAxis ?? throw new ArgumentNullException(nameof(zAxis));
            _originMarker = originMarker ?? throw new ArgumentNullException(nameof(originMarker));
            _cameraProvider = cameraProvider ?? throw new ArgumentNullException(nameof(cameraProvider));
            _sceneModels = sceneModels ?? throw new ArgumentNullException(nameof(sceneModels));

            _inputSurface.PreviewMouseDown += OnPreviewMouseDown;
            _inputSurface.PreviewMouseMove += OnPreviewMouseMove;
            _inputSurface.MouseUp += OnMouseUp;
        }

        public event Action<SceneModel, ModifierKeys> SelectionRequested;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                if (!value) _gizmoCanvas.Visibility = Visibility.Collapsed;
            }
        }

        public void SetSelection(IEnumerable<SceneModel> models, SceneModel activeModel)
        {
            _selectedModels.Clear();
            if (models != null)
                _selectedModels.AddRange(models.Where(model => model != null));
            _activeModel = activeModel;
            if (_activeModel == null)
                _gizmoCanvas.Visibility = Visibility.Collapsed;
        }

        public void Update(Matrix4x4 viewProjection)
        {
            PerspectiveCamera camera = _cameraProvider();
            if (!_isEnabled ||
                _activeModel == null ||
                !_activeModel.IsVisible ||
                _inputSurface.ActualWidth <= 0 ||
                _inputSurface.ActualHeight <= 0 ||
                camera == null)
            {
                _gizmoCanvas.Visibility = Visibility.Collapsed;
                return;
            }

            Vector3 origin = new(
                (float)_activeModel.PositionX,
                (float)_activeModel.PositionY,
                (float)_activeModel.PositionZ);
            _axisWorldLength = Math.Max(35, camera.LookDirection.Length * 0.12);
            if (!ViewerInteractionService.TryProject(
                    origin,
                    viewProjection,
                    _inputSurface.ActualWidth,
                    _inputSurface.ActualHeight,
                    out _originScreen))
            {
                _gizmoCanvas.Visibility = Visibility.Collapsed;
                return;
            }

            _axisEndpoints[TransformAxis.X] =
                Project(origin + Vector3.UnitX * (float)_axisWorldLength, viewProjection);
            _axisEndpoints[TransformAxis.Y] =
                Project(origin + Vector3.UnitY * (float)_axisWorldLength, viewProjection);
            _axisEndpoints[TransformAxis.Z] =
                Project(origin + Vector3.UnitZ * (float)_axisWorldLength, viewProjection);

            SetLine(_xAxis, _originScreen, _axisEndpoints[TransformAxis.X]);
            SetLine(_yAxis, _originScreen, _axisEndpoints[TransformAxis.Y]);
            SetLine(_zAxis, _originScreen, _axisEndpoints[TransformAxis.Z]);
            Canvas.SetLeft(_originMarker, _originScreen.X - _originMarker.Width / 2);
            Canvas.SetTop(_originMarker, _originScreen.Y - _originMarker.Height / 2);
            _gizmoCanvas.Visibility = Visibility.Visible;
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

            PerspectiveCamera camera = _cameraProvider();
            if (!_isEnabled || _pointerMoved || camera == null) return;

            SceneModel picked = ViewerInteractionService.PickModel(
                _sceneModels,
                e.GetPosition(_inputSurface),
                _inputSurface.ActualWidth,
                _inputSurface.ActualHeight,
                camera);
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

        private static void SetLine(Line line, Point start, Point end)
        {
            line.X1 = start.X;
            line.Y1 = start.Y;
            line.X2 = end.X;
            line.Y2 = end.Y;
        }
    }
}
