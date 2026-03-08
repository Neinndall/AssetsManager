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
            OldDiffMapHost.MouseLeftButtonDown -= NavigationPanel_MouseLeftButtonDown;
            OldDiffMapHost.MouseMove -= NavigationPanel_MouseMove;
            OldDiffMapHost.MouseLeftButtonUp -= NavigationPanel_MouseLeftButtonUp;
            OldDiffMapHost.SizeChanged -= MapHost_SizeChanged;

            NewDiffMapHost.MouseLeftButtonDown -= NavigationPanel_MouseLeftButtonDown;
            NewDiffMapHost.MouseMove -= NavigationPanel_MouseMove;
            NewDiffMapHost.MouseLeftButtonUp -= NavigationPanel_MouseLeftButtonUp;
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
            OldDiffMapHost.MouseLeftButtonDown -= NavigationPanel_MouseLeftButtonDown;
            OldDiffMapHost.MouseMove -= NavigationPanel_MouseMove;
            OldDiffMapHost.MouseLeftButtonUp -= NavigationPanel_MouseLeftButtonUp;
            OldDiffMapHost.SizeChanged -= MapHost_SizeChanged;

            NewDiffMapHost.MouseLeftButtonDown -= NavigationPanel_MouseLeftButtonDown;
            NewDiffMapHost.MouseMove -= NavigationPanel_MouseMove;
            NewDiffMapHost.MouseLeftButtonUp -= NavigationPanel_MouseLeftButtonUp;
            NewDiffMapHost.SizeChanged -= MapHost_SizeChanged;

            OldDiffMapHost.MouseLeftButtonDown += NavigationPanel_MouseLeftButtonDown;
            OldDiffMapHost.MouseMove += NavigationPanel_MouseMove;
            OldDiffMapHost.MouseLeftButtonUp += NavigationPanel_MouseLeftButtonUp;
            OldDiffMapHost.SizeChanged += MapHost_SizeChanged;

            NewDiffMapHost.MouseLeftButtonDown += NavigationPanel_MouseLeftButtonDown;
            NewDiffMapHost.MouseMove += NavigationPanel_MouseMove;
            NewDiffMapHost.MouseLeftButtonUp += NavigationPanel_MouseLeftButtonUp;
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
            
            // CRÍTICO: Resetear guías para que se vuelvan a añadir al host tras el ClearVisuals de DrawMapMarkers
            _oldViewportGuide = null;
            _newViewportGuide = null;
        }

        private void DrawMapMarkers(VisualHost host, DiffPaneModel pane)
        {
            host.ClearVisuals();
            var backgroundVisual = new DrawingVisual();
            using (var dc = backgroundVisual.RenderOpen())
            {
                dc.DrawRectangle(_backgroundPanelBrush, null, new Rect(0, 0, host.ActualWidth, host.ActualHeight));

                if (pane.Lines.Count > 0 && host.ActualHeight > 0)
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
            if (_oldEditor != null) UpdateViewport(OldDiffMapHost, _oldEditor, ref _oldViewportGuide);
            if (_newEditor != null) UpdateViewport(NewDiffMapHost, _newEditor, ref _newViewportGuide);
        }

        private void UpdateViewport(VisualHost host, TextEditor editor, ref DrawingVisual guide)
        {
            // Seguridad: Validar nulos y estado del editor para evitar crash en Disposal
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
                if (lines[i].Type != ChangeType.Unchanged && lines[i].Type != ChangeType.Imaginary)
                {
                    _diffLines.Add(i + 1);
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

        public void NavigateToDifferenceByIndex(int index)
        {
            if (index >= 0 && index < _diffLines.Count)
            {
                ParentControl?.ScrollToLine(_diffLines[index]);
            }
        }

        public int FindClosestDifferenceIndex(int line)
        {
            if (!_diffLines.Any()) return -1;
            int index = _diffLines.FindIndex(l => l >= line);
            return index != -1 ? index : _diffLines.Count - 1;
        }

        private void NavigationPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            var host = sender as VisualHost;
            host.CaptureMouse();
            ProcessMousePosition(host, e.GetPosition(host).Y);
        }

        private void NavigationPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging) ProcessMousePosition(sender as VisualHost, e.GetPosition(sender as VisualHost).Y);
        }

        private void NavigationPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            (sender as VisualHost).ReleaseMouseCapture();
        }

        private void ProcessMousePosition(VisualHost host, double y)
        {
            if (host == null || host.ActualHeight <= 0) return;
            int lineCount = _diffModel?.NewText.Lines.Count ?? _unifiedLines?.Count ?? 0;
            if (lineCount <= 0) return;

            int lineNumber = (int)(lineCount * (y / host.ActualHeight)) + 1;
            ParentControl?.ScrollToLine(Math.Max(1, Math.Min(lineCount, lineNumber)));
        }
    }
}
