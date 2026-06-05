using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Utils.Framework;
using Material.Icons;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Master ViewModel for the Viewer Panel.
    /// Orchestrates the sidebar logic and model-specific states.
    /// </summary>
    public class ViewerPanelModel : INotifyPropertyChanged
    {
        private bool _isMapMode;
        private string _loadButtonText = "Model";
        private string _loadButtonTooltip = "Load Model";
        private MaterialIconKind _loadButtonIcon = MaterialIconKind.CubeOutline;

        // --- UI State Properties (v3.2.2.0) ---
        private bool _isChromaGalleryVisible = false;
        private bool _isMainContentVisible = false;
        private bool _isAnimationSyncEnabled = false;
        private bool _isAnimationPlaybackSyncEnabled = false;
        private bool _isMeshSyncEnabled = false;
        private ViewerViewportModel _viewportViewModel;

        // --- Data Collections ---
        private readonly ObservableRangeCollection<SceneModel> _loadedModels = new();
        private readonly ObservableRangeCollection<AnimationModel> _animationModels = new();
        private ObservableRangeCollection<ModelPart> _selectedModelParts;
        private SceneModel _selectedModel;
        private AnimationModel _selectedAnimation;

        private List<SceneModel> _filteredModelsList = new();
        private List<AnimationModel> _filteredAnimationsList = new();

        public ViewerPanelModel()
        {
            _loadedModels.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(HasLoadedModels));
                OnPropertyChanged(nameof(HasMultipleModels));
                UpdateFilteredModels();
            };
            _animationModels.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(HasAnimations));
                OnPropertyChanged(nameof(HasMultipleAnimations));
                UpdateFilteredAnimations();
            };
        }

        // --- Navigation State (Control Deck v3.3) ---
        private bool _isModelsSectionExpanded = false;
        private bool _isInspectorSectionExpanded = true;
        private bool _isEnvironmentSectionExpanded = false;
        private bool _isCameraSectionExpanded = false;
        private bool _isLightingSectionExpanded = false;
        private bool _isRenderSectionExpanded = false;
        private string _modelsSearchText = string.Empty;
        private string _animationsSearchText = string.Empty;

        public ObservableRangeCollection<SceneModel> LoadedModels => _loadedModels;
        public ObservableRangeCollection<AnimationModel> AnimationModels => _animationModels;

        public bool IsMeshSyncEnabled
        {
            get => _isMeshSyncEnabled;
            set { if (_isMeshSyncEnabled != value) { _isMeshSyncEnabled = value; OnPropertyChanged(); } }
        }

        public bool IsAnimationSyncEnabled
        {
            get => _isAnimationSyncEnabled;
            set { if (_isAnimationSyncEnabled != value) { _isAnimationSyncEnabled = value; OnPropertyChanged(); } }
        }

        public bool IsAnimationPlaybackSyncEnabled
        {
            get => _isAnimationPlaybackSyncEnabled;
            set { if (_isAnimationPlaybackSyncEnabled != value) { _isAnimationPlaybackSyncEnabled = value; OnPropertyChanged(); } }
        }

        public ObservableRangeCollection<ModelPart> SelectedModelParts
        {
            get => _selectedModelParts;
            set { if (_selectedModelParts != value) { _selectedModelParts = value; OnPropertyChanged(); OnPropertyChanged(nameof(MeshPartCount)); } }
        }

        public SceneModel SelectedModel
        {
            get => _selectedModel;
            set { if (_selectedModel != value) { _selectedModel = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelectedModel)); } }
        }

        public AnimationModel SelectedAnimation
        {
            get => _selectedAnimation;
            set { if (_selectedAnimation != value) { _selectedAnimation = value; OnPropertyChanged(); } }
        }

        public bool IsChromaGalleryVisible
        {
            get => _isChromaGalleryVisible;
            set { if (_isChromaGalleryVisible != value) { _isChromaGalleryVisible = value; OnPropertyChanged(); } }
        }

        public bool IsMainContentVisible
        {
            get => _isMainContentVisible;
            set { if (_isMainContentVisible != value) { _isMainContentVisible = value; OnPropertyChanged(); } }
        }

        public ViewerViewportModel ViewportViewModel
        {
            get => _viewportViewModel;
            set { if (_viewportViewModel != value) { _viewportViewModel = value; OnPropertyChanged(); } }
        }

        public bool IsMapMode
        {
            get => _isMapMode;
            set
            {
                if (_isMapMode != value)
                {
                    _isMapMode = value;
                    UpdateModeData();
                    OnPropertyChanged();
                }
            }
        }

        public string LoadButtonText
        {
            get => _loadButtonText;
            private set { _loadButtonText = value; OnPropertyChanged(); }
        }

        public string LoadButtonTooltip
        {
            get => _loadButtonTooltip;
            private set { _loadButtonTooltip = value; OnPropertyChanged(); }
        }

        public MaterialIconKind LoadButtonIcon
        {
            get => _loadButtonIcon;
            private set { _loadButtonIcon = value; OnPropertyChanged(); }
        }

        // --- Navigation State (Control Deck v3.3) ---

        public bool IsModelsSectionExpanded
        {
            get => _isModelsSectionExpanded;
            set { if (_isModelsSectionExpanded != value) { _isModelsSectionExpanded = value; OnPropertyChanged(); } }
        }

        public bool IsInspectorSectionExpanded
        {
            get => _isInspectorSectionExpanded;
            set { if (_isInspectorSectionExpanded != value) { _isInspectorSectionExpanded = value; OnPropertyChanged(); } }
        }

        public bool IsEnvironmentSectionExpanded
        {
            get => _isEnvironmentSectionExpanded;
            set { if (_isEnvironmentSectionExpanded != value) { _isEnvironmentSectionExpanded = value; OnPropertyChanged(); } }
        }

        public bool IsCameraSectionExpanded
        {
            get => _isCameraSectionExpanded;
            set { if (_isCameraSectionExpanded != value) { _isCameraSectionExpanded = value; OnPropertyChanged(); } }
        }

        public bool IsLightingSectionExpanded
        {
            get => _isLightingSectionExpanded;
            set { if (_isLightingSectionExpanded != value) { _isLightingSectionExpanded = value; OnPropertyChanged(); } }
        }

        public bool IsRenderSectionExpanded
        {
            get => _isRenderSectionExpanded;
            set { if (_isRenderSectionExpanded != value) { _isRenderSectionExpanded = value; OnPropertyChanged(); } }
        }

        public string ModelsSearchText
        {
            get => _modelsSearchText;
            set
            {
                var v = value ?? string.Empty;
                if (_modelsSearchText != v)
                {
                    _modelsSearchText = v;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasModelsSearchText));
                    UpdateFilteredModels();
                }
            }
        }

        public string AnimationsSearchText
        {
            get => _animationsSearchText;
            set
            {
                var v = value ?? string.Empty;
                if (_animationsSearchText != v)
                {
                    _animationsSearchText = v;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAnimationsSearchText));
                    UpdateFilteredAnimations();
                }
            }
        }

        public bool HasModelsSearchText => !string.IsNullOrWhiteSpace(_modelsSearchText);
        public bool HasAnimationsSearchText => !string.IsNullOrWhiteSpace(_animationsSearchText);

        public IEnumerable<SceneModel> FilteredModels => _filteredModelsList;

        public IEnumerable<AnimationModel> FilteredAnimations => _filteredAnimationsList;

        private void UpdateFilteredModels()
        {
            if (string.IsNullOrWhiteSpace(_modelsSearchText))
            {
                _filteredModelsList = _loadedModels.ToList();
            }
            else
            {
                _filteredModelsList = _loadedModels.Where(m =>
                    m != null && m.Name != null &&
                    m.Name.IndexOf(_modelsSearchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }
            OnPropertyChanged(nameof(FilteredModels));
        }

        private void UpdateFilteredAnimations()
        {
            if (string.IsNullOrWhiteSpace(_animationsSearchText))
            {
                _filteredAnimationsList = _animationModels.ToList();
            }
            else
            {
                _filteredAnimationsList = _animationModels.Where(a =>
                    a != null && a.Name != null &&
                    a.Name.IndexOf(_animationsSearchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }
            OnPropertyChanged(nameof(FilteredAnimations));
        }

        public int MeshPartCount => _selectedModelParts?.Count ?? 0;

        public bool HasSelectedModel => _selectedModel != null;
        public bool HasLoadedModels => _loadedModels.Count > 0;
        public bool HasAnimations => _animationModels.Count > 0;
        public bool HasMultipleModels => _loadedModels.Count >= 3;
        public bool HasMultipleAnimations => _animationModels.Count >= 3;

        // --- Commands / Actions ---

        /// <summary>
        /// Expands or collapses every collapsible section in the panel at once.
        /// </summary>
        public void SetAllSectionsExpanded(bool expanded)
        {
            IsModelsSectionExpanded = expanded;
            IsInspectorSectionExpanded = expanded;
            IsEnvironmentSectionExpanded = expanded;
            IsCameraSectionExpanded = expanded;
            IsLightingSectionExpanded = expanded;
            IsRenderSectionExpanded = expanded;
        }

        private void UpdateModeData()
        {
            if (_isMapMode)
            {
                LoadButtonText = "Map";
                LoadButtonTooltip = "Load MapGeometry";
                LoadButtonIcon = MaterialIconKind.Map;
            }
            else
            {
                LoadButtonText = "Model";
                LoadButtonTooltip = "Load Model";
                LoadButtonIcon = MaterialIconKind.CubeOutline;
            }
        }

        /// <summary>
        /// Switches the UI to the 3D Viewer mode (Viewport + Panel).
        /// </summary>
        public void ShowMainContent()
        {
            IsMainContentVisible = true;
        }

        /// <summary>
        /// Switches the UI to the Empty/Landing state.
        /// </summary>
        public void ShowEmptyState()
        {
            IsMainContentVisible = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
