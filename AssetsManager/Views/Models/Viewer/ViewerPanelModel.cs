using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        private ViewerViewportModel _viewportViewModel;

        // --- Data Collections ---
        private readonly ObservableRangeCollection<SceneModel> _loadedModels = new();
        private readonly ObservableRangeCollection<AnimationModel> _animationModels = new();
        private ObservableRangeCollection<ModelPart> _selectedModelParts;
        private SceneModel _selectedModel;
        private AnimationModel _selectedAnimation;

        public ObservableRangeCollection<SceneModel> LoadedModels => _loadedModels;
        public ObservableRangeCollection<AnimationModel> AnimationModels => _animationModels;

        public ObservableRangeCollection<ModelPart> SelectedModelParts
        {
            get => _selectedModelParts;
            set { if (_selectedModelParts != value) { _selectedModelParts = value; OnPropertyChanged(); } }
        }

        public SceneModel SelectedModel
        {
            get => _selectedModel;
            set { if (_selectedModel != value) { _selectedModel = value; OnPropertyChanged(); } }
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
            MainContentRequested?.Invoke();
        }

        /// <summary>
        /// Switches the UI to the Empty/Landing state.
        /// </summary>
        public void ShowEmptyState()
        {
            IsMainContentVisible = false;
            EmptyStateRequested?.Invoke();
        }

        public event System.Action MainContentRequested;
        public event System.Action EmptyStateRequested;
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
