using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Home
{
    public class HomeModel : INotifyPropertyChanged
    {
        private readonly AppSettings _appSettings;
        private readonly DirectoriesCreator _directoriesCreator;

        private string _greeting;
        private string _monitorSummary;
        private string _lastComparisonText;
        private bool _isConfigIncomplete;
        private bool _isLiveConfigured;
        private bool _isPbeConfigured;
        private bool _isLocalConfigured;
        
        public string Greeting
        {
            get => _greeting;
            set { _greeting = value; OnPropertyChanged(); }
        }

        public bool IsLiveConfigured
        {
            get => _isLiveConfigured;
            set { _isLiveConfigured = value; OnPropertyChanged(); }
        }

        public bool IsPbeConfigured
        {
            get => _isPbeConfigured;
            set { _isPbeConfigured = value; OnPropertyChanged(); }
        }

        public bool IsLocalConfigured
        {
            get => _isLocalConfigured;
            set { _isLocalConfigured = value; OnPropertyChanged(); }
        }

        public bool IsConfigIncomplete
        {
            get => _isConfigIncomplete;
            set { _isConfigIncomplete = value; OnPropertyChanged(); }
        }
        
        public string MonitorSummary
        {
            get => _monitorSummary;
            set { _monitorSummary = value; OnPropertyChanged(); }
        }

        public string LastComparisonText
        {
            get => _lastComparisonText;
            set { _lastComparisonText = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public HomeModel(AppSettings appSettings, DirectoriesCreator directoriesCreator)
        {
            _appSettings = appSettings;
            _directoriesCreator = directoriesCreator;
            _appSettings.ConfigurationSaved += AppSettings_ConfigurationSaved;

            Initialize();
        }

        private void AppSettings_ConfigurationSaved(object sender, EventArgs e)
        {
            Initialize();
        }

        private void Initialize()
        {
            UpdateGreeting();
            
            // Check for individual configs
            IsLiveConfigured = !string.IsNullOrEmpty(_appSettings.LolLiveDirectory) && Directory.Exists(_appSettings.LolLiveDirectory);
            IsPbeConfigured = !string.IsNullOrEmpty(_appSettings.LolPbeDirectory) && Directory.Exists(_appSettings.LolPbeDirectory);
            IsLocalConfigured = !string.IsNullOrEmpty(_directoriesCreator.AssetsDownloadedPath) && Directory.Exists(_directoriesCreator.AssetsDownloadedPath);

            if (IsLiveConfigured && IsPbeConfigured)
            {
                LastComparisonText = "Environment paths are fully configured for comparative analysis.";
                IsConfigIncomplete = false;
            }
            else
            {
                LastComparisonText = "Essential paths are not configured. Please check your settings.";
                IsConfigIncomplete = true;
            }
             
             // Monitor summary
             MonitorSummary = "Tracking server status and remote asset integrity.";
        }

        private void UpdateGreeting()
        {
            var hour = DateTime.Now.Hour;
            if (hour >= 6 && hour < 12) Greeting = "Good Morning, Summoner";
            else if (hour >= 12 && hour < 20) Greeting = "Good Afternoon, Summoner";
            else Greeting = "Good Night, Summoner";
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
        public void Cleanup()
        {
            _appSettings.ConfigurationSaved -= AppSettings_ConfigurationSaved;
        }
    }
}
