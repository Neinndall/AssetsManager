using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Controls
{
    public class JsonDiffModel : INotifyPropertyChanged
    {
        private bool _isInlineMode;
        private bool _isWordLevelDiff;
        private bool _hideUnchangedLines;
        private bool _isWordWrapEnabled;

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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
