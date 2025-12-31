using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class FileGridView : UserControl
    {
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register("ItemsSource", typeof(ObservableCollection<FileGridViewModel>), typeof(FileGridView), new PropertyMetadata(null, OnItemsSourceChanged));

        public ObservableCollection<FileGridViewModel> ItemsSource
        {
            get { return (ObservableCollection<FileGridViewModel>)GetValue(ItemsSourceProperty); }
            set { SetValue(ItemsSourceProperty, value); }
        }

        public event EventHandler<NodeClickedEventArgs> NodeClicked;

        public FileGridView()
        {
            InitializeComponent();
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileGridView control)
            {
                control.FileGridViewItemsControl.ItemsSource = (ObservableCollection<FileGridViewModel>)e.NewValue;
            }
        }

        private void FileGridView_Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is FileGridViewModel item)
            {
                NodeClicked?.Invoke(this, new NodeClickedEventArgs(item.Node));
            }
        }
    }
}
