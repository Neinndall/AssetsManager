using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils;

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

        public FileGridControl()
        {
            InitializeComponent();
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileGridControl control)
            {
                control.FileGridViewItemsControl.ItemsSource = (ObservableRangeCollection<FileGridViewModel>)e.NewValue;
            }
        }

        private void FileGridControl_Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is FileGridViewModel item)
            {
                NodeClicked?.Invoke(this, new NodeClickedEventArgs(item.Node));
            }
        }
    }
}