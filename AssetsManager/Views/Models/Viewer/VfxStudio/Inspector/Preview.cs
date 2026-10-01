namespace AssetsManager.Views.Models.Viewer
{
    public partial class VfxInspectorModel
    {
        private string _bgMode = "Dark";
        private bool _showPreviewSky;
        private bool _showPreviewGrid = true;
        private bool _showPreviewGround;
        private bool _showPreviewStage;
        private VfxPreviewViewMode _previewViewMode = VfxPreviewViewMode.Lit;
        private bool _previewWireOverlay;
        private bool _previewShaders;
        private VfxPreviewCameraPreset _previewCameraPreset = VfxPreviewCameraPreset.Orbit;

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

        public VfxPreviewViewMode PreviewViewMode
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
            _previewViewMode == VfxPreviewViewMode.Lit ||
            _previewViewMode == VfxPreviewViewMode.Untextured;
        public bool EffectivePreviewWireOverlay => _previewWireOverlay && CanPreviewWireOverlay;

        public string PreviewViewModeText => _previewViewMode switch

        {
            VfxPreviewViewMode.Unshaded => "Unshaded",
            VfxPreviewViewMode.Untextured => "Untextured",
            VfxPreviewViewMode.Wireframe => "Wireframe",
            _ => "Lit"
        };

        public bool IsPreviewViewLit
        {
            get => _previewViewMode == VfxPreviewViewMode.Lit;
            set { if (value) PreviewViewMode = VfxPreviewViewMode.Lit; }
        }

        public bool IsPreviewViewUnshaded
        {
            get => _previewViewMode == VfxPreviewViewMode.Unshaded;
            set { if (value) PreviewViewMode = VfxPreviewViewMode.Unshaded; }
        }

        public bool IsPreviewViewUntextured
        {
            get => _previewViewMode == VfxPreviewViewMode.Untextured;
            set { if (value) PreviewViewMode = VfxPreviewViewMode.Untextured; }
        }

        public bool IsPreviewViewWireframe
        {
            get => _previewViewMode == VfxPreviewViewMode.Wireframe;
            set { if (value) PreviewViewMode = VfxPreviewViewMode.Wireframe; }
        }

        public VfxPreviewCameraPreset PreviewCameraPreset
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
            get => _previewCameraPreset == VfxPreviewCameraPreset.Game;
            set { if (value) PreviewCameraPreset = VfxPreviewCameraPreset.Game; }
        }

        public bool IsPreviewCameraOrbit
        {
            get => _previewCameraPreset == VfxPreviewCameraPreset.Orbit;
            set { if (value) PreviewCameraPreset = VfxPreviewCameraPreset.Orbit; }
        }

        public bool IsPreviewCameraTop
        {
            get => _previewCameraPreset == VfxPreviewCameraPreset.Top;
            set { if (value) PreviewCameraPreset = VfxPreviewCameraPreset.Top; }
        }

        public bool IsPreviewCameraFront
        {
            get => _previewCameraPreset == VfxPreviewCameraPreset.Front;
            set { if (value) PreviewCameraPreset = VfxPreviewCameraPreset.Front; }
        }

        public bool IsPreviewCameraSide
        {
            get => _previewCameraPreset == VfxPreviewCameraPreset.Side;
            set { if (value) PreviewCameraPreset = VfxPreviewCameraPreset.Side; }
        }
    }
}
