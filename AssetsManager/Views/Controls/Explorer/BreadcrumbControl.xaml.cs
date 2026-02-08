using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils;

namespace AssetsManager.Views.Controls.Explorer
{
    public class NodeClickedEventArgs : EventArgs
    {
        public FileSystemNodeModel Node { get; }

        public NodeClickedEventArgs(FileSystemNodeModel node)
        {
            Node = node;
        }
    }

    public partial class BreadcrumbControl : UserControl
    {
        public event EventHandler<NodeClickedEventArgs> NodeClicked;

        public BreadcrumbControl()
        {
            InitializeComponent();
            DataContext = this;
            Nodes = new ObservableRangeCollection<FileSystemNodeModel>();
        }

        public ObservableRangeCollection<FileSystemNodeModel> Nodes
        {
            get { return (ObservableRangeCollection<FileSystemNodeModel>)GetValue(NodesProperty); }
            set { SetValue(NodesProperty, value); }
        }

        public static readonly DependencyProperty NodesProperty =
            DependencyProperty.Register("Nodes", typeof(ObservableRangeCollection<FileSystemNodeModel>), typeof(BreadcrumbControl), new PropertyMetadata(new ObservableRangeCollection<FileSystemNodeModel>()));

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is FileSystemNodeModel node)
            {
                NodeClicked?.Invoke(this, new NodeClickedEventArgs(node));
            }
        }
    }
}
