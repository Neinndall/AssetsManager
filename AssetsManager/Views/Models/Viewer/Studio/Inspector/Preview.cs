namespace AssetsManager.Views.Models.Viewer
{
    public partial class StudioModel
    {
        private string _bgMode = "Dark";
        private bool _showPreviewSky;
        private bool _showPreviewGrid = true;
        private bool _showPreviewGround;
        private bool _showPreviewStage;
        private StudioViewMode _previewViewMode = StudioViewMode.Lit;
        private bool _previewWireOverlay;
        private bool _previewShaders;
        private StudioCameraPreset _previewCameraPreset = StudioCameraPreset.Orbit;

        public string BgMode
        {
            get => _bgMode;
            set { _bgMode = value; OnPropertyChanged(); }
        }

        public bool ShowPreviewSky
        {
            get => _showPreviewSky;
            set
            {
                if (_showPreviewSky == value) return;
                _showPreviewSky = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewDisplayCount));
            }
        }

        public bool ShowPreviewGrid
        {
            get => _showPreviewGrid;
            set
            {
                if (_showPreviewGrid == value) return;
                _showPreviewGrid = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewDisplayCount));
            }
        }

        public bool ShowPreviewGround
        {
            get => _showPreviewGround;
            set
            {
                if (_showPreviewGround == value) return;
                _showPreviewGround = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewDisplayCount));
            }
        }

        public bool ShowPreviewStage
        {
            get => _showPreviewStage;
            set
            {
                if (_showPreviewStage == value) return;
                _showPreviewStage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewDisplayCount));
            }
        }

        public int PreviewDisplayCount =>
            (_showPreviewSky ? 1 : 0) +
            (_showPreviewGrid ? 1 : 0) +
            (_showPreviewGround ? 1 : 0) +
            (_showPreviewStage ? 1 : 0);

        public StudioViewMode PreviewViewMode
        {
            get => _previewViewMode;
            set
            {
                if (_previewViewMode == value) return;
                _previewViewMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewViewModeText));
                OnPropertyChanged(nameof(IsPreviewViewLit));
                OnPropertyChanged(nameof(IsPreviewViewUnshaded));
                OnPropertyChanged(nameof(IsPreviewViewUntextured));
                OnPropertyChanged(nameof(IsPreviewViewWireframe));
                OnPropertyChanged(nameof(CanPreviewWireOverlay));
                OnPropertyChanged(nameof(EffectivePreviewWireOverlay));
            }
        }

        public bool PreviewWireOverlay
        {
            get => _previewWireOverlay;
            set
            {
                if (_previewWireOverlay == value) return;
                _previewWireOverlay = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectivePreviewWireOverlay));
            }
        }

        public bool PreviewShaders
        {
            get => _previewShaders;
            set
            {
                if (_previewShaders == value) return;
                _previewShaders = value;
                OnPropertyChanged();
            }
        }

        public bool CanPreviewWireOverlay =>
            _previewViewMode == StudioViewMode.Lit ||
            _previewViewMode == StudioViewMode.Untextured;
        public bool EffectivePreviewWireOverlay => _previewWireOverlay && CanPreviewWireOverlay;

        public string PreviewViewModeText => _previewViewMode switch

        {
            StudioViewMode.Unshaded => "Unshaded",
            StudioViewMode.Untextured => "Untextured",
            StudioViewMode.Wireframe => "Wireframe",
            _ => "Lit"
        };

        public bool IsPreviewViewLit
        {
            get => _previewViewMode == StudioViewMode.Lit;
            set { if (value) PreviewViewMode = StudioViewMode.Lit; }
        }

        public bool IsPreviewViewUnshaded
        {
            get => _previewViewMode == StudioViewMode.Unshaded;
            set { if (value) PreviewViewMode = StudioViewMode.Unshaded; }
        }

        public bool IsPreviewViewUntextured
        {
            get => _previewViewMode == StudioViewMode.Untextured;
            set { if (value) PreviewViewMode = StudioViewMode.Untextured; }
        }

        public bool IsPreviewViewWireframe
        {
            get => _previewViewMode == StudioViewMode.Wireframe;
            set { if (value) PreviewViewMode = StudioViewMode.Wireframe; }
        }

        public StudioCameraPreset PreviewCameraPreset
        {
            get => _previewCameraPreset;
            set
            {
                if (_previewCameraPreset == value) return;
                _previewCameraPreset = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewCameraText));
                OnPropertyChanged(nameof(IsPreviewCameraGame));
                OnPropertyChanged(nameof(IsPreviewCameraOrbit));
                OnPropertyChanged(nameof(IsPreviewCameraTop));
                OnPropertyChanged(nameof(IsPreviewCameraFront));
                OnPropertyChanged(nameof(IsPreviewCameraSide));
            }
        }

        public string PreviewCameraText => _previewCameraPreset.ToString();

        public bool IsPreviewCameraGame
        {
            get => _previewCameraPreset == StudioCameraPreset.Game;
            set { if (value) PreviewCameraPreset = StudioCameraPreset.Game; }
        }

        public bool IsPreviewCameraOrbit
        {
            get => _previewCameraPreset == StudioCameraPreset.Orbit;
            set { if (value) PreviewCameraPreset = StudioCameraPreset.Orbit; }
        }

        public bool IsPreviewCameraTop
        {
            get => _previewCameraPreset == StudioCameraPreset.Top;
            set { if (value) PreviewCameraPreset = StudioCameraPreset.Top; }
        }

        public bool IsPreviewCameraFront
        {
            get => _previewCameraPreset == StudioCameraPreset.Front;
            set { if (value) PreviewCameraPreset = StudioCameraPreset.Front; }
        }

        public bool IsPreviewCameraSide
        {
            get => _previewCameraPreset == StudioCameraPreset.Side;
            set { if (value) PreviewCameraPreset = StudioCameraPreset.Side; }
        }
    }
}
