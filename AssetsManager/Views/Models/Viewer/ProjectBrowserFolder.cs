using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed class ProjectBrowserFolder(string fullPath) : INotifyPropertyChanged, ISelectableTreeNode
    {
        public string FullPath { get; } = fullPath;
        public string Name => FullPath == null ? "Loading..." : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(FullPath));
        public ObservableRangeCollection<ProjectBrowserFolder> Children { get; } = new();
        public IEnumerable SelectionChildren => Children;
        public bool IsSelectionVisible => FullPath != null;
        internal bool IsLoaded { get; set; }
        internal Task LoadingTask { get; set; }
        internal CancellationToken LoadingToken { get; set; }
        private bool _isExpanded, _isSelected, _isMultiSelected;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
        }
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
        }
        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set { if (_isMultiSelected == value) return; _isMultiSelected = value; OnPropertyChanged(); }
        }
        internal static ProjectBrowserFolder Create(string path)
        {
            var folder = new ProjectBrowserFolder(path);
            folder.Children.Add(new ProjectBrowserFolder(null));
            return folder;
        }
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new(name));
    }
}
