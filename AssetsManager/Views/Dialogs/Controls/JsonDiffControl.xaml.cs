using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Dialogs;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class JsonDiffControl : UserControl, IDisposable
    {
        #region Fields
        private SideBySideDiffModel _originalDiffModel;
        private DiffPaneModel _unifiedModel;
        private string _oldText;
        private string _newText;

        // Cache to prevent flickering
        private TextDocument _cachedOldDoc;
        private TextDocument _cachedNewDoc;
        #endregion

        #region Properties
        public JsonDiffModel State { get; } = new JsonDiffModel();
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public JsonFormatterService JsonFormatterService { get; set; }
        public LogService LogService { get; set; }
        #endregion

        #region Events
        public event EventHandler<bool> ComparisonFinished;
        #endregion

        #region Constructor & Setup
        public JsonDiffControl()
        {
            InitializeComponent();
            this.DataContext = State;
            LoadJsonSyntaxHighlighting();
            SetupScrollSync();
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
                // Ignore highlighting errors
            }
        }

        private void SetupScrollSync()
        {
            OldJsonContent.Loaded += (s, e) =>
            {
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged += OldEditor_ScrollChanged;
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged += NewEditor_ScrollChanged;
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged += (sender, args) => RefreshGuidePosition();
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged += (sender, args) => RefreshGuidePosition();
            };
        }
        #endregion

        #region Public Methods
        public void Dispose()
        {
            if (DiffNavigationPanel != null) DiffNavigationPanel.ScrollRequested -= ScrollToLine;
            
            // Unsubscribe events
            if (OldJsonContent?.TextArea?.TextView != null)
            {
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= OldEditor_ScrollChanged;
            }
            if (NewJsonContent?.TextArea?.TextView != null)
            {
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= NewEditor_ScrollChanged;
            }

            // Clear heavy resources
            OldJsonContent?.TextArea?.TextView?.BackgroundRenderers.Clear();
            NewJsonContent?.TextArea?.TextView?.BackgroundRenderers.Clear();
            UnifiedDiffEditor?.TextArea?.TextView?.BackgroundRenderers.Clear();

            OldJsonContent.Document = null;
            NewJsonContent.Document = null;
            UnifiedDiffEditor.Document = null;

            _cachedOldDoc = null;
            _cachedNewDoc = null;
            _originalDiffModel = null;
            _unifiedModel = null;
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

                _cachedOldDoc = null;
                _cachedNewDoc = null;
                _unifiedModel = null;

                _originalDiffModel = await Task.Run(() => new SideBySideDiffBuilder(new Differ()).BuildDiffModel(oldText, newText, false));

                await UpdateDiffView();
            }
            catch (Exception ex)
            {
                CustomMessageBoxService?.ShowError("Error", $"Failed to load comparison: {ex.Message}", Window.GetWindow(this));
                ComparisonFinished?.Invoke(this, false);
            }
        }
        #endregion

        #region View Logic
        private async Task UpdateDiffView(int? diffIndexToRestore = null)
        {
            if (_originalDiffModel == null) return;

            if (State.IsInlineMode)
            {
                await SwitchToInlineView(diffIndexToRestore);
            }
            else
            {
                await SwitchToSideBySideView(diffIndexToRestore);
            }
        }

        private async Task SwitchToInlineView(int? diffIndexToRestore)
        {
            if (_unifiedModel == null)
            {
                _unifiedModel = await Task.Run(() => new InlineDiffBuilder(new Differ()).BuildDiffModel(_oldText, _newText));
            }

            var linesToShow = State.HideUnchangedLines
                ? _unifiedModel.Lines.Where(l => l.Type != ChangeType.Unchanged).ToList()
                : _unifiedModel.Lines;

            string combinedText = string.Join(Environment.NewLine, linesToShow.Select(l => l.Text));

            if (UnifiedDiffEditor.Document == null || UnifiedDiffEditor.Document.TextLength != combinedText.Length || UnifiedDiffEditor.Text != combinedText)
            {
                UnifiedDiffEditor.Document = new TextDocument(combinedText);
                UnifiedDiffEditor.TextArea.TextView.BackgroundRenderers.Clear();
                UnifiedDiffEditor.TextArea.TextView.BackgroundRenderers.Add(new UnifiedDiffBackgroundRenderer(linesToShow));
            }

            DiffNavigationPanel.Initialize(UnifiedDiffEditor, linesToShow);
            SetupNavigationEvents();

            UnifiedDiffEditor.UpdateLayout();
            await RestoreViewPositionAsync(UnifiedDiffEditor, diffIndexToRestore);
        }

        private async Task SwitchToSideBySideView(int? diffIndexToRestore)
        {
            var modelToShow = State.HideUnchangedLines ? FilterDiffModel(_originalDiffModel) : _originalDiffModel;
            var originalModelForNav = State.HideUnchangedLines ? _originalDiffModel : null;

            // Only recreate docs if not cached or if filtering changed content
            bool needRecreate = _cachedOldDoc == null || State.HideUnchangedLines;

            // Reuse cache if just switching modes without filtering
            if (needRecreate && !State.HideUnchangedLines && _cachedOldDoc != null && OldJsonContent.Document == _cachedOldDoc)
            {
                needRecreate = false;
            }

            if (needRecreate)
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
                if (OldJsonContent.Document != _cachedOldDoc) OldJsonContent.Document = _cachedOldDoc;
                if (NewJsonContent.Document != _cachedNewDoc) NewJsonContent.Document = _cachedNewDoc;
            }

            OldJsonContent.UpdateLayout();
            NewJsonContent.UpdateLayout();

            DiffNavigationPanel.Initialize(OldJsonContent, NewJsonContent, modelToShow, originalModelForNav);
            SetupNavigationEvents();

            await RestoreViewPositionAsync(NewJsonContent, diffIndexToRestore);
        }

        private void SetupNavigationEvents()
        {
            DiffNavigationPanel.ScrollRequested -= ScrollToLine;
            DiffNavigationPanel.ScrollRequested += ScrollToLine;
        }

        private async Task RestoreViewPositionAsync(ICSharpCode.AvalonEdit.TextEditor editor, int? diffIndex)
        {
            EventHandler layoutHandler = null;
            layoutHandler = async (s, e) =>
            {
                editor.TextArea.TextView.LayoutUpdated -= layoutHandler;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (diffIndex.HasValue && diffIndex.Value != -1)
                        DiffNavigationPanel?.NavigateToDifferenceByIndex(diffIndex.Value);
                    else
                        FocusFirstDifference();
                    
                    RefreshGuidePosition();
                }, DispatcherPriority.Render);
            };
            editor.TextArea.TextView.LayoutUpdated += layoutHandler;
        }
        #endregion

        #region Event Handlers
        private async void ComparisonInlineMode_Checked(object sender, RoutedEventArgs e)
        {
            if (UnifiedBtn == null || SideBySideBtn == null) return;

            bool switchingToInline = sender == UnifiedBtn;
            var sourceEditor = switchingToInline ? NewJsonContent : UnifiedDiffEditor;

            // Smart persistence: Calculate where we are before switching
            int currentLine = GetCurrentLineRobust(sourceEditor);
            int currentDiffIndex = DiffNavigationPanel?.FindClosestDifferenceIndex(currentLine) ?? 0;

            LogService?.Log($"[JsonDiffControl] Mode Switch. ToInline: {switchingToInline}. Restoring Index: {currentDiffIndex} (Line: {currentLine})");

            await UpdateDiffView(currentDiffIndex);
        }

        private void OldEditor_ScrollChanged(object sender, EventArgs e) => SyncScroll(sender, NewJsonContent);
        private void NewEditor_ScrollChanged(object sender, EventArgs e) => SyncScroll(sender, OldJsonContent);

        private void WordWrapButton_Click(object sender, RoutedEventArgs e)
        {
            OldJsonContent.WordWrap = State.IsWordWrapEnabled;
            NewJsonContent.WordWrap = State.IsWordWrapEnabled;
            UnifiedDiffEditor.WordWrap = State.IsWordWrapEnabled;
        }

        private void NextDiffButton_Click(object sender, RoutedEventArgs e)
        {
            var editor = State.IsInlineMode ? UnifiedDiffEditor : NewJsonContent;
            DiffNavigationPanel?.NavigateToNextDifference(editor.TextArea.Caret.Line);
        }

        private void PreviousDiffButton_Click(object sender, RoutedEventArgs e)
        {
            var editor = State.IsInlineMode ? UnifiedDiffEditor : NewJsonContent;
            DiffNavigationPanel?.NavigateToPreviousDifference(editor.TextArea.Caret.Line);
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
            _cachedOldDoc = null; // Force regeneration
            int currentDiffIndex = DiffNavigationPanel?.FindClosestDifferenceIndex(NewJsonContent.TextArea.Caret.Line) ?? -1;
            await UpdateDiffView(currentDiffIndex);
        }
        #endregion

        #region Helpers
        private int GetCurrentLineRobust(ICSharpCode.AvalonEdit.TextEditor editor)
        {
            if (editor == null) return 1;
            int caretLine = editor.TextArea.Caret.Line;
            double verticalOffset = editor.TextArea.TextView.ScrollOffset.Y;

            if (caretLine == 1 && verticalOffset > 20)
            {
                var visualTop = editor.TextArea.TextView.GetDocumentLineByVisualTop(verticalOffset);
                if (visualTop != null) return visualTop.LineNumber;
            }
            return caretLine;
        }

        private void SyncScroll(object sender, ICSharpCode.AvalonEdit.TextEditor target)
        {
            target.TextArea.TextView.ScrollOffsetChanged -= (State.IsInlineMode ? (EventHandler)null : (target == OldJsonContent ? OldEditor_ScrollChanged : NewEditor_ScrollChanged));
            // Note: Simplification above is tricky due to event signature matching, keeping explicit unsubscription is safer in SyncScroll
            
            // Re-implementing with explicit safety
            if (target == OldJsonContent) target.TextArea.TextView.ScrollOffsetChanged -= OldEditor_ScrollChanged;
            else target.TextArea.TextView.ScrollOffsetChanged -= NewEditor_ScrollChanged;

            try
            {
                var sourceView = (TextView)sender;
                var newV = Math.Min(sourceView.VerticalOffset, target.ExtentHeight - target.ViewportHeight);
                var newH = Math.Min(sourceView.HorizontalOffset, target.ExtentWidth - target.ViewportWidth);
                target.ScrollToVerticalOffset(newV);
                target.ScrollToHorizontalOffset(newH);
            }
            finally
            {
                if (target == OldJsonContent) target.TextArea.TextView.ScrollOffsetChanged += OldEditor_ScrollChanged;
                else target.TextArea.TextView.ScrollOffsetChanged += NewEditor_ScrollChanged;
            }
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
        #endregion
    }
}