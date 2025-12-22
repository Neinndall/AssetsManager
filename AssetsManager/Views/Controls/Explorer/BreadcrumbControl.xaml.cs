using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class BreadcrumbControl : UserControl
    {
        public event EventHandler<NodeClickedEventArgs> NodeClicked;

        public BreadcrumbControl()
        {
            InitializeComponent();
            DataContext = this;
            Nodes = new ObservableCollection<FileSystemNodeModel>();
        }

        public ObservableCollection<FileSystemNodeModel> Nodes
        {
            get { return (ObservableCollection<FileSystemNodeModel>)GetValue(NodesProperty); }
            set { SetValue(NodesProperty, value); }
        }

        public static readonly DependencyProperty NodesProperty =
            DependencyProperty.Register("Nodes", typeof(ObservableCollection<FileSystemNodeModel>), typeof(BreadcrumbControl), new PropertyMetadata(new ObservableCollection<FileSystemNodeModel>()));

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is FileSystemNodeModel node)
            {
                NodeClicked?.Invoke(this, new NodeClickedEventArgs(node));
            }
        }
    }
}
