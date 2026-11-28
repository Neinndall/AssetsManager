using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AssetsManager.Views.Helpers;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using Material.Icons;
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

        private List<(ImageSource oldImage, ImageSource newImage, string oldPath, string newPath)> _preloadedImages;

        private bool _isInitialized = false;
        private Point _lastMousePosition;
        private bool _isDragging = false;
        private double _currentZoom = 1.0;
        private double _panX = 0;
        private double _panY = 0;

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
        private CancellationTokenSource _exportCts;

        // DURATION is the hold time per state; transitions are folded into the state
        // boundaries, so the total stays an exact multiple of the hold time
        private double TotalTimelineDuration =>
            ImageExportUtils.TimelineTotalDuration(_timelineDuration, _timelineRoundTrip, _timelineCycles);

        public ImageDiffWindow(LogService logService = null) : this(null, null, null, null, logService)
        {
        }

        public ImageDiffWindow(ImageSource oldImage, ImageSource newImage, string oldFileName, string newFileName, LogService logService = null)
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
            if (OverlayStage != null)
                OverlayStage.SizeChanged += (_, _) => UpdateSliderEffect();
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

        private void SetImageData(ImageSource oldImage, ImageSource newImage, string oldFileName, string newFileName)
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

            if (OverlayFileNameLabel != null)
            {
                string displayPath = !string.IsNullOrWhiteSpace(newFileName) ? newFileName : oldFileName;
                OverlayFileNameLabel.Text = displayPath ?? string.Empty;
                OverlayFileNameLabel.Visibility = string.IsNullOrWhiteSpace(displayPath) ? Visibility.Collapsed : Visibility.Visible;
            }

            UpdateResolutionMetadata(oldImage, newImage);

            if (ExportSequenceBtn != null)
                ExportSequenceBtn.IsEnabled = oldImage != null && newImage != null;

            if (SliderBtn?.IsChecked == true)
                Dispatcher.InvokeAsync(UpdateSliderEffect, DispatcherPriority.Loaded);
        }

        private void UpdateResolutionMetadata(ImageSource oldImage, ImageSource newImage)
        {
            bool hasOld = TryGetDimensions(oldImage, out int oldW, out int oldH);
            bool hasNew = TryGetDimensions(newImage, out int newW, out int newH);
            bool sizeChanged = hasOld && hasNew && (oldW != newW || oldH != newH);

            if (OverlaySizeBadge != null && OverlaySizeBadgeText != null)
            {
                if (sizeChanged)
                {
                    OverlaySizeBadgeText.Text = $"{oldW}×{oldH} → {newW}×{newH}";
                    OverlaySizeBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    OverlaySizeBadge.Visibility = Visibility.Collapsed;
                }
            }

            if (ResolutionLabel != null)
            {
                if (sizeChanged)
                    ResolutionLabel.Text = $"Resolution: {oldW}×{oldH} → {newW}×{newH}";
                else if (hasNew)
                    ResolutionLabel.Text = $"Resolution: {newW}×{newH}";
                else if (hasOld)
                    ResolutionLabel.Text = $"Resolution: {oldW}×{oldH}";
                else
                    ResolutionLabel.Text = "Resolution: N/A";
            }
        }

        private static bool TryGetDimensions(ImageSource image, out int width, out int height)
        {
            if (image is BitmapSource bmp)
            {
                width = bmp.PixelWidth;
                height = bmp.PixelHeight;
                return width > 0 && height > 0;
            }

            if (image != null &&
                image.Width > 0 &&
                image.Height > 0 &&
                !double.IsNaN(image.Width) &&
                !double.IsNaN(image.Height) &&
                !double.IsInfinity(image.Width) &&
                !double.IsInfinity(image.Height))
            {
                width = Math.Max(1, (int)Math.Round(image.Width));
                height = Math.Max(1, (int)Math.Round(image.Height));
                return true;
            }

            width = 0;
            height = 0;
            return false;
        }

        public void LoadAndDisplayPreloadedBatchAsync(
            List<(ImageSource oldImage, ImageSource newImage, string oldPath, string newPath)> items,
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

            IInputElement zoomTarget = ResolveZoomTarget(
                isSideBySide: SideBySideBtn?.IsChecked == true,
                oldImageBorder: OldImageBorder,
                newImageBorder: NewImageBorder,
                overlayContainer: ImageContainer,
                fallback: MainContentArea);
            Point mousePos = e.GetPosition(zoomTarget);
            double factor = newZoom / _currentZoom;
            (double dx, double dy) = ComputeZoomPanDelta(mousePos, _panX, _panY, factor);

            _currentZoom = newZoom;
            ApplyTransform(scale: _currentZoom, deltaX: dx, deltaY: dy);
            if (Math.Abs(ZoomSlider.Value - _currentZoom * 100) > 0.01) ZoomSlider.Value = _currentZoom * 100;

            e.Handled = true;
        }

        internal static IInputElement ResolveZoomTarget(
            bool isSideBySide,
            UIElement oldImageBorder,
            UIElement newImageBorder,
            UIElement overlayContainer,
            IInputElement fallback)
        {
            if (isSideBySide)
            {
                if (newImageBorder?.IsMouseOver == true) return newImageBorder;
                if (oldImageBorder?.IsMouseOver == true) return oldImageBorder;
                return oldImageBorder ?? fallback;
            }

            return overlayContainer ?? fallback;
        }

        internal static (double DeltaX, double DeltaY) ComputeZoomPanDelta(
            Point localCursorPosition,
            double currentPanX,
            double currentPanY,
            double zoomRatio)
        {
            double dx = (localCursorPosition.X - currentPanX) * (1 - zoomRatio);
            double dy = (localCursorPosition.Y - currentPanY) * (1 - zoomRatio);
            return (dx, dy);
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
            _panX += deltaX;
            _panY += deltaY;

            UpdateTransformGroup(OldScale, OldTranslate, scale, deltaX, deltaY);
            UpdateTransformGroup(NewScale, NewTranslate, scale, deltaX, deltaY);
            UpdateTransformGroup(OverlayStageScale, OverlayStageTranslate, scale, deltaX, deltaY);

            if (ZoomPercentLabel != null)
                ZoomPercentLabel.Text = $"{Math.Round(_currentZoom * 100):0}%";

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
                    // Restore style/default opacity after Timeline's local overrides.
                    OldImageOverlay.ClearValue(UIElement.OpacityProperty);
                    NewImageOverlay.ClearValue(UIElement.OpacityProperty);
                    ResetOverlayClips();
                    if (OldBadge != null) OldBadge.Visibility = Visibility.Collapsed;
                    if (NewBadge != null) NewBadge.Visibility = Visibility.Collapsed;
                    if (SliderSeparatorLine != null) SliderSeparatorLine.Visibility = SliderBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            if (SliderBtn.IsChecked != true && !timeline)
            {
                ResetOverlayClips();
            }

            if (SliderBtn.IsChecked == true)
            {
                this.Dispatcher.InvokeAsync(() => UpdateSliderEffect(), DispatcherPriority.Loaded);
            }
        }

        private void ResetOverlayClips()
        {
            if (SliderClip != null) SliderClip.Rect = new Rect(0, 0, 99999, 99999);
            if (OldSliderClip != null) OldSliderClip.Rect = new Rect(0, 0, 99999, 99999);
        }

        private void OverlaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized) return;
            if (SliderBtn.IsChecked == true) UpdateSliderEffect();
        }

        private void UpdateSliderEffect()
        {
            if (SliderBtn.IsChecked != true || OverlayStage == null || SliderSeparatorLine == null) return;

            double percentage = OverlaySlider.Value / 100.0;
            ApplyHorizontalSplit(percentage, newOnLeft: true);
        }

        // Applies mutually exclusive horizontal clips so transparent or differently sized
        // OLD/NEW images never show through or stack behind each other.
        private void ApplyHorizontalSplit(double stageFraction, bool newOnLeft)
        {
            if (OverlayStage == null || NewImageOverlay == null || OldImageOverlay == null) return;

            double stageW = OverlayStage.ActualWidth;
            if (stageW <= 0) return;

            double cutX = stageW * Math.Clamp(stageFraction, 0, 1);
            double newW = NewImageOverlay.ActualWidth;
            double newH = NewImageOverlay.ActualHeight;
            double oldW = OldImageOverlay.ActualWidth;
            double oldH = OldImageOverlay.ActualHeight;

            double newOffsetX = (stageW - newW) / 2.0;
            double oldOffsetX = (stageW - oldW) / 2.0;

            double localNewCut = Math.Clamp(cutX - newOffsetX, 0, newW);
            double localOldCut = Math.Clamp(cutX - oldOffsetX, 0, oldW);

            if (newOnLeft)
            {
                if (SliderClip != null) SliderClip.Rect = new Rect(0, 0, localNewCut, newH);
                if (OldSliderClip != null) OldSliderClip.Rect = new Rect(localOldCut, 0, Math.Max(0, oldW - localOldCut), oldH);
            }
            else
            {
                if (OldSliderClip != null) OldSliderClip.Rect = new Rect(0, 0, localOldCut, oldH);
                if (SliderClip != null) SliderClip.Rect = new Rect(localNewCut, 0, Math.Max(0, newW - localNewCut), newH);
            }

            if (SliderOffsetTranslate != null)
            {
                SliderOffsetTranslate.X = cutX;
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
                    _timelineStartTime = DateTime.Now;
                    elapsed = 0;
                }
                else
                {
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

            // Restart from start only when already at the end; otherwise resume from the scrubber position.
            if (TimelineSlider.Value >= 100) TimelineSlider.Value = 0;

            _isTimelinePlaying = true;
            double resumeSeconds = (TimelineSlider.Value / 100.0) * TotalTimelineDuration;
            _timelineStartTime = DateTime.Now - TimeSpan.FromSeconds(resumeSeconds);
            TimelinePlayIcon.Kind = MaterialIconKind.Pause;
            _timelineTimer.Start();
        }

        private void PauseTimeline()
        {
            _isTimelinePlaying = false;
            _timelineTimer.Stop();
            if (TimelinePlayIcon != null) TimelinePlayIcon.Kind = MaterialIconKind.Play;
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
                double.TryParse(item.Tag as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) &&
                duration > 0)
            {
                _timelineDuration = duration;
                if (_isInitialized && _timelineModeActive)
                    ApplyTimelineFrame(TimelineSlider.Value / 100.0);
                else
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
            ApplyTransform(scale: 1.0, deltaX: -_panX, deltaY: -_panY);
            ZoomSlider.Value = 100;
        }

        internal static Point ResolveZoomCenter(
            bool isSideBySide,
            double oldWidth,
            double oldHeight,
            double overlayWidth,
            double overlayHeight,
            double fallbackWidth,
            double fallbackHeight)
        {
            if (isSideBySide && oldWidth > 0 && oldHeight > 0)
                return new Point(oldWidth / 2.0, oldHeight / 2.0);
            if (!isSideBySide && overlayWidth > 0 && overlayHeight > 0)
                return new Point(overlayWidth / 2.0, overlayHeight / 2.0);
            return new Point(fallbackWidth / 2.0, fallbackHeight / 2.0);
        }

        private void ZoomTo(double newZoom)
        {
            newZoom = Math.Max(0.25, Math.Min(4.0, newZoom));
            if (Math.Abs(newZoom - _currentZoom) < 0.001) return;

            double factor = newZoom / _currentZoom;
            Point center = ResolveZoomCenter(
                isSideBySide: SideBySideBtn?.IsChecked == true,
                oldWidth: OldImageBorder?.ActualWidth ?? 0,
                oldHeight: OldImageBorder?.ActualHeight ?? 0,
                overlayWidth: ImageContainer?.ActualWidth ?? 0,
                overlayHeight: ImageContainer?.ActualHeight ?? 0,
                fallbackWidth: MainContentArea.ActualWidth,
                fallbackHeight: MainContentArea.ActualHeight);
            double dx = (center.X - _panX) * (1 - factor);
            double dy = (center.Y - _panY) * (1 - factor);

            _currentZoom = newZoom;
            ApplyTransform(scale: _currentZoom, deltaX: dx, deltaY: dy);
            if (Math.Abs(ZoomSlider.Value - _currentZoom * 100) > 0.01) ZoomSlider.Value = _currentZoom * 100;
        }

        private TimelineTransitionEffect GetSelectedEffect()
        {
            if (FadeEffectBtn?.IsChecked == true) return TimelineTransitionEffect.Fade;
            if (CutEffectBtn?.IsChecked == true) return TimelineTransitionEffect.Cut;
            return TimelineTransitionEffect.Wipe;
        }

        private void ApplyTimelineFrame(double progress)
        {
            if (!_timelineModeActive || _isApplyingTimelineFrame || NewImageOverlay == null) return;

            _isApplyingTimelineFrame = true;
            try
            {
                progress = Math.Max(0, Math.Min(1, progress));
                TimelineSlider.Value = progress * 100;
                UpdateTimelineTimeCode(progress);

                double t = ImageExportUtils.TimelineProgress(progress, _timelineRoundTrip, _timelineCycles, _timelineDuration);
                TimelineTransitionEffect effect = GetSelectedEffect();
                UpdateTimelineBadges(effect, t);

                switch (effect)
                {
                    case TimelineTransitionEffect.Wipe:
                        // Sweep the divider left to right (revealing NEW) while keeping OLD top-left and NEW top-right.
                        bool sweeping = t > 0.001 && t < 0.999;
                        if (SliderSeparatorLine != null)
                            SliderSeparatorLine.Visibility = sweeping ? Visibility.Visible : Visibility.Collapsed;
                        ApplyHorizontalSplit(t, newOnLeft: true);
                        OldImageOverlay.Opacity = 1.0;
                        NewImageOverlay.Opacity = 1.0;
                        break;

                    case TimelineTransitionEffect.Cut:
                        if (SliderSeparatorLine != null) SliderSeparatorLine.Visibility = Visibility.Collapsed;
                        ResetOverlayClips();
                        bool showNew = t >= 0.5;
                        OldImageOverlay.Opacity = showNew ? 0.0 : 1.0;
                        NewImageOverlay.Opacity = showNew ? 1.0 : 0.0;
                        break;

                    default: // Fade (true cross-dissolve so transparent OLD/NEW silhouettes never ghost)
                        if (SliderSeparatorLine != null) SliderSeparatorLine.Visibility = Visibility.Collapsed;
                        ResetOverlayClips();
                        OldImageOverlay.Opacity = 1.0 - t;
                        NewImageOverlay.Opacity = t;
                        break;
                }
            }
            finally
            {
                _isApplyingTimelineFrame = false;
            }
        }

        private void UpdateTimelineBadges(TimelineTransitionEffect effect, double t)
        {
            if (OldBadge == null || NewBadge == null) return;

            OldBadge.HorizontalAlignment = HorizontalAlignment.Left;
            NewBadge.HorizontalAlignment = HorizontalAlignment.Right;

            if (effect == TimelineTransitionEffect.Wipe)
            {
                bool sweeping = t > 0.001 && t < 0.999;
                OldBadge.Visibility = t < 0.999 ? Visibility.Visible : Visibility.Collapsed;
                NewBadge.Visibility = (sweeping || t >= 0.999) ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            bool showNew = t >= 0.5;
            OldBadge.Visibility = showNew ? Visibility.Collapsed : Visibility.Visible;
            NewBadge.Visibility = showNew ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateTimelineTimeCode(double progress)
        {
            if (TimelineTimeCode == null) return;
            progress = Math.Max(0, Math.Min(1, progress));
            string current = TimeSpan.FromSeconds(progress * TotalTimelineDuration).ToString(@"mm\:ss\.f");
            string total = TimeSpan.FromSeconds(TotalTimelineDuration).ToString(@"mm\:ss\.f");
            TimelineTimeCode.Text = $"{current} / {total}";
        }

        #endregion

        #region Timeline Sequence Export (MP4 / GIF)

        private void ExportOptionsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ExportOptionsPopup != null)
                ExportOptionsPopup.IsOpen = !ExportOptionsPopup.IsOpen;
        }

        private void ExportFormat_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || ExportSequenceLabel == null || ExportSequenceIcon == null) return;

            bool isMp4 = ExportMp4Radio?.IsChecked == true;
            ExportSequenceLabel.Text = isMp4 ? "EXPORT MP4" : "EXPORT GIF";
            ExportSequenceIcon.Kind = isMp4 ? MaterialIconKind.MovieOpenOutline : MaterialIconKind.FileGifBox;
        }

        private void CancelExportBtn_Click(object sender, RoutedEventArgs e)
        {
            _exportCts?.Cancel();
        }

        private async void ExportSequenceBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_timelineModeActive || _exportCts != null) return;
            if (OldImage.Source == null || NewImage.Source == null)
            {
                _logService?.LogError(new InvalidOperationException("Missing OLD or NEW image"), "Timeline export requires both OLD and NEW images.");
                return;
            }

            PauseTimeline();
            if (ExportOptionsPopup != null) ExportOptionsPopup.IsOpen = false;

            TimelineExportFormat format = ExportMp4Radio?.IsChecked == true
                ? TimelineExportFormat.Mp4
                : TimelineExportFormat.Gif;

            var saveDialog = new SaveFileDialog
            {
                Filter = format == TimelineExportFormat.Mp4
                    ? "MP4 Video (*.mp4)|*.mp4"
                    : "Animated GIF (*.gif)|*.gif",
                Title = format == TimelineExportFormat.Mp4
                    ? "Export Frame Sequence as MP4 Video"
                    : "Export Frame Sequence as Animated GIF",
                FileName = BuildExportFileName(format)
            };
            if (saveDialog.ShowDialog(this) != true) return;

            string path = saveDialog.FileName;
            var options = new TimelineExportOptions(
                Format: format,
                Effect: GetSelectedEffect(),
                RoundTrip: _timelineRoundTrip,
                Cycles: _timelineCycles,
                HoldDuration: _timelineDuration,
                Fps: GetSelectedFps(),
                TargetSize: GetSelectedTargetSize(),
                UpscaleSmallImages: UpscaleSmallCheckBox?.IsChecked == true,
                BackgroundMode: GetSelectedBackgroundMode(),
                NormalizedCropRect: GetNormalizedViewportCrop());

            _exportCts = new CancellationTokenSource();
            SetExportProgressState(active: true);

            SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> oldBadgeImg = null;
            SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> newBadgeImg = null;

            try
            {
                int rasterFallbackSize = options.TargetSize > 0 ? options.TargetSize : 1024;
                BitmapSource oldS = EnsureBitmapSource(OldImage.Source, rasterFallbackSize);
                BitmapSource newS = EnsureBitmapSource(NewImage.Source, rasterFallbackSize);
                if (oldS == null || newS == null)
                    throw new InvalidOperationException("Unable to rasterize vector image for timeline export.");

                var (oldPixels, oldW, oldH, newPixels, newW, newH) = ImageExportUtils.PrepareTimelineSourcePixels(oldS, newS);
                (oldBadgeImg, newBadgeImg) = ImageExportUtils.RenderTimelineBadges(
                    TryFindResource("AccentOrange") as Brush,
                    TryFindResource("AccentGreen") as Brush,
                    FontFamily);

                var progress = new Progress<(int Completed, int Total)>(p =>
                {
                    if (ExportProgressBar != null && p.Total > 0)
                        ExportProgressBar.Value = p.Completed * 100.0 / p.Total;
                    if (ExportProgressText != null)
                        ExportProgressText.Text = $"{p.Completed} / {p.Total}";
                });

                await ImageExportUtils.SaveTimelineSequenceAsync(
                    oldPixels, oldW, oldH,
                    newPixels, newW, newH,
                    options,
                    path,
                    oldBadgeImg,
                    newBadgeImg,
                    progress,
                    _exportCts.Token);

                _logService?.LogInteractiveSuccess($"Exported timeline sequence to {Path.GetFileName(path)}", path, Path.GetFileName(path));
            }
            catch (OperationCanceledException)
            {
                // User cancelled export cleanly.
            }
            catch (Exception ex)
            {
                _logService?.LogError(ex, $"Failed to export timeline {format}.");
                if (IsLoaded)
                {
                    MessageBox.Show(this, $"Failed to export {format.ToString().ToUpperInvariant()}: {ex.Message}", "Export Timeline", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                oldBadgeImg?.Dispose();
                newBadgeImg?.Dispose();
                _exportCts?.Dispose();
                _exportCts = null;
                SetExportProgressState(active: false);
            }
        }

        private void SetExportProgressState(bool active)
        {
            if (ExportProgressPanel != null) ExportProgressPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (ExportActionsPanel != null) ExportActionsPanel.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            if (ExportProgressBar != null) ExportProgressBar.Value = 0;
            if (ExportProgressText != null) ExportProgressText.Text = "0 / 0";
        }

        private int GetSelectedFps()
        {
            if (ExportFpsCombo?.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag as string, out int fps) && fps > 0)
                return fps;
            return 30;
        }

        private int GetSelectedTargetSize()
        {
            if (ExportSizeCombo?.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag as string, out int size) && size >= 0)
                return size;
            return 1024;
        }

        private TimelineBackgroundMode GetSelectedBackgroundMode()
        {
            if (BgGridBtn?.IsChecked == true) return TimelineBackgroundMode.Checkerboard;
            if (BgLightBtn?.IsChecked == true) return TimelineBackgroundMode.Light;
            return TimelineBackgroundMode.Dark;
        }

        private Rect? GetNormalizedViewportCrop()
        {
            if (CropToZoomCheckBox?.IsChecked != true || ImageContainer == null || OverlayStage == null)
                return null;

            double stageW = OverlayStage.ActualWidth;
            double stageH = OverlayStage.ActualHeight;
            if (stageW <= 0 || stageH <= 0 || ImageContainer.ActualWidth <= 0 || ImageContainer.ActualHeight <= 0)
                return null;

            GeneralTransform toStage = ImageContainer.TransformToDescendant(OverlayStage);
            Rect visibleBounds = toStage.TransformBounds(new Rect(0, 0, ImageContainer.ActualWidth, ImageContainer.ActualHeight));

            Rect stageRect = new Rect(0, 0, stageW, stageH);
            visibleBounds.Intersect(stageRect);
            if (visibleBounds.IsEmpty || visibleBounds.Width <= 1 || visibleBounds.Height <= 1)
                return null;

            double nx = visibleBounds.X / stageW;
            double ny = visibleBounds.Y / stageH;
            double nw = visibleBounds.Width / stageW;
            double nh = visibleBounds.Height / stageH;

            if (nw >= 0.99 && nh >= 0.99) return null;
            return new Rect(nx, ny, nw, nh);
        }

        private static BitmapSource EnsureBitmapSource(ImageSource source, int targetMaxDimension)
        {
            if (source is BitmapSource bitmap)
                return bitmap;

            if (!TryGetDimensions(source, out int sourceWidth, out int sourceHeight))
                return null;

            int maxDim = Math.Max(1, targetMaxDimension);
            double scale = Math.Min((double)maxDim / sourceWidth, (double)maxDim / sourceHeight);
            int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawImage(source, new Rect(0, 0, width, height));
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        private string BuildExportFileName(TimelineExportFormat format)
        {
            string baseName = Path.GetFileNameWithoutExtension(NewFileNameLabel?.Text ?? "image_comparison");
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "image_comparison";
            string ext = format == TimelineExportFormat.Mp4 ? "mp4" : "gif";
            return $"{baseName}_timeline.{ext}";
        }

        #endregion

        private void OnWindowClosed(object sender, EventArgs e)
        {
            _exportCts?.Cancel();
            PauseTimeline();
            _timelineTimer?.Stop();
            OldImage.Source = null;
            NewImage.Source = null;
            OldImageOverlay.Source = null;
            NewImageOverlay.Source = null;
        }
    }
}
