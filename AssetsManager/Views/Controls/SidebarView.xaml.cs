using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using AssetsManager.Views.Models.Controls;

namespace AssetsManager.Views.Controls
{
    public partial class SidebarView : UserControl
    {
        private readonly SidebarViewModel _viewModel;
        public MainWindow ParentWindow { get; set; }

        public SidebarView()
        {
            InitializeComponent();
            _viewModel = new SidebarViewModel();
            DataContext = _viewModel;
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as RadioButton;
            if (button?.Tag is string viewTag)
            {
                ParentWindow?.OnSidebarNavigationRequested(viewTag);
            }
        }

        private void ToggleExpansion_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.IsExpanded = !_viewModel.IsExpanded;

            if (_viewModel.IsExpanded)
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

        public void SelectNavigationItem(string viewTag)
        {
            foreach (var child in ((StackPanel)JsonDiffToolButton.Parent).Children)
            {
                if (child is RadioButton rb && rb.Tag?.ToString() == viewTag)
                {
                    rb.IsChecked = true;
                    return;
                }
            }
        }
    }
}
