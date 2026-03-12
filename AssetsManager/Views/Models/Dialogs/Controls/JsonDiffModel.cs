using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Models.Dialogs.Controls
{
    public class JsonDiffModel : INotifyPropertyChanged
    {
        private bool _isInlineMode;
        private bool _isWordLevelDiff;
        private bool _hideUnchangedLines;
        private bool _isWordWrapEnabled;

        // Batch Mode Properties
        private bool _isBatchMode;
        private int _currentFileIndex = 1;
        private int _totalFilesCount = 1;

        public bool IsInlineMode
        {
            get => _isInlineMode;
            set { _isInlineMode = value; OnPropertyChanged(); }
        }

        public bool IsWordLevelDiff
        {
            get => _isWordLevelDiff;
            set { _isWordLevelDiff = value; OnPropertyChanged(); }
        }

        public bool HideUnchangedLines
        {
            get => _hideUnchangedLines;
            set { _hideUnchangedLines = value; OnPropertyChanged(); }
        }

        public bool IsWordWrapEnabled
        {
            get => _isWordWrapEnabled;
            set { _isWordWrapEnabled = value; OnPropertyChanged(); }
        }

        public bool IsBatchMode
        {
            get => _isBatchMode;
            set { _isBatchMode = value; OnPropertyChanged(); }
        }

        public int CurrentFileIndex
        {
            get => _currentFileIndex;
            set { _currentFileIndex = value; OnPropertyChanged(); }
        }

        public int TotalFilesCount
        {
            get => _totalFilesCount;
            set { _totalFilesCount = value; OnPropertyChanged(); }
        }

        // Playlist data
        public List<SerializableChunkDiff> BatchItems { get; set; }
        public string OldPbePath { get; set; }
        public string NewPbePath { get; set; }
        public Func<SerializableChunkDiff, string, string, Task<(string oldText, string newText)>> LoadDataFunc { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
