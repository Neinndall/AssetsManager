using System.Linq;
using System.Windows.Controls;
using AssetsManager.Utils;

namespace AssetsManager.Views.Settings
{
    public partial class GeneralSettingsView : UserControl
    {
        private AppSettings _appSettings;

        public GeneralSettingsView()
        {
            InitializeComponent();
        }
        
        public void ApplySettingsToUI(AppSettings appSettings)
        {
            _appSettings = appSettings;
            
            checkBoxSyncHashes.IsChecked = _appSettings.SyncHashesWithCDTB;
            checkBoxCheckJsonData.IsChecked = _appSettings.CheckJsonDataUpdates;
            checkBoxAutoCopy.IsChecked = _appSettings.AutoCopyHashes;
            checkBoxCreateBackUp.IsChecked = _appSettings.CreateBackUpOldHashes;
            checkBoxOnlyCheckDifferences.IsChecked = _appSettings.OnlyCheckDifferences;
            checkBoxSaveDiffHistory.IsChecked = _appSettings.SaveDiffHistory;
            checkBoxBackgroundUpdates.IsChecked = _appSettings.BackgroundUpdates;
            comboBoxUpdateFrequency.SelectedItem = _appSettings.UpdateCheckFrequency;
            checkBoxMinimizeToTray.IsChecked = _appSettings.MinimizeToTrayOnClose;
        }

        public void SaveSettings()
        {   
            if (_appSettings == null) return;
            
            _appSettings.SyncHashesWithCDTB = checkBoxSyncHashes.IsChecked ?? false;
            _appSettings.CheckJsonDataUpdates = checkBoxCheckJsonData.IsChecked ?? false;
            _appSettings.AutoCopyHashes = checkBoxAutoCopy.IsChecked ?? false;
            _appSettings.CreateBackUpOldHashes = checkBoxCreateBackUp.IsChecked ?? false;
            _appSettings.OnlyCheckDifferences = checkBoxOnlyCheckDifferences.IsChecked ?? false;
            _appSettings.SaveDiffHistory = checkBoxSaveDiffHistory.IsChecked ?? false;
            _appSettings.BackgroundUpdates = checkBoxBackgroundUpdates.IsChecked ?? false;
            _appSettings.UpdateCheckFrequency = (int)(comboBoxUpdateFrequency.SelectedItem ?? 10);
            _appSettings.MinimizeToTrayOnClose = checkBoxMinimizeToTray.IsChecked ?? false;
        }
    }
}