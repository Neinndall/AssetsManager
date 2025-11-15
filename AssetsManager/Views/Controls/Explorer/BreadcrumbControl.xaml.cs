using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Models;

namespace AssetsManager.Views.Controls.Explorer
{
  public class NodeClickedEventArgs : RoutedEventArgs
  {
    public FileSystemNodeModel Node { get; }

    public NodeClickedEventArgs(RoutedEvent routedEvent, FileSystemNodeModel node) : base(routedEvent)
    {
      Node = node;
    }
  }

  public partial class BreadcrumbControl : UserControl
  {
    public delegate void NodeClickedEventHandler(object sender, NodeClickedEventArgs e);

    public static readonly RoutedEvent NodeClickedEvent = EventManager.RegisterRoutedEvent(
        "NodeClicked", RoutingStrategy.Bubble, typeof(NodeClickedEventHandler), typeof(BreadcrumbControl));

    public event NodeClickedEventHandler NodeClicked
    {
      add { AddHandler(NodeClickedEvent, value); }
      remove { RemoveHandler(NodeClickedEvent, value); }
    }

    public BreadcrumbControl()
    {
      InitializeComponent();
      DataContext = this;
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
        var args = new NodeClickedEventArgs(NodeClickedEvent, node);
        RaiseEvent(args);
      }
    }
  }
}
