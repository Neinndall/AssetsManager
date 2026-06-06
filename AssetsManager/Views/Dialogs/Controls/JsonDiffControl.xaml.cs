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
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Dialogs.Controls;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class JsonDiffControl : UserControl, IDisposable
    {
        #region Fields
        private static readonly IDiffer _differ = new Differ();
        private SideBySideDiffModel _originalDiffModel;
        private DiffPaneModel _unifiedModel;
        private string _oldText;
        private string _newText;

        // Cache to prevent flickering
        private TextDocument _cachedOldDoc;
        private TextDocument _cachedNewDoc;
        private bool _isSyncing;
        private int _lastAbsoluteLine = 1;
        #endregion

        #region Properties
        private readonly JsonDiffModel _viewModel;
        public JsonDiffModel ViewModel => _viewModel;
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public JsonFormatterService JsonFormatterService { get; set; }
        public JsonDiffWindow ParentWindow { get; set; }
        #endregion

        #region Constructor & Setup
        public JsonDiffControl()
        {
            InitializeComponent();
            _viewModel = new JsonDiffModel();
            this.DataContext = _viewModel;

            // Peer injection
            DiffNavigationPanel.ParentControl = this;

            LoadJsonSyntaxHighlighting();
            
            this.Loaded += JsonDiffControl_Loaded;
            this.Unloaded += JsonDiffControl_Unloaded;
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

        private void JsonDiffControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Defensive detach to ensure no duplicate event handlers are registered
            DetachScrollSyncEvents();

            if (OldJsonContent?.TextArea?.TextView != null)
            {
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged += OldEditor_ScrollChanged;
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged += Editor_GuideScrollChanged;
                OldJsonContent.TextArea.Caret.PositionChanged += Caret_PositionChanged;
            }
            if (NewJsonContent?.TextArea?.TextView != null)
            {
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged += NewEditor_ScrollChanged;
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged += Editor_GuideScrollChanged;
                NewJsonContent.TextArea.Caret.PositionChanged += Caret_PositionChanged;
            }
            if (UnifiedDiffEditor?.TextArea != null)
            {
                UnifiedDiffEditor.TextArea.Caret.PositionChanged += Caret_PositionChanged;
            }
        }

        private void JsonDiffControl_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachScrollSyncEvents();
        }

        private void DetachScrollSyncEvents()
        {
            if (OldJsonContent?.TextArea?.TextView != null)
            {
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= OldEditor_ScrollChanged;
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= Editor_GuideScrollChanged;
            }
            if (OldJsonContent?.TextArea?.Caret != null)
            {
                OldJsonContent.TextArea.Caret.PositionChanged -= Caret_PositionChanged;
            }

            if (NewJsonContent?.TextArea?.TextView != null)
            {
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= NewEditor_ScrollChanged;
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= Editor_GuideScrollChanged;
            }
            if (NewJsonContent?.TextArea?.Caret != null)
            {
                NewJsonContent.TextArea.Caret.PositionChanged -= Caret_PositionChanged;
            }

            if (UnifiedDiffEditor?.TextArea?.Caret != null)
            {
                UnifiedDiffEditor.TextArea.Caret.PositionChanged -= Caret_PositionChanged;
            }
        }

        private void Caret_PositionChanged(object sender, EventArgs e)
        {
            if (sender is ICSharpCode.AvalonEdit.Editing.Caret caret)
            {
                UpdateLineNumber(caret.Line);
            }
        }

        private void UpdateLineNumber(int line)
        {
            ViewModel.CurrentLine = line;
        }

        private void Editor_GuideScrollChanged(object sender, EventArgs e)
        {
            // Esto asegura que la guía se mueva en tiempo real durante el scroll manual o arrastre
            RefreshGuidePosition();
        }
        #endregion

        #region Public Methods
        public void Dispose()
        {
            this.Loaded -= JsonDiffControl_Loaded;
            this.Unloaded -= JsonDiffControl_Unloaded;

            DetachScrollSyncEvents();

            ParentWindow = null;
            if (DiffNavigationPanel != null)
            {
                DiffNavigationPanel.ParentControl = null;
                DiffNavigationPanel.Cleanup();
            }

            // Clear heavy resources
            OldJsonContent?.TextArea?.TextView?.BackgroundRenderers.Clear();
            NewJsonContent?.TextArea?.TextView?.BackgroundRenderers.Clear();
            UnifiedDiffEditor?.TextArea?.TextView?.BackgroundRenderers.Clear();

            // Set documents to null at the very end to avoid event triggers during cleanup
            if (OldJsonContent != null) OldJsonContent.Document = null;
            if (NewJsonContent != null) NewJsonContent.Document = null;
            if (UnifiedDiffEditor != null) UnifiedDiffEditor.Document = null;

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
                _originalDiffModel = null;
                _unifiedModel = null;

                // Detection: Is this the first time the asset is being tracked?
                ViewModel.IsInitialComparison = string.IsNullOrEmpty(oldText) && !string.IsNullOrEmpty(newText);

                // Build metrics using the most efficient way (Raw blocks)
                ViewModel.UpdateMetrics(oldText, newText);
                
                // [PROGRESS] Only report granular updates if NOT in batch mode to avoid bar jumps
                bool reportProgress = !ViewModel.IsBatchMode && ParentWindow != null && ParentWindow.LoadingWindow != null;

                if (reportProgress)
                {
                    ParentWindow.LoadingWindow.SetState(DiffLoadingState.CalculatingDifferences);
                }

                await Task.Run(() => UpdateChangeCounts());

                if (reportProgress)
                {
                    ParentWindow.LoadingWindow.SetState(DiffLoadingState.RenderingUI);
                }

                // IMPORTANT: We do the UI update, but the Window is still Hidden
                await UpdateDiffView();
            }
            catch (Exception ex)
            {
                CustomMessageBoxService?.ShowError("Error", $"Failed to load comparison: {ex.Message}", Window.GetWindow(this));
                ParentWindow?.Close();
            }
        }

        private void UpdateChangeCounts()
        {
            if (ViewModel.IsInitialComparison)
            {
                ViewModel.InsertionsCount = 0;
                ViewModel.DeletionsCount = 0;
                ViewModel.ModificationsCount = 0;
                return;
            }

            // FAST TRACK: Use raw diff blocks to count changes (O(Blocks) instead of O(Lines))
            // DiffPlex 1.9.0 handles hashing internally for performance.
            var diffResult = _differ.CreateDiffs(_oldText, _newText, false, false, new DiffPlex.Chunkers.LineChunker());
            
            int ins = 0, del = 0, mod = 0;
            foreach (var block in diffResult.DiffBlocks)
            {
                if (block.DeleteCountA > 0 && block.InsertCountB > 0) mod += Math.Max(block.DeleteCountA, block.InsertCountB);
                else if (block.InsertCountB > 0) ins += block.InsertCountB;
                else if (block.DeleteCountA > 0) del += block.DeleteCountA;
            }

            ViewModel.InsertionsCount = ins;
            ViewModel.DeletionsCount = del;
            ViewModel.ModificationsCount = mod;
        }

        private void JumpToInsertion_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => DiffNavigationPanel?.NavigateToFirstChangeByType(ChangeType.Inserted);
        private void JumpToDeletion_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => DiffNavigationPanel?.NavigateToFirstChangeByType(ChangeType.Deleted);
        private void JumpToModification_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => DiffNavigationPanel?.NavigateToFirstChangeByType(ChangeType.Modified);

        public async Task LoadAndDisplayPreloadedBatchAsync(List<(string oldText, string newText, string oldPath, string newPath)> items, int startIndex)
        {
            ViewModel.PreloadedData = items;

            ViewModel.IsBatchMode = true;
            ViewModel.TotalFilesCount = items.Count;
            ViewModel.CurrentFileIndex = startIndex + 1;

            await LoadCurrentBatchItemAsync();
        }

        private async Task LoadCurrentBatchItemAsync()
        {
            if (ViewModel.PreloadedData == null || ViewModel.PreloadedData.Count == 0) return;

            var currentItem = ViewModel.PreloadedData[ViewModel.CurrentFileIndex - 1];

            await LoadAndDisplayDiffAsync(currentItem.oldText, currentItem.newText, currentItem.oldPath, currentItem.newPath);
            FocusFirstDifference();
            RefreshGuidePosition();
        }
        #endregion

        #region View Logic
        private async Task UpdateDiffView(double? percentageToRestore = null, int? explicitLine = null)
        {
            try
            {
                if (ViewModel.IsInlineMode)
                {
                    await SwitchToInlineView(percentageToRestore, explicitLine);
                }
                else
                {
                    await SwitchToSideBySideView(percentageToRestore, explicitLine);
                }
            }
            catch
            {
                throw;
            }
        }

        private async Task SwitchToInlineView(double? percentageToRestore, int? explicitLine)
        {
            if (_unifiedModel == null)
            {
                _unifiedModel = await Task.Run(() => new InlineDiffBuilder(_differ).BuildDiffModel(_oldText, _newText));
            }

            var linesToShow = _unifiedModel.Lines;

            if (ViewModel.HideUnchangedLines)
            {
                linesToShow = _unifiedModel.Lines.Where(l => 
                {
                    if (l.Type == ChangeType.Unchanged) return false;
                    if (l.Type == ChangeType.Inserted && !ViewModel.ShowInsertions) return false;
                    if (l.Type == ChangeType.Deleted && !ViewModel.ShowDeletions) return false;
                    if (l.Type == ChangeType.Modified && !ViewModel.ShowModifications) return false;
                    return true;
                }).ToList();
            }

            string combinedText = string.Join(Environment.NewLine, linesToShow.Select(l => l.Text));

            if (UnifiedDiffEditor.Document == null || UnifiedDiffEditor.Document.TextLength != combinedText.Length || UnifiedDiffEditor.Text != combinedText)
            {
                UnifiedDiffEditor.Document = new TextDocument(combinedText);
                UnifiedDiffEditor.TextArea.TextView.BackgroundRenderers.Clear();
                
                if (!ViewModel.IsInitialComparison)
                {
                    UnifiedDiffEditor.TextArea.TextView.BackgroundRenderers.Add(new UnifiedDiffBackgroundRenderer(linesToShow));
                }
            }

            DiffNavigationPanel.Initialize(UnifiedDiffEditor, linesToShow);

            RestoreViewPosition(UnifiedDiffEditor, percentageToRestore, explicitLine);
        }

        private async Task SwitchToSideBySideView(double? percentageToRestore, int? explicitLine)
        {
            if (_originalDiffModel == null)
            {
                _originalDiffModel = await Task.Run(() => new SideBySideDiffBuilder(_differ).BuildDiffModel(_oldText, _newText, false));
            }

            var modelToShow = ViewModel.HideUnchangedLines ? FilterDiffModel(_originalDiffModel) : _originalDiffModel;
            var originalModelForNav = ViewModel.HideUnchangedLines ? _originalDiffModel : null;

            // Only recreate docs if not cached or if filtering changed content
            bool needRecreate = _cachedOldDoc == null || ViewModel.HideUnchangedLines;

            // Reuse cache if just switching modes without filtering
            if (needRecreate && !ViewModel.HideUnchangedLines && _cachedOldDoc != null && OldJsonContent.Document == _cachedOldDoc)
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

            DiffNavigationPanel.Initialize(OldJsonContent, NewJsonContent, modelToShow, originalModelForNav);

            RestoreViewPosition(NewJsonContent, percentageToRestore, explicitLine);
        }

        private void RestoreViewPosition(ICSharpCode.AvalonEdit.TextEditor editor, double? percentage, int? explicitLine)
        {
            // Force immediate layout update to allow synchronous scrolling
            editor.UpdateLayout();

            if (explicitLine.HasValue && explicitLine.Value > 0)
            {
                ScrollToLine(explicitLine.Value);
            }
            else if (percentage.HasValue)
            {
                ScrollToPercentage(percentage.Value);
            }
            else
            {
                FocusFirstDifference();
            }

            RefreshGuidePosition();
        }
        #endregion

        #region Shortcut Commands Handlers
        private void NextDifference_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            NextDiffButton_Click(null, null);
        }

        private void PreviousDifference_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            PreviousDiffButton_Click(null, null);
        }

        private void PreviousFile_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            BtnPrevFile_Click(null, null);
        }

        private void NextFile_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            BtnNextFile_Click(null, null);
        }
        #endregion

        #region Event Handlers
        private async void ComparisonInlineMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!this.IsLoaded || UnifiedBtn == null || SideBySideBtn == null) return;

            bool switchingToInline = sender == UnifiedBtn;
            var sourceEditor = switchingToInline ? NewJsonContent : UnifiedDiffEditor;

            // Smart persistence: Save current scroll percentage before switching
            int currentLine = GetCurrentLineRobust(sourceEditor);
            double currentPercentage = GetCurrentScrollPercentage(sourceEditor);

            await UpdateDiffView(currentPercentage, currentLine);
        }

        private void OldEditor_ScrollChanged(object sender, EventArgs e)
        {
            SyncScroll(sender, NewJsonContent);
        }

        private void NewEditor_ScrollChanged(object sender, EventArgs e)
        {
            SyncScroll(sender, OldJsonContent);
        }

        private void WordWrapButton_Click(object sender, RoutedEventArgs e)
        {
            var editor = ViewModel.IsInlineMode ? UnifiedDiffEditor : NewJsonContent;
            int currentLine = GetCurrentLineRobust(editor);

            OldJsonContent.WordWrap = ViewModel.IsWordWrapEnabled;
            NewJsonContent.WordWrap = ViewModel.IsWordWrapEnabled;
            UnifiedDiffEditor.WordWrap = ViewModel.IsWordWrapEnabled;

            ForceLayoutUpdate();
            ScrollToLine(currentLine);
        }

        private void ForceLayoutUpdate()
        {
            if (ViewModel.IsInlineMode)
            {
                UnifiedDiffEditor.UpdateLayout();
            }
            else
            {
                OldJsonContent.UpdateLayout();
                NewJsonContent.UpdateLayout();
            }
        }

        private void NextDiffButton_Click(object sender, RoutedEventArgs e)
        {
            var editor = ViewModel.IsInlineMode ? UnifiedDiffEditor : NewJsonContent;
            DiffNavigationPanel?.NavigateToNextDifference(GetCurrentLineRobust(editor));
        }

        private void PreviousDiffButton_Click(object sender, RoutedEventArgs e)
        {
            var editor = ViewModel.IsInlineMode ? UnifiedDiffEditor : NewJsonContent;
            DiffNavigationPanel?.NavigateToPreviousDifference(GetCurrentLineRobust(editor));
        }

        public async void BtnPrevFile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CurrentFileIndex > 1)
            {
                ViewModel.CurrentFileIndex--;
                await LoadCurrentBatchItemAsync();
            }
        }

        public async void BtnNextFile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CurrentFileIndex < ViewModel.TotalFilesCount)
            {
                ViewModel.CurrentFileIndex++;
                await LoadCurrentBatchItemAsync();
            }
        }

        private void WordLevelDiffButton_Click(object sender, RoutedEventArgs e)
        {
            var modelToShow = ViewModel.HideUnchangedLines ? FilterDiffModel(_originalDiffModel) : _originalDiffModel;
            ApplyDiffHighlighting(modelToShow);
            OldJsonContent.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            NewJsonContent.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }

        private async void HideUnchangedButton_Click(object sender, RoutedEventArgs e)
        {
            var editor = ViewModel.IsInlineMode ? UnifiedDiffEditor : NewJsonContent;
            
            if (ViewModel.HideUnchangedLines)
            {
                // 1. Si vamos a OCULTAR, guardamos la línea real y vamos al inicio de resultados
                _lastAbsoluteLine = GetCurrentLineRobust(editor);
                _cachedOldDoc = null;
                await UpdateDiffView(null, null);
            }
            else
            {
                // 2. Si vamos a MOSTRAR todo, restauramos la línea absoluta guardada
                _cachedOldDoc = null;
                await UpdateDiffView(null, _lastAbsoluteLine);
            }
        }

        private async void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            var editor = ViewModel.IsInlineMode ? UnifiedDiffEditor : NewJsonContent;

            if (!ViewModel.HideUnchangedLines)
            {
                // Capturar posición antes de activar el filtrado automático
                _lastAbsoluteLine = GetCurrentLineRobust(editor);
                ViewModel.HideUnchangedLines = true;
            }

            // Al cambiar filtros siempre vamos al inicio de los resultados para inspección fresca
            _cachedOldDoc = null;
            await UpdateDiffView(null, null);
        }

        private void FilterInsertion_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ApplySoloFilter(true, false, false);
        private void FilterDeletion_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ApplySoloFilter(false, true, false);
        private void FilterModification_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ApplySoloFilter(false, false, true);

        private async void ApplySoloFilter(bool ins, bool del, bool mod)
        {
            var editor = ViewModel.IsInlineMode ? UnifiedDiffEditor : NewJsonContent;

            if (!ViewModel.HideUnchangedLines)
            {
                _lastAbsoluteLine = GetCurrentLineRobust(editor);
                ViewModel.HideUnchangedLines = true;
            }

            ViewModel.ShowInsertions = ins;
            ViewModel.ShowDeletions = del;
            ViewModel.ShowModifications = mod;

            _cachedOldDoc = null;
            await UpdateDiffView(null, null);
        }
        #endregion

        #region Helpers
        private double GetCurrentScrollPercentage(ICSharpCode.AvalonEdit.TextEditor editor)
        {
            if (editor == null || editor.ExtentHeight <= editor.ViewportHeight) return 0;
            return editor.VerticalOffset / (editor.ExtentHeight - editor.ViewportHeight);
        }

        private int GetCurrentLineRobust(ICSharpCode.AvalonEdit.TextEditor editor)
        {
            if (editor == null) return 1;

            int caretLine = editor.TextArea.Caret.Line;
            double verticalOffset = editor.TextArea.TextView.ScrollOffset.Y;

            // Determine the line at the top of the viewport
            var visualTop = editor.TextArea.TextView.GetDocumentLineByVisualTop(verticalOffset);
            int topVisibleLine = visualTop?.LineNumber ?? 1;

            // Check if caret is visible in the current viewport
            var textView = editor.TextArea.TextView;
            bool isCaretVisible = false;
            if (textView.VisualLines.Any())
            {
                int firstLine = textView.VisualLines.First().FirstDocumentLine.LineNumber;
                int lastLine = textView.VisualLines.Last().LastDocumentLine.LineNumber;
                isCaretVisible = caretLine >= firstLine && caretLine <= lastLine;
            }

            // If the caret is at the default starting position (1) or not visible in current viewport,
            // prioritize the top visible line as the reference for navigation.
            if (caretLine == 1 || !isCaretVisible)
            {
                if (topVisibleLine > 0) return topVisibleLine;
            }

            return caretLine;
        }

        private void SyncScroll(object sender, ICSharpCode.AvalonEdit.TextEditor target)
        {
            if (_isSyncing || target == null || target.TextArea?.TextView == null || !(sender is TextView sourceView)) return;

            _isSyncing = true;
            try
            {
                // FIX: Sincronización por Línea Documental con Blindaje de Límites (v3.3.0.0)
                // Esta lógica mantiene la alineación incluso con Word Wrap pero respeta los límites del documento (Anti-Crazy Jumps).
                
                var visualTop = sourceView.GetDocumentLineByVisualTop(sourceView.VerticalOffset);
                if (visualTop != null)
                {
                    int line = visualTop.LineNumber;
                    double sourceLineTop = sourceView.GetVisualTopByDocumentLine(line);
                    double relativeOffset = sourceView.VerticalOffset - sourceLineTop;

                    double targetLineTop = target.TextArea.TextView.GetVisualTopByDocumentLine(line);
                    double targetOffset = targetLineTop + relativeOffset;

                    // CLAMPING: Evitar saltos fuera de los límites del target (Fix Crazy Jumps)
                    double targetMax = target.ExtentHeight - target.ViewportHeight;
                    targetOffset = Math.Max(0, Math.Min(targetOffset, targetMax));

                    if (Math.Abs(target.VerticalOffset - targetOffset) > 0.5)
                    {
                        target.ScrollToVerticalOffset(targetOffset);
                    }
                }

                // Sincronización horizontal (estándar)
                if (Math.Abs(target.HorizontalOffset - sourceView.HorizontalOffset) > 0.5)
                {
                    target.ScrollToHorizontalOffset(sourceView.HorizontalOffset);
                }
            }
            catch
            {
                // Fallback ultra-seguro por porcentajes si falla el cálculo por línea
                var sourceEditor = target == OldJsonContent ? NewJsonContent : OldJsonContent;
                double sourceMax = sourceEditor.ExtentHeight - sourceEditor.ViewportHeight;
                double targetMax = target.ExtentHeight - target.ViewportHeight;
                if (sourceMax > 0)
                {
                    double percentage = Math.Max(0, Math.Min(sourceView.VerticalOffset / sourceMax, 1.0));
                    target.ScrollToVerticalOffset(targetMax * percentage);
                }
            }
            finally
            {
                _isSyncing = false;
            }
        }

        public void ScrollToPercentage(double percentage)
        {
            if (ViewModel.IsInlineMode)
            {
                UnifiedDiffEditor.ScrollToVerticalOffset((UnifiedDiffEditor.ExtentHeight - UnifiedDiffEditor.ViewportHeight) * percentage);
                return;
            }

            var oldTarget = (OldJsonContent.ExtentHeight - OldJsonContent.ViewportHeight) * percentage;
            var newTarget = (NewJsonContent.ExtentHeight - NewJsonContent.ViewportHeight) * percentage;
            
            OldJsonContent.ScrollToVerticalOffset(oldTarget);
            NewJsonContent.ScrollToVerticalOffset(newTarget);
        }

        public void ScrollToLine(int lineNumber)
        {
            if (ViewModel.IsInlineMode)
            {
                UnifiedDiffEditor.ScrollTo(lineNumber, 0);
                UnifiedDiffEditor.TextArea.Caret.Line = lineNumber;
                UpdateLineNumber(lineNumber);
                UnifiedDiffEditor.Focus();
                return;
            }

            OldJsonContent.ScrollTo(lineNumber, 0);
            NewJsonContent.ScrollTo(lineNumber, 0);

            OldJsonContent.TextArea.Caret.Line = lineNumber;
            NewJsonContent.TextArea.Caret.Line = lineNumber;
            
            UpdateLineNumber(lineNumber);
            NewJsonContent.Focus();
        }

        private SideBySideDiffModel FilterDiffModel(SideBySideDiffModel originalModel)
        {
            var filteredModel = new SideBySideDiffModel();
            for (int i = 0; i < originalModel.OldText.Lines.Count; i++)
            {
                var oldLine = originalModel.OldText.Lines[i];
                var newLine = originalModel.NewText.Lines[i];

                bool isChanged = oldLine.Type != ChangeType.Unchanged || newLine.Type != ChangeType.Unchanged;
                if (isChanged)
                {
                    bool shouldShow = false;
                    
                    // Logic: If a line is a deletion, it only exists in OldText (usually)
                    // If it's an insertion, it only exists in NewText.
                    // If it's a modification, both have changed.
                    
                    if (oldLine.Type == ChangeType.Deleted || newLine.Type == ChangeType.Deleted)
                    {
                        if (ViewModel.ShowDeletions) shouldShow = true;
                    }
                    else if (oldLine.Type == ChangeType.Inserted || newLine.Type == ChangeType.Inserted)
                    {
                        if (ViewModel.ShowInsertions) shouldShow = true;
                    }
                    else if (oldLine.Type == ChangeType.Modified || newLine.Type == ChangeType.Modified)
                    {
                        if (ViewModel.ShowModifications) shouldShow = true;
                    }

                    if (shouldShow)
                    {
                        filteredModel.OldText.Lines.Add(oldLine);
                        filteredModel.NewText.Lines.Add(newLine);
                    }
                }
            }
            return filteredModel;
        }

        private void ApplyDiffHighlighting(SideBySideDiffModel diffModel)
        {
            OldJsonContent.TextArea.TextView.BackgroundRenderers.Clear();
            NewJsonContent.TextArea.TextView.BackgroundRenderers.Clear();

            // Always add to Old (though it will be empty and covered by overlay if initial)
            OldJsonContent.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(diffModel, ViewModel.IsWordLevelDiff, true));
            
            // Only add to New if NOT an initial comparison to avoid the "all green" effect
            if (!ViewModel.IsInitialComparison)
            {
                NewJsonContent.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(diffModel, ViewModel.IsWordLevelDiff, false));
            }
        }
        #endregion
    }
}
