using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;

namespace AssetsManager.Views.Controls.Shared
{
    public partial class FindInDocumentControl : UserControl
    {
        public static readonly DependencyProperty TargetTextEditorProperty =
            DependencyProperty.Register("TargetTextEditor", typeof(TextEditor), typeof(FindInDocumentControl), new PropertyMetadata(null));

        public TextEditor TargetTextEditor
        {
            get { return (TextEditor)GetValue(TargetTextEditorProperty); }
            set { SetValue(TargetTextEditorProperty, value); }
        }

        public static readonly RoutedEvent CloseEvent = EventManager.RegisterRoutedEvent(
            "Close", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(FindInDocumentControl));

        public event RoutedEventHandler Close
        {
            add { AddHandler(CloseEvent, value); }
            remove { RemoveHandler(CloseEvent, value); }
        }

        public string SearchText => SearchTextBox.Text;

        public FindInDocumentControl()
        {
            InitializeComponent();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
        }

        public void FocusInput()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Keyboard.Focus(SearchTextBox);
                SearchTextBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            Find(true);
        }

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            Find(false);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new RoutedEventArgs(CloseEvent));
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Find(true);
            }
            else if (e.Key == Key.Escape)
            {
                RaiseEvent(new RoutedEventArgs(CloseEvent));
            }
        }

        private void Find(bool forward)
        {
            string searchText = SearchTextBox.Text;
            if (string.IsNullOrEmpty(searchText))
                return;

            if (TargetTextEditor == null)
            {
                MessageBox.Show("Error: TargetTextEditor is null. Cannot perform search.", "Search Error");
                return;
            }
            if (TargetTextEditor.Document == null)
                return;

            int start = forward ? TargetTextEditor.CaretOffset : TargetTextEditor.SelectionStart - 1;
            if (start < 0) start = 0;

            int index = -1;
            if (forward)
            {
                index = TargetTextEditor.Document.Text.IndexOf(searchText, start, StringComparison.OrdinalIgnoreCase);
                if (index == -1) // Wrap around
                {
                    index = TargetTextEditor.Document.Text.IndexOf(searchText, 0, StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                if (start >= TargetTextEditor.Document.Text.Length)
                {
                    start = TargetTextEditor.Document.Text.Length - 1;
                }
                index = TargetTextEditor.Document.Text.LastIndexOf(searchText, start, StringComparison.OrdinalIgnoreCase);
                if (index == -1) // Wrap around
                {
                    index = TargetTextEditor.Document.Text.LastIndexOf(searchText, TargetTextEditor.Document.Text.Length - 1, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (index != -1)
            {
                TargetTextEditor.Select(index, searchText.Length);
                var location = TargetTextEditor.Document.GetLocation(index);
                TargetTextEditor.ScrollTo(location.Line, location.Column);
            }
        }
    }
}
