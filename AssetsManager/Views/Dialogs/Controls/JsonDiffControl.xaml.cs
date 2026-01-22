using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Xml;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Media.Animation;
using ICSharpCode.AvalonEdit.Document;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class JsonDiffControl : UserControl, IDisposable
    {
        private SideBySideDiffModel _originalDiffModel;
        private DiffPaneModel _unifiedModel;
        private string _oldText;
        private string _newText;
        private bool _isInlineMode = false;
        private bool _isWordLevelDiff = false;
        private bool _hideUnchangedLines = false;
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public JsonFormatterService JsonFormatterService { get; set; }
        public event EventHandler<bool> ComparisonFinished;

        public JsonDiffControl()
        {
            InitializeComponent();
            LoadJsonSyntaxHighlighting();
            SetupScrollSync();
        }

        public void Dispose()
        {
            // Desuscribir todos los eventos
            if (DiffNavigationPanel != null)
            {
                DiffNavigationPanel.ScrollRequested -= ScrollToLine;
            }
            if (OldJsonContent != null)
            {
                OldJsonContent.Loaded -= OldJsonContent_Loaded;
                if (OldJsonContent.TextArea?.TextView != null)
                {
                    OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= OldEditor_ScrollChanged;
                    OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= OldEditor_UpdateViewportGuide;
                }
            }
            if (NewJsonContent != null)
            {
                if (NewJsonContent.TextArea?.TextView != null)
                {
                    NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= NewEditor_ScrollChanged;
                    NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= NewEditor_UpdateViewportGuide;
                }
            }

            // Limpiar renderers y documentos de AvalonEdit
            OldJsonContent?.TextArea?.TextView?.BackgroundRenderers.Clear();
            NewJsonContent?.TextArea?.TextView?.BackgroundRenderers.Clear();
            UnifiedDiffEditor?.TextArea?.TextView?.BackgroundRenderers.Clear();
            OldJsonContent.Document = null;
            NewJsonContent.Document = null;
            UnifiedDiffEditor.Document = null;

            // Anular referencias a objetos pesados
            _originalDiffModel = null;
            _oldText = null;
            _newText = null;

            // Anular referencias a otros controles y servicios
            DiffNavigationPanel = null;
            CustomMessageBoxService = null;
        }


        public void FocusFirstDifference()
        {
            if (_isInlineMode) return; // Navigation not yet implemented for inline
            DiffNavigationPanel?.NavigateToNextDifference(0);
        }

        public void RefreshGuidePosition()
        {
            DiffNavigationPanel?.UpdateViewportGuide();
        }

        public async Task LoadAndDisplayDiffAsync(string oldText, string newText, string oldFileName, string newFileName)
        {
            try
            {
                _oldText = oldText;
                _newText = newText;
                OldFileNameLabel.Text = oldFileName;
                NewFileNameLabel.Text = newFileName;
                UnifiedFileNameLabel.Text = newFileName;

                _originalDiffModel = await Task.Run(() => new SideBySideDiffBuilder(new Differ()).BuildDiffModel(oldText, newText, false));

                await UpdateDiffView();
            }
            catch (Exception ex)
            {
                CustomMessageBoxService.ShowError("Error", $"Failed to load comparison: {ex.Message}. Check logs for details.", Window.GetWindow(this));
                OnComparisonFinished(false);
            }
        }


        private void LoadJsonSyntaxHighlighting()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = Array.Find(assembly.GetManifestResourceNames(), name => name.EndsWith("JsonSyntaxHighlighting.xshd"));

                if (resourceName != null)
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var reader = XmlReader.Create(stream);
                        var jsonHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                        OldJsonContent.SyntaxHighlighting = jsonHighlighting;
                        NewJsonContent.SyntaxHighlighting = jsonHighlighting;
                        UnifiedDiffEditor.SyntaxHighlighting = jsonHighlighting;
                    }
                }
            }
            catch
            {
                // Silently continue
            }
        }

        private async Task UpdateDiffView(int? diffIndexToRestore = null)
        {
            if (_originalDiffModel == null) return;

            if (_isInlineMode)
            {
                DiffGrid.Visibility = Visibility.Collapsed;
                UnifiedDiffContainer.Visibility = Visibility.Visible;
                await DisplayUnifiedDiff();
                return;
            }

            DiffGrid.Visibility = Visibility.Visible;
            UnifiedDiffContainer.Visibility = Visibility.Collapsed;

            var modelToShow = _hideUnchangedLines ? FilterDiffModel(_originalDiffModel) : _originalDiffModel;
            var originalModelForNav = _hideUnchangedLines ? _originalDiffModel : null;

            var (normalizedOld, normalizedNew) = await Task.Run(() =>
            {
                var nOld = JsonFormatterService.NormalizeTextForAlignment(modelToShow.OldText);
                var nNew = JsonFormatterService.NormalizeTextForAlignment(modelToShow.NewText);
                return (nOld, nNew);
            });

            OldJsonContent.Document = new TextDocument(normalizedOld.Text);
            NewJsonContent.Document = new TextDocument(normalizedNew.Text);

            OldJsonContent.UpdateLayout();
            NewJsonContent.UpdateLayout();

            ApplyDiffHighlighting(modelToShow);

            DiffNavigationPanel.Initialize(OldJsonContent, NewJsonContent, modelToShow, originalModelForNav);
            DiffNavigationPanel.ScrollRequested -= ScrollToLine; // Avoid multiple subscriptions
            DiffNavigationPanel.ScrollRequested += ScrollToLine;

            EventHandler layoutUpdatedHandler = null;
            layoutUpdatedHandler = (s, e) =>
            {
                NewJsonContent.TextArea.TextView.LayoutUpdated -= layoutUpdatedHandler;
                if (diffIndexToRestore.HasValue && diffIndexToRestore.Value != -1)
                {
                    DiffNavigationPanel?.NavigateToDifferenceByIndex(diffIndexToRestore.Value);
                }

            };
            NewJsonContent.TextArea.TextView.LayoutUpdated += layoutUpdatedHandler;
        }

        private async Task DisplayUnifiedDiff()
        {
            _unifiedModel = await Task.Run(() => new InlineDiffBuilder(new Differ()).BuildDiffModel(_oldText, _newText));
            
            var linesToShow = _hideUnchangedLines 
                ? _unifiedModel.Lines.Where(l => l.Type != ChangeType.Unchanged).ToList() 
                : _unifiedModel.Lines;

            string combinedText = string.Join(Environment.NewLine, linesToShow.Select(l => l.Text));
            UnifiedDiffEditor.Document = new TextDocument(combinedText);

            UnifiedDiffEditor.TextArea.TextView.BackgroundRenderers.Clear();
            UnifiedDiffEditor.TextArea.TextView.BackgroundRenderers.Add(new UnifiedDiffBackgroundRenderer(linesToShow));

            // Initialize navigation for Unified mode
            DiffNavigationPanel.Initialize(UnifiedDiffEditor, linesToShow);
            DiffNavigationPanel.ScrollRequested -= ScrollToLine;
            DiffNavigationPanel.ScrollRequested += ScrollToLine;
        }

        private void InlineViewButton_Click(object sender, RoutedEventArgs e)
        {
            _isInlineMode = InlineViewButton.IsChecked ?? false;
            _ = UpdateDiffView();
        }

        private SideBySideDiffModel FilterDiffModel(SideBySideDiffModel originalModel)
        {
            var filteredModel = new SideBySideDiffModel();
            for (int i = 0; i < originalModel.OldText.Lines.Count; i++)
            {
                var oldLine = originalModel.OldText.Lines[i];
                var newLine = originalModel.NewText.Lines[i];

                if (oldLine.Type != ChangeType.Unchanged || newLine.Type != ChangeType.Unchanged)
                {
                    filteredModel.OldText.Lines.Add(oldLine);
                    filteredModel.NewText.Lines.Add(newLine);
                }
            }
            return filteredModel;
        }

        private void ApplyDiffHighlighting(SideBySideDiffModel diffModel)
        {
            OldJsonContent.TextArea.TextView.BackgroundRenderers.Clear();
            NewJsonContent.TextArea.TextView.BackgroundRenderers.Clear();

            OldJsonContent.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(diffModel, _isWordLevelDiff, true));
            NewJsonContent.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(diffModel, _isWordLevelDiff, false));
        }

        private void ScrollToLine(int lineNumber)
        {
            if (_isInlineMode)
            {
                UnifiedDiffEditor.ScrollTo(lineNumber, 0);
                UnifiedDiffEditor.TextArea.Caret.Line = lineNumber;
                UnifiedDiffEditor.Focus();
                return;
            }

            OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= OldEditor_ScrollChanged;
            NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= NewEditor_ScrollChanged;

            try
            {
                OldJsonContent.ScrollTo(lineNumber, 0);
                NewJsonContent.ScrollTo(lineNumber, 0);

                if (DiffNavigationPanel != null)
                {
                    DiffNavigationPanel.CurrentLine = lineNumber;
                    DiffNavigationPanel.UpdateViewportGuide();
                }

                UpdateLayout();

                // The viewport guide is updated automatically by the ScrollOffsetChanged event

                NewJsonContent.TextArea.Caret.Line = lineNumber;
                NewJsonContent.TextArea.Caret.Column = 1;
                NewJsonContent.Focus();
            }
            finally
            {
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged += OldEditor_ScrollChanged;
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged += NewEditor_ScrollChanged;
            }
        }

        private void SetupScrollSync()
        {
            OldJsonContent.Loaded += OldJsonContent_Loaded;
        }

        private void OldJsonContent_Loaded(object sender, RoutedEventArgs e)
        {
            SetupScrollSyncAfterLoaded();
        }

        private void SetupScrollSyncAfterLoaded()
        {
            OldJsonContent.TextArea.TextView.ScrollOffsetChanged += OldEditor_ScrollChanged;
            NewJsonContent.TextArea.TextView.ScrollOffsetChanged += NewEditor_ScrollChanged;

            // This is the main optimization: only update the viewport guide on scroll
            OldJsonContent.TextArea.TextView.ScrollOffsetChanged += OldEditor_UpdateViewportGuide;
            NewJsonContent.TextArea.TextView.ScrollOffsetChanged += NewEditor_UpdateViewportGuide;
        }

        private void OldEditor_UpdateViewportGuide(object sender, EventArgs e)
        {
            DiffNavigationPanel?.UpdateViewportGuide();
        }

        private void NewEditor_UpdateViewportGuide(object sender, EventArgs e)
        {
            DiffNavigationPanel?.UpdateViewportGuide();
        }

        private void OldEditor_ScrollChanged(object sender, EventArgs e)
        {
            NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= NewEditor_ScrollChanged;
            try
            {
                var sourceView = (TextView)sender;
                var newVerticalOffset = Math.Min(sourceView.VerticalOffset, NewJsonContent.ExtentHeight - NewJsonContent.ViewportHeight);
                var newHorizontalOffset = Math.Min(sourceView.HorizontalOffset, NewJsonContent.ExtentWidth - NewJsonContent.ViewportWidth);
                NewJsonContent.ScrollToVerticalOffset(newVerticalOffset);
                NewJsonContent.ScrollToHorizontalOffset(newHorizontalOffset);
            }
            finally
            {
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged += NewEditor_ScrollChanged;
            }
        }

        private void NewEditor_ScrollChanged(object sender, EventArgs e)
        {
            OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= OldEditor_ScrollChanged;
            try
            {
                var sourceView = (TextView)sender;
                var newVerticalOffset = Math.Min(sourceView.VerticalOffset, OldJsonContent.ExtentHeight - OldJsonContent.ViewportHeight);
                var newHorizontalOffset = Math.Min(sourceView.HorizontalOffset, OldJsonContent.ExtentWidth - OldJsonContent.ViewportWidth);
                OldJsonContent.ScrollToVerticalOffset(newVerticalOffset);
                OldJsonContent.ScrollToHorizontalOffset(newHorizontalOffset);
            }
            finally
            {
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged += OldEditor_ScrollChanged;
            }
        }

        private void WordWrapButton_Click(object sender, RoutedEventArgs e)
        {
            bool isEnabled = WordWrapButton.IsChecked ?? false;
            OldJsonContent.WordWrap = isEnabled;
            NewJsonContent.WordWrap = isEnabled;
            UnifiedDiffEditor.WordWrap = isEnabled;
        }

        private void NextDiffButton_Click(object sender, RoutedEventArgs e)
        {
            int currentLine = _isInlineMode ? UnifiedDiffEditor.TextArea.Caret.Line : NewJsonContent.TextArea.Caret.Line;
            DiffNavigationPanel?.NavigateToNextDifference(currentLine);
        }

        private void PreviousDiffButton_Click(object sender, RoutedEventArgs e)
        {
            int currentLine = _isInlineMode ? UnifiedDiffEditor.TextArea.Caret.Line : NewJsonContent.TextArea.Caret.Line;
            DiffNavigationPanel?.NavigateToPreviousDifference(currentLine);
        }

        private void WordLevelDiffButton_Click(object sender, RoutedEventArgs e)
        {
            _isWordLevelDiff = WordLevelDiffButton.IsChecked ?? false;

            var modelToShow = _hideUnchangedLines ? FilterDiffModel(_originalDiffModel) : _originalDiffModel;
            ApplyDiffHighlighting(modelToShow);

            OldJsonContent.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            NewJsonContent.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }

        private async void HideUnchangedButton_Click(object sender, RoutedEventArgs e)
        {
            _hideUnchangedLines = HideUnchangedButton.IsChecked ?? false;
            int currentDiffIndex = DiffNavigationPanel?.FindClosestDifferenceIndex(NewJsonContent.TextArea.Caret.Line) ?? -1;
            try
            {
                await UpdateDiffView(currentDiffIndex);
            }
            catch (Exception ex)
            {
                CustomMessageBoxService.ShowError("Error", $"Failed to update view: {ex.Message}", Window.GetWindow(this));
            }
        }

        protected virtual void OnComparisonFinished(bool success)
        {
            ComparisonFinished?.Invoke(this, success);
        }
    }
}
