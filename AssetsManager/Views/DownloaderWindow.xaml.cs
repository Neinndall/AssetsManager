using AssetsManager.Services.Downloads;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views
{
    public partial class DownloaderWindow : UserControl
    {
        private AppSettings _appSettings;

        public DownloaderWindow(
            LogService logService,
            ExtractionService extractionService,
            AppSettings appSettings,
            CustomMessageBoxService customMessageBoxService,
            DirectoriesCreator directoriesCreator)
        {
            InitializeComponent();
            _appSettings = appSettings;

            // Inject dependencies into child controls
            DownloaderControl.LogService = logService;
            DownloaderControl.AppSettings = appSettings;
            DownloaderControl.ExtractionService = extractionService;
            DownloaderControl.CustomMessageBoxService = customMessageBoxService;
            DownloaderControl.DirectoriesCreator = directoriesCreator;
        }

        public void UpdateSettings(AppSettings newSettings, bool wasResetToDefaults)
        {
            _appSettings = newSettings;
            DownloaderControl.UpdateSettings(newSettings, wasResetToDefaults);
        }
    }
}
