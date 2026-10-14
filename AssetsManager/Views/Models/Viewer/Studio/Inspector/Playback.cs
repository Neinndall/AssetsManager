using System;
using AssetsManager.Services.Viewer.Vfx.Session;

namespace AssetsManager.Views.Models.Viewer
{
    public partial class StudioModel
    {
        private bool _isPlaying;
        private bool _isReplayState;
        private double _currentTime;
        private double _totalDuration = 5.0;
        private double _activeLoopStart;
        private double _activeLoopDuration = 0.0;
        private bool _isPreviewLoopEnabled = true;
        private float _speed = 1.0f;
        private int _liveParticleCount;
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
            set
            {
                if (_liveParticleCount == value) return;
                _liveParticleCount = value;
                OnPropertyChanged();
                if (_selectedMapNode == null)
                    OnPropertyChanged(nameof(ViewportSelectionDetail));
            }
        }
    }
}
