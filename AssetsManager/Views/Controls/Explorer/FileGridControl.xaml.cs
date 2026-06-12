using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class FileGridControl : UserControl
    {
        public FilePreviewerControl ParentPreviewer { get; set; }
        public FileGridModel ViewModel => DataContext as FileGridModel;

        private ObservableRangeCollection<FileGridViewModel> _allItems = new ObservableRangeCollection<FileGridViewModel>();
        public ObservableRangeCollection<FileGridViewModel> DisplayItems { get; } = new ObservableRangeCollection<FileGridViewModel>();

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register("ItemsSource", typeof(ObservableRangeCollection<FileGridViewModel>), typeof(FileGridControl), new PropertyMetadata(null, OnItemsSourceChanged));

        public ObservableRangeCollection<FileGridViewModel> ItemsSource
        {
            get { return (ObservableRangeCollection<FileGridViewModel>)GetValue(ItemsSourceProperty); }
            set { SetValue(ItemsSourceProperty, value); }
        }

        public FileGridControl()
        {
            InitializeComponent();
            FileGridListBox.ItemsSource = DisplayItems;
        }

        private bool _isUpdatingItemsSource = false;

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileGridControl control)
            {
                control._isUpdatingItemsSource = true;
                try
                {
                    var newItems = (ObservableRangeCollection<FileGridViewModel>)e.NewValue;
                    control._allItems = newItems ?? new ObservableRangeCollection<FileGridViewModel>();
                    
                    // Re-apply current filter when folder or search changes
                    if (control.ViewModel != null)
                    {
                        control.ApplyFilter(control.ViewModel.CurrentFilter);
                    }
                    else
                    {
                        control.DisplayItems.ReplaceRange(control._allItems);
                    }
                }
                finally
                {
                    control._isUpdatingItemsSource = false;
                }
            }
        }

        private void FileGridListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingItemsSource) return;

            // Sync selection state to ViewModels for multi-select consistency
            foreach (FileGridViewModel item in e.RemovedItems)
            {
                item.IsSelected = false;
                item.IsMultiSelected = false;
            }
            foreach (FileGridViewModel item in e.AddedItems)
            {
                item.IsSelected = true;
            }

            // Sync IsMultiSelected if multiple items are selected OR if the user is using CTRL to select
            bool isMulti = FileGridListBox.SelectedItems.Count > 1 || SelectionBehavior.IsMultiSelectIntent();
            foreach (FileGridViewModel item in DisplayItems)
            {
                item.IsMultiSelected = isMulti && FileGridListBox.SelectedItems.Contains(item);
            }

            if (ViewModel != null)
            {
                ViewModel.SelectedCount = FileGridListBox.SelectedItems.Count;
            }
        }



        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string filterType && ViewModel != null)
            {
                ViewModel.CurrentFilter = filterType;
                ApplyFilter(filterType);
            }
        }

        private void ApplyFilter(string type)
        {
            if (_allItems == null) return;

            if (type == "All")
            {
                DisplayItems.ReplaceRange(_allItems);
                return;
            }

            var filtered = _allItems.Where(item =>
            {
                return type switch
                {
                    "Images" => SupportedFileTypes.IsImage(item.Node.Extension),
                    "Audio" => SupportedFileTypes.IsAudio(item.Node.Extension),
                    "3D" => SupportedFileTypes.Is3D(item.Node.Extension),
                    "Data" => SupportedFileTypes.IsText(item.Node.Extension),
                    _ => true
                };
            }).ToList();

            DisplayItems.ReplaceRange(filtered);
        }

        private void SelectionBehavior_PrimaryAction(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is ListBoxItem item && item.DataContext is FileGridViewModel viewModel)
            {
                ParentPreviewer?.HandleNodeClicked(viewModel.Node);
            }
        }

        private void ActionBar_Action_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string action)
            {
                if (action == "Close")
                {
                    FileGridListBox.UnselectAll();
                    return;
                }

                var selectedNodes = FileGridListBox.SelectedItems.Cast<FileGridViewModel>().Select(i => i.Node).ToList();
                ParentPreviewer?.HandleSelectionActionRequested(action, selectedNodes);
            }
        }
    }
}
