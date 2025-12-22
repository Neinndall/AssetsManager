using System;
using System.IO;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Models.Explorer
{
    public enum NodeType { RealDirectory, RealFile, WadFile, VirtualDirectory, VirtualFile, AudioEvent, WemFile, SoundBank }
    public enum DiffStatus { Unchanged, New, Modified, Renamed, Deleted }
    public enum AudioSourceType { Wpk, Bnk }

    public class FileSystemNodeModel : INotifyPropertyChanged, IDisposable
    {
        public string Name { get; set; }
        public NodeType Type { get; set; }
        public string FullPath { get; set; } // Real path for RealDirectory/WadFile/RealFile, Virtual path for Virtual items
        public DiffStatus Status { get; set; } = DiffStatus.Unchanged;
        public string OldPath { get; set; }
        public SerializableChunkDiff ChunkDiff { get; set; }
        public uint WemId { get; set; } // Only for WemFile
        public uint WemOffset { get; set; } // Only for WemFile from BNK
        public uint WemSize { get; set; } // Only for WemFile from BNK
        public bool IsEnabled { get; set; } = true;
        public AudioSourceType AudioSource { get; set; } // Only for WemFile

        public ObservableCollection<FileSystemNodeModel> Children { get; set; }

        private ImageSource _imagePreview;
        public ImageSource ImagePreview
        {
            get { return _imagePreview; }
            set
            {
                if (_imagePreview != value)
                {
                    _imagePreview = value;
                    OnPropertyChanged(nameof(ImagePreview));
                }
            }
        }

        // --- Data for WADs and Chunks ---
        public string SourceWadPath { get; set; } // Only for VirtualFile/VirtualDirectory
        public string BackupChunkPath { get; set; } // Only for nodes from a backup
        public ulong SourceChunkPathHash { get; set; } // Only for VirtualFile

        public string Extension => (Type == NodeType.RealDirectory || Type == NodeType.VirtualDirectory) ? "" : Path.GetExtension(FullPath).ToLowerInvariant();
        public bool IsGroupingFolder => Name != null && Name.StartsWith("[");

        public string DisplayName
        {
            get
            {
                if (Type == NodeType.WadFile)
                {
                    string lowerName = Name.ToLowerInvariant();
                    if (lowerName.EndsWith(".wad.client"))
                    {
                        return Name.Substring(0, Name.Length - ".wad.client".Length);
                    }
                    else if (lowerName.EndsWith(".wad"))
                    {
                        return Name.Substring(0, Name.Length - ".wad".Length);
                    }
                }
                return Name;
            }
        }

        public string BreadcrumbDisplayName
        {
            get
            {
                var name = DisplayName;

                if (name.StartsWith("[") && name.Length > 4 && name[3] == ' ')
                {
                    name = name.Substring(4);
                }

                int parenthesisIndex = name.LastIndexOf(" (");
                if (parenthesisIndex > 0)
                {
                    string potentialNumber = name.Substring(parenthesisIndex + 2);
                    if (potentialNumber.Length > 1 && potentialNumber.EndsWith(")") && int.TryParse(potentialNumber.Substring(0, potentialNumber.Length - 1), out _))
                    {
                        name = name.Substring(0, parenthesisIndex).Trim();
                    }
                }
                return name;
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
            FullPath = path;
            Name = Path.GetFileName(path);
            Children = new ObservableCollection<FileSystemNodeModel>();

            if (Directory.Exists(path))
            {
                Type = NodeType.RealDirectory;
                Children.Add(new FileSystemNodeModel()); // Add dummy child for lazy loading
            }
            else
            {
                string lowerPath = path.ToLowerInvariant();
                if (lowerPath.EndsWith(".wad") || lowerPath.EndsWith(".wad.client"))
                {
                    Type = NodeType.WadFile;
                    Children.Add(new FileSystemNodeModel()); // Add dummy child for lazy loading
                }
                else if (lowerPath.EndsWith(".wpk") || lowerPath.EndsWith(".bnk"))
                {
                    Type = NodeType.SoundBank;
                    Children.Add(new FileSystemNodeModel()); // Add dummy child for lazy loading
                }
                else
                {
                    Type = NodeType.RealFile; // It's a real file on the filesystem
                }
            }
        }

        // Constructor for virtual nodes inside a WAD
        public FileSystemNodeModel(string name, bool isDirectory, string virtualPath, string sourceWad)
        {
            Name = name;
            FullPath = virtualPath;
            SourceWadPath = sourceWad;
            Children = new ObservableCollection<FileSystemNodeModel>();

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
                    Children.Add(new FileSystemNodeModel()); // Add dummy child for lazy loading
                }
                else
                {
                    Type = NodeType.VirtualFile;
                }
            }
        }

        // Internal constructor for the dummy node
        internal FileSystemNodeModel()
        {
            Name = "Loading...";
            Children = new ObservableCollection<FileSystemNodeModel>();
        }

        // Constructor for custom UI nodes like Audio Events
        public FileSystemNodeModel(string name, NodeType type)
        {
            Name = name;
            Type = type;
            FullPath = name; // Path is not relevant for these nodes
            Children = new ObservableCollection<FileSystemNodeModel>();
        }

        // Constructor for WemFile nodes
        public FileSystemNodeModel(string name, uint wemId, uint wemOffset = 0, uint wemSize = 0)
        {
            Name = name;
            Type = NodeType.WemFile;
            WemId = wemId;
            WemOffset = wemOffset;
            WemSize = wemSize;
            FullPath = name; // Path is not relevant for these nodes
            Children = new ObservableCollection<FileSystemNodeModel>();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            // Limpiar hijos recursivamente
            if (Children != null)
            {
                foreach (var child in Children)
                {
                    child.Dispose();
                }
                Children.Clear();
            }

            // Limpiar referencias
            ChunkDiff = null;
            SourceWadPath = null;
            BackupChunkPath = null;
            FullPath = null;
            OldPath = null;
            Name = null;

            // Desuscribir todos los eventos
            PropertyChanged = null;
        }
    }
}
