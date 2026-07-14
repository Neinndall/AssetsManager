using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit.Document;
using System.Collections.Generic;

namespace AssetsManager.Views.Helpers
{
    public class UnifiedDiffBackgroundRenderer : IBackgroundRenderer
    {
        private readonly IList<DiffPiece> _lines;
        private readonly SolidColorBrush _removedBrush;
        private readonly SolidColorBrush _addedBrush;
        private readonly SolidColorBrush _modifiedBrush;

        public UnifiedDiffBackgroundRenderer(IList<DiffPiece> lines)
        {
            _lines = lines;

            // Cache brushes
            _addedBrush = new SolidColorBrush((Color)Application.Current.FindResource("DiffTextBackgroundAdded"));
            _removedBrush = new SolidColorBrush((Color)Application.Current.FindResource("DiffTextBackgroundRemoved"));
            _modifiedBrush = new SolidColorBrush((Color)Application.Current.FindResource("DiffTextBackgroundModified"));
            _addedBrush.Freeze();
            _removedBrush.Freeze();
            _modifiedBrush.Freeze();
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (textView.VisualLines.Count == 0 || _lines == null) return;

            foreach (var line in textView.VisualLines)
            {
                var lineNumber = line.FirstDocumentLine.LineNumber - 1;
                if (lineNumber < 0 || lineNumber >= _lines.Count) continue;

                var diffLine = _lines[lineNumber];
                if (diffLine.Type == ChangeType.Unchanged) continue;

                var backgroundBrush = GetBrushForChangeType(diffLine.Type);
                if (backgroundBrush != null)
                {
                    var rect = new Rect(0, line.VisualTop - textView.ScrollOffset.Y, textView.ActualWidth, line.Height);
                    drawingContext.DrawRectangle(backgroundBrush, null, rect);
                }
            }
        }

        private SolidColorBrush GetBrushForChangeType(ChangeType changeType)
        {
            return changeType switch
            {
                ChangeType.Inserted => _addedBrush,
                ChangeType.Deleted => _removedBrush,
                ChangeType.Modified => _modifiedBrush,
                _ => null
            };
        }
    }
}
