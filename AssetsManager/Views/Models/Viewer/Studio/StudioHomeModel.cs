using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed record StudioRecentProject(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    }

    public sealed class StudioHomeModel : INotifyPropertyChanged
    {
        private bool _isStudioVisible;
        public bool IsStudioVisible
        {
            get => _isStudioVisible;
            set
            {
                if (_isStudioVisible == value) return;
                _isStudioVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActiveContent));
            }
        }
        private bool _isChromaLibraryVisible;
        public bool IsChromaLibraryVisible
        {
            get => _isChromaLibraryVisible;
            set
            {
                if (_isChromaLibraryVisible == value) return;
                _isChromaLibraryVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActiveContent));
            }
        }
        public bool HasActiveContent => IsStudioVisible || IsChromaLibraryVisible;
        public ObservableRangeCollection<StudioRecentProject> RecentProjects { get; } = new();
        public bool HasRecentProjects => RecentProjects.Count > 0;

        internal void SetRecentProjects(IEnumerable<string> paths)
        {
            RecentProjects.ReplaceRange((paths ?? Array.Empty<string>()).Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(6).Select(path => new StudioRecentProject(path)));
            OnPropertyChanged(nameof(HasRecentProjects));
        }

        internal static IReadOnlyList<string> RememberProject(IEnumerable<string> paths, string path) =>
            new[] { System.IO.Path.GetFullPath(path) }.Concat(paths ?? Array.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
