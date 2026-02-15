using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Explorer
{
    public class FileGridAnalyticsModel : INotifyPropertyChanged
    {
        private int _totalFiles;
        private int _imageCount;
        private long _imageSize;
        private int _audioCount;
        private long _audioSize;
        private int _modelCount;
        private long _modelSize;
        private int _dataCount;
        private long _dataSize;
        private string _healthStatus = "Ready";

        public int TotalFiles { get => _totalFiles; set { _totalFiles = value; OnPropertyChanged(); } }
        public int ImageCount { get => _imageCount; set { _imageCount = value; OnPropertyChanged(); } }
        public long ImageSize { get => _imageSize; set { _imageSize = value; OnPropertyChanged(); } }
        public int AudioCount { get => _audioCount; set { _audioCount = value; OnPropertyChanged(); } }
        public long AudioSize { get => _audioSize; set { _audioSize = value; OnPropertyChanged(); } }
        public int ModelCount { get => _modelCount; set { _modelCount = value; OnPropertyChanged(); } }
        public long ModelSize { get => _modelSize; set { _modelSize = value; OnPropertyChanged(); } }
        public int DataCount { get => _dataCount; set { _dataCount = value; OnPropertyChanged(); } }
        public long DataSize { get => _dataSize; set { _dataSize = value; OnPropertyChanged(); } }
        public string HealthStatus { get => _healthStatus; set { _healthStatus = value; OnPropertyChanged(); } }

        public string ImageSizeText => FormatSize((ulong)ImageSize);
        public string AudioSizeText => FormatSize((ulong)AudioSize);
        public string ModelSizeText => FormatSize((ulong)ModelSize);
        public string DataSizeText => FormatSize((ulong)DataSize);

        private string FormatSize(ulong sizeInBytes)
        {
            if (sizeInBytes < 1024) return $"{sizeInBytes} B";
            double sizeInKB = sizeInBytes / 1024.0;
            if (sizeInKB < 1024) return $"{sizeInKB:F1} KB";
            double sizeInMB = sizeInKB / 1024.0;
            return $"{sizeInMB:F1} MB";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
