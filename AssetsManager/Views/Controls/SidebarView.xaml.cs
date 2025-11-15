using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace AssetsManager.Views.Controls
{
  public partial class SidebarView : UserControl
  {
    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register("IsExpanded", typeof(bool), typeof(SidebarView),
            new PropertyMetadata(false));

    public bool IsExpanded
    {
      get { return (bool)GetValue(IsExpandedProperty); }
      set { SetValue(IsExpandedProperty, value); }
    }

    public event Action<string> NavigationRequested;

    public SidebarView()
    {
      InitializeComponent();
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
      var button = sender as RadioButton;
      if (button?.Tag is string viewTag)
      {
        NavigationRequested?.Invoke(viewTag);
      }
    }

    private void ToggleExpansion_Click(object sender, RoutedEventArgs e)
    {
      IsExpanded = !IsExpanded;

      if (IsExpanded)
      {
        ExpandSidebar();
      }
      else
      {
        CollapseSidebar();
      }

      ToggleExpansionButton.IsChecked = false;
    }

    private void ExpandSidebar()
    {
      var storyboard = (Storyboard)this.FindResource("ExpandSidebar");
      storyboard.Begin(this);
    }

    private void CollapseSidebar()
    {
      var storyboard = (Storyboard)this.FindResource("CollapseSidebar");
      storyboard.Begin(this);
    }
  }
}
