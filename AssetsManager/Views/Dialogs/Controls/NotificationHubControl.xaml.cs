using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AssetsManager.Views.Models.Notifications;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class NotificationHubControl : UserControl
    {
        public NotificationHubModel ViewModel => DataContext as NotificationHubModel;

        private bool _isDragging;
        private Point _startPoint;

        public NotificationHubControl()
        {
            InitializeComponent();
        }

        private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsOpen = false;
        }

        // --- Drag Logic ---

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var border = sender as FrameworkElement;
            if (border == null) return;

            _isDragging = true;
            _startPoint = e.GetPosition(this);
            border.CaptureMouse();
        }

        private void Header_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                // We access the transform directly via the field name defined in XAML
                // I'll ensure the Transform is named 'PanelTranslateTransform' in XAML
                if (PanelTranslateTransform != null)
                {
                    var currentPoint = e.GetPosition(this);
                    var offset = currentPoint - _startPoint;

                    PanelTranslateTransform.X += offset.X;
                    PanelTranslateTransform.Y += offset.Y;

                    // Update start point to prevent acceleration
                    _startPoint = currentPoint;
                }
            }
        }

        private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            var border = sender as FrameworkElement;
            border?.ReleaseMouseCapture();
        }

        // --- Buttons ---

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) ViewModel.IsOpen = false;
            ResetPosition();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) ViewModel.IsOpen = false;
            // Optional: Don't reset position if just minimizing?
        }

        private void ResetPosition()
        {
             if (PanelTranslateTransform != null)
             {
                 PanelTranslateTransform.X = 0;
                 PanelTranslateTransform.Y = 0;
             }
        }

        private void MarkAllRead_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.MarkAllRead();
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ClearAll();
        }

        private void RemoveNotification_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is NotificationModel note)
            {
                ViewModel?.RemoveNotification(note);
            }
        }
    }
}
