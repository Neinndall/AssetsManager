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

        // --- DIRECTORY STATE ---
        private string _newDirectoryPath;
        private string _oldDirectoryPath;
        private BackupModel _selectedTargetDirectoryBackup;
        private BackupModel _selectedBaseDirectoryBackup;
        private string _targetDirectoryVersion = "---";
        private string _baseDirectoryVersion = "---";
        private bool _isTargetDirectoryPbe;
        private bool _isBaseDirectoryPbe;
        private bool _isTargetDirectoryMain;
        private bool _isBaseDirectoryMain;

        // --- WAD FILE STATE ---
        private string _newWadFilePath;
        private string _oldWadFilePath;
        private BackupModel _selectedTargetWadBackup;
        private BackupModel _selectedBaseWadBackup;
        private string _targetWadVersion = "---";
        private string _baseWadVersion = "---";
        private bool _isTargetWadPbe;
        private bool _isBaseWadPbe;
        private bool _isTargetWadMain;
        private bool _isBaseWadMain;

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
                    NotifyAllPropertiesChanged();
                }
            }
        }

        private void NotifyAllPropertiesChanged()
        {
            OnPropertyChanged(nameof(NewDirectoryPath));
            OnPropertyChanged(nameof(OldDirectoryPath));
            OnPropertyChanged(nameof(NewWadFilePath));
            OnPropertyChanged(nameof(OldWadFilePath));
            OnPropertyChanged(nameof(SelectedTargetBackup));
            OnPropertyChanged(nameof(SelectedBaseBackup));
            OnPropertyChanged(nameof(TargetSourcePath));
            OnPropertyChanged(nameof(BaseSourcePath));
            OnPropertyChanged(nameof(TargetSourceRoot));
            OnPropertyChanged(nameof(BaseSourceRoot));
            OnPropertyChanged(nameof(TargetVersion));
            OnPropertyChanged(nameof(BaseVersion));
            OnPropertyChanged(nameof(IsTargetPbe));
            OnPropertyChanged(nameof(IsBasePbe));
            OnPropertyChanged(nameof(IsTargetMain));
            OnPropertyChanged(nameof(IsBaseMain));
        }

        public bool IsFileMode
        {
            get => !IsDirectoryMode;
            set => IsDirectoryMode = !value;
        }

        public string TargetSourcePath => IsDirectoryMode ? TargetSourceRoot : _newWadFilePath;
        public string BaseSourcePath => IsDirectoryMode ? BaseSourceRoot : _oldWadFilePath;

        public string TargetSourceRoot => IsDirectoryMode 
            ? (_selectedTargetDirectoryBackup != null ? _selectedTargetDirectoryBackup.Path : _newDirectoryPath)
            : (_selectedTargetWadBackup != null ? _selectedTargetWadBackup.Path : (_newWadFilePath != null ? GetRootFromWadPath(_newWadFilePath) : null));

        public string BaseSourceRoot => IsDirectoryMode 
            ? (_selectedBaseDirectoryBackup != null ? _selectedBaseDirectoryBackup.Path : _oldDirectoryPath)
            : (_selectedBaseWadBackup != null ? _selectedBaseWadBackup.Path : (_oldWadFilePath != null ? GetRootFromWadPath(_oldWadFilePath) : null));

        private string GetRootFromWadPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string dir = System.IO.Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir))
            {
                if (System.IO.Directory.Exists(System.IO.Path.Combine(dir, "Game")) || System.IO.Directory.Exists(System.IO.Path.Combine(dir, "Plugins")))
                    return dir;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            return System.IO.Path.GetDirectoryName(path);
        }

        public BackupModel SelectedTargetBackup
        {
            get => IsDirectoryMode ? _selectedTargetDirectoryBackup : _selectedTargetWadBackup;
            set
            {
                if (IsDirectoryMode)
                {
                    if (_selectedTargetDirectoryBackup != value)
                    {
                        _selectedTargetDirectoryBackup = value;
                        if (value != null) _newDirectoryPath = value.Path;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(NewDirectoryPath));
                        NotifySourceChanges();
                    }
                }
                else
                {
                    if (_selectedTargetWadBackup != value)
                    {
                        _selectedTargetWadBackup = value;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(NewWadFilePath));
                        NotifySourceChanges();
                    }
                }
            }
        }

        public BackupModel SelectedBaseBackup
        {
            get => IsDirectoryMode ? _selectedBaseDirectoryBackup : _selectedBaseWadBackup;
            set
            {
                if (IsDirectoryMode)
                {
                    if (_selectedBaseDirectoryBackup != value)
                    {
                        _selectedBaseDirectoryBackup = value;
                        if (value != null) _oldDirectoryPath = value.Path;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(OldDirectoryPath));
                        NotifySourceChanges();
                    }
                }
                else
                {
                    if (_selectedBaseWadBackup != value)
                    {
                        _selectedBaseWadBackup = value;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(OldWadFilePath));
                        NotifySourceChanges();
                    }
                }
            }
        }

        private void NotifySourceChanges()
        {
            OnPropertyChanged(nameof(TargetSourcePath));
            OnPropertyChanged(nameof(BaseSourcePath));
            OnPropertyChanged(nameof(TargetSourceRoot));
            OnPropertyChanged(nameof(BaseSourceRoot));
        }

        public string NewDirectoryPath
        {
            get => _newDirectoryPath;
            set 
            { 
                if (_newDirectoryPath != value) 
                { 
                    _newDirectoryPath = value;
                    if (value == null || (_selectedTargetDirectoryBackup != null && _selectedTargetDirectoryBackup.Path != value))
                        _selectedTargetDirectoryBackup = null;
                    if (string.IsNullOrEmpty(value)) ClearMetadata(false);
                    OnPropertyChanged();
                    NotifySourceChanges();
                    OnPropertyChanged(nameof(SelectedTargetBackup));
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
                    if (value == null || (_selectedBaseDirectoryBackup != null && _selectedBaseDirectoryBackup.Path != value))
                        _selectedBaseDirectoryBackup = null;
                    if (string.IsNullOrEmpty(value)) ClearMetadata(true);
                    OnPropertyChanged(); 
                    NotifySourceChanges();
                    OnPropertyChanged(nameof(SelectedBaseBackup));
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
                    // INTELLIGENT CLEAR: Only clear if the path doesn't start with the backup path
                    if (value != null && _selectedTargetWadBackup != null && value.StartsWith(_selectedTargetWadBackup.Path, StringComparison.OrdinalIgnoreCase))
                    { /* Keep selection */ }
                    else if (value != null)
                    { _selectedTargetWadBackup = null; }

                    if (string.IsNullOrEmpty(value)) ClearMetadata(false);
                    OnPropertyChanged(); 
                    NotifySourceChanges();
                    OnPropertyChanged(nameof(SelectedTargetBackup));
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
                    // INTELLIGENT CLEAR: Only clear if the path doesn't start with the backup path
                    if (value != null && _selectedBaseWadBackup != null && value.StartsWith(_selectedBaseWadBackup.Path, StringComparison.OrdinalIgnoreCase))
                    { /* Keep selection */ }
                    else if (value != null)
                    { _selectedBaseWadBackup = null; }

                    if (string.IsNullOrEmpty(value)) ClearMetadata(true);
                    OnPropertyChanged(); 
                    NotifySourceChanges();
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
            get => IsDirectoryMode ? _baseDirectoryVersion : _baseWadVersion;
            set { if (IsDirectoryMode) _baseDirectoryVersion = value; else _baseWadVersion = value; OnPropertyChanged(); }
        }

        public string TargetVersion
        {
            get => IsDirectoryMode ? _targetDirectoryVersion : _targetWadVersion;
            set { if (IsDirectoryMode) _targetDirectoryVersion = value; else _targetWadVersion = value; OnPropertyChanged(); }
        }

        public bool IsBasePbe
        {
            get => IsDirectoryMode ? _isBaseDirectoryPbe : _isBaseWadPbe;
            set { if (IsDirectoryMode) _isBaseDirectoryPbe = value; else _isBaseWadPbe = value; OnPropertyChanged(); }
        }

        public bool IsTargetPbe
        {
            get => IsDirectoryMode ? _isTargetDirectoryPbe : _isTargetWadPbe;
            set { if (IsDirectoryMode) _isTargetDirectoryPbe = value; else _isTargetWadPbe = value; OnPropertyChanged(); }
        }

        public bool IsBaseMain
        {
            get => IsDirectoryMode ? _isBaseDirectoryMain : _isBaseWadMain;
            set { if (IsDirectoryMode) _isBaseDirectoryMain = value; else _isBaseWadMain = value; OnPropertyChanged(); }
        }

        public bool IsTargetMain
        {
            get => IsDirectoryMode ? _isTargetDirectoryMain : _isTargetWadMain;
            set { if (IsDirectoryMode) _isTargetDirectoryMain = value; else _isTargetWadMain = value; OnPropertyChanged(); }
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

            string root = backupManager.GetGameRoot(path);
            var version = await versionService.GetGameVersionAsync(root ?? path);
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
