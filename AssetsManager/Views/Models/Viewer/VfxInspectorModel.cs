using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Vfx;

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
        private string _status = "Ready";
        private Brush _statusBrush = Brushes.LightGreen;

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
        private string _name;
        private VfxEmitterDefinition _emitterDef;
        private string _texturePath;
        private string _textureStatus;
        private Brush _textureStatusBrush;
        private string _meshPath;
        private string _meshStatus;
        private Brush _meshStatusBrush;
        private string _blendMode;
        private string _texDiv;
        private bool _isMeshPrimitive;
        private bool _disableBackfaceCull;
        private int _activeParticleCount;

        public event Action<VfxEmitterDiagnosticItem, bool> OnEnabledChanged;

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
    /// Primary view model for the VFX Inspector & Diagnostic Studio window.
    /// Manages root directory scanning, system selection, emitter live controls, and diagnostics.
    /// </summary>
    public class VfxInspectorModel : INotifyPropertyChanged
    {
        private string _rootPath;
        private string _selectedBin;
        private string _searchQuery;
        private VfxSystemDiagnosticItem _selectedSystem;
        private bool _isPlaying;
        private double _currentTime;
        private double _totalDuration = 5.0;
        private float _speed = 1.0f;
        private int _liveParticleCount;
        private string _bgMode = "Dark";
        private bool _isWireframe;
        private string _statusText = "Ready";

        public ObservableCollection<string> DetectedBins { get; } = new();
        public ObservableCollection<VfxSystemDiagnosticItem> Systems { get; } = new();
        public ObservableCollection<VfxEmitterDiagnosticItem> Emitters { get; } = new();
        public ObservableCollection<VfxTextureDiagnosticItem> Textures { get; } = new();
        public ObservableCollection<VfxMeshDiagnosticItem> Meshes { get; } = new();
        public ObservableCollection<string> LogMessages { get; } = new();

        public string RootPath
        {
            get => _rootPath;
            set { _rootPath = value; OnPropertyChanged(); }
        }

        public string SelectedBin
        {
            get => _selectedBin;
            set { _selectedBin = value; OnPropertyChanged(); }
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set { _searchQuery = value; OnPropertyChanged(); }
        }

        public VfxSystemDiagnosticItem SelectedSystem
        {
            get => _selectedSystem;
            set { _selectedSystem = value; OnPropertyChanged(); }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set { _isPlaying = value; OnPropertyChanged(); }
        }

        public double CurrentTime
        {
            get => _currentTime;
            set { _currentTime = value; OnPropertyChanged(); }
        }

        public double TotalDuration
        {
            get => _totalDuration;
            set { _totalDuration = value; OnPropertyChanged(); }
        }

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

        public bool IsWireframe
        {
            get => _isWireframe;
            set { _isWireframe = value; OnPropertyChanged(); }
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
