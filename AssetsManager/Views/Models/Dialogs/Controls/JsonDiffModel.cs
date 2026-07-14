using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Dialogs.Controls
{
    public class JsonDiffModel : INotifyPropertyChanged
    {
        private bool _isInlineMode;
        private bool _isWordLevelDiff;
        private bool _hideUnchangedLines;
        private bool _isWordWrapEnabled;
        private bool _showInsertions = true;
        private bool _showDeletions = true;
        private bool _showModifications = true;

        // Metrics Properties
        private int _insertionsCount;
        private int _deletionsCount;
        private int _modificationsCount;

        // Metadata Properties
        private string _oldSize;
        private string _newSize;
        private int _currentLine = 1;

        // Batch Mode Properties
        private bool _isBatchMode;
        private bool _isInitialComparison;
        private int _currentFileIndex = 1;
        private int _totalFilesCount = 1;

        public bool IsInitialComparison
        {
            get => _isInitialComparison;
            set { _isInitialComparison = value; OnPropertyChanged(); }
        }

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

        public bool ShowInsertions
        {
            get => _showInsertions;
            set { _showInsertions = value; OnPropertyChanged(); }
        }

        public bool ShowDeletions
        {
            get => _showDeletions;
            set { _showDeletions = value; OnPropertyChanged(); }
        }

        public bool ShowModifications
        {
            get => _showModifications;
            set { _showModifications = value; OnPropertyChanged(); }
        }

        public int InsertionsCount
        {
            get => _insertionsCount;
            set { _insertionsCount = value; OnPropertyChanged(); }
        }

        public int DeletionsCount
        {
            get => _deletionsCount;
            set { _deletionsCount = value; OnPropertyChanged(); }
        }

        public int ModificationsCount
        {
            get => _modificationsCount;
            set { _modificationsCount = value; OnPropertyChanged(); }
        }

        public string OldSize
        {
            get => _oldSize;
            set { _oldSize = value; OnPropertyChanged(); }
        }

        public string NewSize
        {
            get => _newSize;
            set { _newSize = value; OnPropertyChanged(); }
        }

        public int CurrentLine
        {
            get => _currentLine;
            set { _currentLine = value; OnPropertyChanged(); }
        }

        public bool IsBatchMode
        {
            get => _isBatchMode;
            set { _isBatchMode = value; OnPropertyChanged(); }
        }

        public int CurrentFileIndex
        {
            get => _currentFileIndex;
            set { _currentFileIndex = value; _totalFilesCountString = null; OnPropertyChanged(); OnPropertyChanged(nameof(TotalFilesCountString)); }
        }

        public int TotalFilesCount
        {
            get => _totalFilesCount;
            set { _totalFilesCount = value; _totalFilesCountString = null; OnPropertyChanged(); OnPropertyChanged(nameof(TotalFilesCountString)); }
        }

        private string _totalFilesCountString;
        public string TotalFilesCountString => _totalFilesCountString ??= $"{_currentFileIndex} / {_totalFilesCount}";

        // Preloaded batch data
        public List<(string oldText, string newText, string oldPath, string newPath)> PreloadedData { get; set; }

        public void UpdateMetrics(string oldText, string newText)
        {
            // Note: Detailed metrics (ins/del/mod) are calculated in the Control 
            // after the DiffPlex model is built.
            
            // Metadata Updates
            OldSize = FormatUtils.FormatSize((long)(oldText?.Length ?? 0));
            NewSize = FormatUtils.FormatSize((long)(newText?.Length ?? 0));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
