using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Views.Models.Home
{
    public class TextureConverterItem : INotifyPropertyChanged
    {
        private string _fileName;
        private string _filePath;
        private string _status = "Pending";
        private double _progress = 0;

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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class TextureConverterModel : INotifyPropertyChanged
    {
        private bool _isProcessing;
        private ImageExportFormat _selectedFormat = ImageExportFormat.Png;

        public ObservableRangeCollection<TextureConverterItem> Items { get; } = new ObservableRangeCollection<TextureConverterItem>();

        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; OnPropertyChanged(); }
        }

        public ImageExportFormat SelectedFormat
        {
            get => _selectedFormat;
            set { _selectedFormat = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
