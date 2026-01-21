using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models
{
    public class HomeViewModel : INotifyPropertyChanged
    {
        private readonly AppSettings _appSettings;

        private string _greeting;
        private string _monitorSummary;
        private string _lastComparisonText;
        
        public string Greeting
        {
            get => _greeting;
            set { _greeting = value; OnPropertyChanged(); }
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

        public HomeViewModel(AppSettings appSettings)
        {
            _appSettings = appSettings;

            Initialize();
        }

        private void Initialize()
        {
            UpdateGreeting();
            
            // Check for last comparison config
            if (!string.IsNullOrEmpty(_appSettings.LolPbeDirectory) && !string.IsNullOrEmpty(_appSettings.LolLiveDirectory))
            {
                LastComparisonText = "Ready to compare configured paths.";
            }
            else
            {
                LastComparisonText = "Configure paths to start comparing.";
            }
             
             // Monitor summary
             MonitorSummary = "Track PBE status and remote assets.";
        }

        private void UpdateGreeting()
        {
            var hour = DateTime.Now.Hour;
            if (hour < 12) Greeting = "Good Morning, Summoner";
            else if (hour < 18) Greeting = "Good Afternoon, Summoner";
            else Greeting = "Good Evening, Summoner";
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
        public void Cleanup()
        {
            // Nothing to cleanup for now
        }
    }
}
