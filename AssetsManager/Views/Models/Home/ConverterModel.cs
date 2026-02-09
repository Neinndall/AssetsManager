using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Views.Models.Home
{
    public enum ConverterFileType
    {
        Image,
        Audio
    }

    public class ConverterItem : INotifyPropertyChanged
    {
        private string _fileName;
        private string _filePath;
        private string _status = "Pending";
        private double _progress = 0;
        private ConverterFileType _fileType;

        public string FileName
        {
            get => _fileName;
            set { _fileName = value; OnPropertyChanged(); }
        }

        public string FilePath
        {
            get => _filePath;
            set { _filePath = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        public ConverterFileType FileType
        {
            get => _fileType;
            set { _fileType = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ConverterModel : INotifyPropertyChanged
    {
        private bool _isProcessing;
        private ImageExportFormat _selectedImageFormat = ImageExportFormat.Png;
        private AudioExportFormat _selectedAudioFormat = AudioExportFormat.Ogg;

        public ObservableRangeCollection<ConverterItem> Items { get; } = new ObservableRangeCollection<ConverterItem>();

        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; OnPropertyChanged(); }
        }

        public bool HasImages => Items.Any(i => i.FileType == ConverterFileType.Image);
        public bool HasAudio => Items.Any(i => i.FileType == ConverterFileType.Audio);

        public ConverterModel()
        {
            Items.CollectionChanged += (s, e) => 
            {
                OnPropertyChanged(nameof(HasImages));
                OnPropertyChanged(nameof(HasAudio));
            };
        }

        public ImageExportFormat SelectedImageFormat
        {
            get => _selectedImageFormat;
            set { _selectedImageFormat = value; OnPropertyChanged(); }
        }

        public AudioExportFormat SelectedAudioFormat
        {
            get => _selectedAudioFormat;
            set { _selectedAudioFormat = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
