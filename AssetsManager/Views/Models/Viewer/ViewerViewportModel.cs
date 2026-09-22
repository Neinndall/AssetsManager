using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// ViewModel for the 3D Viewport Control.
    /// Manages the state of the viewport tools and layout.
    /// </summary>
    public class ViewerViewportModel : INotifyPropertyChanged
    {
        internal const double DefaultAmbientIntensity = 60.0;
        internal const double DefaultLightRotation = 101.30993247402021;
        internal const double DefaultLightHeight = 71.22532394572126;

        private bool _isFpsVisible = false;
        private bool _limitFps = false;
        private bool _isAutoRotateActive = false;
        private bool _isMaximized = false;
        private bool _isToolbarVisible = false;

        // --- Studio Lighting Properties ---
        private double _ambientIntensity = DefaultAmbientIntensity;
        private double _lightRotation = DefaultLightRotation; // Phi
        private double _lightHeight = DefaultLightHeight;     // Theta
        private double _fieldOfView = 45;
        private bool _isGroundVisible = false;
        private bool _isGridVisible = true;
        private bool _isMapStructuresVisible = true;
        private bool _isMapParticlesVisible = true;
        private string _sceneDisplayName = "No model loaded";

        public bool IsFpsVisible
        {
            get => _isFpsVisible;
            set { if (_isFpsVisible != value) { _isFpsVisible = value; OnPropertyChanged(); } }
        }

        public bool LimitFps
        {
            get => _limitFps;
            set { if (_limitFps != value) { _limitFps = value; OnPropertyChanged(); } }
        }

        private string _displayFps = "0";
        public string DisplayFps
        {
            get => _displayFps;
            set { if (_displayFps != value) { _displayFps = value; OnPropertyChanged(); } }
        }

        public bool IsAutoRotateActive
        {
            get => _isAutoRotateActive;
            set { if (_isAutoRotateActive != value) { _isAutoRotateActive = value; OnPropertyChanged(); } }
        }

        public bool IsMaximized
        {
            get => _isMaximized;
            set { if (_isMaximized != value) { _isMaximized = value; OnPropertyChanged(); } }
        }

        public bool IsToolbarVisible
        {
            get => _isToolbarVisible;
            set { if (_isToolbarVisible != value) { _isToolbarVisible = value; OnPropertyChanged(); } }
        }

        public bool IsGroundVisible
        {
            get => _isGroundVisible;
            set { if (_isGroundVisible != value) { _isGroundVisible = value; OnPropertyChanged(); } }
        }

        public bool IsGridVisible
        {
            get => _isGridVisible;
            set { if (_isGridVisible != value) { _isGridVisible = value; OnPropertyChanged(); } }
        }

        public bool IsMapStructuresVisible
        {
            get => _isMapStructuresVisible;
            set { if (_isMapStructuresVisible != value) { _isMapStructuresVisible = value; OnPropertyChanged(); } }
        }

        public bool IsMapParticlesVisible
        {
            get => _isMapParticlesVisible;
            set { if (_isMapParticlesVisible != value) { _isMapParticlesVisible = value; OnPropertyChanged(); } }
        }

        public string SceneDisplayName
        {
            get => _sceneDisplayName;
            private set { if (_sceneDisplayName != value) { _sceneDisplayName = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Updates the scene badge label based on the current model list state.
        /// Called by the ViewportControl whenever the loaded models collection changes.
        /// </summary>
        public void UpdateSceneDisplay(int modelCount, string firstModelName)
        {
            if (modelCount == 0)
                SceneDisplayName = "No model loaded";
            else if (modelCount == 1)
                SceneDisplayName = string.IsNullOrEmpty(firstModelName) ? "Untitled model" : firstModelName;
            else
                SceneDisplayName = $"{modelCount} models";
        }

        public double AmbientIntensity
        {
            get => _ambientIntensity;
            set { if (_ambientIntensity != value) { _ambientIntensity = value; OnPropertyChanged(); } }
        }

        public double LightRotation
        {
            get => _lightRotation;
            set { if (_lightRotation != value) { _lightRotation = value; OnPropertyChanged(); } }
        }

        public double LightHeight
        {
            get => _lightHeight;
            set { if (_lightHeight != value) { _lightHeight = value; OnPropertyChanged(); } }
        }

        public double FieldOfView
        {
            get => _fieldOfView;
            set { if (_fieldOfView != value) { _fieldOfView = value; OnPropertyChanged(); } }
        }

        // --- Environment Properties ---
        private bool _isTransparentBg = false;
        private bool _showSkybox = false;

        public bool IsTransparentBg
        {
            get => _isTransparentBg;
            set 
            { 
                if (_isTransparentBg != value) 
                { 
                    _isTransparentBg = value; 
                    if (_isTransparentBg) ShowSkybox = false;
                    OnPropertyChanged(); 
                } 
            }
        }

        public bool ShowSkybox
        {
            get => _showSkybox;
            set 
            { 
                if (_showSkybox != value) 
                { 
                    _showSkybox = value; 
                    if (_showSkybox) IsTransparentBg = false;
                    OnPropertyChanged(); 
                } 
            }
        }

        public void ResetStudioSettings()
        {
            AmbientIntensity = DefaultAmbientIntensity;
            LightRotation = DefaultLightRotation;
            LightHeight = DefaultLightHeight;
            FieldOfView = 45;
            IsGroundVisible = false;
            IsGridVisible = true;
            IsTransparentBg = false;
            ShowSkybox = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
