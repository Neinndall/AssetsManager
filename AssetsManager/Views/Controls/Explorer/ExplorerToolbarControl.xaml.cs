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
        public event RoutedPropertyChangedEventHandler<bool> BreadcrumbVisibilityChanged;
        public event RoutedPropertyChangedEventHandler<bool> SortStateChanged;
        public event RoutedPropertyChangedEventHandler<bool> ViewModeChanged; // True for Grid, False for Preview

        public string SearchText => SearchTextBox.Text;

        public bool IsSortButtonVisible
        {
            get => SortToggleButton.Visibility == Visibility.Visible;
            set => SortToggleButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public bool IsBreadcrumbChecked => BreadcrumbToggleButton.IsChecked ?? false;

        public ExplorerToolbarControl()
        {
            InitializeComponent();
        }

        public void SetViewMode(bool isGridMode)
        {
            GridViewToggleButton.IsChecked = isGridMode;
        }

        private void GridViewToggle_Click(object sender, RoutedEventArgs e)
        {
            ViewModeChanged?.Invoke(this, new RoutedPropertyChangedEventArgs<bool>(!(GridViewToggleButton.IsChecked ?? false), GridViewToggleButton.IsChecked ?? false));
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchTextChanged?.Invoke(this, e);
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

        private void BreadcrumbToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton toggleButton)
            {
                BreadcrumbVisibilityChanged?.Invoke(this, new RoutedPropertyChangedEventArgs<bool>(!(toggleButton.IsChecked ?? false), toggleButton.IsChecked ?? false));
            }
        }

        private void SortToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton toggleButton)
            {
                SortStateChanged?.Invoke(this, new RoutedPropertyChangedEventArgs<bool>(!(toggleButton.IsChecked ?? false), toggleButton.IsChecked ?? false));
            }
        }
    }
}