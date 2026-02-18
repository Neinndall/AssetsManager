using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class WadResultsTreeControl : UserControl, INotifyPropertyChanged
    {
        public event RoutedPropertyChangedEventHandler<object> SelectedItemChanged;
        public event TextChangedEventHandler SearchTextChanged;
        public event RoutedEventHandler WadContextMenuOpening;
        public object SelectedItem => resultsTreeView.SelectedItem;

        public MenuItem ViewDifferencesMenuItem => (this.FindResource("WadDiffContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ViewDifferencesMenuItem");

        public static readonly RoutedEvent ViewDifferencesClickEvent = EventManager.RegisterRoutedEvent(
            nameof(ViewDifferencesClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(WadResultsTreeControl));

        public event RoutedEventHandler ViewDifferencesClick
        {
            add { AddHandler(ViewDifferencesClickEvent, value); }
            remove { RemoveHandler(ViewDifferencesClickEvent, value); }
        }

        public WadResultsTreeControl()
        {
            InitializeComponent();
            Loaded += WadResultsTreeControl_Loaded;
            Unloaded += WadResultsTreeControl_Unloaded;
        }

        public void Cleanup()
        {
            Loaded -= WadResultsTreeControl_Loaded;
            Unloaded -= WadResultsTreeControl_Unloaded;
            resultsTreeView.SelectedItemChanged -= ResultsTreeView_SelectedItemChanged;
            searchTextBox.TextChanged -= SearchTextBox_TextChanged;
            resultsTreeView.ItemsSource = null;
        }

        private void WadResultsTreeControl_Loaded(object sender, RoutedEventArgs e)
        {
            resultsTreeView.SelectedItemChanged += ResultsTreeView_SelectedItemChanged;
            searchTextBox.TextChanged += SearchTextBox_TextChanged;
        }

        private void WadResultsTreeControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchTextChanged?.Invoke(this, e);
        }

        private void ResultsTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            SelectedItemChanged?.Invoke(this, e);
        }

        private void ViewDifferences_Click(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new RoutedEventArgs(ViewDifferencesClickEvent, SelectedItem));
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
            WadContextMenuOpening?.Invoke(this, e);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
