using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AssetsManager.Views.Dialogs
{
    public partial class ImageDiffWindow : Window
    {
        private bool _isInitialized = false;

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
            
            _isInitialized = true;
            UpdateUIMode();
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            UpdateUIMode();
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
                    SliderClip.Rect = new Rect(0, 0, 99999, 99999); // No clipping
                    UpdateOpacityEffect();
                }
            }
        }

        private void OverlaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized) return;
            
            if (SliderBtn.IsChecked == true)
                UpdateSliderEffect();
            else if (OnionSkinBtn.IsChecked == true)
                UpdateOpacityEffect();
        }

        private void UpdateSliderEffect()
        {
            if (SliderBtn.IsChecked != true) return;

            // Percentage (0-100) to actual width
            double percentage = OverlaySlider.Value / 100.0;
            
            // We clip the NEW image (on top). 
            // If slider is at 50%, we show the right 50% of the NEW image,
            // revealing the left 50% of the OLD image underneath.
            
            double width = NewImageOverlay.ActualWidth;
            double height = NewImageOverlay.ActualHeight;

            if (width > 0)
            {
                double clipX = width * percentage;
                // Reveal from left to right:
                // Clip the left part of the top image
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
        }
    }
}