using System.Windows;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        internal void CollapseInspectorSections_Click(object sender, RoutedEventArgs e) =>
            CollapseInspectorSections();

        private void CollapseInspectorSections()
        {
            StudioInspectorView.VfxMotionSection.IsChecked = false;
            StudioInspectorView.StudioSceneActorsSection.IsChecked = false;
            StudioInspectorView.StudioSynchronizationSection.IsChecked = false;
            StudioInspectorView.MapBackdropSection.IsChecked = false;
            StudioInspectorView.CharacterTransformSection.IsChecked = false;
            StudioInspectorView.CharacterGameStateSection.IsChecked = false;
            StudioInspectorView.CharacterGeometrySection.IsChecked = false;
            StudioInspectorView.MapSceneSection.IsChecked = false;
            StudioInspectorView.MapLookSection.IsChecked = false;
        }

        private void InspectorSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            if (CenterContentCol != null)
            {
                CenterContentCol.Width = new GridLength(1, GridUnitType.Star);
            }
            if (InspectorCol != null && InspectorCol.ActualWidth >= 180)
            {
                _savedInspectorWidth = new GridLength(InspectorCol.ActualWidth, GridUnitType.Pixel);
                InspectorCol.Width = _savedInspectorWidth;
            }
        }

        private void UpdateInspectorColumnVisibility()
        {
            if (InspectorCol == null || InspectorSplitterCol == null || CenterContentCol == null) return;

            if (_model.IsInspectorPanelVisible)
            {
                double targetWidth = _savedInspectorWidth.Value >= 180 ? _savedInspectorWidth.Value : 330;
                InspectorCol.Width = new GridLength(targetWidth, GridUnitType.Pixel);
                InspectorSplitterCol.Width = GridLength.Auto;
                CenterContentCol.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                if (InspectorCol.ActualWidth >= 180)
                {
                    _savedInspectorWidth = new GridLength(InspectorCol.ActualWidth, GridUnitType.Pixel);
                }
                else if (InspectorCol.Width.GridUnitType == GridUnitType.Pixel && InspectorCol.Width.Value >= 180)
                {
                    _savedInspectorWidth = InspectorCol.Width;
                }

                InspectorCol.Width = new GridLength(0, GridUnitType.Pixel);
                InspectorSplitterCol.Width = new GridLength(0, GridUnitType.Pixel);
                CenterContentCol.Width = new GridLength(1, GridUnitType.Star);
            }
        }
    }
}
