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
        private bool _isComparing;

        private string _baseVersion = "---";
        private string _targetVersion = "---";
        private bool _isBasePbe;
        private bool _isTargetPbe;
        private bool _isBaseMain;
        private bool _isTargetMain;

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

        public bool IsFileMode
        {
            get => !IsDirectoryMode;
            set => IsDirectoryMode = !value;
        }

        private string _newManualPath;
        private string _oldManualPath;
        private BackupModel _selectedTargetBackup;
        private BackupModel _selectedBaseBackup;

        public string TargetSourcePath => _selectedTargetBackup != null ? _selectedTargetBackup.Path : _newManualPath;
        public string BaseSourcePath => _selectedBaseBackup != null ? _selectedBaseBackup.Path : _oldManualPath;

        public BackupModel SelectedTargetBackup
        {
            get => _selectedTargetBackup;
            set
            {
                if (_selectedTargetBackup != value)
                {
                    _selectedTargetBackup = value;
                    if (value != null) _newManualPath = value.Path; 
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TargetSourcePath));
                    OnPropertyChanged(nameof(NewDirectoryPath));
                    OnPropertyChanged(nameof(NewWadFilePath));
                    if (value == null && string.IsNullOrEmpty(_newManualPath)) ClearMetadata(false);
                }
            }
        }

        public BackupModel SelectedBaseBackup
        {
            get => _selectedBaseBackup;
            set
            {
                if (_selectedBaseBackup != value)
                {
                    _selectedBaseBackup = value;
                    if (value != null) _oldManualPath = value.Path;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BaseSourcePath));
                    OnPropertyChanged(nameof(OldDirectoryPath));
                    OnPropertyChanged(nameof(OldWadFilePath));
                    if (value == null && string.IsNullOrEmpty(_oldManualPath)) ClearMetadata(true);
                }
            }
        }

        public string NewDirectoryPath
        {
            get => IsDirectoryMode ? _newManualPath : null;
            set 
            { 
                if (_newManualPath != value) 
                { 
                    _newManualPath = value;
                    if (string.IsNullOrEmpty(value)) 
                    {
                        _selectedTargetBackup = null;
                        ClearMetadata(false);
                    }
                    else
                    {
                        _selectedTargetBackup = null;
                    }
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TargetSourcePath));
                    OnPropertyChanged(nameof(SelectedTargetBackup));
                } 
            }
        }

        public string OldDirectoryPath
        {
            get => IsDirectoryMode ? _oldManualPath : null;
            set 
            { 
                if (_oldManualPath != value) 
                { 
                    _oldManualPath = value; 
                    if (string.IsNullOrEmpty(value))
                    {
                        _selectedBaseBackup = null;
                        ClearMetadata(true);
                    }
                    else
                    {
                        _selectedBaseBackup = null;
                    }
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(BaseSourcePath));
                    OnPropertyChanged(nameof(SelectedBaseBackup));
                } 
            }
        }

        public string NewWadFilePath
        {
            get => IsFileMode ? _newManualPath : null;
            set 
            { 
                if (_newManualPath != value) 
                { 
                    _newManualPath = value; 
                    if (string.IsNullOrEmpty(value))
                    {
                        _selectedTargetBackup = null;
                        ClearMetadata(false);
                    }
                    else
                    {
                        _selectedTargetBackup = null;
                    }
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(TargetSourcePath));
                    OnPropertyChanged(nameof(SelectedTargetBackup));
                } 
            }
        }

        public string OldWadFilePath
        {
            get => IsFileMode ? _oldManualPath : null;
            set 
            { 
                if (_oldManualPath != value) 
                { 
                    _oldManualPath = value; 
                    if (string.IsNullOrEmpty(value))
                    {
                        _selectedBaseBackup = null;
                        ClearMetadata(true);
                    }
                    else
                    {
                        _selectedBaseBackup = null;
                    }
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(BaseSourcePath));
                    OnPropertyChanged(nameof(SelectedBaseBackup));
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

        public bool IsBaseMain
        {
            get => _isBaseMain;
            set { if (_isBaseMain != value) { _isBaseMain = value; OnPropertyChanged(); } }
        }

        public bool IsTargetMain
        {
            get => _isTargetMain;
            set { if (_isTargetMain != value) { _isTargetMain = value; OnPropertyChanged(); } }
        }

        public void ClearMetadata(bool isBase)
        {
            if (isBase)
            {
                BaseVersion = "---";
                IsBasePbe = false;
                IsBaseMain = false;
            }
            else
            {
                TargetVersion = "---";
                IsTargetPbe = false;
                IsTargetMain = false;
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
            var (isPbe, isMain) = backupManager.GetPathIdentification(path);

            if (isBase)
            {
                BaseVersion = version ?? "Unknown";
                IsBasePbe = isPbe;
                IsBaseMain = isMain;
            }
            else
            {
                TargetVersion = version ?? "Unknown";
                IsTargetPbe = isPbe;
                IsTargetMain = isMain;
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
