using System.Windows;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private void CollapseInspectorSections_Click(object sender, RoutedEventArgs e) =>
            CollapseInspectorSections();

        private void CollapseInspectorSections()
        {
            VfxMotionSection.IsChecked = false;
            StudioSceneActorsSection.IsChecked = false;
            MapBackdropSection.IsChecked = false;
            CharacterTransformSection.IsChecked = false;
            CharacterGameStateSection.IsChecked = false;
            CharacterGeometrySection.IsChecked = false;
            MapSceneSection.IsChecked = false;
            MapLookSection.IsChecked = false;
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
