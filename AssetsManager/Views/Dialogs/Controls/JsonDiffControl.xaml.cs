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
using System.Collections.Generic;
using AssetsManager.Views.Models.Dialogs;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class JsonDiffControl : UserControl, IDisposable
    {
        private SideBySideDiffModel _originalDiffModel;
        private DiffPaneModel _unifiedModel;
        private string _oldText;
        private string _newText;
        
        // Cache documents to avoid recreation and flickering
        private TextDocument _cachedOldDoc;
        private TextDocument _cachedNewDoc;

        public JsonDiffModel State { get; } = new JsonDiffModel();
        
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public JsonFormatterService JsonFormatterService { get; set; }
        public LogService LogService { get; set; }
        public event EventHandler<bool> ComparisonFinished;

        public JsonDiffControl()
        {
            InitializeComponent();
            this.DataContext = State;
            LoadJsonSyntaxHighlighting();
            SetupScrollSync();
        }

        public void Dispose()
        {
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

            OldJsonContent?.TextArea?.TextView?.BackgroundRenderers.Clear();
            NewJsonContent?.TextArea?.TextView?.BackgroundRenderers.Clear();
            UnifiedDiffEditor?.TextArea?.TextView?.BackgroundRenderers.Clear();
            
            OldJsonContent.Document = null;
            NewJsonContent.Document = null;
            UnifiedDiffEditor.Document = null;
            
            _cachedOldDoc = null;
            _cachedNewDoc = null;

            _originalDiffModel = null;
            _oldText = null;
            _newText = null;
            _unifiedModel = null;

            DiffNavigationPanel = null;
            CustomMessageBoxService = null;
        }

        public void FocusFirstDifference()
        {
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
                
                // Reset caches on new load
                _cachedOldDoc = null;
                _cachedNewDoc = null;
                _unifiedModel = null;

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

            // --- INLINE MODE LOGIC ---
            if (State.IsInlineMode)
            {
                // Only build model and document if not already cached or if hiding unchanged lines
                // (Filtering changes the document content, so we might need to regenerate if that setting changed)
                // For this implementation, to ensure smoothness on mode switch, we cache the FULL unified doc.
                // If HideUnchangedLines is toggled, we might need to regenerate. 
                // But for simple Mode Switching, we use the cache.

                if (_unifiedModel == null)
                {
                     _unifiedModel = await Task.Run(() => new InlineDiffBuilder(new Differ()).BuildDiffModel(_oldText, _newText));
                }

                var linesForUnified = State.HideUnchangedLines
                    ? _unifiedModel.Lines.Where(l => l.Type != ChangeType.Unchanged).ToList()
                    : _unifiedModel.Lines;
                
                // Fix variable naming consistency
                var linesToShow = linesForUnified;

                // Create document only if needed or if content changed due to filtering
                // Note: Recreating document is necessary if we filter lines, as the text changes.
                // But for mode switch without filtering change, we can preserve it?
                // For simplicity and fluidity, we will regenerate the text only if needed.
                
                // Let's just generate the text. Text generation is fast. Document creation causes flash if assigned.
                string combinedText = string.Join(Environment.NewLine, linesToShow.Select(l => l.Text));
                
                if (UnifiedDiffEditor.Document == null || UnifiedDiffEditor.Document.TextLength != combinedText.Length || UnifiedDiffEditor.Text != combinedText)
                {
                     UnifiedDiffEditor.Document = new TextDocument(combinedText);
                     UnifiedDiffEditor.TextArea.TextView.BackgroundRenderers.Clear();
                     UnifiedDiffEditor.TextArea.TextView.BackgroundRenderers.Add(new UnifiedDiffBackgroundRenderer(linesToShow));
                }

                DiffNavigationPanel.Initialize(UnifiedDiffEditor, linesForUnified);
                DiffNavigationPanel.ScrollRequested -= ScrollToLine;
                DiffNavigationPanel.ScrollRequested += ScrollToLine;

                // Force layout update and restore position instantly
                UnifiedDiffEditor.UpdateLayout();
                
                await Dispatcher.InvokeAsync(() =>
                {
                     if (diffIndexToRestore.HasValue && diffIndexToRestore.Value != -1)
                        DiffNavigationPanel?.NavigateToDifferenceByIndex(diffIndexToRestore.Value);
                    else
                        FocusFirstDifference();
                }, System.Windows.Threading.DispatcherPriority.Render);

                return;
            }

            // --- SIDE BY SIDE MODE LOGIC ---
            var modelToShow = State.HideUnchangedLines ? FilterDiffModel(_originalDiffModel) : _originalDiffModel;
            var originalModelForNav = State.HideUnchangedLines ? _originalDiffModel : null;

            // Only regenerate documents if we don't have them cached OR if we are filtering (which changes content)
            // If State.HideUnchangedLines changed, we MUST regenerate.
            // If just switching modes, _cachedOldDoc should be reused.
            
            bool needToRecreateDocs = _cachedOldDoc == null || State.HideUnchangedLines; 
            // Better check: compare current doc text with model text? Too slow.
            // Simple approach: If cached is null, create. If HideUnchanged is true, we always recreate for now (filtering).
            // But for the specific case of SWITCHING MODES (HideUnchanged=false), we reuse.

            if (needToRecreateDocs && !State.HideUnchangedLines)
            {
                 // Only reuse if not filtering. If filtering, we regenerate for now to be safe.
                 if (_cachedOldDoc != null && OldJsonContent.Document == _cachedOldDoc) needToRecreateDocs = false;
            }

            if (needToRecreateDocs)
            {
                var (normalizedOld, normalizedNew) = await Task.Run(() =>
                {
                    var nOld = JsonFormatterService.NormalizeTextForAlignment(modelToShow.OldText);
                    var nNew = JsonFormatterService.NormalizeTextForAlignment(modelToShow.NewText);
                    return (nOld, nNew);
                });

                _cachedOldDoc = new TextDocument(normalizedOld.Text);
                _cachedNewDoc = new TextDocument(normalizedNew.Text);
                
                OldJsonContent.Document = _cachedOldDoc;
                NewJsonContent.Document = _cachedNewDoc;
                
                ApplyDiffHighlighting(modelToShow);
            }
            else
            {
                // Ensure the editor has the cached docs assigned (in case they were detached, though we don't detach them)
                if (OldJsonContent.Document != _cachedOldDoc) OldJsonContent.Document = _cachedOldDoc;
                if (NewJsonContent.Document != _cachedNewDoc) NewJsonContent.Document = _cachedNewDoc;
            }

            OldJsonContent.UpdateLayout();
            NewJsonContent.UpdateLayout();

            DiffNavigationPanel.Initialize(OldJsonContent, NewJsonContent, modelToShow, originalModelForNav);
            DiffNavigationPanel.ScrollRequested -= ScrollToLine;
            DiffNavigationPanel.ScrollRequested += ScrollToLine;

            // Instant restore
            await Dispatcher.InvokeAsync(() =>
            {
                if (diffIndexToRestore.HasValue && diffIndexToRestore.Value != -1)
                {
                    DiffNavigationPanel?.NavigateToDifferenceByIndex(diffIndexToRestore.Value);
                }
                else
                {
                    if (diffIndexToRestore == null) FocusFirstDifference();
                }
                RefreshGuidePosition();
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        private async void ComparisonInlineMode_Checked(object sender, RoutedEventArgs e)
        {
            if (UnifiedBtn == null || SideBySideBtn == null) return;

            // Always go to the first difference when switching modes to prevent jumps.
            int currentDiffIndex = 0;

            LogService?.Log($"[JsonDiffControl] Mode Switch. Resetting to First Difference.");

            await UpdateDiffView(currentDiffIndex);
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

            OldJsonContent.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(diffModel, State.IsWordLevelDiff, true));
            NewJsonContent.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(diffModel, State.IsWordLevelDiff, false));
        }

        private void ScrollToLine(int lineNumber)
        {
            if (State.IsInlineMode)
            {
                UnifiedDiffEditor.UpdateLayout();
                UnifiedDiffEditor.ScrollTo(lineNumber, 0);
                UnifiedDiffEditor.TextArea.Caret.Line = lineNumber;
                UnifiedDiffEditor.Focus();
                return;
            }

            OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= OldEditor_ScrollChanged;
            NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= NewEditor_ScrollChanged;

            try
            {
                OldJsonContent.UpdateLayout();
                NewJsonContent.UpdateLayout();

                OldJsonContent.ScrollTo(lineNumber, 0);
                NewJsonContent.ScrollTo(lineNumber, 0);

                if (DiffNavigationPanel != null)
                {
                    DiffNavigationPanel.CurrentLine = lineNumber;
                    DiffNavigationPanel.UpdateViewportGuide();
                }

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
            OldJsonContent.WordWrap = State.IsWordWrapEnabled;
            NewJsonContent.WordWrap = State.IsWordWrapEnabled;
            UnifiedDiffEditor.WordWrap = State.IsWordWrapEnabled;
        }

        private void NextDiffButton_Click(object sender, RoutedEventArgs e)
        {
            int currentLine = State.IsInlineMode ? UnifiedDiffEditor.TextArea.Caret.Line : NewJsonContent.TextArea.Caret.Line;
            DiffNavigationPanel?.NavigateToNextDifference(currentLine);
        }

        private void PreviousDiffButton_Click(object sender, RoutedEventArgs e)
        {
            int currentLine = State.IsInlineMode ? UnifiedDiffEditor.TextArea.Caret.Line : NewJsonContent.TextArea.Caret.Line;
            DiffNavigationPanel?.NavigateToPreviousDifference(currentLine);
        }

        private void WordLevelDiffButton_Click(object sender, RoutedEventArgs e)
        {
            var modelToShow = State.HideUnchangedLines ? FilterDiffModel(_originalDiffModel) : _originalDiffModel;
            ApplyDiffHighlighting(modelToShow);

            OldJsonContent.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            NewJsonContent.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }

        private async void HideUnchangedButton_Click(object sender, RoutedEventArgs e)
        {
            // When filtering toggles, we MUST recreate documents/content, so flash is expected here, but not on mode switch.
            _cachedOldDoc = null; 
            
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
