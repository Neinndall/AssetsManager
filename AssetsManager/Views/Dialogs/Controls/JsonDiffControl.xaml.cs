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
                
                // Ambos editores actualizan la guía (v3.2.1.0 style) para máxima robustez ante asincronismo
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged += Editor_GuideScrollChanged;
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged += Editor_GuideScrollChanged;

                // Breadcrumb events
                OldJsonContent.TextArea.Caret.PositionChanged += Caret_PositionChanged;
                NewJsonContent.TextArea.Caret.PositionChanged += Caret_PositionChanged;
            };

            UnifiedDiffEditor.Loaded += (s, e) =>
            {
                UnifiedDiffEditor.TextArea.Caret.PositionChanged += Caret_PositionChanged;
            };
        }

        private void Caret_PositionChanged(object sender, EventArgs e)
        {
            if (sender is ICSharpCode.AvalonEdit.Editing.Caret caret)
            {
                UpdateJsonPath(caret.Line);
            }
        }

        private void UpdateJsonPath(int line)
        {
            ViewModel.CurrentLine = line;
            var editor = ViewModel.IsInlineMode ? UnifiedDiffEditor : NewJsonContent;
            if (editor.Document == null) return;

            try
            {
                var docLine = editor.Document.GetLineByNumber(line);
                ViewModel.CurrentLineText = editor.Document.GetText(docLine).Trim();
            }
            catch { ViewModel.CurrentLineText = ""; }

            ViewModel.CurrentPath = GetJsonPathAtLine(editor.Document, line);
        }

        private string GetJsonPathAtLine(TextDocument doc, int lineNumber)
        {
            try
            {
                if (doc == null || lineNumber <= 0) return "root";

                var path = new List<string>();
                int currentLineNum = lineNumber;
                
                // Heurística de escaneo ascendente
                // 1. Empezamos buscando si la línea actual ya tiene una clave (ej: "key": "val")
                var startLine = doc.GetLineByNumber(currentLineNum);
                var startText = doc.GetText(startLine);
                int lastIndent = GetIndentLevel(startText);

                // Si la línea actual es una propiedad simple, la añadimos como primer segmento
                string initialKey = ExtractKeyFromLine(startText);
                if (!string.IsNullOrEmpty(initialKey)) path.Insert(0, initialKey);

                while (currentLineNum > 1)
                {
                    currentLineNum--;
                    var line = doc.GetLineByNumber(currentLineNum);
                    var text = doc.GetText(line);
                    int indent = GetIndentLevel(text);
                    var trimmed = text.Trim();

                    // Detectamos apertura de bloques con menor sangría
                    if (indent < lastIndent && (trimmed.EndsWith("{") || trimmed.EndsWith("[")))
                    {
                        string key = ExtractKeyFromLine(trimmed);
                        if (!string.IsNullOrEmpty(key))
                        {
                            path.Insert(0, key);
                        }
                        else if (trimmed.EndsWith("["))
                        {
                            path.Insert(0, "[]");
                        }
                        lastIndent = indent;
                    }
                    if (lastIndent <= 0) break;
                }

                return path.Count > 0 ? string.Join(" > ", path) : "root";
            }
            catch { return "root"; }
        }

        private string ExtractKeyFromLine(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains(":")) return null;
            try
            {
                var parts = text.Split(':');
                if (parts.Length > 0)
                {
                    return parts[0].Trim('"', ' ', '{', '[', ',');
                }
            }
            catch { }
            return null;
        }

        private int GetIndentLevel(string text)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (c == ' ') count++;
                else if (c == '\t') count += 4;
                else break;
            }
            return count;
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
            ParentWindow = null;
            if (DiffNavigationPanel != null)
            {
                DiffNavigationPanel.ParentControl = null;
                DiffNavigationPanel.Cleanup();
            }
            
            // Unsubscribe events safely
            if (OldJsonContent?.TextArea?.TextView != null)
            {
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= OldEditor_ScrollChanged;
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= Editor_GuideScrollChanged;
            }
            if (NewJsonContent?.TextArea?.TextView != null)
            {
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= NewEditor_ScrollChanged;
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= Editor_GuideScrollChanged;
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
                _unifiedModel = null;

                _originalDiffModel = await Task.Run(() => new SideBySideDiffBuilder(new Differ()).BuildDiffModel(oldText, newText, false));

                UpdateMetricsAndMetadata(oldText, newText, oldFileName, newFileName);

                await UpdateDiffView();
            }
            catch (Exception ex)
            {
                CustomMessageBoxService?.ShowError("Error", $"Failed to load comparison: {ex.Message}", Window.GetWindow(this));
                ParentWindow?.Close();
            }
        }

        private void UpdateMetricsAndMetadata(string oldText, string newText, string oldPath, string newPath)
        {
            // 1. Calculate Metrics (One single pass over the model)
            int ins = 0, del = 0, mod = 0;
            
            if (_originalDiffModel?.NewText?.Lines != null)
            {
                foreach (var line in _originalDiffModel.NewText.Lines)
                {
                    switch (line.Type)
                    {
                        case ChangeType.Inserted: ins++; break;
                        case ChangeType.Modified: mod++; break;
                    }
                }
            }

            if (_originalDiffModel?.OldText?.Lines != null)
            {
                foreach (var line in _originalDiffModel.OldText.Lines)
                {
                    if (line.Type == ChangeType.Deleted) del++;
                }
            }

            ViewModel.InsertionsCount = ins;
            ViewModel.DeletionsCount = del;
            ViewModel.ModificationsCount = mod;

            // 2. Metadata Updates
            ViewModel.OldSize = FormatUtils.FormatSize((ulong)(oldText?.Length ?? 0));
            ViewModel.NewSize = FormatUtils.FormatSize((ulong)(newText?.Length ?? 0));
            ViewModel.OldOrigin = InferOrigin(oldPath);
            ViewModel.NewOrigin = InferOrigin(newPath);
        }

        private string InferOrigin(string path)
        {
            if (string.IsNullOrEmpty(path)) return "N/A";
            string lower = path.ToLowerInvariant();
            
            if (lower.Contains("wad_chunks")) return "WAD CHUNK";
            if (lower.Contains("backup"))     return "BACKUP";
            if (lower.Contains("pbe"))        return "PBE CLIENT";
            if (lower.Contains("live"))       return "LIVE CLIENT";
            
            return "LOCAL FILE";
        }

        private void BtnCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(ViewModel.CurrentPath))
            {
                Clipboard.SetText(ViewModel.CurrentPath);
            }
        }

        private void JumpToInsertion_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => DiffNavigationPanel?.NavigateToFirstChangeByType(ChangeType.Inserted);
        private void JumpToDeletion_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => DiffNavigationPanel?.NavigateToFirstChangeByType(ChangeType.Deleted);
        private void JumpToModification_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => DiffNavigationPanel?.NavigateToFirstChangeByType(ChangeType.Modified);

        public async Task LoadAndDisplayBatchDiffAsync(
            List<AssetsManager.Views.Models.Wad.SerializableChunkDiff> items,
            int startIndex,
            string oldPbePath,
            string newPbePath,
            Func<AssetsManager.Views.Models.Wad.SerializableChunkDiff, string, string, Task<(string oldText, string newText)>> loadDataFunc)
        {
            ViewModel.BatchItems = items;
            ViewModel.OldPbePath = oldPbePath;
            ViewModel.NewPbePath = newPbePath;
            ViewModel.LoadDataFunc = loadDataFunc;

            ViewModel.IsBatchMode = true;
            ViewModel.TotalFilesCount = items.Count;
            ViewModel.CurrentFileIndex = startIndex + 1;

            await LoadCurrentBatchItemAsync();
        }

        private async Task LoadCurrentBatchItemAsync()
        {
            if (ViewModel.BatchItems == null || ViewModel.BatchItems.Count == 0 || ViewModel.LoadDataFunc == null) return;

            var currentItem = ViewModel.BatchItems[ViewModel.CurrentFileIndex - 1];

            var (oldText, newText) = await ViewModel.LoadDataFunc(currentItem, ViewModel.OldPbePath, ViewModel.NewPbePath);

            await LoadAndDisplayDiffAsync(oldText, newText, currentItem.OldPath, currentItem.NewPath);
            FocusFirstDifference();
            RefreshGuidePosition();
        }
        #endregion

        #region View Logic
        private async Task UpdateDiffView(double? percentageToRestore = null, int? explicitLine = null)
        {
            if (_originalDiffModel == null) return;

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
                _unifiedModel = await Task.Run(() => new InlineDiffBuilder(new Differ()).BuildDiffModel(_oldText, _newText));
            }

            var linesToShow = ViewModel.HideUnchangedLines
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

            RestoreViewPosition(UnifiedDiffEditor, percentageToRestore, explicitLine);
        }

        private async Task SwitchToSideBySideView(double? percentageToRestore, int? explicitLine)
        {
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

        private void NextFile_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            BtnNextFile_Click(null, null);
        }

        private void PreviousFile_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            BtnPrevFile_Click(null, null);
        }
        #endregion

        #region Event Handlers
        private async void ComparisonInlineMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!this.IsLoaded || UnifiedBtn == null || SideBySideBtn == null) return;

            bool switchingToInline = sender == UnifiedBtn;
            var sourceEditor = switchingToInline ? NewJsonContent : UnifiedDiffEditor;

            // Smart persistence: Save current scroll percentage before switching
            double currentPercentage = GetCurrentScrollPercentage(sourceEditor);

            await UpdateDiffView(currentPercentage);
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
            OldJsonContent.WordWrap = ViewModel.IsWordWrapEnabled;
            NewJsonContent.WordWrap = ViewModel.IsWordWrapEnabled;
            UnifiedDiffEditor.WordWrap = ViewModel.IsWordWrapEnabled;
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
            
            // 1. Si vamos a OCULTAR (acaba de pasar a true), guardamos la línea real
            if (ViewModel.HideUnchangedLines)
            {
                _lastAbsoluteLine = GetCurrentLineRobust(editor);
                _cachedOldDoc = null;
                await UpdateDiffView(GetCurrentScrollPercentage(editor), null);
            }
            else
            {
                // 2. Si vamos a MOSTRAR todo (acaba de pasar a false), restauramos la línea absoluta
                _cachedOldDoc = null;
                await UpdateDiffView(null, _lastAbsoluteLine);
            }
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

            // Identificar el editor de origen para obtener las métricas correctas
            var sourceEditor = target == OldJsonContent ? NewJsonContent : OldJsonContent;

            // FIX: Usar porcentajes para garantizar alineación visual perfecta (v3.2.1.0 style)
            // ANOTACION: Fix "Crazy Jumps" at boundaries using Clamping (0.0-1.0) and Sync Flag.
            double sourceMax = sourceEditor.ExtentHeight - sourceEditor.ViewportHeight;
            double targetMax = target.ExtentHeight - target.ViewportHeight;

            if (sourceMax <= 0) return;
            
            // Clamp percentage between 0 and 1 to prevent "crazy jumps" beyond boundaries
            double percentage = Math.Max(0, Math.Min(sourceView.VerticalOffset / sourceMax, 1.0));
            double targetOffset = targetMax * percentage;

            // Only scroll if there's a meaningful change to avoid jitter
            if (Math.Abs(target.VerticalOffset - targetOffset) < 0.5 && 
                Math.Abs(target.HorizontalOffset - sourceView.HorizontalOffset) < 0.5) 
                return;

            _isSyncing = true;
            try
            {
                target.ScrollToVerticalOffset(targetOffset);
                target.ScrollToHorizontalOffset(sourceView.HorizontalOffset);
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
                UpdateJsonPath(lineNumber);
                return;
            }

            OldJsonContent.ScrollTo(lineNumber, 0);
            NewJsonContent.ScrollTo(lineNumber, 0);

            OldJsonContent.TextArea.Caret.Line = lineNumber;
            NewJsonContent.TextArea.Caret.Line = lineNumber;
            
            UpdateJsonPath(lineNumber);
            NewJsonContent.Focus();
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

            OldJsonContent.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(diffModel, ViewModel.IsWordLevelDiff, true));
            NewJsonContent.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(diffModel, ViewModel.IsWordLevelDiff, false));
        }
        #endregion
    }
}
