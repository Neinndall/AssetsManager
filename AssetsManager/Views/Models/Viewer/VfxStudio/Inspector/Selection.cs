using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AssetsManager.Views.Models.Viewer
{
    public partial class VfxInspectorModel
    {
        private VfxSystemDiagnosticItem _selectedSystem;
        private AnimationClipCatalogItem _selectedAnimation;
        private VfxSpellBrowserItem _selectedSpell;
        private float? _animationParameter;
        private bool _isAnimationMode = true;
        private VfxEmitterDiagnosticItem _selectedEmitter;
        private bool _hasAnySolo;
        private bool _isAllMuted;

        public ObservableCollection<AnimationClipCatalogItem> DetectedAnimations { get; } = new();
        public ObservableCollection<float> AnimationParameterValues { get; } = new();
        public ObservableCollection<VfxSystemDiagnosticItem> Systems { get; } = new();
        public ObservableCollection<VfxEmitterDiagnosticItem> Emitters { get; } = new();
        public ObservableCollection<VfxTextureDiagnosticItem> Textures { get; } = new();

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
                NotifyViewportSelectionChanged();
            }
        }

        public VfxSpellBrowserItem SelectedSpell
        {
            get => _selectedSpell;
            set
            {
                _selectedSpell = value;
                OnPropertyChanged();
                NotifyViewportSelectionChanged();
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
                OnPropertyChanged(nameof(ViewportSelectionTitle));
                OnPropertyChanged(nameof(ViewportSelectionDetail));
            }
        }

        public bool HasStandaloneSystem => IsRawSystemsMode && SelectedSystem != null;

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
            }
        }
    }
}
