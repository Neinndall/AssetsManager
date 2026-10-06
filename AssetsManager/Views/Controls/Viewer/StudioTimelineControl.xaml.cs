using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioTimelineControl : UserControl
    {
        internal StudioControl Owner { get; set; }

        public StudioTimelineControl() => InitializeComponent();

        private void ChancePinButton_Click(object sender, RoutedEventArgs e) => Owner?.ChancePinButton_Click(sender, e);
        private void ChancePinPopup_Closed(object sender, EventArgs e) => Owner?.ChancePinPopup_Closed(sender, e);
        private void ChancePinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Owner?.ChancePinSlider_ValueChanged(sender, e);
        private void ChancePinToggle_Click(object sender, RoutedEventArgs e) => Owner?.ChancePinToggle_Click(sender, e);
        private void ClearEmitterFilter_Click(object sender, RoutedEventArgs e) => Owner?.ClearEmitterFilter_Click(sender, e);
        private void EmitterFilter_TextChanged(object sender, TextChangedEventArgs e) => Owner?.EmitterFilter_TextChanged(sender, e);
        private void EmitterLane_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Owner?.EmitterLane_MouseLeftButtonDown(sender, e);
        private void EmitterMute_Click(object sender, RoutedEventArgs e) => Owner?.EmitterMute_Click(sender, e);
        private void EmitterSolo_Click(object sender, RoutedEventArgs e) => Owner?.EmitterSolo_Click(sender, e);
        private void LoopBoundaryHandle_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Owner?.LoopBoundaryHandle_PreviewMouseDown(sender, e);
        private void LoopBoundaryHandle_PreviewMouseMove(object sender, MouseEventArgs e) => Owner?.LoopBoundaryHandle_PreviewMouseMove(sender, e);
        private void LoopBoundaryHandle_PreviewMouseUp(object sender, MouseButtonEventArgs e) => Owner?.LoopBoundaryHandle_PreviewMouseUp(sender, e);
        private void LoopStartHandle_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Owner?.LoopStartHandle_PreviewMouseDown(sender, e);
        private void LoopStartHandle_PreviewMouseMove(object sender, MouseEventArgs e) => Owner?.LoopStartHandle_PreviewMouseMove(sender, e);
        private void LoopStartHandle_PreviewMouseUp(object sender, MouseButtonEventArgs e) => Owner?.LoopStartHandle_PreviewMouseUp(sender, e);
        private void PlayPauseToggle_Click(object sender, RoutedEventArgs e) => Owner?.PlayPauseToggle_Click(sender, e);
        private void Ruler_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Owner?.Ruler_PreviewMouseDown(sender, e);
        private void Ruler_PreviewMouseMove(object sender, MouseEventArgs e) => Owner?.Ruler_PreviewMouseMove(sender, e);
        private void Ruler_PreviewMouseUp(object sender, MouseButtonEventArgs e) => Owner?.Ruler_PreviewMouseUp(sender, e);
        private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e) => Owner?.Speed_SelectionChanged(sender, e);
        private void StepBack_Click(object sender, RoutedEventArgs e) => Owner?.StepBack_Click(sender, e);
        private void StepForward_Click(object sender, RoutedEventArgs e) => Owner?.StepForward_Click(sender, e);
        private void TimeSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Owner?.TimeSlider_PreviewMouseDown(sender, e);
        private void TimeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e) => Owner?.TimeSlider_PreviewMouseUp(sender, e);
        private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Owner?.TimeSlider_ValueChanged(sender, e);
        private void TimelineGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Owner?.TimelineGrid_PreviewMouseDown(sender, e);
        private void TimelineGrid_PreviewMouseMove(object sender, MouseEventArgs e) => Owner?.TimelineGrid_PreviewMouseMove(sender, e);
        private void TimelineGrid_PreviewMouseUp(object sender, MouseButtonEventArgs e) => Owner?.TimelineGrid_PreviewMouseUp(sender, e);
        private void TimelineLoopToggleButton_Click(object sender, RoutedEventArgs e) => Owner?.TimelineLoopToggleButton_Click(sender, e);
        private void TimelineOptionsButton_Click(object sender, RoutedEventArgs e) => Owner?.TimelineOptionsButton_Click(sender, e);
        private void TimelineOptionsPopup_Closed(object sender, EventArgs e) => Owner?.TimelineOptionsPopup_Closed(sender, e);
        private void ToggleMuteAll_Click(object sender, RoutedEventArgs e) => Owner?.ToggleMuteAll_Click(sender, e);
        private void ToggleSoloAll_Click(object sender, RoutedEventArgs e) => Owner?.ToggleSoloAll_Click(sender, e);
        private void TracksCanvasContainer_SizeChanged(object sender, SizeChangedEventArgs e) => Owner?.TracksCanvasContainer_SizeChanged(sender, e);
    }
}
