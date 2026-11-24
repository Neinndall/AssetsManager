using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Monitor
{
    public enum HistoryEntryType
    {
        WadFile,
        WadArchive,
        WatcherUpdate
    }

    public class HistoryEntry : INotifyPropertyChanged
    {
        private string _fileName;
        private string _displayName;

        public event PropertyChangedEventHandler PropertyChanged;

        public string FileName
        {
            get => _fileName;
            set
            {
                if (_fileName != value)
                {
                    _fileName = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Version { get; set; }   // Structured Version (e.g. 14.10.1234)

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    OnPropertyChanged();
                }
            }
        }

        public string OldFilePath { get; set; }
        public string NewFilePath { get; set; }
        public DateTime Timestamp { get; set; }
        public HistoryEntryType Type { get; set; } = HistoryEntryType.WadFile;
        public string ReferenceId { get; set; }
        public string ComparisonKey { get; set; } // Identity fingerprint: Version + OldPath + NewPath (normalized)

        public bool Rename(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return false;

            string trimmed = newName.Trim();
            string current = !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : FileName;
            if (string.Equals(trimmed, current, StringComparison.Ordinal)) return false;

            DisplayName = trimmed;
            FileName = trimmed;
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}