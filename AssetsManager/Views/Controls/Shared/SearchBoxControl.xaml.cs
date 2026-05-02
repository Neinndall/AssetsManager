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

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register("Text", typeof(string), typeof(SearchBoxControl), 
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SearchBoxControl control)
            {
                if (control.SearchTextBox.Text != (string)e.NewValue)
                {
                    control.SearchTextBox.Text = (string)e.NewValue;
                }
            }
        }

        public SearchBoxControl()
        {
            InitializeComponent();

            searchTimer = new DispatcherTimer();
            searchTimer.Interval = TimeSpan.FromMilliseconds(300);
            searchTimer.Tick += SearchTimer_Tick;

            Unloaded += SearchBoxControl_Unloaded;
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            Text = SearchTextBox.Text;
            searchTimer.Stop();
            searchTimer.Start();
        }

        private void SearchTimer_Tick(object sender, EventArgs e)
        {
            searchTimer.Stop();
            RaiseEvent(new RoutedEventArgs(SearchTextChangedEvent, Text));
        }

        private void SearchBoxControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (searchTimer != null)
            {
                searchTimer.Stop();
                searchTimer.Tick -= SearchTimer_Tick;
            }
        }
    }
}
