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

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
