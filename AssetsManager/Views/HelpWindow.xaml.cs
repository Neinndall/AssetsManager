using System;
using System.Windows;
using AssetsManager.Views.Help;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views
{
    public partial class HelpWindow : HudWindow
    {
        private readonly AboutView _aboutView;
        private readonly DocumentationView _documentationView;
        private readonly ChangelogsView _changelogsView;
        private readonly BugReportsView _bugReportsView;
        private readonly UpdatesView _updatesView;

        public HelpWindow(
            AboutView aboutView,
            DocumentationView documentationView,
            ChangelogsView changelogsView,
            BugReportsView bugReportsView,
            UpdatesView updatesView)
        {
            InitializeComponent();
            _aboutView = aboutView;
            _documentationView = documentationView;
            _changelogsView = changelogsView;
            _bugReportsView = bugReportsView;
            _updatesView = updatesView;

            SetupNavigation();
            // Load initial view
            NavigateToView(_aboutView);
            Closed += HelpWindow_Closed;
        }

        private void SetupNavigation()
        {
            NavAbout.Checked += NavAbout_Checked;
            NavDocumentation.Checked += NavDocumentation_Checked;
            NavChangelogs.Checked += NavChangelogs_Checked;
            NavBugsReport.Checked += NavBugsReport_Checked;
            NavUpdates.Checked += NavUpdates_Checked;
        }

        private void NavAbout_Checked(object sender, RoutedEventArgs e) => NavigateToView(_aboutView);
        private void NavDocumentation_Checked(object sender, RoutedEventArgs e) => NavigateToView(_documentationView);
        private void NavChangelogs_Checked(object sender, RoutedEventArgs e) => NavigateToView(_changelogsView);
        private void NavBugsReport_Checked(object sender, RoutedEventArgs e) => NavigateToView(_bugReportsView);
        private void NavUpdates_Checked(object sender, RoutedEventArgs e) => NavigateToView(_updatesView);

        private void NavigateToView(object view)
        {
            HelpContentArea.Content = view;
        }

        private void HelpWindow_Closed(object sender, EventArgs e)
        {
            Closed -= HelpWindow_Closed;
            NavAbout.Checked -= NavAbout_Checked;
            NavDocumentation.Checked -= NavDocumentation_Checked;
            NavChangelogs.Checked -= NavChangelogs_Checked;
            NavBugsReport.Checked -= NavBugsReport_Checked;
            NavUpdates.Checked -= NavUpdates_Checked;
        }
    }
}
