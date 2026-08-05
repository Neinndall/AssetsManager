using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using Microsoft.Win32;

namespace AssetsManager.Views.Dialogs
{
    public partial class ImageDiffWindow : HudWindow
    {
        public LoadingDiffWindow LoadingWindow { get; set; }
        private readonly LogService _logService;

        // Batch Mode Properties
        public static readonly DependencyProperty IsBatchModeProperty =
            DependencyProperty.Register("IsBatchMode", typeof(bool), typeof(ImageDiffWindow), new PropertyMetadata(false));

        public static readonly DependencyProperty CurrentFileIndexProperty =
            DependencyProperty.Register("CurrentFileIndex", typeof(int), typeof(ImageDiffWindow), new PropertyMetadata(1));

        public static readonly DependencyProperty TotalFilesCountProperty =
            DependencyProperty.Register("TotalFilesCount", typeof(int), typeof(ImageDiffWindow), new PropertyMetadata(1));

        public bool IsBatchMode
        {
            get => (bool)GetValue(IsBatchModeProperty);
            set => SetValue(IsBatchModeProperty, value);
        }

        public int CurrentFileIndex
        {
            get => (int)GetValue(CurrentFileIndexProperty);
            set => SetValue(CurrentFileIndexProperty, value);
        }

        public int TotalFilesCount
        {
            get => (int)GetValue(TotalFilesCountProperty);
            set => SetValue(TotalFilesCountProperty, value);
        }

        private List<(BitmapSource oldImage, BitmapSource newImage, string oldPath, string newPath)> _preloadedImages;

        private bool _isInitialized = false;
        private Point _lastMousePosition;
        private bool _isDragging = false;
        private double _currentZoom = 1.0;

        // Timeline Frame Sequence State
        private readonly DispatcherTimer _timelineTimer;
        private bool _timelineModeActive = false;
        private bool _isTimelinePlaying = false;
        private bool _timelineLoop = false;
        private bool _isApplyingTimelineFrame = false;
        private DateTime _timelineStartTime;
        private double _timelineDuration = 2.0;
        private bool _timelineRoundTrip = false;
        private int _timelineCycles = 1;

        // DURATION is the hold time per state; transitions are folded into the state
        // boundaries, so the total stays an exact multiple of the hold time
        private double TotalTimelineDuration =>
            ImageExportUtils.TimelineTotalDuration(_timelineDuration, _timelineRoundTrip, _timelineCycles);

        public ImageDiffWindow(LogService logService = null) : this(null, null, null, null, logService)
        {
        }

        public ImageDiffWindow(BitmapSource oldImage, BitmapSource newImage, string oldFileName, string newFileName, LogService logService = null)
        {
            InitializeComponent();
            _logService = logService;

            // Timeline Frame Sequence Timer (Render priority for fluid frame updates)
            _timelineTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timelineTimer.Tick += TimelineTimer_Tick;

            // Smooth Handover: Handled via Loaded event
            Loaded += ImageDiffWindow_Loaded;

            SetImageData(oldImage, newImage, oldFileName, newFileName);

            this.Closed += OnWindowClosed;
            this.SizeChanged += (s, e) => UpdateSliderEffect();
            this.PreviewKeyDown += ImageDiffWindow_PreviewKeyDown;

            // Register Mouse Events for Zoom & Pan on MainContentArea only
            MainContentArea.MouseWheel += ImageDiffWindow_MouseWheel;
            MainContentArea.MouseDown += ImageDiffWindow_MouseDown;
            MainContentArea.MouseMove += ImageDiffWindow_MouseMove;
            MainContentArea.MouseUp += ImageDiffWindow_MouseUp;

            // Sync Slider value
            OverlaySlider.Value = 50;
            _isInitialized = true;

            // Force Side-by-Side as default
            SideBySideBtn.IsChecked = true;
            UpdateUIMode();
        }

        private async void ImageDiffWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ImageDiffWindow_Loaded;

            // 1. IMPORTANT: Wait for the UI to process the initial rendering (especially for high-res textures)
            // Using Render priority for images as they are faster to draw than heavy text
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            // 2. Smooth Handover: Take focus first, then close loader
            this.Activate();
            this.Focus();

            if (LoadingWindow != null)
            {
                LoadingWindow.Close();
                LoadingWindow = null;
            }
        }

        private void ImageDiffWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Alt)
            {
                if (e.Key == Key.Right)
                {
                    BtnNextFile_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    BtnPrevFile_Click(null, null);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Space && _timelineModeActive && TimelineBtn?.IsChecked == true && !(Keyboard.FocusedElement is TextBox))
            {
                TimelinePlayPauseBtn_Click(null, null);
                e.Handled = true;
            }
        }

        private void SetImageData(BitmapSource oldImage, BitmapSource newImage, string oldFileName, string newFileName)
        {
            // Stop and reset the timeline before switching assets
            PauseTimeline();
            if (_timelineModeActive)
            {
                TimelineSlider.Value = 0;
                ApplyTimelineFrame(0);
            }

            // Set data for both modes
            OldImage.Source = oldImage;
            NewImage.Source = newImage;
            OldImageOverlay.Source = oldImage;
            NewImageOverlay.Source = oldImage != null && newImage == null ? null : newImage; // Handle removals

            OldFileNameLabel.Text = oldFileName ?? "N/A";
            NewFileNameLabel.Text = newFileName ?? "N/A";
        }

        public void LoadAndDisplayPreloadedBatchAsync(
            List<(BitmapSource oldImage, BitmapSource newImage, string oldPath, string newPath)> items,
            int startIndex)
        {
            _preloadedImages = items;

            IsBatchMode = true;
            TotalFilesCount = items.Count;
            CurrentFileIndex = startIndex + 1;

            LoadCurrentBatchItem();
        }

        private void LoadCurrentBatchItem()
        {
            if (_preloadedImages == null || _preloadedImages.Count == 0) return;

            var currentItem = _preloadedImages[CurrentFileIndex - 1];

            SetImageData(currentItem.oldImage, currentItem.newImage, currentItem.oldPath, currentItem.newPath);
        }

        private void BtnPrevFile_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentFileIndex > 1)
            {
                CurrentFileIndex--;
                LoadCurrentBatchItem();
            }
        }

        private void BtnNextFile_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentFileIndex < TotalFilesCount)
            {
                CurrentFileIndex++;
                LoadCurrentBatchItem();
            }
        }

        #region Zoom & Pan Logic (Mouse Centered)

        private void ImageDiffWindow_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoomFactor = e.Delta > 0 ? 1.1 : 0.9;
            double newZoom = Math.Max(0.25, Math.Min(4.0, _currentZoom * zoomFactor));
            if (Math.Abs(newZoom - _currentZoom) < 0.001) return;

            Point mousePos = e.GetPosition(MainContentArea);
            double factor = newZoom / _currentZoom;
            double dx = (mousePos.X - OldTranslate.X) * (1 - factor);
            double dy = (mousePos.Y - OldTranslate.Y) * (1 - factor);

            _currentZoom = newZoom;
            ApplyTransform(scale: _currentZoom, deltaX: dx, deltaY: dy);
            if (Math.Abs(ZoomSlider.Value - _currentZoom * 100) > 0.01) ZoomSlider.Value = _currentZoom * 100;

            e.Handled = true;
        }

        private void ImageDiffWindow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Middle)
            {
                _isDragging = true;
                _lastMousePosition = e.GetPosition(MainContentArea);
                MainContentArea.Cursor = Cursors.SizeAll;
                MainContentArea.CaptureMouse();
            }
        }

        private void ImageDiffWindow_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                Point currentPos = e.GetPosition(MainContentArea);
                Vector delta = currentPos - _lastMousePosition;
                ApplyTransform(deltaX: delta.X, deltaY: delta.Y);
                _lastMousePosition = currentPos;
            }
        }

        private void ImageDiffWindow_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                MainContentArea.Cursor = Cursors.Arrow;
                MainContentArea.ReleaseMouseCapture();
            }
        }

        private void ApplyTransform(double? scale = null, double deltaX = 0, double deltaY = 0)
        {
            UpdateTransformGroup(OldScale, OldTranslate, scale, deltaX, deltaY);
            UpdateTransformGroup(NewScale, NewTranslate, scale, deltaX, deltaY);
            UpdateTransformGroup(OldOverlayScale, OldOverlayTranslate, scale, deltaX, deltaY);
            UpdateTransformGroup(NewOverlayScale, NewOverlayTranslate, scale, deltaX, deltaY);
            if (SliderSeparatorScale != null && SliderSeparatorTranslate != null)
                UpdateTransformGroup(SliderSeparatorScale, SliderSeparatorTranslate, scale, deltaX, deltaY);
            UpdateSliderEffect();
        }

        private void UpdateTransformGroup(ScaleTransform st, TranslateTransform tt, double? scale, double dx, double dy)
        {
            if (st == null || tt == null) return;
            if (scale.HasValue) { st.ScaleX = scale.Value; st.ScaleY = scale.Value; }
            tt.X += dx; tt.Y += dy;
        }

        #endregion

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            UpdateUIMode();
        }

        private void Background_Checked(object sender, RoutedEventArgs e)
        {
            // Handled by XAML DataTriggers
        }

        private void UpdateUIMode()
        {
            if (!_isInitialized) return;

            bool timeline = TimelineBtn.IsChecked == true;

            // Timeline Mode state transition
            if (timeline != _timelineModeActive)
            {
                _timelineModeActive = timeline;
                PauseTimeline();
                if (timeline)
                {
                    TimelineSlider.Value = 0;
                    // The overlay panel was just made visible: defer the first frame until
                    // layout has run, otherwise the wipe clip math sees an overlay size of 0
                    this.Dispatcher.InvokeAsync(() => ApplyTimelineFrame(0), DispatcherPriority.Loaded);
                }
                else
                {
                    NewImageOverlay.Opacity = 1.0;
                    if (SliderClip != null) SliderClip.Rect = new Rect(0, 0, 99999, 99999);
                    if (OldBadge != null) OldBadge.Visibility = Visibility.Collapsed;
                    if (NewBadge != null) NewBadge.Visibility = Visibility.Collapsed;
                    if (SliderSeparatorLine != null) SliderSeparatorLine.Visibility = SliderBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // Reset Slider Clip if not in Slider mode
            if (SliderBtn.IsChecked != true && SliderClip != null && !timeline)
            {
                SliderClip.Rect = new Rect(0, 0, 99999, 99999);
            }

            if (SliderBtn.IsChecked == true)
            {
                // Force layout update and then update the effect
                this.Dispatcher.InvokeAsync(() => UpdateSliderEffect(), DispatcherPriority.Loaded);
            }
        }

        private void OverlaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized) return;
            if (SliderBtn.IsChecked == true) UpdateSliderEffect();
        }

        private void UpdateSliderEffect()
        {
            if (SliderBtn.IsChecked != true || NewImageOverlay == null || SliderSeparatorLine == null) return;
            
            double percentage = OverlaySlider.Value / 100.0;
            double width = NewImageOverlay.ActualWidth;
            double height = NewImageOverlay.ActualHeight;

            if (width <= 0) return;

            // Natural movement: xPos follows the slider
            double xPos = width * percentage;
            
            // Revelado desde la izquierda (NEW oculta a OLD progresivamente)
            SliderClip.Rect = new Rect(0, 0, xPos, height);

            if (SliderOffsetTranslate != null)
            {
                SliderOffsetTranslate.X = xPos;
                SliderSeparatorLine.Margin = new Thickness(0); 
            }
        }

        #region Timeline Frame Sequence

        private void TimelineTimer_Tick(object sender, EventArgs e)
        {
            if (!_timelineModeActive || !_isTimelinePlaying) return;

            double total = TotalTimelineDuration;
            double elapsed = (DateTime.Now - _timelineStartTime).TotalSeconds;
            if (elapsed >= total)
            {
                if (_timelineLoop)
                {
                    // Loop: restart the cycle from the first frame
                    _timelineStartTime = DateTime.Now;
                    elapsed = 0;
                }
                else
                {
                    // End of sequence: hold the final frame
                    PauseTimeline();
                    ApplyTimelineFrame(1.0);
                    return;
                }
            }

            ApplyTimelineFrame(elapsed / total);
        }

        private void TimelinePlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_timelineModeActive) return;
            if (_isTimelinePlaying) PauseTimeline();
            else StartTimeline();
        }

        private void StartTimeline()
        {
            if (NewImageOverlay.Source == null) return;

            // If the sequence finished without loop, restart from the beginning
            if (TimelineSlider.Value >= 100) TimelineSlider.Value = 0;

            _isTimelinePlaying = true;
            _timelineStartTime = DateTime.Now;
            TimelinePlayIcon.Kind = Material.Icons.MaterialIconKind.Pause;
            _timelineTimer.Start();
        }

        private void PauseTimeline()
        {
            _isTimelinePlaying = false;
            _timelineTimer.Stop();
            if (TimelinePlayIcon != null) TimelinePlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
        }

        private void TimelineStopBtn_Click(object sender, RoutedEventArgs e)
        {
            PauseTimeline();
            ApplyTimelineFrame(0);
        }

        private void TimelineLoopBtn_Click(object sender, RoutedEventArgs e)
        {
            _timelineLoop = !_timelineLoop;
            TimelineLoopIcon.Foreground = _timelineLoop
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("TextSecondary");
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized || !_timelineModeActive) return;
            if (_isTimelinePlaying || _isApplyingTimelineFrame) return;

            // Manual scrubbing: render the exact frame at the slider position
            ApplyTimelineFrame(TimelineSlider.Value / 100.0);
        }

        private void TimelineEffect_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || !_timelineModeActive) return;
            ApplyTimelineFrame(TimelineSlider.Value / 100.0);
        }

        private void TimelineDuration_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TimelineDurationCombo.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag as string, out int duration) && duration > 0)
            {
                _timelineDuration = duration;
                UpdateTimelineTimeCode(TimelineSlider.Value / 100.0);
            }
        }

        private void TimelineSequence_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || !_timelineModeActive) return;
            _timelineRoundTrip = TimelineSequenceCombo.SelectedItem is ComboBoxItem seq && seq.Tag as string == "RoundTrip";
            ApplyTimelineFrame(TimelineSlider.Value / 100.0);
        }

        private void TimelineCycles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || !_timelineModeActive) return;
            if (TimelineCyclesCombo.SelectedItem is ComboBoxItem cyclesItem &&
                int.TryParse(cyclesItem.Tag as string, out int cycles) && cycles >= 1)
            {
                _timelineCycles = cycles;
            }
            ApplyTimelineFrame(TimelineSlider.Value / 100.0);
        }

        private void ZoomInBtn_Click(object sender, RoutedEventArgs e) => ZoomTo(_currentZoom * 1.25);
        private void ZoomOutBtn_Click(object sender, RoutedEventArgs e) => ZoomTo(_currentZoom * 0.8);

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized) return;
            ZoomTo(ZoomSlider.Value / 100.0);
        }

        private void ZoomResetBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentZoom = 1.0;
            ApplyTransform(scale: 1.0, deltaX: -OldTranslate.X, deltaY: -OldTranslate.Y);
            ZoomSlider.Value = 100;
        }

        private void ZoomTo(double newZoom)
        {
            newZoom = Math.Max(0.25, Math.Min(4.0, newZoom));
            if (Math.Abs(newZoom - _currentZoom) < 0.001) return;

            double factor = newZoom / _currentZoom;
            Point center = new Point(MainContentArea.ActualWidth / 2, MainContentArea.ActualHeight / 2);
            double dx = (center.X - OldTranslate.X) * (1 - factor);
            double dy = (center.Y - OldTranslate.Y) * (1 - factor);

            _currentZoom = newZoom;
            ApplyTransform(scale: _currentZoom, deltaX: dx, deltaY: dy);
            if (Math.Abs(ZoomSlider.Value - _currentZoom * 100) > 0.01) ZoomSlider.Value = _currentZoom * 100;
        }

        private void ApplyTimelineFrame(double progress)
        {
            if (!_timelineModeActive || _isApplyingTimelineFrame || NewImageOverlay == null) return;

            _isApplyingTimelineFrame = true;
            try
            {
                progress = Math.Max(0, Math.Min(1, progress));
                // Always sync the slider (the ValueChanged guard prevents feedback loops)
                TimelineSlider.Value = progress * 100;
                UpdateTimelineTimeCode(progress);

                double t = ImageExportUtils.TimelineProgress(progress, _timelineRoundTrip, _timelineCycles, _timelineDuration);
                UpdateTimelineBadges(t);

                if (WipeEffectBtn?.IsChecked == true)
                {
                    // Wipe: reveal NEW over OLD horizontally; the separator line is only
                    // shown while the wipe is actually sweeping (during holds it stays idle)
                    bool sweeping = t > 0 && t < 1;
                    if (SliderSeparatorLine != null) SliderSeparatorLine.Visibility = sweeping ? Visibility.Visible : Visibility.Collapsed;
                    double width = NewImageOverlay.ActualWidth;
                    double height = NewImageOverlay.ActualHeight;
                    if (width > 0 && SliderClip != null)
                    {
                        double xPos = width * t;
                        SliderClip.Rect = new Rect(0, 0, xPos, height);
                        if (SliderOffsetTranslate != null) SliderOffsetTranslate.X = xPos;
                    }
                    NewImageOverlay.Opacity = 1.0;
                }
                else
                {
                    // Fade: NEW fades in over OLD
                    if (SliderSeparatorLine != null) SliderSeparatorLine.Visibility = Visibility.Collapsed;
                    NewImageOverlay.Opacity = t;
                    if (SliderClip != null) SliderClip.Rect = new Rect(0, 0, 99999, 99999);
                }
            }
            finally
            {
                _isApplyingTimelineFrame = false;
            }
        }

        // Only the badge of the dominant image is shown: OLD below 50%, NEW above
        private void UpdateTimelineBadges(double t)
        {
            if (OldBadge == null || NewBadge == null) return;
            bool showNew = t >= 0.5;
            OldBadge.Visibility = showNew ? Visibility.Collapsed : Visibility.Visible;
            NewBadge.Visibility = showNew ? Visibility.Visible : Visibility.Collapsed;
        }

        // Timecode readout for the transport bar (editor-style mm:ss.f / total)
        private void UpdateTimelineTimeCode(double progress)
        {
            if (TimelineTimeCode == null) return;
            progress = Math.Max(0, Math.Min(1, progress));
            string current = TimeSpan.FromSeconds(progress * TotalTimelineDuration).ToString(@"mm\:ss\.f");
            string total = TimeSpan.FromSeconds(TotalTimelineDuration).ToString(@"mm\:ss\.f");
            TimelineTimeCode.Text = $"{current} / {total}";
        }

        #endregion

        #region GIF Export

        private async void ExportGifBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_timelineModeActive) return;
            if (OldImage.Source is not BitmapSource oldS || NewImage.Source is not BitmapSource newS || newS == null)
            {
                _logService?.LogError(new InvalidOperationException("Missing OLD or NEW image"), "GIF export requires both OLD and NEW images.");
                return;
            }

            PauseTimeline();

            int fps = 24;
            if (TimelineFpsCombo.SelectedItem is ComboBoxItem fpsItem &&
                int.TryParse(fpsItem.Tag as string, out int fpsValue)) fps = fpsValue;
            int frameCount = Math.Max(2, (int)Math.Ceiling(TotalTimelineDuration * fps));

            int maxDimension = 1024;
            if (TimelineSizeCombo.SelectedItem is ComboBoxItem sizeItem &&
                int.TryParse(sizeItem.Tag as string, out int sizeValue) && sizeValue >= 0) maxDimension = sizeValue;

            var saveDialog = new SaveFileDialog
            {
                Filter = "Animated GIF (*.gif)|*.gif",
                Title = "Export Frame Sequence as GIF",
                FileName = BuildGifFileName()
            };
            if (saveDialog.ShowDialog(this) != true) return;

            string path = saveDialog.FileName;
            bool wipe = WipeEffectBtn?.IsChecked == true;
            bool roundTrip = _timelineRoundTrip;
            int cycles = _timelineCycles;
            Cursor = Cursors.Wait;
            try
            {
                // Prepare pixel data and OLD/NEW badges on the UI thread, encode off-thread
                var (oldPixels, oldW, oldH, newPixels, newW, newH, width, height) =
                    ImageExportUtils.PrepareGifPixels(oldS, newS, maxDimension);
                var badges = ImageExportUtils.RenderGifBadges(OldBadge, NewBadge);
                await ImageExportUtils.SaveAsGifSequenceAsync(
                    oldPixels, oldW, oldH, newPixels, newW, newH, width, height, frameCount, fps, _timelineDuration, wipe, roundTrip, cycles, path,
                    badges.Old, badges.New);
            }
            catch (Exception ex)
            {
                _logService?.LogError(ex, "Failed to export timeline GIF.");
                MessageBox.Show(this, "Failed to export GIF: " + ex.Message, "Export GIF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cursor = Cursors.Arrow;
            }
        }

        private string BuildGifFileName()
        {
            string baseName = Path.GetFileNameWithoutExtension(NewFileNameLabel?.Text ?? "image_comparison");
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "image_comparison";
            return $"{baseName}_timeline.gif";
        }

        #endregion

        private void OnWindowClosed(object sender, EventArgs e)
        {
            PauseTimeline();
            _timelineTimer?.Stop();
            OldImage.Source = null;
            NewImage.Source = null;
            OldImageOverlay.Source = null;
            NewImageOverlay.Source = null;
        }
    }
}
