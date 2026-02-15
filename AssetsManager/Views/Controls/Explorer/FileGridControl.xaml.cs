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
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileGridControl control)
            {
                var newItems = (ObservableRangeCollection<FileGridViewModel>)e.NewValue;
                control.FileGridListBox.ItemsSource = newItems;
                
                // Re-apply current filter when folder or search changes
                if (newItems != null)
                {
                    control.ApplyFilter(control._currentFilter);
                }
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
            if (sender is FrameworkElement element && element.DataContext is FileGridViewModel item)
            {
                // Uses the global interaction rules
                if (InteractionHelper.IsPrimaryActionIntent())
                {
                    NodeClicked?.Invoke(this, new NodeClickedEventArgs(item.Node));
                }
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
            if (ItemsSource == null) return;

            foreach (var item in ItemsSource)
            {
                if (type == "All") { item.Node.IsVisible = true; continue; }
                
                bool match = type switch
                {
                    "Images" => SupportedFileTypes.Images.Contains(item.Node.Extension) || SupportedFileTypes.Textures.Contains(item.Node.Extension) || SupportedFileTypes.VectorImages.Contains(item.Node.Extension),
                    "Audio" => SupportedFileTypes.AudioBank.Contains(item.Node.Extension) || SupportedFileTypes.Media.Contains(item.Node.Extension),
                    "3D" => SupportedFileTypes.Viewer3D.Contains(item.Node.Extension),
                    "Data" => SupportedFileTypes.Bin.Contains(item.Node.Extension) || SupportedFileTypes.Json.Contains(item.Node.Extension) || SupportedFileTypes.StringTable.Contains(item.Node.Extension) || SupportedFileTypes.PlainText.Contains(item.Node.Extension) || SupportedFileTypes.Css.Contains(item.Node.Extension) || SupportedFileTypes.JavaScript.Contains(item.Node.Extension) || SupportedFileTypes.Troybin.Contains(item.Node.Extension) || SupportedFileTypes.Preload.Contains(item.Node.Extension),
                    _ => true
                };
                item.Node.IsVisible = match;
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

