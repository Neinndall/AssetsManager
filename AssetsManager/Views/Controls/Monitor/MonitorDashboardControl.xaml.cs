using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Services.Downloads;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class MonitorDashboardControl : UserControl
    {
        // Public properties for Service Injection from Parent Window
        private MonitorService _monitorService;
        public MonitorService MonitorService { get => _monitorService; set { _monitorService = value; TryInitModel(); } }

        private PbeStatusService _pbeStatusService;
        public PbeStatusService PbeStatusService { get => _pbeStatusService; set { _pbeStatusService = value; TryInitModel(); } }

        private AppSettings _appSettings;
        public AppSettings AppSettings { get => _appSettings; set { _appSettings = value; TryInitModel(); } }

        private VersionService _versionService;
        public VersionService VersionService { get => _versionService; set { _versionService = value; TryInitModel(); } }

        private Status _statusService;
        public Status StatusService { get => _statusService; set { _statusService = value; TryInitModel(); } }

        private UpdateCheckService _updateCheckService;
        public UpdateCheckService UpdateCheckService { get => _updateCheckService; set { _updateCheckService = value; TryInitModel(); } }

        public MonitorDashboardModel ViewModel => DataContext as MonitorDashboardModel;

        public MonitorDashboardControl()
        {
            InitializeComponent();
        }

        private void TryInitModel()
        {
            // Solo creamos el modelo si no existe ya y si tenemos todos los servicios inyectados
            if (DataContext == null && MonitorService != null && PbeStatusService != null && AppSettings != null && VersionService != null && StatusService != null && UpdateCheckService != null)
            {
                DataContext = new MonitorDashboardModel(MonitorService, PbeStatusService, AppSettings, VersionService, StatusService, UpdateCheckService);
            }
        }
    }
}
