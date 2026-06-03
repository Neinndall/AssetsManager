using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Windows.Media;
using System.Threading.Tasks;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Models.Explorer
{
    public enum NodeType { RealDirectory, RealFile, WadFile, VirtualDirectory, VirtualFile, AudioEvent, WemFile, SoundBank }
    public enum DiffStatus { Unchanged, New, Modified, Renamed, Removed, Dependency }
    public enum AudioSourceType { Wpk, Bnk }

    public class FileSystemNodeModel : INotifyPropertyChanged, IDisposable
    {
        private static readonly Dictionary<string, string> _wadPathPool = new(StringComparer.OrdinalIgnoreCase);

        public string Name { get; set; }
        public NodeType Type { get; set; }

        private string _virtualPath;
        public string VirtualPath
        {
            get
            {
                if (string.IsNullOrEmpty(_virtualPath))
                {
                    if (Type == NodeType.VirtualFile || Type == NodeType.VirtualDirectory || Type == NodeType.WemFile || Type == NodeType.AudioEvent || Type == NodeType.SoundBank)
                    {
                        if (Parent == null || Parent.Type == NodeType.WadFile || Parent.Type == NodeType.RealDirectory)
                        {
                            _virtualPath = Name;
                        }
                        else
                        {
                            var parentPath = Parent.VirtualPath;
                            _virtualPath = string.IsNullOrEmpty(parentPath) ? Name : $"{parentPath}/{Name}";
                        }
                    }
                }
                return _virtualPath;
            }
            set => _virtualPath = value;
        }

        private string _copyFullPath;
        public string CopyFullPath
        {
            get
            {
                if (_copyFullPath == null)
                {
                    if (Parent != null)
                    {
                        var parentPath = Parent.CopyFullPath;
                        _copyFullPath = string.IsNullOrEmpty(parentPath) ? Name : $"{parentPath}/{Name}";
                    }
                    else
                    {
                        _copyFullPath = Name;
                    }
                }
                return _copyFullPath;
            }
        }

        public DiffStatus Status { get; set; } = DiffStatus.Unchanged;
        public string OldPath { get; set; }
        public SerializableChunkDiff ChunkDiff { get; set; }
        public uint WemId { get; set; } // Only for WemFile
        public uint WemOffset { get; set; } // Only for WemFile from BNK
        public uint WemSize { get; set; } // Only for WemFile from BNK
        public bool IsEnabled { get; set; } = true;
        public AudioSourceType AudioSource { get; set; } // Only for WemFile

        private ObservableRangeCollection<FileSystemNodeModel> _children;
        public ObservableRangeCollection<FileSystemNodeModel> Children
        {
            get
            {
                if (_children == null && CanHaveChildren(Type))
                {
                    _children = new ObservableRangeCollection<FileSystemNodeModel>();
                }
                return _children;
            }
            set => _children = value;
        }

        public ObservableRangeCollection<FileSystemNodeModel> LoadedChildren => _children;
        public bool HasLoadedChildren => _children != null && _children.Count > 0;

        public static bool CanHaveChildren(NodeType type)
        {
            return type == NodeType.RealDirectory || 
                   type == NodeType.WadFile || 
                   type == NodeType.VirtualDirectory || 
                   type == NodeType.SoundBank || 
                   type == NodeType.AudioEvent;
        }

        public FileSystemNodeModel Parent { get; set; }

        private string _sourceWadPath;
        public string SourceWadPath
        {
            get => _sourceWadPath;
            set
            {
                if (value == null) _sourceWadPath = null;
                else
                {
                    lock (_wadPathPool)
                    {
                        if (!_wadPathPool.TryGetValue(value, out _sourceWadPath))
                        {
                            _sourceWadPath = value;
                            _wadPathPool[value] = value;
                        }
                    }
                }
            }
        }

        public string BackupChunkPath { get; set; } // Only for nodes from a backup
        public ulong SourceChunkPathHash { get; set; } // Only for VirtualFile

        private string _extension;
        public string Extension
        {
            get
            {
                if (_extension == null)
                {
                    if (Type == NodeType.RealDirectory || Type == NodeType.VirtualDirectory)
                        _extension = "";
                    else
                    {
                        string path = VirtualPath;
                        _extension = string.IsNullOrEmpty(path) ? "" : Path.GetExtension(path).ToLowerInvariant();
                    }
                }
                return _extension;
            }
        }
        public bool IsGroupingFolder { get; set; }

        private string _displayName;
        public string DisplayName
        {
            get
            {
                if (_displayName == null)
                {
                    if (Type == NodeType.WadFile)
                    {
                        string lowerName = Name.ToLowerInvariant();
                        if (lowerName.EndsWith(".wad.client"))
                        {
                            _displayName = Name.Substring(0, Name.Length - ".wad.client".Length);
                        }
                        else if (lowerName.EndsWith(".wad"))
                        {
                            _displayName = Name.Substring(0, Name.Length - ".wad".Length);
                        }
                        else
                        {
                            _displayName = Name;
                        }
                    }
                    else
                    {
                        _displayName = Name;
                    }
                }
                return _displayName;
            }
        }

        private string _breadcrumbDisplayName;
        public string BreadcrumbDisplayName
        {
            get
            {
                if (_breadcrumbDisplayName == null)
                {
                    var name = DisplayName;

                    int parenthesisIndex = name.LastIndexOf(" (");
                    if (parenthesisIndex > 0)
                    {
                        string potentialNumber = name.Substring(parenthesisIndex + 2);
                        if (potentialNumber.Length > 1 && potentialNumber.EndsWith(")") && int.TryParse(potentialNumber.Substring(0, potentialNumber.Length - 1), out _))
                        {
                            name = name.Substring(0, parenthesisIndex).Trim();
                        }
                    }
                    _breadcrumbDisplayName = name;
                }
                return _breadcrumbDisplayName;
            }
        }

        private ObservableRangeCollection<FileSystemNodeModel> _visibleChildren;
        public ObservableRangeCollection<FileSystemNodeModel> VisibleChildren
        {
            get => _visibleChildren ?? Children;
            set
            {
                _visibleChildren = value;
                OnPropertyChanged(nameof(VisibleChildren));
            }
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged(nameof(IsExpanded));
                }
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        private bool _isMultiSelected;
        public bool IsMultiSelected
        {
            get { return _isMultiSelected; }
            set
            {
                if (_isMultiSelected != value)
                {
                    _isMultiSelected = value;
                    OnPropertyChanged(nameof(IsMultiSelected));
                }
            }
        }

        private bool _isVisible = true;
        public bool IsVisible
        {
            get { return _isVisible; }
            set
            {
                if (_isVisible != value)
                {
                    _isVisible = value;
                    OnPropertyChanged(nameof(IsVisible));
                }
            }
        }

        private string _preMatch;
        public string PreMatch
        {
            get { return _preMatch; }
            set
            {
                if (_preMatch != value)
                {
                    _preMatch = value;
                    OnPropertyChanged(nameof(PreMatch));
                }
            }
        }

        private string _match;
        public string Match
        {
            get { return _match; }
            set
            {
                if (_match != value)
                {
                    _match = value;
                    OnPropertyChanged(nameof(Match));
                }
            }
        }

        private string _postMatch;
        public string PostMatch
        {
            get { return _postMatch; }
            set
            {
                if (_postMatch != value)
                {
                    _postMatch = value;
                    OnPropertyChanged(nameof(PostMatch));
                }
            }
        }

        private bool _hasMatch;
        public bool HasMatch
        {
            get { return _hasMatch; }
            set
            {
                if (_hasMatch != value)
                {
                    _hasMatch = value;
                    OnPropertyChanged(nameof(HasMatch));
                }
            }
        }

        // Constructor for real files/directories
        public FileSystemNodeModel(string path)
        {
            _virtualPath = path;
            Name = Path.GetFileName(path);

            if (Directory.Exists(path))
            {
                Type = NodeType.RealDirectory;
            }
            else
            {
                string lowerPath = path.ToLowerInvariant();
                if (lowerPath.EndsWith(".wad") || lowerPath.EndsWith(".wad.client"))
                {
                    Type = NodeType.WadFile;
                }
                else if (lowerPath.EndsWith(".wpk") || lowerPath.EndsWith(".bnk"))
                {
                    Type = NodeType.SoundBank;
                    Children.Add(new FileSystemNodeModel()); // Add dummy child
                }
                else
                {
                    Type = NodeType.RealFile;
                }
            }
        }

        // Constructor for virtual nodes inside a WAD
        public FileSystemNodeModel(string name, bool isDirectory, string virtualPath, string sourceWad)
        {
            Name = name;
            _virtualPath = virtualPath;
            SourceWadPath = sourceWad;

            if (isDirectory)
            {
                Type = NodeType.VirtualDirectory;
            }
            else
            {
                string lowerName = name.ToLowerInvariant();
                if (lowerName.EndsWith(".wpk") || lowerName.EndsWith(".bnk"))
                {
                    Type = NodeType.SoundBank;
                    Children.Add(new FileSystemNodeModel()); // Add dummy child
                }
                else
                {
                    Type = NodeType.VirtualFile;
                }
            }
        }

        // Internal constructor for the dummy node (Keep for SoundBanks if they still use it)
        internal FileSystemNodeModel()
        {
            Name = "Loading...";
        }

        // Constructor for custom UI nodes like Audio Events
        public FileSystemNodeModel(string name, NodeType type)
        {
            Name = name;
            Type = type;
        }

        // Constructor for WemFile nodes
        public FileSystemNodeModel(string name, uint wemId, uint wemOffset = 0, uint wemSize = 0)
        {
            Name = name;
            Type = NodeType.WemFile;
            WemId = wemId;
            WemOffset = wemOffset;
            WemSize = wemSize;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            // Limpiar hijos recursivamente
            if (_children != null)
            {
                foreach (var child in _children)
                {
                    child.Dispose();
                }
                _children.Clear();
            }

            // Limpiar referencias
            ChunkDiff = null;
            _sourceWadPath = null;
            BackupChunkPath = null;
            _virtualPath = null;
            _copyFullPath = null;
            _breadcrumbDisplayName = null;
            OldPath = null;
            Name = null;

            // Desuscribir todos los eventos
            Parent = null;
            PropertyChanged = null;
        }
    }
}