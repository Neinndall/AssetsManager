using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit.Rendering;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;

namespace AssetsManager.Views.Helpers
{
  public class DiffBackgroundRenderer : IBackgroundRenderer
  {
    private readonly SideBySideDiffModel _diffModel;
    private readonly bool _isWordLevel;
    private readonly bool _isOldEditor;

    private readonly SolidColorBrush _removedBrush;
    private readonly SolidColorBrush _addedBrush;
    private readonly SolidColorBrush _modifiedBrush;

    public DiffBackgroundRenderer(SideBySideDiffModel diffModel, bool isWordLevel, bool isOldEditor)
    {
      _diffModel = diffModel;
      _isWordLevel = isWordLevel;
      _isOldEditor = isOldEditor;

      // Cache brushes for performance
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
      if (textView.VisualLines.Count == 0 || _diffModel == null) return;

      var diffLines = _isOldEditor ? _diffModel.OldText.Lines : _diffModel.NewText.Lines;

      foreach (var line in textView.VisualLines)
      {
        var lineNumber = line.FirstDocumentLine.LineNumber - 1;
        if (lineNumber < 0 || lineNumber >= diffLines.Count) continue;

        var diffLine = diffLines[lineNumber];
        if (diffLine.Type == ChangeType.Unchanged && !_isWordLevel) continue;

        var backgroundBrush = GetBrushForChangeType(diffLine.Type);
        if (backgroundBrush != null)
        {
          var rect = new Rect(0, line.VisualTop - textView.ScrollOffset.Y, textView.ActualWidth, line.Height);
          drawingContext.DrawRectangle(backgroundBrush, null, rect);
        }

        if (_isWordLevel && diffLine.Type != ChangeType.Unchanged)
        {
          var wordHighlightBrush = GetBrushForChangeType(diffLine.Type, useOpacity: true);
          if (wordHighlightBrush == null) continue;

          if (diffLine.SubPieces != null)
          {
            int startOffset = line.FirstDocumentLine.Offset;
            foreach (var piece in diffLine.SubPieces)
            {
              if (string.IsNullOrEmpty(piece.Text)) continue;

              if (piece.Type == ChangeType.Unchanged)
              {
                startOffset += piece.Text.Length;
                continue;
              }

              int endOffset = startOffset + piece.Text.Length;
              var geoBuilder = new BackgroundGeometryBuilder();
              geoBuilder.AddSegment(textView, new TextSegment { StartOffset = startOffset, EndOffset = endOffset });
              var geometry = geoBuilder.CreateGeometry();
              if (geometry != null)
              {
                drawingContext.DrawGeometry(wordHighlightBrush, null, geometry);
              }
              startOffset = endOffset;
            }
          }
        }
      }
    }

    private SolidColorBrush GetBrushForChangeType(ChangeType changeType, bool useOpacity = false)
    {
      SolidColorBrush brush = changeType switch
      {
        ChangeType.Inserted => _addedBrush,
        ChangeType.Deleted => _removedBrush,
        ChangeType.Modified => _modifiedBrush,
        _ => null
      };

      if (brush != null && useOpacity)
      {
        var clonedBrush = brush.Clone();
        clonedBrush.Opacity = 0.85; // Este valor es para la opcion de resaltado (high level word)
        clonedBrush.Freeze();
        return clonedBrush;
      }

      return brush;
    }
  }
}
