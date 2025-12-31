using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Help;

namespace AssetsManager.Views
{
    public partial class HelpWindow : Window
    {
        private readonly IServiceProvider _serviceProvider;

        public HelpWindow(IServiceProvider serviceProvider)
        {
            InitializeComponent();
            _serviceProvider = serviceProvider;

            SetupNavigation();
            // Load initial view
            NavigateToView(_serviceProvider.GetRequiredService<AboutView>());
        }

        private void SetupNavigation()
        {
            NavAbout.Checked += NavAbout_Checked;
            NavDocumentation.Checked += NavDocumentation_Checked;
            NavChangelogs.Checked += NavChangelogs_Checked;
            NavBugsReport.Checked += NavBugsReport_Checked;
            NavUpdates.Checked += NavUpdates_Checked;
        }

        private void NavAbout_Checked(object sender, RoutedEventArgs e)
        {
            NavigateToView(_serviceProvider.GetRequiredService<AboutView>());
        }

        private void NavDocumentation_Checked(object sender, RoutedEventArgs e)
        {
            NavigateToView(_serviceProvider.GetRequiredService<DocumentationView>());
        }

        private void NavChangelogs_Checked(object sender, RoutedEventArgs e)
        {
            NavigateToView(_serviceProvider.GetRequiredService<ChangelogsView>());
        }

        private void NavBugsReport_Checked(object sender, RoutedEventArgs e)
        {
            NavigateToView(_serviceProvider.GetRequiredService<BugReportsView>());
        }

        private void NavUpdates_Checked(object sender, RoutedEventArgs e)
        {
            NavigateToView(_serviceProvider.GetRequiredService<UpdatesView>());
        }

        private void NavigateToView(object view)
        {
            HelpContentArea.Content = view;
        }

        private void HelpWindow_Closed(object sender, EventArgs e)
        {
            NavAbout.Checked -= NavAbout_Checked;
            NavDocumentation.Checked -= NavDocumentation_Checked;
            NavChangelogs.Checked -= NavChangelogs_Checked;
            NavBugsReport.Checked -= NavBugsReport_Checked;
            NavUpdates.Checked -= NavUpdates_Checked;
        }
    }
}
