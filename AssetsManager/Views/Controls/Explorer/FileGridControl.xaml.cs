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
        private ObservableRangeCollection<FileGridViewModel> _allItems = new ObservableRangeCollection<FileGridViewModel>();
        public ObservableRangeCollection<FileGridViewModel> DisplayItems { get; } = new ObservableRangeCollection<FileGridViewModel>();

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register("ItemsSource", typeof(ObservableRangeCollection<FileGridViewModel>), typeof(FileGridControl), new PropertyMetadata(null, OnItemsSourceChanged));

        public ObservableRangeCollection<FileGridViewModel> ItemsSource
        {
            get { return (ObservableRangeCollection<FileGridViewModel>)GetValue(ItemsSourceProperty); }
            set { SetValue(ItemsSourceProperty, value); }
        }

        public event EventHandler<NodeClickedEventArgs> NodeClicked;
        public event EventHandler<SelectionActionEventArgs> SelectionActionRequested;

        private string _currentFilter = "All";

        public FileGridControl()
        {
            InitializeComponent();
            FileGridListBox.ItemsSource = DisplayItems;
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileGridControl control)
            {
                var newItems = (ObservableRangeCollection<FileGridViewModel>)e.NewValue;
                control._allItems = newItems ?? new ObservableRangeCollection<FileGridViewModel>();
                
                // Re-apply current filter when folder or search changes
                control.ApplyFilter(control._currentFilter);
            }
        }

        private void FileGridListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Sync selection state to ViewModels for multi-select consistency
            foreach (FileGridViewModel item in e.RemovedItems) item.IsSelected = false;
            foreach (FileGridViewModel item in e.AddedItems) item.IsSelected = true;

            UpdateActionBarVisibility();
        }

        private void UpdateActionBarVisibility()
        {
            int selectedCount = FileGridListBox.SelectedItems.Count;
            ActionBarBorder.Visibility = selectedCount > 1 ? Visibility.Visible : Visibility.Collapsed;
            SelectedCountText.Text = $"{selectedCount} items selected";
        }

        private void FileGridControl_Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem itemContainer && itemContainer.DataContext is FileGridViewModel item)
            {
                // We use Preview event to intercept the click before ListBox selection logic
                if (InteractionHelper.IsPrimaryActionIntent())
                {
                    // Primary action = Navigation
                    NodeClicked?.Invoke(this, new NodeClickedEventArgs(item.Node));
                    
                    // Mark as handled to prevent ListBox from selecting/focusing
                    e.Handled = true;
                }
                // If it's NOT primary (Ctrl/Shift), we DON'T set Handled=true 
                // so the ListBox can perform its normal multi-selection logic.
            }
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string filterType)
            {
                _currentFilter = filterType;
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
                    "Images" => SupportedFileTypes.Images.Contains(item.Node.Extension) || SupportedFileTypes.Textures.Contains(item.Node.Extension) || SupportedFileTypes.VectorImages.Contains(item.Node.Extension),
                    "Audio" => SupportedFileTypes.AudioBank.Contains(item.Node.Extension) || SupportedFileTypes.Media.Contains(item.Node.Extension),
                    "3D" => SupportedFileTypes.Viewer3D.Contains(item.Node.Extension),
                    "Data" => SupportedFileTypes.Bin.Contains(item.Node.Extension) || SupportedFileTypes.Json.Contains(item.Node.Extension) || SupportedFileTypes.StringTable.Contains(item.Node.Extension) || SupportedFileTypes.PlainText.Contains(item.Node.Extension) || SupportedFileTypes.Css.Contains(item.Node.Extension) || SupportedFileTypes.JavaScript.Contains(item.Node.Extension) || SupportedFileTypes.Troybin.Contains(item.Node.Extension) || SupportedFileTypes.Preload.Contains(item.Node.Extension),
                    _ => true
                };
            }).ToList();

            DisplayItems.ReplaceRange(filtered);
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
                SelectionActionRequested?.Invoke(this, new SelectionActionEventArgs(action, selectedNodes));
            }
        }
    }

    public class SelectionActionEventArgs : EventArgs
    {
        public string Action { get; }
        public List<FileSystemNodeModel> Nodes { get; }
        public SelectionActionEventArgs(string action, List<FileSystemNodeModel> nodes)
        {
            Action = action;
            Nodes = nodes;
        }
    }
}

