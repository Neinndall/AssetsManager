using AssetsManager.Services.Downloads;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views
{
    public partial class AssetsDownloaderWindow : UserControl
    {
        private AppSettings _appSettings;

        public AssetsDownloaderWindow(
            LogService logService,
            ExtractionService extractionService,
            AppSettings appSettings,
            CustomMessageBoxService customMessageBoxService,
            DirectoriesCreator directoriesCreator)
        {
            InitializeComponent();
            _appSettings = appSettings;

            // Inject dependencies into child controls
            AssetsDownloaderControl.LogService = logService;
            AssetsDownloaderControl.AppSettings = appSettings;
            AssetsDownloaderControl.ExtractionService = extractionService;
            AssetsDownloaderControl.CustomMessageBoxService = customMessageBoxService;
            AssetsDownloaderControl.DirectoriesCreator = directoriesCreator;
        }

        public void UpdateSettings(AppSettings newSettings, bool wasResetToDefaults)
        {
            _appSettings = newSettings;
            AssetsDownloaderControl.UpdateSettings(newSettings, wasResetToDefaults);
        }
    }
}
