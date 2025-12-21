using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class MonitorDashboardControl : UserControl
    {
        public MonitorService MonitorService { get; set; }
        public PbeStatusService PbeStatusService { get; set; }
        public AppSettings AppSettings { get; set; }
        public VersionService VersionService { get; set; }

        public MonitorDashboardControl()
        {
            InitializeComponent();
            this.Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext == null && MonitorService != null && PbeStatusService != null && AppSettings != null && VersionService != null)
            {
                DataContext = new MonitorDashboardModel(MonitorService, PbeStatusService, AppSettings, VersionService);
            }
        }
    }
}