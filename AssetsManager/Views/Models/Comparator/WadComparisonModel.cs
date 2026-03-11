using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Comparator
{
    public class WadComparisonModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isDirectoryMode = true;
        private string _newDirectoryPath;
        private string _oldDirectoryPath;
        private string _newWadFilePath;
        private string _oldWadFilePath;
        private bool _isComparing;

        public bool IsDirectoryMode
        {
            get => _isDirectoryMode;
            set
            {
                if (_isDirectoryMode != value)
                {
                    _isDirectoryMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsFileMode));
                }
            }
        }

        public bool IsFileMode => !IsDirectoryMode;

        public string NewDirectoryPath
        {
            get => _newDirectoryPath;
            set { if (_newDirectoryPath != value) { _newDirectoryPath = value; OnPropertyChanged(); } }
        }

        public string OldDirectoryPath
        {
            get => _oldDirectoryPath;
            set { if (_oldDirectoryPath != value) { _oldDirectoryPath = value; OnPropertyChanged(); } }
        }

        public string NewWadFilePath
        {
            get => _newWadFilePath;
            set { if (_newWadFilePath != value) { _newWadFilePath = value; OnPropertyChanged(); } }
        }

        public string OldWadFilePath
        {
            get => _oldWadFilePath;
            set { if (_oldWadFilePath != value) { _oldWadFilePath = value; OnPropertyChanged(); } }
        }

        public bool IsComparing
        {
            get => _isComparing;
            set { if (_isComparing != value) { _isComparing = value; OnPropertyChanged(); } }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
