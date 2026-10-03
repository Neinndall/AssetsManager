using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Primary view model for the VFX Inspector & Diagnostic Studio window.
    /// Manages root directory scanning, system selection, emitter live controls, and diagnostics.
    /// </summary>
    public partial class VfxInspectorModel : INotifyPropertyChanged
    {
        private string _rootPath;
        private VfxSkinItem _selectedSkin;
        private VfxWorkspaceTab _selectedWorkspaceTab;
        private string _searchQuery;
        private string _statusText = "Ready";
        private bool _inspectorVisible;
        private bool _viewportToolbarVisible;

        public ObservableCollection<object> BrowserRoots { get; } = new();
        public ObservableCollection<VfxWorkspaceTab> WorkspaceTabs { get; } = new();
        public ObservableCollection<VfxSkinItem> DetectedSkins { get; } = new();
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
            if (e.PropertyName is nameof(VfxWorkspaceTab.FocusedActor) or
                nameof(VfxWorkspaceTab.Title) or nameof(VfxWorkspaceTab.Subtitle))
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

        private bool _timelineVisible = true;
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

        public VfxWorkspaceTab SelectedWorkspaceTab
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
                OnPropertyChanged(nameof(IsMapWorkspace));
                OnPropertyChanged(nameof(HasContextInspector));
                OnPropertyChanged(nameof(IsInspectorPanelVisible));
                OnPropertyChanged(nameof(HasActiveCharacterBackdrop));
                NotifyViewportSelectionChanged();
            }
        }

        public bool IsSkinWorkspace => SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin;
        public bool HasContextInspector => IsSkinWorkspace || IsMapWorkspace || HasStandaloneSystem;
        public bool IsInspectorPanelVisible => HasContextInspector && InspectorVisible;

        public void NotifyWorkspaceTabsChanged()
        {
            OnPropertyChanged(nameof(HasWorkspaceTabs));
        }

        public VfxSkinItem SelectedSkin
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
