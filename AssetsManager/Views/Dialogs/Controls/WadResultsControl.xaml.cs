using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class WadResultsControl : UserControl
    {
        public event Action<SerializableChunkDiff, bool> ItemVisibilityChanged;

        public event Action<ChunkDiffType> DiffTypeChanged;

        public event Action FilterApplied;

        public WadComparisonResultWindow ParentWindow { get; set; }

        public ChunkDiffType SelectedDiffType { get; private set; } = ChunkDiffType.New;

        private readonly ObservableRangeCollection<WadResultItemModel> _displayItems = new();
        private List<WadResultItemModel> _allItems = new();
        private string _searchText = string.Empty;

        public WadResultsControl()
        {
            InitializeComponent();
            ResultsListBox.ItemsSource = _displayItems;
            FileTypeFilter.FilterChanged += FileTypeFilter_FilterChanged;
        }

        public void SetItems(List<WadResultItemModel> items)
        {
            _allItems = items ?? new List<WadResultItemModel>();
            CountText.Text = _allItems.Count.ToString();
            RetryFailedButton.Visibility = SelectedDiffType == ChunkDiffType.New && _allItems.Any(i => i.IsFailed)
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateActionButtonsEnabled();
            ApplyFilter(FileTypeFilter.SelectedFilter);
        }

        private void DiffTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DiffTypeComboBox.SelectedItem is ComboBoxItem { Tag: ChunkDiffType type })
            {
                SelectedDiffType = type;
                RetryFailedButton.Visibility = type == ChunkDiffType.New && _allItems.Any(i => i.IsFailed)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                UpdateActionButtonsEnabled();
                DiffTypeChanged?.Invoke(type);
            }
        }

        private void UpdateActionButtonsEnabled()
        {
            bool isNew = SelectedDiffType == ChunkDiffType.New;
            if (ExtractButton != null) ExtractButton.IsEnabled = isNew;
            if (SaveButton != null) SaveButton.IsEnabled = isNew;
        }

        public List<WadResultItemModel> GetAllItems() => _allItems;

        public void UpdateRetryButton()
        {
            RetryFailedButton.Visibility = _allItems.Any(i => i.IsFailed) ? Visibility.Visible : Visibility.Collapsed;
        }

        public void LoadRealizedItems()
        {
            foreach (var entry in _displayItems)
            {
                if (ResultsListBox.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem { IsLoaded: true })
                {
                    ItemVisibilityChanged?.Invoke(entry.Diff, true);
                }
            }
        }

        private void GridItem_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: WadResultItemModel item })
            {
                ItemVisibilityChanged?.Invoke(item.Diff, true);
            }
        }

        private void GridItem_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: WadResultItemModel item })
            {
                ItemVisibilityChanged?.Invoke(item.Diff, false);
            }
        }

        private void FileTypeFilter_FilterChanged(string filterType)
        {
            ApplyFilter(filterType);
        }

        private void ApplyFilter(string type)
        {
            ClearSelection();

            var filtered = string.IsNullOrWhiteSpace(_searchText)
                ? _allItems.ToList()
                : _allItems.Where(item =>
                    item.Diff.FileName.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Diff.Path.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (type != "All")
            {
                filtered = filtered.Where(item => SupportedFileTypes.MatchesFilter(type, item.Diff.Path)).ToList();
            }

            _displayItems.ReplaceRange(filtered);
            EmptyStateBorder.Visibility = _displayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FilterApplied?.Invoke();
        }

        public void SetSearchText(string text)
        {
            _searchText = text ?? string.Empty;
            ApplyFilter(FileTypeFilter.SelectedFilter);
        }

        public int VisibleCount => _displayItems.Count;

        public int TotalCount => _allItems.Count;

        private void ResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (var item in e.RemovedItems.OfType<WadResultItemModel>())
            {
                item.IsMultiSelected = false;
            }

            foreach (var item in ResultsListBox.SelectedItems.OfType<WadResultItemModel>())
            {
                item.IsMultiSelected = true;
            }

            int count = ResultsListBox.SelectedItems.Count;
            SelectedCountText.Text = count == 1 ? "1 selected" : $"{count} selected";
            ActionBarBorder.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateOpenFolderButtonState();
        }

        private void UpdateOpenFolderButtonState()
        {
            if (OpenFolderButton == null) return;
            OpenFolderButton.Visibility = ResultsListBox.SelectedItems
                .OfType<WadResultItemModel>()
                .Any(i => !string.IsNullOrEmpty(i.OutputPath))
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        public void RefreshOpenFolderButton()
        {
            UpdateOpenFolderButtonState();
        }

        private void RetryFailed_Click(object sender, RoutedEventArgs e)
        {
            var failed = _allItems.Where(i => i.IsFailed).ToList();
            if (failed.Count > 0)
            {
                ParentWindow?.HandleResultsAction("Retry", failed);
            }
        }

        private void ActionBar_Action_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string action)
            {
                if (action == "Close")
                {
                    ClearSelection();
                    return;
                }

                var selected = ResultsListBox.SelectedItems.OfType<WadResultItemModel>().ToList();
                if (selected.Count > 0)
                {
                    ParentWindow?.HandleResultsAction(action, selected);
                }
            }
        }

        private void ClearSelection()
        {
            foreach (var item in ResultsListBox.SelectedItems.OfType<WadResultItemModel>())
            {
                item.IsMultiSelected = false;
            }
            ResultsListBox.UnselectAll();
            ActionBarBorder.Visibility = Visibility.Collapsed;
        }
    }
}
