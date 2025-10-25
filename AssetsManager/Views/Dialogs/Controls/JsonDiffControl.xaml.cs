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
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class JsonDiffControl : UserControl, IDisposable
    {
        private SideBySideDiffModel _originalDiffModel;
        private bool _isWordLevelDiff = false;
        private bool _hideUnchangedLines = false;
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public event EventHandler<bool> ComparisonFinished;

        public JsonDiffControl()
        {
            InitializeComponent();
            ConfigureEditors();
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
            OldJsonContent.Document = null;
            NewJsonContent.Document = null;

            // Anular referencias a objetos pesados
            _originalDiffModel = null;

            // Anular referencias a otros controles y servicios
            DiffNavigationPanel = null;
            CustomMessageBoxService = null;
        }


        public void FocusFirstDifference()
        {
            DiffNavigationPanel?.NavigateToNextDifference(0);
        }

        public async Task LoadAndDisplayDiffAsync(string oldText, string newText, string oldFileName, string newFileName)
        {
            try
            {
                OldFileNameLabel.Text = oldFileName;
                NewFileNameLabel.Text = newFileName;

                _originalDiffModel = await Task.Run(() => new SideBySideDiffBuilder(new Differ()).BuildDiffModel(oldText, newText, false));

                DiffGrid.Visibility = Visibility.Visible;
                await UpdateDiffView();
            }
            catch (Exception ex)
            {
                CustomMessageBoxService.ShowError("Error", $"Failed to load comparison: {ex.Message}. Check logs for details.");
                OnComparisonFinished(false);
            }
        }

        private void ConfigureEditors()
        {
            var editors = new[] { OldJsonContent, NewJsonContent };
            foreach (var editor in editors)
            {
                editor.Options.EnableHyperlinks = false;
                editor.Options.EnableEmailHyperlinks = false;
                editor.Options.ShowEndOfLine = false;
                editor.Options.ShowSpaces = false;
                editor.Options.ShowTabs = false;
                editor.Options.ConvertTabsToSpaces = true;
                editor.Options.IndentationSize = 2;
                editor.FontFamily = new FontFamily("Consolas, Courier New, monospace");
                editor.FontSize = 13;
                editor.ShowLineNumbers = true;
                editor.WordWrap = false;
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

            var modelToShow = _hideUnchangedLines ? FilterDiffModel(_originalDiffModel) : _originalDiffModel;
            var originalModelForNav = _hideUnchangedLines ? _originalDiffModel : null;

            var (normalizedOld, normalizedNew) = await Task.Run(() =>
            {
                var nOld = JsonFormatter.NormalizeTextForAlignment(modelToShow.OldText);
                var nNew = JsonFormatter.NormalizeTextForAlignment(modelToShow.NewText);
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

        private void NextDiffButton_Click(object sender, RoutedEventArgs e)
        {
            DiffNavigationPanel?.NavigateToNextDifference(NewJsonContent.TextArea.Caret.Line);
        }

        private void PreviousDiffButton_Click(object sender, RoutedEventArgs e)
        {
            DiffNavigationPanel?.NavigateToPreviousDifference(NewJsonContent.TextArea.Caret.Line);
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
                CustomMessageBoxService.ShowError("Error", $"Failed to update view: {ex.Message}");
            }
        }

        protected virtual void OnComparisonFinished(bool success)
        {
            ComparisonFinished?.Invoke(this, success);
        }
    }
}