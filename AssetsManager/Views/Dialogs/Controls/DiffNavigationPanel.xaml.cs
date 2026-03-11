using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class DiffNavigationPanel : UserControl
    {
        public JsonDiffControl ParentControl { get; set; }

        private TextEditor _oldEditor;
        private TextEditor _newEditor;
        private SideBySideDiffModel _diffModel;
        private SideBySideDiffModel _originalDiffModel;
        private IList<DiffPiece> _unifiedLines;
        private bool _isDragging;
        private readonly List<int> _diffLines;
        public int CurrentLine { get; set; }

        private readonly SolidColorBrush _backgroundPanelBrush, _addedBrush, _removedBrush, _modifiedBrush, _imaginaryBrush, _viewportBrush;
        private DrawingVisual _oldViewportGuide, _newViewportGuide;

        public DiffNavigationPanel()
        {
            InitializeComponent();
            _diffLines = new List<int>();

            _backgroundPanelBrush = new SolidColorBrush((Color)Application.Current.FindResource("BackgroundPanelNavigation"));
            _addedBrush = new SolidColorBrush((Color)Application.Current.FindResource("DiffNavigationAdded"));
            _removedBrush = new SolidColorBrush((Color)Application.Current.FindResource("DiffNavigationRemoved"));
            _modifiedBrush = new SolidColorBrush((Color)Application.Current.FindResource("DiffNavigationModified"));
            _imaginaryBrush = new SolidColorBrush((Color)Application.Current.FindResource("DiffNavigationImaginary"));
            _viewportBrush = new SolidColorBrush((Color)Application.Current.FindResource("DiffNavigationViewPort"));

            _backgroundPanelBrush.Freeze();
            _addedBrush.Freeze();
            _removedBrush.Freeze();
            _modifiedBrush.Freeze();
            _imaginaryBrush.Freeze();
            _viewportBrush.Freeze();

            this.Unloaded += DiffNavigationPanel_Unloaded;
        }

        private void DiffNavigationPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        public void Cleanup()
        {
            // Desuscribir de los contenedores para permitir navegación en toda la superficie
            OldDiffMapContainer.MouseLeftButtonDown -= NavigationPanel_MouseLeftButtonDown;
            OldDiffMapContainer.MouseMove -= NavigationPanel_MouseMove;
            OldDiffMapContainer.MouseLeftButtonUp -= NavigationPanel_MouseLeftButtonUp;
            OldDiffMapHost.SizeChanged -= MapHost_SizeChanged;

            NewDiffMapContainer.MouseLeftButtonDown -= NavigationPanel_MouseLeftButtonDown;
            NewDiffMapContainer.MouseMove -= NavigationPanel_MouseMove;
            NewDiffMapContainer.MouseLeftButtonUp -= NavigationPanel_MouseLeftButtonUp;
            NewDiffMapHost.SizeChanged -= MapHost_SizeChanged;

            OldDiffMapHost.ClearVisuals();
            NewDiffMapHost.ClearVisuals();

            _oldEditor = null;
            _newEditor = null;
            _diffModel = null;
            _originalDiffModel = null;
            _unifiedLines = null;
            _diffLines.Clear();
            _oldViewportGuide = null;
            _newViewportGuide = null;
            ParentControl = null;
        }

        public void Initialize(TextEditor oldEditor, TextEditor newEditor, SideBySideDiffModel diffModel, SideBySideDiffModel originalDiffModel = null)
        {
            _oldEditor = oldEditor;
            _newEditor = newEditor;
            _diffModel = diffModel;
            _originalDiffModel = originalDiffModel ?? diffModel;
            _unifiedLines = null;

            SetupEvents();
            FindDiffLines();
            InitializeDiffMarkers();
            UpdateViewportGuide();
        }

        public void Initialize(TextEditor editor, IList<DiffPiece> lines)
        {
            _oldEditor = null;
            _newEditor = editor;
            _unifiedLines = lines;
            _diffModel = null;
            _originalDiffModel = null;

            FindDiffLines();
        }

        private void SetupEvents()
        {
            // Limpieza previa
            OldDiffMapContainer.MouseLeftButtonDown -= NavigationPanel_MouseLeftButtonDown;
            OldDiffMapContainer.MouseMove -= NavigationPanel_MouseMove;
            OldDiffMapContainer.MouseLeftButtonUp -= NavigationPanel_MouseLeftButtonUp;
            OldDiffMapHost.SizeChanged -= MapHost_SizeChanged;

            NewDiffMapContainer.MouseLeftButtonDown -= NavigationPanel_MouseLeftButtonDown;
            NewDiffMapContainer.MouseMove -= NavigationPanel_MouseMove;
            NewDiffMapContainer.MouseLeftButtonUp -= NavigationPanel_MouseLeftButtonUp;
            NewDiffMapHost.SizeChanged -= MapHost_SizeChanged;

            // Suscribir a los contenedores
            OldDiffMapContainer.MouseLeftButtonDown += NavigationPanel_MouseLeftButtonDown;
            OldDiffMapContainer.MouseMove += NavigationPanel_MouseMove;
            OldDiffMapContainer.MouseLeftButtonUp += NavigationPanel_MouseLeftButtonUp;
            OldDiffMapHost.SizeChanged += MapHost_SizeChanged;

            NewDiffMapContainer.MouseLeftButtonDown += NavigationPanel_MouseLeftButtonDown;
            NewDiffMapContainer.MouseMove += NavigationPanel_MouseMove;
            NewDiffMapContainer.MouseLeftButtonUp += NavigationPanel_MouseLeftButtonUp;
            NewDiffMapHost.SizeChanged += MapHost_SizeChanged;
        }

        private void MapHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            InitializeDiffMarkers();
            UpdateViewportGuide();
        }

        public void InitializeDiffMarkers()
        {
            if (_originalDiffModel == null) return;

            DrawMapMarkers(OldDiffMapHost, _originalDiffModel.OldText);
            DrawMapMarkers(NewDiffMapHost, _originalDiffModel.NewText);
            
            _oldViewportGuide = null;
            _newViewportGuide = null;
        }

        private void DrawMapMarkers(VisualHost host, DiffPaneModel pane)
        {
            if (host == null || host.ActualWidth <= 0 || host.ActualHeight <= 0) return;
            host.ClearVisuals();
            
            var backgroundVisual = new DrawingVisual();
            using (var dc = backgroundVisual.RenderOpen())
            {
                if (pane.Lines.Count > 0)
                {
                    double ratio = host.ActualHeight / pane.Lines.Count;
                    for (int i = 0; i < pane.Lines.Count; i++)
                    {
                        var line = pane.Lines[i];
                        if (line.Type == ChangeType.Unchanged) continue;

                        Brush brush = line.Type switch
                        {
                            ChangeType.Inserted => _addedBrush,
                            ChangeType.Deleted => _removedBrush,
                            ChangeType.Modified => _modifiedBrush,
                            ChangeType.Imaginary => _imaginaryBrush,
                            _ => null
                        };

                        if (brush != null)
                        {
                            dc.DrawRectangle(brush, null, new Rect(0, i * ratio, host.ActualWidth, Math.Max(1, ratio)));
                        }
                    }
                }
            }
            host.AddVisual(backgroundVisual);
        }

        public void UpdateViewportGuide()
        {
            // Esto arregla el scroll arrastrando la guia (evita que el editor luche contra el ratón)
            if (_isDragging) return;

            if (_oldEditor != null) UpdateViewport(OldDiffMapHost, _oldEditor, ref _oldViewportGuide);
            if (_newEditor != null) UpdateViewport(NewDiffMapHost, _newEditor, ref _newViewportGuide);
        }

        private void UpdateViewport(VisualHost host, TextEditor editor, ref DrawingVisual guide)
        {
            if (host == null || editor == null || editor.Document == null || editor.TextArea?.TextView == null || host.ActualHeight <= 0 || editor.Document.LineCount <= 0) return;

            if (guide == null)
            {
                guide = new DrawingVisual();
                host.AddVisual(guide);
            }

            double visibleLines = editor.TextArea.TextView.ActualHeight / editor.TextArea.TextView.DefaultLineHeight;
            double topLines = editor.TextArea.TextView.VerticalOffset / editor.TextArea.TextView.DefaultLineHeight;
            double ratio = host.ActualHeight / editor.Document.LineCount;

            using (var dc = guide.RenderOpen())
            {
                dc.DrawRectangle(null, new Pen(_viewportBrush, 1), new Rect(0, topLines * ratio, host.ActualWidth, Math.Max(visibleLines * ratio, 2)));
                dc.DrawRectangle(_viewportBrush, null, new Rect(0, topLines * ratio, host.ActualWidth, Math.Max(visibleLines * ratio, 2)));
            }
        }

        private void FindDiffLines()
        {
            _diffLines.Clear();
            var lines = _diffModel?.NewText.Lines ?? _unifiedLines;
            if (lines == null) return;

            for (int i = 0; i < lines.Count; i++)
            {
                bool isChange = _diffModel != null 
                    ? (_diffModel.OldText.Lines[i].Type != ChangeType.Unchanged && _diffModel.OldText.Lines[i].Type != ChangeType.Imaginary) ||
                      (_diffModel.NewText.Lines[i].Type != ChangeType.Unchanged && _diffModel.NewText.Lines[i].Type != ChangeType.Imaginary)
                    : (lines[i].Type != ChangeType.Unchanged && lines[i].Type != ChangeType.Imaginary);

                if (isChange)
                {
                    bool isStart = i == 0 || (_diffModel != null 
                        ? _diffModel.OldText.Lines[i].Type != _diffModel.OldText.Lines[i - 1].Type || _diffModel.NewText.Lines[i].Type != _diffModel.NewText.Lines[i - 1].Type
                        : lines[i].Type != lines[i - 1].Type);

                    if (isStart) _diffLines.Add(i + 1);
                }
            }
        }

        public void NavigateToNextDifference(int currentLine)
        {
            if (!_diffLines.Any()) return;
            int next = _diffLines.FirstOrDefault(l => l > currentLine);
            if (next == 0) next = _diffLines.First();
            ParentControl?.ScrollToLine(next);
        }

        public void NavigateToPreviousDifference(int currentLine)
        {
            if (!_diffLines.Any()) return;
            int prev = _diffLines.LastOrDefault(l => l < currentLine);
            if (prev == 0) prev = _diffLines.Last();
            ParentControl?.ScrollToLine(prev);
        }

        private void NavigationPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Grid container)
            {
                _isDragging = true;
                container.CaptureMouse();
                var host = container.Children.OfType<VisualHost>().FirstOrDefault();
                if (host != null) ProcessMousePosition(host, e.GetPosition(container).Y);
            }
        }

        private void NavigationPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging && sender is Grid container)
            {
                var host = container.Children.OfType<VisualHost>().FirstOrDefault();
                if (host != null) ProcessMousePosition(host, e.GetPosition(container).Y);
            }
        }

        private void NavigationPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                if (sender is Grid container && container.IsMouseCaptured) container.ReleaseMouseCapture();
                UpdateViewportGuide();
            }
        }

        private void ProcessMousePosition(VisualHost host, double y)
        {
            if (host == null || host.ActualHeight <= 0) return;
            double percentage = Math.Max(0, Math.Min(y / host.ActualHeight, 1.0));
            ParentControl?.ScrollToPercentage(percentage);
            UpdateViewportGuideManually(percentage);
        }

        private void UpdateViewportGuideManually(double percentage)
        {
            if (_oldEditor != null) UpdateViewportWithPercentage(OldDiffMapHost, _oldEditor, ref _oldViewportGuide, percentage);
            if (_newEditor != null) UpdateViewportWithPercentage(NewDiffMapHost, _newEditor, ref _newViewportGuide, percentage);
        }

        private void UpdateViewportWithPercentage(VisualHost host, TextEditor editor, ref DrawingVisual guide, double percentage)
        {
            if (host == null || editor == null || host.ActualHeight <= 0) return;

            if (guide == null)
            {
                guide = new DrawingVisual();
                host.AddVisual(guide);
            }

            double viewportHeight = editor.ViewportHeight;
            double extentHeight = editor.ExtentHeight;
            if (extentHeight <= 0) return;

            double ratio = host.ActualHeight / extentHeight;
            double top = (extentHeight - viewportHeight) * percentage * ratio;
            double height = viewportHeight * ratio;

            using (var dc = guide.RenderOpen())
            {
                dc.DrawRectangle(null, new Pen(_viewportBrush, 1), new Rect(0, top, host.ActualWidth, Math.Max(height, 2)));
                dc.DrawRectangle(_viewportBrush, null, new Rect(0, top, host.ActualWidth, Math.Max(height, 2)));
            }
        }
    }
}
