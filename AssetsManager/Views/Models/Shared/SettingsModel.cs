using System.ComponentModel;
using AssetsManager.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Views.Models.Shared
{
    public class SettingsModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private AppSettings _settings;
        public AppSettings Settings
        {
            get { return _settings; }
            set
            {
                _settings = value;
                OnPropertyChanged(nameof(Settings));
            }
        }

        public AudioExportFormat AudioExportFormat
        {
            get => _settings.AudioExportFormat;
            set
            {
                if (_settings.AudioExportFormat != value)
                {
                    _settings.AudioExportFormat = value;
                    OnPropertyChanged(nameof(AudioExportFormat));
                }
            }
        }

        public List<AudioFormatOption> AudioFormatOptions { get; } = new List<AudioFormatOption>
        {
            new AudioFormatOption { Name = "OGG (Default)", Value = AudioExportFormat.Ogg },
            new AudioFormatOption { Name = "WAV (Lossless)", Value = AudioExportFormat.Wav },
            new AudioFormatOption { Name = "MP3 Audio", Value = AudioExportFormat.Mp3 }
        };

        public ImageExportFormat ImageExportFormat
        {
            get => _settings.ImageExportFormat;
            set
            {
                if (_settings.ImageExportFormat != value)
                {
                    _settings.ImageExportFormat = value;
                    OnPropertyChanged(nameof(ImageExportFormat));
                }
            }
        }

        public List<ImageFormatOption> ImageFormatOptions { get; } = new List<ImageFormatOption>
        {
            new ImageFormatOption { Name = "PNG (Default)", Value = ImageExportFormat.Png }
        };

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
