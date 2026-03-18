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

        public List<SerializableChunkDiff> SelectedDiffs
        {
            get
            {
                var selected = new List<SerializableChunkDiff>();
                if (ViewModel?.WadGroups == null) return selected;

                foreach (var wad in ViewModel.WadGroups)
                {
                    foreach (var typeGroup in wad.Types)
                    {
                        foreach (var diff in typeGroup.Diffs)
                        {
                            if (diff.IsMultiSelected) selected.Add(diff);
                        }
                    }
                }

                // If multi-selection is empty, use the single selected item if it's a file
                if (selected.Count == 0 && resultsTreeView.SelectedItem is SerializableChunkDiff singleDiff)
                {
                    selected.Add(singleDiff);
                }
                else if (selected.Count > 0 && resultsTreeView.SelectedItem is SerializableChunkDiff activeDiff && !selected.Contains(activeDiff))
                {
                    // Ensure the primary clicked item is included
                    selected.Insert(0, activeDiff);
                }

                return selected;
            }
        }

        public MenuItem ViewDifferencesMenuItem => (this.FindResource("WadDiffContextMenu") as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ViewDifferencesMenuItem");

        public WadResultsTreeControl()
        {
            InitializeComponent();
        }

        public void Cleanup()
        {
            if (resultsTreeView != null)
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
            ParentWindow?.HandleTreeSelectionChanged(e.NewValue as SerializableChunkDiff);
        }

        private void ViewDifferences_Click(object sender, RoutedEventArgs e)
        {
            var selectedDiffs = SelectedDiffs;
            if (selectedDiffs.Count > 1)
            {
                ParentWindow?.HandleBatchViewDifferencesRequest(selectedDiffs);
            }
            else
            {
                ParentWindow?.HandleViewDifferencesRequest();
            }
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            ParentWindow?.HandleTreeContextMenuOpening();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
