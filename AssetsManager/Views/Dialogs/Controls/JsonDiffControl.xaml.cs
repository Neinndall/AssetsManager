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
        #endregion

        #region Properties
        public JsonDiffModel ViewModel { get; } = new JsonDiffModel();
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public JsonFormatterService JsonFormatterService { get; set; }
        public JsonDiffWindow ParentWindow { get; set; }
        #endregion

        #region Constructor & Setup
        public JsonDiffControl()
        {
            InitializeComponent();
            this.DataContext = ViewModel;
            
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
                
                // Solo el editor de la derecha (New) actualiza la guía para evitar conflictos dobles
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged += Editor_GuideScrollChanged;
            };
        }

        private void Editor_GuideScrollChanged(object sender, EventArgs e)
        {
            // Esto arregla el scroll arrastrando la guia (evita rebotes visuales)
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

                await UpdateDiffView();
            }
            catch (Exception ex)
            {
                CustomMessageBoxService?.ShowError("Error", $"Failed to load comparison: {ex.Message}", Window.GetWindow(this));
                ParentWindow?.Close();
            }
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

        private void BtnPrevFile_Click(object sender, RoutedEventArgs e)
        {
            ParentWindow?.BtnPrevFile_Click(sender, e);
        }

        private void BtnNextFile_Click(object sender, RoutedEventArgs e)
        {
            ParentWindow?.BtnNextFile_Click(sender, e);
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
            _cachedOldDoc = null; // Force regeneration

            var editor = ViewModel.IsInlineMode ? UnifiedDiffEditor : NewJsonContent;
            double currentPercentage = GetCurrentScrollPercentage(editor);
            
            await UpdateDiffView(currentPercentage, null);
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

            if (caretLine == 1 && verticalOffset > 20)
            {
                var visualTop = editor.TextArea.TextView.GetDocumentLineByVisualTop(verticalOffset);
                if (visualTop != null) return visualTop.LineNumber;
            }
            return caretLine;
        }

        private void SyncScroll(object sender, ICSharpCode.AvalonEdit.TextEditor target)
        {
            if (target == null || target.TextArea?.TextView == null) return;

            // Bloquear eventos de scroll en el destino mientras sincronizamos
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

        public void ScrollToPercentage(double percentage)
        {
            if (ViewModel.IsInlineMode)
            {
                var targetY = (UnifiedDiffEditor.ExtentHeight - UnifiedDiffEditor.ViewportHeight) * percentage;
                UnifiedDiffEditor.ScrollToVerticalOffset(targetY);
                return;
            }

            if (OldJsonContent == null || NewJsonContent == null) return;

            // DESCONEXIÓN FÍSICA de eventos (Método v3.0 ultra-seguro)
            OldJsonContent.TextArea.TextView.ScrollOffsetChanged -= OldEditor_ScrollChanged;
            NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= NewEditor_ScrollChanged;
            NewJsonContent.TextArea.TextView.ScrollOffsetChanged -= Editor_GuideScrollChanged;

            try
            {
                var oldTargetY = (OldJsonContent.ExtentHeight - OldJsonContent.ViewportHeight) * percentage;
                var newTargetY = (NewJsonContent.ExtentHeight - NewJsonContent.ViewportHeight) * percentage;

                OldJsonContent.ScrollToVerticalOffset(oldTargetY);
                NewJsonContent.ScrollToVerticalOffset(newTargetY);
            }
            finally
            {
                // RECONEXIÓN
                OldJsonContent.TextArea.TextView.ScrollOffsetChanged += OldEditor_ScrollChanged;
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged += NewEditor_ScrollChanged;
                NewJsonContent.TextArea.TextView.ScrollOffsetChanged += Editor_GuideScrollChanged;
            }
        }

        public void ScrollToLine(int lineNumber)
        {
            if (ViewModel.IsInlineMode)
            {
                UnifiedDiffEditor.ScrollTo(lineNumber, 0);
                UnifiedDiffEditor.TextArea.Caret.Line = lineNumber;
                UnifiedDiffEditor.Focus();
                return;
            }

            if (OldJsonContent?.TextArea?.TextView == null || NewJsonContent?.TextArea?.TextView == null) return;

            // Desconexión física de eventos de sincronización de scroll
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

                // Layout global del control (como en v3.0)
                this.UpdateLayout();

                NewJsonContent.TextArea.Caret.Line = lineNumber;
                NewJsonContent.TextArea.Caret.Column = 1;
                NewJsonContent.Focus();
            }
            finally
            {
                // Reconexión
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

            OldJsonContent.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(diffModel, ViewModel.IsWordLevelDiff, true));
            NewJsonContent.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(diffModel, ViewModel.IsWordLevelDiff, false));
        }
        #endregion
    }
}
