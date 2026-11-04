using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows.Media;
using System.Threading.Tasks;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Models.Explorer
{
    public enum NodeType { RealDirectory, RealFile, WadFile, VirtualDirectory, VirtualFile, AudioEvent, WemFile, SoundBank }
    public enum DiffStatus { Unchanged, New, Modified, Renamed, Removed, Dependency }
    public enum AudioSourceType { Wpk, Bnk, EventsBnk }

    public class FileSystemNodeModel : INotifyPropertyChanged, IDisposable, ISelectableTreeNode
    {
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

        public string CopyFullPath
        {
            get
            {
                if (Parent != null)
                {
                    var parentPath = Parent.CopyFullPath;
                    return string.IsNullOrEmpty(parentPath) ? Name : $"{parentPath}/{Name}";
                }
                return Name;
            }
        }

        public DiffStatus Status { get; set; } = DiffStatus.Unchanged;
        public string OldPath { get; set; }
        public SerializableChunkDiff ChunkDiff { get; set; }
        public bool IsTechnicalNode { get; set; }
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
                    Children = new ObservableRangeCollection<FileSystemNodeModel>();
                }
                return _children;
            }
            set
            {
                if (ReferenceEquals(_children, value)) return;
                if (_projectionSource != null) _projectionSource.CollectionChanged -= OnChildrenChanged;
                _children = value;
                _projectionSource = null;
                if (_visibleChildren != null)
                {
                    ObserveChildren();
                    if (!_isChildrenFiltered) ReconcileVisibleChildren(_children);
                }
            }
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
            set => _sourceWadPath = value;
        }

        public string BackupChunkPath { get; set; } // Only for nodes from a backup
        public ulong SourceChunkPathHash { get; set; } // Only for VirtualFile

        public string Extension
        {
            get
            {
                if (Type == NodeType.RealDirectory || Type == NodeType.VirtualDirectory)
                    return "";

                string path = VirtualPath;
                return string.IsNullOrEmpty(path) ? "" : Path.GetExtension(path).ToLowerInvariant();
            }
        }
        public bool IsGroupingFolder { get; set; }

        public string DisplayName
        {
            get
            {
                if (Type == NodeType.WadFile)
                {
                    if (string.IsNullOrEmpty(Name)) return string.Empty;
                    if (Name.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                    {
                        return Name.Substring(0, Name.Length - ".wad.client".Length);
                    }
                    else if (Name.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
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
                if (string.IsNullOrEmpty(name)) return string.Empty;

                int parenthesisIndex = name.LastIndexOf(" (");
                if (parenthesisIndex > 0)
                {
                    string potentialNumber = name.Substring(parenthesisIndex + 2);
                    if (potentialNumber.Length > 1 && potentialNumber.EndsWith(")") && int.TryParse(potentialNumber.Substring(0, potentialNumber.Length - 1), out _))
                    {
                        return name.Substring(0, parenthesisIndex).Trim();
                    }
                }
                return name;
            }
        }

        private ObservableRangeCollection<FileSystemNodeModel> _visibleChildren;
        private ObservableRangeCollection<FileSystemNodeModel> _projectionSource;
        private bool _isChildrenFiltered;
        internal bool HasFilteredChildren => _isChildrenFiltered;
        public ObservableRangeCollection<FileSystemNodeModel> VisibleChildren
        {
            get
            {
                if (_visibleChildren == null && Children != null)
                {
                    _visibleChildren = new ObservableRangeCollection<FileSystemNodeModel>(_children);
                    ObserveChildren();
                }
                return _visibleChildren;
            }
        }

        internal void SetVisibleChildren(IReadOnlyList<FileSystemNodeModel> items)
        {
            _isChildrenFiltered = items != null;
            if (VisibleChildren != null) ReconcileVisibleChildren(items ?? _children);
        }

        private void ObserveChildren()
        {
            _projectionSource = _children;
            if (_projectionSource != null) _projectionSource.CollectionChanged += OnChildrenChanged;
        }

        private void OnChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isChildrenFiltered) return;
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                for (int i = 0; i < e.NewItems.Count; i++)
                    _visibleChildren.Insert(e.NewStartingIndex + i, (FileSystemNodeModel)e.NewItems[i]);
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove)
            {
                for (int i = 0; i < e.OldItems.Count; i++) _visibleChildren.RemoveAt(e.OldStartingIndex);
            }
            else ReconcileVisibleChildren(_children);
        }

        private void ReconcileVisibleChildren(IReadOnlyList<FileSystemNodeModel> items)
        {
            items ??= Array.Empty<FileSystemNodeModel>();
            if (_visibleChildren.Count == items.Count && _visibleChildren.SequenceEqual(items)) return;
            // Keep the ItemsSource and surviving rows intact, including the selected container.
            var wanted = new HashSet<FileSystemNodeModel>(items);
            for (int i = _visibleChildren.Count - 1; i >= 0; i--)
                if (!wanted.Contains(_visibleChildren[i])) _visibleChildren.RemoveAt(i);
            var retained = new HashSet<FileSystemNodeModel>(_visibleChildren);
            for (int i = 0; i < items.Count; i++)
            {
                if (i < _visibleChildren.Count && ReferenceEquals(_visibleChildren[i], items[i])) continue;
                if (retained.Contains(items[i])) _visibleChildren.Move(_visibleChildren.IndexOf(items[i]), i);
                else _visibleChildren.Insert(i, items[i]);
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

        System.Collections.IEnumerable ISelectableTreeNode.SelectionChildren => Children;
        bool ISelectableTreeNode.IsSelectionVisible => IsVisible;

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
                if (path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                {
                    Type = NodeType.WadFile;
                }
                else if (path.EndsWith(".wpk", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
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
                if (name.EndsWith(".wpk", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
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
            if (_projectionSource != null) _projectionSource.CollectionChanged -= OnChildrenChanged;
            _projectionSource = null;
            if (_visibleChildren != null && !ReferenceEquals(_visibleChildren, _children))
            {
                _visibleChildren.Clear();
                _visibleChildren = null;
            }

            // Limpiar hijos recursivamente sin copias intermedias
            if (_children != null)
            {
                for (int i = 0; i < _children.Count; i++)
                {
                    _children[i]?.Dispose();
                }
                _children.Clear();
                _children = null;
            }

            // Limpiar referencias
            ChunkDiff = null;
            _sourceWadPath = null;
            BackupChunkPath = null;
            _virtualPath = null;
            OldPath = null;
            Name = null;

            // Desuscribir todos los eventos
            Parent = null;
            PropertyChanged = null;
        }
    }
}
