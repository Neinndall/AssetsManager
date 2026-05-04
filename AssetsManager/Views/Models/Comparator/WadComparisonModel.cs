using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;

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

        private string _baseVersion = "---";
        private string _targetVersion = "---";
        private bool _isBasePbe;
        private bool _isTargetPbe;
        private bool _isBaseActive;
        private bool _isTargetActive;

        public ObservableCollection<BackupModel> AvailableBackups { get; } = new ObservableCollection<BackupModel>();

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
            set 
            { 
                if (_newDirectoryPath != value) 
                { 
                    _newDirectoryPath = value; 
                    OnPropertyChanged();
                    if (string.IsNullOrEmpty(value)) ClearMetadata(false);
                } 
            }
        }

        public string OldDirectoryPath
        {
            get => _oldDirectoryPath;
            set 
            { 
                if (_oldDirectoryPath != value) 
                { 
                    _oldDirectoryPath = value; 
                    OnPropertyChanged(); 
                    if (string.IsNullOrEmpty(value)) ClearMetadata(true);
                } 
            }
        }

        public string NewWadFilePath
        {
            get => _newWadFilePath;
            set 
            { 
                if (_newWadFilePath != value) 
                { 
                    _newWadFilePath = value; 
                    OnPropertyChanged(); 
                    if (string.IsNullOrEmpty(value)) ClearMetadata(false);
                } 
            }
        }

        public string OldWadFilePath
        {
            get => _oldWadFilePath;
            set 
            { 
                if (_oldWadFilePath != value) 
                { 
                    _oldWadFilePath = value; 
                    OnPropertyChanged(); 
                    if (string.IsNullOrEmpty(value)) ClearMetadata(true);
                } 
            }
        }

        public bool IsComparing
        {
            get => _isComparing;
            set { if (_isComparing != value) { _isComparing = value; OnPropertyChanged(); } }
        }

        public string BaseVersion
        {
            get => _baseVersion;
            set { if (_baseVersion != value) { _baseVersion = value; OnPropertyChanged(); } }
        }

        public string TargetVersion
        {
            get => _targetVersion;
            set { if (_targetVersion != value) { _targetVersion = value; OnPropertyChanged(); } }
        }

        public bool IsBasePbe
        {
            get => _isBasePbe;
            set { if (_isBasePbe != value) { _isBasePbe = value; OnPropertyChanged(); } }
        }

        public bool IsTargetPbe
        {
            get => _isTargetPbe;
            set { if (_isTargetPbe != value) { _isTargetPbe = value; OnPropertyChanged(); } }
        }

        public bool IsBaseActive
        {
            get => _isBaseActive;
            set { if (_isBaseActive != value) { _isBaseActive = value; OnPropertyChanged(); } }
        }

        public bool IsTargetActive
        {
            get => _isTargetActive;
            set { if (_isTargetActive != value) { _isTargetActive = value; OnPropertyChanged(); } }
        }

        public void ClearMetadata(bool isBase)
        {
            if (isBase)
            {
                BaseVersion = "---";
                IsBasePbe = false;
                IsBaseActive = false;
            }
            else
            {
                TargetVersion = "---";
                IsTargetPbe = false;
                IsTargetActive = false;
            }
        }

        public async Task UpdateMetadataFromPathAsync(bool isBase, string path, VersionService versionService, BackupManager backupManager)
        {
            if (string.IsNullOrEmpty(path))
            {
                ClearMetadata(isBase);
                return;
            }

            var version = await versionService.GetGameVersionAsync(path);
            var (isPbe, isActive) = backupManager.GetPathIdentification(path);

            if (isBase)
            {
                BaseVersion = version ?? "Unknown";
                IsBasePbe = isPbe;
                IsBaseActive = isActive;
            }
            else
            {
                TargetVersion = version ?? "Unknown";
                IsTargetPbe = isPbe;
                IsTargetActive = isActive;
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
