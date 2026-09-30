using System.Windows;

namespace AssetsManager.Views.Controls.Viewer;

public partial class VfxInspectorControl
{
    private GridLength _expandedTimelineHeight = new(170);

    private void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        ReleaseCurrentProject();
        OpenTkControl?.InvalidateVisual();
    }

    private void ApplyTimelineVisibility()
    {
        if (TimelineDeckRow == null || TimelineSplitterRow == null) return;
        if (_model.TimelineVisible)
        {
            TimelineDeckRow.MinHeight = 170;
            TimelineDeckRow.Height = _expandedTimelineHeight;
            TimelineSplitterRow.Height = new GridLength(6);
        }
        else
        {
            _expandedTimelineHeight = TimelineDeckRow.Height;
            TimelineDeckRow.MinHeight = 0;
            TimelineDeckRow.Height = new GridLength(0);
            TimelineSplitterRow.Height = new GridLength(0);
            TimelineOptionsPopup.IsOpen = false;
        }
    }
}