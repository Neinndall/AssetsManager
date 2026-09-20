using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Item model representing a single VFX system definition inside the inspector.
    /// </summary>
    public class VfxSystemDiagnosticItem : INotifyPropertyChanged
    {
        private string _name;
        private uint _pathHash;
        private VfxSystemDefinition _definition;
        private int _emitterCount;
        private int _textureCount;
        private int _meshCount;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public uint PathHash
        {
            get => _pathHash;
            set { _pathHash = value; OnPropertyChanged(); }
        }

        public VfxSystemDefinition Definition
        {
            get => _definition;
            set { _definition = value; OnPropertyChanged(); }
        }

        public int EmitterCount
        {
            get => _emitterCount;
            set { _emitterCount = value; OnPropertyChanged(); }
        }

        public int TextureCount
        {
            get => _textureCount;
            set { _textureCount = value; OnPropertyChanged(); }
        }

        public int MeshCount
        {
            get => _meshCount;
            set { _meshCount = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// Item model representing a single emitter inside a selected VFX system.
    /// Allows live toggling of emitter playback and diagnostic verification.
    /// </summary>
    public class VfxEmitterDiagnosticItem : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        private bool _isSolo = false;
        private bool _isMuted = false;
        private bool _isSelected;
        private string _name;
        private VfxEmitterDefinition _emitterDef;
        private string _texturePath;
        private string _textureSources;
        private string _textureStatus;
        private Brush _textureStatusBrush;
        private string _meshPath;
        private string _meshStatus;
        private Brush _meshStatusBrush;
        private string _blendMode;
        private string _texDiv;
        private bool _isMeshPrimitive;
        private string _primitiveKindName = "Billboard";
        private bool _disableBackfaceCull;
        private int _activeParticleCount;
        private double _timeStart;
        private double _timeDuration;

        public event Action<VfxEmitterDiagnosticItem, bool> OnEnabledChanged;
        public event Action<VfxEmitterDiagnosticItem> OnVisibilityStateChanged;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                    OnEnabledChanged?.Invoke(this, value);
                }
            }
        }

        public bool IsSolo
        {
            get => _isSolo;
            set
            {
                if (_isSolo != value)
                {
                    _isSolo = value;
                    OnPropertyChanged();
                    OnVisibilityStateChanged?.Invoke(this);
                }
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted != value)
                {
                    _isMuted = value;
                    OnPropertyChanged();
                    OnVisibilityStateChanged?.Invoke(this);
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public string PrimitiveKindName
        {
            get => _primitiveKindName;
            set { _primitiveKindName = value; OnPropertyChanged(); }
        }

        public double TimeStart
        {
            get => _timeStart;
            set { _timeStart = value; OnPropertyChanged(); }
        }

        public double TimeDuration
        {
            get => _timeDuration;
            set { _timeDuration = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public VfxEmitterDefinition EmitterDef
        {
            get => _emitterDef;
            set { _emitterDef = value; OnPropertyChanged(); }
        }

        public string TexturePath
        {
            get => _texturePath;
            set { _texturePath = value; OnPropertyChanged(); }
        }

        public string TextureSources
        {
            get => _textureSources;
            set { _textureSources = value; OnPropertyChanged(); }
        }

        public string TextureStatus
        {
            get => _textureStatus;
            set { _textureStatus = value; OnPropertyChanged(); }
        }

        public Brush TextureStatusBrush
        {
            get => _textureStatusBrush;
            set { _textureStatusBrush = value; OnPropertyChanged(); }
        }

        public string MeshPath
        {
            get => _meshPath;
            set { _meshPath = value; OnPropertyChanged(); }
        }

        public string MeshStatus
        {
            get => _meshStatus;
            set { _meshStatus = value; OnPropertyChanged(); }
        }

        public Brush MeshStatusBrush
        {
            get => _meshStatusBrush;
            set { _meshStatusBrush = value; OnPropertyChanged(); }
        }

        public string BlendMode
        {
            get => _blendMode;
            set { _blendMode = value; OnPropertyChanged(); }
        }

        public string TexDiv
        {
            get => _texDiv;
            set { _texDiv = value; OnPropertyChanged(); }
        }

        public bool IsMeshPrimitive
        {
            get => _isMeshPrimitive;
            set { _isMeshPrimitive = value; OnPropertyChanged(); }
        }

        public bool DisableBackfaceCull
        {
            get => _disableBackfaceCull;
            set { _disableBackfaceCull = value; OnPropertyChanged(); }
        }

        public int ActiveParticleCount
        {
            get => _activeParticleCount;
            set { _activeParticleCount = value; OnPropertyChanged(); }
        }

        private int _indexNumber;
        private Brush _trackBrush = Brushes.MediumTurquoise;
        private Thickness _trackMargin;
        private double _trackWidth = 100;
        private int _sourceOrder;

        public int SourceOrder
        {
            get => _sourceOrder;
            set { _sourceOrder = value; OnPropertyChanged(); }
        }

        public int IndexNumber
        {
            get => _indexNumber;
            set { _indexNumber = value; OnPropertyChanged(); }
        }

        public Brush TrackBrush
        {
            get => _trackBrush;
            set { _trackBrush = value; OnPropertyChanged(); }
        }

        private Brush _trackBorderBrush = Brushes.SlateBlue;
        public Brush TrackBorderBrush
        {
            get => _trackBorderBrush;
            set { _trackBorderBrush = value; OnPropertyChanged(); }
        }

        private BitmapSource _imagePreview;
        public BitmapSource ImagePreview
        {
            get => _imagePreview;
            set { _imagePreview = value; OnPropertyChanged(); }
        }

        public Thickness TrackMargin
        {
            get => _trackMargin;
            set { _trackMargin = value; OnPropertyChanged(); }
        }

        public double TrackWidth
        {
            get => _trackWidth;
            set { _trackWidth = value; OnPropertyChanged(); }
        }

        private bool _hasDelay;
        private double _delayTime;
        private Thickness _delayMarkerMargin;

        public bool HasDelay
        {
            get => _hasDelay;
            set { _hasDelay = value; OnPropertyChanged(); }
        }

        public double DelayTime
        {
            get => _delayTime;
            set { _delayTime = value; OnPropertyChanged(); }
        }

        public Thickness DelayMarkerMargin
        {
            get => _delayMarkerMargin;
            set { _delayMarkerMargin = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// Audit item for textures referenced by an effect system.
    /// </summary>
    public class VfxTextureDiagnosticItem : INotifyPropertyChanged
    {
        private string _authoredPath;
        private string _resolvedPath;
        private string _status;
        private Brush _statusBrush;
        private int _width;
        private int _height;
        private BitmapSource _imagePreview;
        private string _texDiv;

        public string AuthoredPath
        {
            get => _authoredPath;
            set { _authoredPath = value; OnPropertyChanged(); }
        }

        public string ResolvedPath
        {
            get => _resolvedPath;
            set { _resolvedPath = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public Brush StatusBrush
        {
            get => _statusBrush;
            set { _statusBrush = value; OnPropertyChanged(); }
        }

        public int Width
        {
            get => _width;
            set { _width = value; OnPropertyChanged(); }
        }

        public int Height
        {
            get => _height;
            set { _height = value; OnPropertyChanged(); }
        }

        public BitmapSource ImagePreview
        {
            get => _imagePreview;
            set { _imagePreview = value; OnPropertyChanged(); }
        }

        public string TexDiv
        {
            get => _texDiv;
            set { _texDiv = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// Audit item for static/skinned mesh primitives (.scb, .sco, .skn).
    /// </summary>
    public class VfxMeshDiagnosticItem : INotifyPropertyChanged
    {
        private string _authoredPath;
        private string _resolvedPath;
        private string _status;
        private Brush _statusBrush;
        private int _vertexCount;
        private int _faceCount;
        private string _format;

        public string AuthoredPath
        {
            get => _authoredPath;
            set { _authoredPath = value; OnPropertyChanged(); }
        }

        public string ResolvedPath
        {
            get => _resolvedPath;
            set { _resolvedPath = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public Brush StatusBrush
        {
            get => _statusBrush;
            set { _statusBrush = value; OnPropertyChanged(); }
        }

        public int VertexCount
        {
            get => _vertexCount;
            set { _vertexCount = value; OnPropertyChanged(); }
        }

        public int FaceCount
        {
            get => _faceCount;
            set { _faceCount = value; OnPropertyChanged(); }
        }

        public string Format
        {
            get => _format;
            set { _format = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// Item model representing a skin selection option inside the VFX Studio.
    /// </summary>
    public class VfxSkinItem : INotifyPropertyChanged
    {
        public VfxSkinItem()
        {
            Sections.Add(new VfxBrowserSection(this, "Systems", false));
            Sections.Add(new VfxBrowserSection(this, "Clips", true));
        }

        public ObservableCollection<VfxBrowserSection> Sections { get; } = new();
        public string Title => !string.IsNullOrWhiteSpace(BrowserTitle)
            ? BrowserTitle
            : SkinIndex == int.MaxValue
                ? System.IO.Path.GetFileName(BinPath)
                : $"Skin {SkinIndex}";
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
        }

        private string _displayName;
        private string _browserTitle;
        private string _binPath;
        private int _skinIndex;

        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        public string BrowserTitle
        {
            get => _browserTitle;
            set
            {
                if (_browserTitle == value) return;
                _browserTitle = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Title));
            }
        }

        public string BinPath
        {
            get => _binPath;
            set { _binPath = value; OnPropertyChanged(); }
        }

        public int SkinIndex
        {
            get => _skinIndex;
            set { _skinIndex = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// Primary view model for the VFX Inspector & Diagnostic Studio window.
    /// Manages root directory scanning, system selection, emitter live controls, and diagnostics.
    /// </summary>
    public class VfxInspectorModel : INotifyPropertyChanged
    {
        private string _rootPath;
        private VfxSkinItem _selectedSkin;
        private string _searchQuery;
        private VfxSystemDiagnosticItem _selectedSystem;
        private AnimationClipCatalogItem _selectedAnimation;
        private float? _animationParameter;
        private bool _isAnimationMode = true;
        private bool _isPlaying;
        private bool _isReplayState;
        private double _currentTime;
        private double _totalDuration = 5.0;
        private double _activeLoopStart;
        private double _activeLoopDuration = 0.0;
        private bool _isPreviewLoopEnabled;
        private float _speed = 1.0f;
        private int _liveParticleCount;
        private string _bgMode = "Dark";
        private bool _showPreviewGrid = true;
        private bool _showPreviewGround;
        private VfxPreviewWireframeMode _previewWireframeMode = VfxPreviewWireframeMode.Off;
        private VfxPreviewCameraPreset _previewCameraPreset = VfxPreviewCameraPreset.Game;
        private VfxEmitterDiagnosticItem _selectedEmitter;
        private string _statusText = "Ready";
        private bool _hasAnySolo;
        private bool _isAllMuted;
        private bool _showChampionMesh = true;
        private bool _hasChampionMesh;
        private int _playbackSeed = 1337;
        private VfxRigPreset _rigPreset = VfxRigPreset.Still;

        public VfxRigPreset RigPreset
        {
            get => _rigPreset;
            set
            {
                if (_rigPreset != value)
                {
                    _rigPreset = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RigPresetText));
                    OnPropertyChanged(nameof(RigPresetIcon));
                    OnPropertyChanged(nameof(IsStillRig));
                    OnPropertyChanged(nameof(IsBurstRig));
                    OnPropertyChanged(nameof(IsMissileRig));
                    OnPropertyChanged(nameof(IsTrailRig));
                }
            }
        }

        public string RigPresetText => _rigPreset switch
        {
            VfxRigPreset.Missile => "Missile",
            VfxRigPreset.Trail => "Trail",
            VfxRigPreset.Burst => "Burst",
            _ => "Still"
        };

        public string RigPresetIcon => _rigPreset switch
        {
            VfxRigPreset.Missile => "RocketLaunchOutline",
            VfxRigPreset.Trail => "RotateRight",
            VfxRigPreset.Burst => "Sparkles",
            _ => "CrosshairsGps"
        };

        public bool IsStillRig => _rigPreset == VfxRigPreset.Still;
        public bool IsBurstRig => _rigPreset == VfxRigPreset.Burst;
        public bool IsMissileRig => _rigPreset == VfxRigPreset.Missile;
        public bool IsTrailRig => _rigPreset == VfxRigPreset.Trail;

        public ObservableCollection<VfxBrowserFolder> BrowserRoots { get; } = new();
        public ObservableCollection<VfxSkinItem> DetectedSkins { get; } = new();
        public ObservableCollection<AnimationClipCatalogItem> DetectedAnimations { get; } = new();
        public ObservableCollection<float> AnimationParameterValues { get; } = new();
        public ObservableCollection<VfxSystemDiagnosticItem> Systems { get; } = new();
        public ObservableCollection<VfxEmitterDiagnosticItem> Emitters { get; } = new();
        public ObservableCollection<VfxTextureDiagnosticItem> Textures { get; } = new();
        public ObservableCollection<VfxMeshDiagnosticItem> Meshes { get; } = new();
        public ObservableCollection<string> LogMessages { get; } = new();

        public bool IsAnimationMode
        {
            get => _isAnimationMode;
            set
            {
                _isAnimationMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRawSystemsMode));
                OnPropertyChanged(nameof(HasStandaloneSystem));
            }
        }

        public bool IsRawSystemsMode
        {
            get => !_isAnimationMode;
            set { IsAnimationMode = !value; }
        }

        public AnimationClipCatalogItem SelectedAnimation
        {
            get => _selectedAnimation;
            set
            {
                _selectedAnimation = value;
                OnPropertyChanged();
            }
        }

        public float? AnimationParameter
        {
            get => _animationParameter;
            set
            {
                if (_animationParameter == value) return;
                _animationParameter = value;
                OnPropertyChanged();
            }
        }

        public bool HasAnimationParameters => AnimationParameterValues.Count > 1;

        public void SetAnimationParameterOptions(
            IReadOnlyList<float> values,
            float? selected)
        {
            AnimationParameterValues.Clear();
            foreach (float value in values ?? Array.Empty<float>())
                AnimationParameterValues.Add(value);
            OnPropertyChanged(nameof(HasAnimationParameters));
            AnimationParameter = selected;
        }

        public bool HasAnySolo
        {
            get => _hasAnySolo;
            set { _hasAnySolo = value; OnPropertyChanged(); }
        }

        public bool IsAllMuted
        {
            get => _isAllMuted;
            set { _isAllMuted = value; OnPropertyChanged(); }
        }

        public bool ShowChampionMesh
        {
            get => _showChampionMesh;
            set { _showChampionMesh = value; OnPropertyChanged(); }
        }

        public bool HasChampionMesh
        {
            get => _hasChampionMesh;
            set { _hasChampionMesh = value; OnPropertyChanged(); }
        }

        public int PlaybackSeed
        {
            get => _playbackSeed;
            set
            {
                if (_playbackSeed == value) return;
                _playbackSeed = value;
                OnPropertyChanged();
            }
        }

        public double ActiveLoopStart
        {
            get => _activeLoopStart;
            set
            {
                if (Math.Abs(_activeLoopStart - value) <= 0.0001) return;
                _activeLoopStart = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveLoopDurationText));
            }
        }

        public double ActiveLoopDuration
        {
            get => _activeLoopDuration;
            set
            {
                if (Math.Abs(_activeLoopDuration - value) <= 0.0001) return;
                _activeLoopDuration = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveLoopDurationText));
            }
        }

        public bool IsPreviewLoopEnabled
        {
            get => _isPreviewLoopEnabled;
            set
            {
                _isPreviewLoopEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveLoopDurationText));
            }
        }

        public string ActiveLoopDurationText => _isPreviewLoopEnabled && _activeLoopDuration > _activeLoopStart
            ? $" · PREVIEW ↺ {_activeLoopStart:F2}–{_activeLoopDuration:F2}s"
            : string.Empty;

        public string RootPath
        {
            get => _rootPath;
            set { _rootPath = value; OnPropertyChanged(); }
        }

        public VfxSkinItem SelectedSkin
        {
            get => _selectedSkin;
            set { _selectedSkin = value; OnPropertyChanged(); }
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set { _searchQuery = value; OnPropertyChanged(); }
        }

        private string _emitterFilterText = string.Empty;
        public string EmitterFilterText
        {
            get => _emitterFilterText;
            set
            {
                if (_emitterFilterText != value)
                {
                    _emitterFilterText = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasEmitterFilter));
                }
            }
        }

        public bool HasEmitterFilter => !string.IsNullOrWhiteSpace(_emitterFilterText);

        public VfxSystemDiagnosticItem SelectedSystem
        {
            get => _selectedSystem;
            set
            {
                _selectedSystem = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStandaloneSystem));
            }
        }

        public bool HasStandaloneSystem => IsRawSystemsMode && SelectedSystem != null;

        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying != value)
                {
                    _isPlaying = value;
                    OnPropertyChanged();
                    UpdateReplayState();
                }
            }
        }

        public double CurrentTime
        {
            get => _currentTime;
            set
            {
                if (Math.Abs(_currentTime - value) > 0.0001)
                {
                    _currentTime = value;
                    OnPropertyChanged();
                    UpdateReplayState();
                }
            }
        }

        public double TotalDuration
        {
            get => _totalDuration;
            set
            {
                if (Math.Abs(_totalDuration - value) > 0.0001)
                {
                    _totalDuration = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(QuarterDuration));
                    OnPropertyChanged(nameof(HalfDuration));
                    OnPropertyChanged(nameof(ThreeQuarterDuration));
                    UpdateReplayState();
                }
            }
        }

        public bool IsReplayState
        {
            get => _isReplayState;
            set
            {
                if (_isReplayState != value)
                {
                    _isReplayState = value;
                    OnPropertyChanged();
                }
            }
        }

        private void UpdateReplayState()
        {
            bool newState = _isPlaying || (_totalDuration > 0 && _currentTime >= _totalDuration - 0.02);
            if (_isReplayState != newState)
            {
                _isReplayState = newState;
                OnPropertyChanged(nameof(IsReplayState));
            }
        }

        public double QuarterDuration => _totalDuration * 0.25;
        public double HalfDuration => _totalDuration * 0.5;
        public double ThreeQuarterDuration => _totalDuration * 0.75;

        public float Speed
        {
            get => _speed;
            set { _speed = value; OnPropertyChanged(); }
        }

        public int LiveParticleCount
        {
            get => _liveParticleCount;
            set { _liveParticleCount = value; OnPropertyChanged(); }
        }

        public string BgMode
        {
            get => _bgMode;
            set { _bgMode = value; OnPropertyChanged(); }
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

        public int PreviewDisplayCount =>
            (_showPreviewGrid ? 1 : 0) +
            (_showPreviewGround ? 1 : 0);

        public VfxPreviewWireframeMode PreviewWireframeMode
        {
            get => _previewWireframeMode;
            set
            {
                if (_previewWireframeMode == value) return;
                _previewWireframeMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewWireframeText));
                OnPropertyChanged(nameof(IsPreviewWireframeOff));
                OnPropertyChanged(nameof(IsPreviewWireframeOnly));
                OnPropertyChanged(nameof(IsPreviewWireframeOverlay));
            }
        }

        public string PreviewWireframeText => _previewWireframeMode switch
        {
            VfxPreviewWireframeMode.Only => "Wire",
            VfxPreviewWireframeMode.Overlay => "Overlay",
            _ => "Shaded"
        };

        public bool IsPreviewWireframeOff
        {
            get => _previewWireframeMode == VfxPreviewWireframeMode.Off;
            set { if (value) PreviewWireframeMode = VfxPreviewWireframeMode.Off; }
        }

        public bool IsPreviewWireframeOnly
        {
            get => _previewWireframeMode == VfxPreviewWireframeMode.Only;
            set { if (value) PreviewWireframeMode = VfxPreviewWireframeMode.Only; }
        }

        public bool IsPreviewWireframeOverlay
        {
            get => _previewWireframeMode == VfxPreviewWireframeMode.Overlay;
            set { if (value) PreviewWireframeMode = VfxPreviewWireframeMode.Overlay; }
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

        public VfxEmitterDiagnosticItem SelectedEmitter
        {
            get => _selectedEmitter;
            set
            {
                if (ReferenceEquals(_selectedEmitter, value)) return;
                if (_selectedEmitter != null) _selectedEmitter.IsSelected = false;
                _selectedEmitter = value;
                if (_selectedEmitter != null) _selectedEmitter.IsSelected = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedEmitter));
            }
        }

        public bool HasSelectedEmitter => _selectedEmitter != null;

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
