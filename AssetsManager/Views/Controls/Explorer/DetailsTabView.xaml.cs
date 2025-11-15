using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views.Controls.Explorer
{
  public partial class DetailsTabView : UserControl
  {
    public static readonly RoutedEvent CloseRequestedEvent =
        EventManager.RegisterRoutedEvent(nameof(CloseRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DetailsTabView));

    public event RoutedEventHandler CloseRequested
    {
      add { AddHandler(CloseRequestedEvent, value); }
      remove { RemoveHandler(CloseRequestedEvent, value); }
    }

    public DetailsTabView()
    {
      InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
      RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, this));
    }
  }
}
