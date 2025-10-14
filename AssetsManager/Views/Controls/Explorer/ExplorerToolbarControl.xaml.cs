using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class ExplorerToolbarControl : UserControl
    {
        public event TextChangedEventHandler SearchTextChanged;
        public event RoutedEventHandler CollapseToContainerClicked;
        public event RoutedEventHandler LoadComparisonClicked;
        public event RoutedEventHandler SwitchModeClicked;

        public string SearchText => SearchTextBox.Text;

        public ExplorerToolbarControl()
        {
            InitializeComponent();
            Loaded += ExplorerToolbarControl_Loaded;
        }

        private void ExplorerToolbarControl_Loaded(object sender, RoutedEventArgs e)
        {
            SearchTextBox.ApplyTemplate();
            if (SearchTextBox.Template.FindName("ClearTextButton", SearchTextBox) is Button clearButton)
            {
                clearButton.Click += (s, args) => 
                {
                    SearchTextBox.Text = string.Empty;
                };
            }
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchTextChanged?.Invoke(this, e);

            if (SearchTextBox.Template.FindName("ClearTextButton", SearchTextBox) is Button clearButton)
            {
                clearButton.Visibility = string.IsNullOrEmpty(SearchTextBox.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void CollapseToContainerButton_Click(object sender, RoutedEventArgs e)
        {
            CollapseToContainerClicked?.Invoke(this, e);
        }

        private void LoadComparisonButton_Click(object sender, RoutedEventArgs e)
        {
            LoadComparisonClicked?.Invoke(this, e);
        }

        private void SwitchModeButton_Click(object sender, RoutedEventArgs e)
        {
            SwitchModeClicked?.Invoke(this, e);
        }
    }
}
