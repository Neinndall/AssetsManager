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
                // NOTA TÉCNICA: Al resetear, la instancia de Settings sigue siendo la misma,
                // pero sus valores internos han cambiado. Al disparar PropertyChanged para "Settings",
                // WPF se ve obligado a re-evaluar todos los bindings que cuelgan de él (Settings.LolPbeDirectory, etc.)
                _settings = value;
                OnPropertyChanged(nameof(Settings));
            }
        }

        public List<AudioFormatOption> AudioFormatOptions { get; } = new List<AudioFormatOption>
        {
            new AudioFormatOption { Name = "OGG", Value = AudioExportFormat.Ogg },
            new AudioFormatOption { Name = "WAV", Value = AudioExportFormat.Wav },
            new AudioFormatOption { Name = "MP3", Value = AudioExportFormat.Mp3 }
        };

        public List<ImageFormatOption> ImageFormatOptions { get; } = new List<ImageFormatOption>
        {
            new ImageFormatOption { Name = "Original", Value = ImageExportFormat.Original },
            new ImageFormatOption { Name = "PNG", Value = ImageExportFormat.Png },
            new ImageFormatOption { Name = "JPEG", Value = ImageExportFormat.Jpeg }
        };

        public void NotifySettingsChanged()
        {
            // Notificamos que el objeto Settings ha cambiado para refrescar todos sus sub-bindings
            OnPropertyChanged(nameof(Settings));
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
