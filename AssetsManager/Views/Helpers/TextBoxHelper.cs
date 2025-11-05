using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views.Helpers
{
    public static class TextBoxHelper
    {
        public static readonly DependencyProperty ShowClearButtonProperty =
            DependencyProperty.RegisterAttached(
                "ShowClearButton",
                typeof(bool),
                typeof(TextBoxHelper),
                new PropertyMetadata(false, OnShowClearButtonChanged));

        public static bool GetShowClearButton(DependencyObject obj)
        {
            return (bool)obj.GetValue(ShowClearButtonProperty);
        }

        public static void SetShowClearButton(DependencyObject obj, bool value)
        {
            obj.SetValue(ShowClearButtonProperty, value);
        }

        private static void OnShowClearButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBox textBox)
            {
                if ((bool)e.NewValue)
                {
                    textBox.Loaded += TextBox_Loaded;
                    textBox.Unloaded += TextBox_Unloaded;
                }
                else
                {
                    textBox.Loaded -= TextBox_Loaded;
                    textBox.Unloaded -= TextBox_Unloaded;
                }
            }
        }

        private static void TextBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                UpdateClearButtonVisibility(textBox);
                textBox.TextChanged += TextBox_TextChanged;

                if (GetClearButton(textBox) is Button clearButton)
                {
                    clearButton.Click += ClearButton_Click;
                }
            }
        }

        private static void TextBox_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.TextChanged -= TextBox_TextChanged;
                if (GetClearButton(textBox) is Button clearButton)
                {
                    clearButton.Click -= ClearButton_Click;
                }
            }
        }

        private static void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                UpdateClearButtonVisibility(textBox);
            }
        }

        private static void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if ((e.Source as FrameworkElement)?.TemplatedParent is TextBox textBox)
            {
                textBox.Text = string.Empty;
            }
        }

        private static void UpdateClearButtonVisibility(TextBox textBox)
        {
            if (GetClearButton(textBox) is Button clearButton)
            {
                clearButton.Visibility = string.IsNullOrEmpty(textBox.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private static Button GetClearButton(TextBox textBox)
        {
            return textBox.Template.FindName("ClearTextButton", textBox) as Button;
        }
    }
}
