using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Services.Audio;

namespace AssetsManager.Views.Models.Dialogs
{
    public class AudioPlayerModel : INotifyPropertyChanged
    {
        private readonly AudioPlayerService _service;
        private bool _isPlaying;
        private double _volume = 0.5;
        private string _currentTrackName = "No track selected";
        private string _currentTimeText = "0:00";
        private string _totalTimeText = "0:00";
        private double _currentTime;
        private double _totalTime = 100;
        private string _activePackName = "New Playlist";

        public event PropertyChangedEventHandler PropertyChanged;

        public AudioPlayerModel(AudioPlayerService service)
        {
            _service = service;
        }

        public AudioPlayerService Service => _service;

        public bool IsPlaying
        {
            get => _isPlaying;
            set { if (_isPlaying != value) { _isPlaying = value; OnPropertyChanged(); } }
        }

        public double Volume
        {
            get => _volume;
            set 
            { 
                if (_volume != value) 
                { 
                    _volume = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(IsMuted));
                } 
            }
        }

        public bool IsMuted => Volume == 0;

        public string CurrentTrackName
        {
            get => _currentTrackName;
            set { if (_currentTrackName != value) { _currentTrackName = value; OnPropertyChanged(); } }
        }

        public string CurrentTimeText
        {
            get => _currentTimeText;
            set { if (_currentTimeText != value) { _currentTimeText = value; OnPropertyChanged(); } }
        }

        public string TotalTimeText
        {
            get => _totalTimeText;
            set { if (_totalTimeText != value) { _totalTimeText = value; OnPropertyChanged(); } }
        }

        public double CurrentTime
        {
            get => _currentTime;
            set { if (_currentTime != value) { _currentTime = value; OnPropertyChanged(); } }
        }

        public double TotalTime
        {
            get => _totalTime;
            set { if (_totalTime != value) { _totalTime = value; OnPropertyChanged(); } }
        }

        public string ActivePackName
        {
            get => _activePackName;
            set { if (_activePackName != value) { _activePackName = value; OnPropertyChanged(); } }
        }

        public void ResetToDefault()
        {
            CurrentTrackName = "No track selected";
            CurrentTimeText = "0:00";
            TotalTimeText = "0:00";
            CurrentTime = 0;
            TotalTime = 100;
            ActivePackName = "New Playlist";
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
