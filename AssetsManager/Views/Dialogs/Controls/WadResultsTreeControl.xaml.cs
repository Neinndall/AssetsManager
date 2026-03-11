using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class WadResultsTreeControl : UserControl, INotifyPropertyChanged
    {
        public WadComparisonResultWindow ParentWindow { get; set; }
        public WadResultsTreeModel ViewModel => DataContext as WadResultsTreeModel;
        public object SelectedItem => resultsTreeView.SelectedItem;

        public MenuItem ViewDifferencesMenuItem => (this.FindResource("WadDiffContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ViewDifferencesMenuItem");

        public WadResultsTreeControl()
        {
            InitializeComponent();
        }

        public void Cleanup()
        {
            resultsTreeView.SelectedItemChanged -= ResultsTreeView_SelectedItemChanged;
            searchTextBox.TextChanged -= SearchTextBox_TextChanged;
            resultsTreeView.ItemsSource = null;
            ParentWindow = null;
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ParentWindow?.HandleSearchTextChanged(searchTextBox.Text);
        }

        private void ResultsTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            ParentWindow?.HandleTreeSelectionChanged(e.NewValue);
        }

        private void ViewDifferences_Click(object sender, RoutedEventArgs e)
        {
            ParentWindow?.HandleViewDifferencesRequest();
        }

        public void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (VisualUpwardSearch(e.OriginalSource as DependencyObject) is TreeViewItem treeViewItem)
            {
                treeViewItem.IsSelected = true;
                e.Handled = true;
            }
        }

        private static TreeViewItem VisualUpwardSearch(DependencyObject source)
        {
            while (source != null && !(source is TreeViewItem))
            {
                source = VisualTreeHelper.GetParent(source);
            }
            return source as TreeViewItem;
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            ParentWindow?.HandleTreeContextMenuOpening();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
