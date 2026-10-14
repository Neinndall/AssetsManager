using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AssetsManager.Views.Models.Viewer
{
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
}
