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

        public List<FormatOption<AudioExportFormat>> AudioFormatOptions { get; } = new List<FormatOption<AudioExportFormat>>
        {
            new FormatOption<AudioExportFormat> { Name = "OGG", Value = AudioExportFormat.Ogg },
            new FormatOption<AudioExportFormat> { Name = "WAV", Value = AudioExportFormat.Wav },
            new FormatOption<AudioExportFormat> { Name = "MP3", Value = AudioExportFormat.Mp3 }
        };

        public List<FormatOption<ImageExportFormat>> ImageFormatOptions { get; } = new List<FormatOption<ImageExportFormat>>
        {
            new FormatOption<ImageExportFormat> { Name = "Original", Value = ImageExportFormat.Original },
            new FormatOption<ImageExportFormat> { Name = "PNG", Value = ImageExportFormat.Png },
            new FormatOption<ImageExportFormat> { Name = "JPEG", Value = ImageExportFormat.Jpeg }
        };

        public List<FormatOption<PreferredClient>> PreferredClientOptions { get; } = new List<FormatOption<PreferredClient>>
        {
            new FormatOption<PreferredClient> { Name = "PBE", Value = PreferredClient.PBE },
            new FormatOption<PreferredClient> { Name = "LIVE", Value = PreferredClient.LIVE }
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
