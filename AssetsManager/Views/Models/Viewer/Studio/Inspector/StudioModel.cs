using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Presentation state for the 3D Studio project browser, scenes, playback and contextual inspector.
    /// </summary>
    public partial class StudioModel : INotifyPropertyChanged
    {
        private string _rootPath;
        private bool _isProjectFilesVisible;
        public bool IsProjectFilesVisible
        {
            get => _isProjectFilesVisible;
            set
            {
                if (_isProjectFilesVisible == value) return;
                _isProjectFilesVisible = value;
                OnPropertyChanged();
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
            }
        }
        private StudioSkinItem _selectedSkin;
        private StudioWorkspaceTab _selectedWorkspaceTab;
        private string _searchQuery;
        private string _statusText = "Ready";
        private bool _inspectorVisible;
        private bool _viewportToolbarVisible;

        public ObservableCollection<object> BrowserRoots { get; } = new();
        public ObservableCollection<StudioWorkspaceTab> WorkspaceTabs { get; } = new();
        public ObservableCollection<StudioSkinItem> DetectedSkins { get; } = new();
        public ObservableCollection<string> LogMessages { get; } = new();
        public bool HasViewportContentControls => HasChampionMesh || HasMapPreview;

        private string CharacterSelectionTitle => IsSkinWorkspace
            ? SelectedWorkspaceTab.FocusedActor?.Title ?? SelectedWorkspaceTab.Title
            : HasChampionMesh && SelectedSkin != null
                ? string.IsNullOrWhiteSpace(SelectedSkin.OwnerName)
                    ? SelectedSkin.Title
                    : $"{SelectedSkin.OwnerName} · {SelectedSkin.Title}"
                : null;

        public string ViewportSelectionTitle =>
            _selectedMapNode?.Title ?? _selectedSpell?.Name ??
            (_selectedAnimation is { IsBindPose: false } ? _selectedAnimation.DisplayName : null) ??
            _selectedSystem?.Name ?? CharacterSelectionTitle ??
            (IsMapWorkspace ? SelectedWorkspaceTab.Title : null) ?? "No asset selected";

        public string ViewportSelectionDetail =>
            !string.IsNullOrWhiteSpace(_selectedMapNode?.InspectorSummary)
                ? _selectedMapNode.InspectorSummary
                : _selectedMapNode != null
                    ? _selectedMapNode.Subtitle ?? _selectedMapNode.Kind.ToString()
                    : _selectedSpell != null
                        ? $"Spell · Particles: {_liveParticleCount}"
                        : _selectedAnimation is { IsBindPose: false }
                            ? $"Animation clip · Particles: {_liveParticleCount}"
                            : _selectedSystem != null
                                ? $"Particles: {_liveParticleCount}"
                                : CharacterSelectionTitle != null
                                    ? _selectedAnimation?.IsBindPose == true ? "Character · Bind pose" : "Character"
                                    : IsMapWorkspace ? "Map" : "Select an asset to inspect";

        private void NotifyViewportSelectionChanged()
        {
            OnPropertyChanged(nameof(ViewportSelectionTitle));
            OnPropertyChanged(nameof(ViewportSelectionDetail));
        }

        private void SelectedWorkspaceTab_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StudioWorkspaceTab.HasExtraActors))
                OnPropertyChanged(nameof(CanSynchronizeScene));
            if (e.PropertyName is nameof(StudioWorkspaceTab.IsMeshSyncEnabled) or
                nameof(StudioWorkspaceTab.IsTextureSyncEnabled) or nameof(StudioWorkspaceTab.IsAnimationSyncEnabled) or
                nameof(StudioWorkspaceTab.IsAnimationPlaybackSyncEnabled))
                OnPropertyChanged(e.PropertyName);
            if (e.PropertyName is nameof(StudioWorkspaceTab.FocusedActor) or
                nameof(StudioWorkspaceTab.Title) or nameof(StudioWorkspaceTab.Subtitle))
                NotifyViewportSelectionChanged();
        }

        public bool InspectorVisible
        {
            get => _inspectorVisible;
            set
            {
                if (_inspectorVisible == value) return;
                _inspectorVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsInspectorPanelVisible));
            }
        }

        public bool ViewportToolbarVisible
        {
            get => _viewportToolbarVisible;
            set
            {
                if (_viewportToolbarVisible == value) return;
                _viewportToolbarVisible = value;
                OnPropertyChanged();
            }
        }

        public string RootPath
        {
            get => _rootPath;
            set
            {
                if (string.Equals(_rootPath, value, StringComparison.OrdinalIgnoreCase)) return;
                _rootPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProjectName));
                OnPropertyChanged(nameof(ProjectPath));
                OnPropertyChanged(nameof(HasProject));
            }
        }

        private bool _timelineVisible;
        private bool _isProjectLoading;
        private string _projectBrowserMessage = "Open a project folder to browse assets.";

        public bool TimelineVisible
        {
            get => _timelineVisible;
            set
            {
                if (_timelineVisible == value) return;
                _timelineVisible = value;
                OnPropertyChanged();
            }
        }

        public bool IsProjectLoading
        {
            get => _isProjectLoading;
            set
            {
                if (_isProjectLoading == value) return;
                _isProjectLoading = value;
                OnPropertyChanged();
            }
        }

        public string ProjectBrowserMessage
        {
            get => _projectBrowserMessage;
            set
            {
                if (_projectBrowserMessage == value) return;
                _projectBrowserMessage = value;
                OnPropertyChanged();
            }
        }

        public string ProjectName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_rootPath)) return "No project";
                string trimmed = _rootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string name = System.IO.Path.GetFileName(trimmed);
                return string.IsNullOrWhiteSpace(name) ? trimmed : name;
            }
        }

        public string ProjectPath => _rootPath ?? string.Empty;
        public bool HasProject => !string.IsNullOrWhiteSpace(_rootPath);
        public bool HasWorkspaceTabs => WorkspaceTabs.Count > 0;

        public StudioWorkspaceTab SelectedWorkspaceTab
        {
            get => _selectedWorkspaceTab;
            set
            {
                if (ReferenceEquals(_selectedWorkspaceTab, value)) return;
                if (_selectedWorkspaceTab != null)
                {
                    _selectedWorkspaceTab.PropertyChanged -= SelectedWorkspaceTab_PropertyChanged;
                    _selectedWorkspaceTab.IsSelected = false;
                }
                _selectedWorkspaceTab = value;
                if (_selectedWorkspaceTab != null)
                {
                    _selectedWorkspaceTab.PropertyChanged += SelectedWorkspaceTab_PropertyChanged;
                    _selectedWorkspaceTab.IsSelected = true;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSkinWorkspace));
                OnPropertyChanged(nameof(CanSynchronizeScene));
                OnPropertyChanged(nameof(IsMapWorkspace));
                OnPropertyChanged(nameof(HasContextInspector));
                OnPropertyChanged(nameof(IsInspectorPanelVisible));
                OnPropertyChanged(nameof(HasActiveCharacterBackdrop));
                NotifyViewportSelectionChanged();
            }
        }

        public bool IsSkinWorkspace => SelectedWorkspaceTab?.Kind == StudioWorkspaceTabKind.Skin;
        public bool CanSynchronizeScene => IsSkinWorkspace && SelectedWorkspaceTab.Actors.Count > 1;
        public bool HasContextInspector => IsSkinWorkspace || IsMapWorkspace || HasStandaloneSystem;
        public bool IsInspectorPanelVisible => HasContextInspector && InspectorVisible;

        public void NotifyWorkspaceTabsChanged()
        {
            OnPropertyChanged(nameof(HasWorkspaceTabs));
        }

        public StudioSkinItem SelectedSkin
        {
            get => _selectedSkin;
            set { _selectedSkin = value; OnPropertyChanged(); NotifyViewportSelectionChanged(); }
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set { _searchQuery = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
