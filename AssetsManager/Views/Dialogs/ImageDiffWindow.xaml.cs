using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AssetsManager.Views.Dialogs
{
    public partial class ImageDiffWindow : Window
    {
        private bool _isInitialized = false;
        private Point _lastMousePosition;
        private bool _isDragging = false;
        private double _currentZoom = 1.0;
        private WriteableBitmap _diffMap;

        public ImageDiffWindow(BitmapSource oldImage, BitmapSource newImage, string oldFileName, string newFileName)
        {
            InitializeComponent();
            
            // Set data for both modes
            OldImage.Source = oldImage;
            NewImage.Source = newImage;
            OldImageOverlay.Source = oldImage;
            NewImageOverlay.Source = newImage;

            OldFileNameLabel.Text = oldFileName;
            NewFileNameLabel.Text = newFileName;

            this.Closed += OnWindowClosed;
            this.SizeChanged += (s, e) => UpdateSliderEffect();

            // Register Mouse Events for Zoom & Pan
            this.MouseWheel += ImageDiffWindow_MouseWheel;
            this.MouseDown += ImageDiffWindow_MouseDown;
            this.MouseMove += ImageDiffWindow_MouseMove;
            this.MouseUp += ImageDiffWindow_MouseUp;
            
            _isInitialized = true;
            UpdateUIMode();
            UpdateBackground();
        }

        #region Difference Map Logic

        private void GenerateDifferenceMap()
        {
            if (_diffMap != null) return; // Only generate once

            var oldBmp = OldImage.Source as BitmapSource;
            var newBmp = NewImage.Source as BitmapSource;

            if (oldBmp == null || newBmp == null) return;

            // Ensure we are working with the same format (Bgra32 is easy to manipulate)
            FormatConvertedBitmap oldConverted = new FormatConvertedBitmap(oldBmp, PixelFormats.Bgra32, null, 0);
            FormatConvertedBitmap newConverted = new FormatConvertedBitmap(newBmp, PixelFormats.Bgra32, null, 0);

            int width = oldConverted.PixelWidth;
            int height = oldConverted.PixelHeight;

            _diffMap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

            int stride = width * 4;
            byte[] oldPixels = new byte[height * stride];
            byte[] newPixels = new byte[height * stride];
            byte[] diffPixels = new byte[height * stride];

            oldConverted.CopyPixels(oldPixels, stride, 0);
            newConverted.CopyPixels(newPixels, stride, 0);

            for (int i = 0; i < oldPixels.Length; i += 4)
            {
                // Compare B, G, R, A
                bool isDifferent = oldPixels[i] != newPixels[i] || 
                                   oldPixels[i + 1] != newPixels[i + 1] || 
                                   oldPixels[i + 2] != newPixels[i + 2] ||
                                   oldPixels[i + 3] != newPixels[i + 3];

                if (isDifferent)
                {
                    // Magenta for differences
                    diffPixels[i] = 255; diffPixels[i + 1] = 0; diffPixels[i + 2] = 255; diffPixels[i + 3] = 255;
                }
                else
                {
                    // Black for identical
                    diffPixels[i] = 0; diffPixels[i + 1] = 0; diffPixels[i + 2] = 0; diffPixels[i + 3] = 255;
                }
            }

            _diffMap.WritePixels(new Int32Rect(0, 0, width, height), diffPixels, stride, 0);
            DiffImageOverlay.Source = _diffMap;
        }

        #endregion

        #region Zoom & Pan Logic (Mouse Centered)

        private void ImageDiffWindow_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoomFactor = e.Delta > 0 ? 1.1 : 0.9;
            double newZoom = _currentZoom * zoomFactor;

            if (newZoom < 0.1 || newZoom > 20) return;

            // Get mouse position relative to the Window (stable reference)
            Point mousePos = e.GetPosition(this);

            // For each transform group, we need to adjust translation to center zoom on mouse
            // We use OldTranslate as reference since all are synchronized
            double dx = (mousePos.X - OldTranslate.X) * (1 - zoomFactor);
            double dy = (mousePos.Y - OldTranslate.Y) * (1 - zoomFactor);

            _currentZoom = newZoom;
            ApplyTransform(scale: _currentZoom, deltaX: dx, deltaY: dy);
            
            e.Handled = true;
        }

        private void ImageDiffWindow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Middle)
            {
                _isDragging = true;
                _lastMousePosition = e.GetPosition(this);
                this.Cursor = Cursors.SizeAll;
                this.CaptureMouse();
            }
        }

        private void ImageDiffWindow_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                Point currentPos = e.GetPosition(this);
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
                this.Cursor = Cursors.Arrow;
                this.ReleaseMouseCapture();
            }
        }

        private void ApplyTransform(double? scale = null, double deltaX = 0, double deltaY = 0)
        {
            UpdateTransformGroup(OldScale, OldTranslate, scale, deltaX, deltaY);
            UpdateTransformGroup(NewScale, NewTranslate, scale, deltaX, deltaY);
            UpdateTransformGroup(OldOverlayScale, OldOverlayTranslate, scale, deltaX, deltaY);
            UpdateTransformGroup(NewOverlayScale, NewOverlayTranslate, scale, deltaX, deltaY);
            UpdateTransformGroup(DiffOverlayScale, DiffOverlayTranslate, scale, deltaX, deltaY);
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
            if (!_isInitialized) return;
            UpdateBackground();
        }

        private void UpdateBackground()
        {
            Brush bgBrush;
            if (BgGridBtn.IsChecked == true) bgBrush = (Brush)FindResource("CheckerboardBrush");
            else if (BgLightBtn.IsChecked == true) bgBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)); 
            else bgBrush = (Brush)FindResource("DarkBackground");

            if (OldImageBorder != null) OldImageBorder.Background = bgBrush;
            if (NewImageBorder != null) NewImageBorder.Background = bgBrush;
            if (ImageContainer != null) ImageContainer.Background = bgBrush;
        }

        private void UpdateUIMode()
        {
            if (SideBySidePanel == null || OverlayPanel == null) return;

            if (SideBySideBtn.IsChecked == true)
            {
                SideBySidePanel.Visibility = Visibility.Visible;
                OverlayPanel.Visibility = Visibility.Collapsed;
                ToolsContainer.Visibility = Visibility.Collapsed;
            }
            else
            {
                SideBySidePanel.Visibility = Visibility.Collapsed;
                OverlayPanel.Visibility = Visibility.Visible;

                if (DiffBtn.IsChecked == true)
                {
                    ToolsContainer.Visibility = Visibility.Collapsed;
                    OverlayInfoLabel.Text = "Mathematical Difference Map (Heatmap)";
                    NewImageOverlay.Visibility = Visibility.Collapsed;
                    DiffImageOverlay.Visibility = Visibility.Visible;
                    GenerateDifferenceMap();
                }
                else
                {
                    NewImageOverlay.Visibility = Visibility.Visible;
                    DiffImageOverlay.Visibility = Visibility.Collapsed;
                    ToolsContainer.Visibility = Visibility.Visible;

                    if (SliderBtn.IsChecked == true)
                    {
                        OverlayInfoLabel.Text = "Slider Mode (Swipe)";
                        NewImageOverlay.Opacity = 1;
                        UpdateSliderEffect();
                    }
                    else if (OnionSkinBtn.IsChecked == true)
                    {
                        OverlayInfoLabel.Text = "Onion Skin Mode (Opacity)";
                        SliderClip.Rect = new Rect(0, 0, 99999, 99999);
                        UpdateOpacityEffect();
                    }
                }
            }
        }

        private void OverlaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized) return;
            if (SliderBtn.IsChecked == true) UpdateSliderEffect();
            else if (OnionSkinBtn.IsChecked == true) UpdateOpacityEffect();
        }

        private void UpdateSliderEffect()
        {
            if (SliderBtn.IsChecked != true || NewImageOverlay == null) return;
            double percentage = OverlaySlider.Value / 100.0;
            double width = NewImageOverlay.ActualWidth;
            double height = NewImageOverlay.ActualHeight;
            if (width > 0)
            {
                double clipX = width * percentage;
                SliderClip.Rect = new Rect(clipX, 0, width - clipX, height);
            }
        }

        private void UpdateOpacityEffect()
        {
            if (OnionSkinBtn.IsChecked != true) return;
            NewImageOverlay.Opacity = OverlaySlider.Value / 100.0;
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            OldImage.Source = null;
            NewImage.Source = null;
            OldImageOverlay.Source = null;
            NewImageOverlay.Source = null;
            DiffImageOverlay.Source = null;
            _diffMap = null;
        }
    }
}
