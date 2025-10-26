using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System;

namespace AssetsManager.Views.Controls.Shared
{
    public partial class SearchBoxControl : UserControl
    {
        private readonly DispatcherTimer searchTimer;

        public static readonly RoutedEvent SearchTextChangedEvent =
            EventManager.RegisterRoutedEvent("SearchTextChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SearchBoxControl));

        public event RoutedEventHandler SearchTextChanged
        {
            add { AddHandler(SearchTextChangedEvent, value); }
            remove { RemoveHandler(SearchTextChangedEvent, value); }
        }

        public string Text
        {
            get { return SearchTextBox.Text; }
            set { SearchTextBox.Text = value; }
        }

        public SearchBoxControl()
        {
            InitializeComponent();

            searchTimer = new DispatcherTimer();
            searchTimer.Interval = TimeSpan.FromMilliseconds(300);
            searchTimer.Tick += SearchTimer_Tick;

            Loaded += SearchBoxControl_Loaded;
            Unloaded += SearchBoxControl_Unloaded;
        }

        private void SearchBoxControl_Loaded(object sender, RoutedEventArgs e)
        {
            SearchTextBox.ApplyTemplate();
            if (SearchTextBox.Template.FindName("ClearTextButton", SearchTextBox) is Button clearButton)
            {
                clearButton.Click += ClearTextButton_Click;
            }
        }

        private void ClearTextButton_Click(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Text = string.Empty;
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            searchTimer.Stop();
            searchTimer.Start();

            if (SearchTextBox.Template.FindName("ClearTextButton", SearchTextBox) is Button clearButton)
            {
                clearButton.Visibility = string.IsNullOrEmpty(SearchTextBox.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void SearchTimer_Tick(object sender, EventArgs e)
        {
            searchTimer.Stop();
            RaiseEvent(new RoutedEventArgs(SearchTextChangedEvent, SearchTextBox.Text));
        }

        private void SearchBoxControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (searchTimer != null)
            {
                searchTimer.Stop();
                searchTimer.Tick -= SearchTimer_Tick;
            }
            if (SearchTextBox.Template.FindName("ClearTextButton", SearchTextBox) is Button clearButton)
            {
                clearButton.Click -= ClearTextButton_Click;
            }
        }
    }
}