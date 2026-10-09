using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioInspectorControl : UserControl
    {
        internal StudioControl Owner { get; set; }

        public StudioInspectorControl() => InitializeComponent();

        private void CollapseInspectorSections_Click(object sender, RoutedEventArgs e) => Owner?.CollapseInspectorSections_Click(sender, e);
        private void InspectorRigPreset_SelectionChanged(object sender, SelectionChangedEventArgs e) => Owner?.InspectorRigPreset_SelectionChanged(sender, e);
        private void MapLayerCheckBox_Click(object sender, RoutedEventArgs e) => Owner?.MapLayerCheckBox_Click(sender, e);
        private void ResetOtherMapControllers_Click(object sender, RoutedEventArgs e) => Owner?.ResetOtherMapControllers_Click(sender, e);
        private void MapControllerLabel_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is MapVisibilityControllerOption { CanToggle: true } controller)
                controller.IsVisible = !controller.IsVisible;
        }
        private void MapPostControl_Changed(object sender, RoutedEventArgs e) => Owner?.MapPostControl_Changed(sender, e);
        private void MapPostReset_Click(object sender, RoutedEventArgs e) => Owner?.MapPostReset_Click(sender, e);
        private void MapPostToggle_Click(object sender, RoutedEventArgs e) => Owner?.MapPostToggle_Click(sender, e);
        private void MapSsaoControl_Changed(object sender, RoutedEventArgs e) => Owner?.MapSsaoControl_Changed(sender, e);
        private void MapSunColor_TextChanged(object sender, TextChangedEventArgs e) => Owner?.MapSunColor_TextChanged(sender, e);
        private void MapSunControl_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Owner?.MapSunControl_ValueChanged(sender, e);
        private void MapSunReset_Click(object sender, RoutedEventArgs e) => Owner?.MapSunReset_Click(sender, e);
        private void MapSunToggle_Click(object sender, RoutedEventArgs e) => Owner?.MapSunToggle_Click(sender, e);
        private void RemoveSceneActor_Click(object sender, RoutedEventArgs e) => Owner?.RemoveSceneActor_Click(sender, e);
        private void RerollSeed_Click(object sender, RoutedEventArgs e) => Owner?.RerollSeed_Click(sender, e);
        private void ResetCharacterPlacement_Click(object sender, RoutedEventArgs e) => Owner?.ResetCharacterPlacement_Click(sender, e);
        private void ResetCharacterPosition_Click(object sender, RoutedEventArgs e) => Owner?.ResetCharacterPosition_Click(sender, e);
        private void ResetCharacterRotation_Click(object sender, RoutedEventArgs e) => Owner?.ResetCharacterRotation_Click(sender, e);
        private void ResetCharacterScale_Click(object sender, RoutedEventArgs e) => Owner?.ResetCharacterScale_Click(sender, e);
        private void ResetCharacterSubmeshOverrides_Click(object sender, RoutedEventArgs e) => Owner?.ResetCharacterSubmeshOverrides_Click(sender, e);
        private void RigDistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Owner?.RigDistanceSlider_ValueChanged(sender, e);
        private void RigHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Owner?.RigHeightSlider_ValueChanged(sender, e);
        private void RigPeriodSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Owner?.RigPeriodSlider_ValueChanged(sender, e);
        private void RigRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Owner?.RigRadiusSlider_ValueChanged(sender, e);
        private void RigSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Owner?.RigSpeedSlider_ValueChanged(sender, e);
        private void SceneActorsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Owner?.SceneActorsList_SelectionChanged(sender, e);
        private void SceneActorsList_TargetUpdated(object sender, DataTransferEventArgs e) => Owner?.SceneActorsList_TargetUpdated(sender, e);
    }
}
