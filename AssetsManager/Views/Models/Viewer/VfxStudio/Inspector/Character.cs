using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AssetsManager.Views.Models.Viewer
{
    public partial class VfxInspectorModel
    {
        private bool _showChampionMesh = true;
        private bool _hasChampionMesh;
        private bool _hasCharacterSkeleton;
        private bool _characterEffectsEnabled;
        private bool _showCharacterArmature;
        private bool _showCharacterJointNames;
        private bool _characterAutoRotate;
        private bool _characterTransformGizmoEnabled = true;
        private bool _characterBackdropEnabled;
        private VfxCharacterBackdropOption _selectedCharacterBackdrop;
        private double _characterPositionX;
        private double _characterPositionY;
        private double _characterPositionZ;
        private double _characterRotationX;
        private double _characterRotationY;
        private double _characterRotationZ;
        private double _characterScaleMultiplier = 1d;

        public ObservableCollection<CharacterGameStateOption> CharacterGameStates { get; } = new();
        public ObservableCollection<VfxCharacterBackdropOption> CharacterBackdrops { get; } = new();
        public ObservableCollection<VfxCharacterSubmeshOption> CharacterSubmeshes { get; } = new();
        public ObservableCollection<VfxCharacterFormOption> CharacterForms { get; } = new();

        private VfxCharacterFormOption _selectedCharacterForm;

        public VfxCharacterFormOption SelectedCharacterForm
        {
            get => _selectedCharacterForm;
            set
            {
                if (ReferenceEquals(_selectedCharacterForm, value)) return;
                _selectedCharacterForm = value;
                OnPropertyChanged();
            }
        }

        public bool HasCharacterForms => CharacterForms.Count > 1;
        public bool HasCharacterGameStates => CharacterGameStates.Count > 0;

        /// <summary>Offers <paramref name="buffs"/> as toggles, on when <paramref name="enabled"/> holds them.</summary>
        internal void SetCharacterGameStates(IEnumerable<string> buffs, IReadOnlySet<string> enabled, Action changed)
        {
            CharacterGameStates.Clear();
            foreach (string buff in buffs ?? Array.Empty<string>())
                CharacterGameStates.Add(new CharacterGameStateOption(buff, enabled?.Contains(buff) == true, changed));
            OnPropertyChanged(nameof(HasCharacterGameStates));
        }

        public bool ShowChampionMesh
        {
            get => _showChampionMesh;
            set { _showChampionMesh = value; OnPropertyChanged(); }
        }

        public bool HasChampionMesh
        {
            get => _hasChampionMesh;
            set
            {
                if (_hasChampionMesh == value) return;
                _hasChampionMesh = value;
                OnPropertyChanged();
                NotifyViewportSelectionChanged();
                OnPropertyChanged(nameof(HasViewportContentControls));
                OnPropertyChanged(nameof(HasMapOnlyWorkspace));
            }
        }

        public bool HasCharacterSkeleton
        {
            get => _hasCharacterSkeleton;
            internal set
            {
                if (_hasCharacterSkeleton == value) return;
                _hasCharacterSkeleton = value;
                if (!value)
                {
                    _showCharacterArmature = false;
                    _showCharacterJointNames = false;
                    OnPropertyChanged(nameof(ShowCharacterArmature));
                    OnPropertyChanged(nameof(ShowCharacterJointNames));
                }
                OnPropertyChanged();
            }
        }

        public bool CharacterEffectsEnabled
        {
            get => _characterEffectsEnabled;
            set
            {
                if (_characterEffectsEnabled == value) return;
                _characterEffectsEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool ShowCharacterArmature
        {
            get => _showCharacterArmature;
            set
            {
                bool next = value && HasCharacterSkeleton;
                if (_showCharacterArmature == next) return;
                _showCharacterArmature = next;
                if (!next && _showCharacterJointNames)
                {
                    _showCharacterJointNames = false;
                    OnPropertyChanged(nameof(ShowCharacterJointNames));
                }
                OnPropertyChanged();
            }
        }

        public bool ShowCharacterJointNames
        {
            get => _showCharacterJointNames;
            set
            {
                bool next = value && HasCharacterSkeleton && ShowCharacterArmature;
                if (_showCharacterJointNames == next) return;
                _showCharacterJointNames = next;
                OnPropertyChanged();
            }
        }

        public bool CharacterAutoRotate
        {
            get => _characterAutoRotate;
            set
            {
                if (_characterAutoRotate == value) return;
                _characterAutoRotate = value;
                OnPropertyChanged();
            }
        }

        public bool CharacterTransformGizmoEnabled
        {
            get => _characterTransformGizmoEnabled;
            set
            {
                if (_characterTransformGizmoEnabled == value) return;
                _characterTransformGizmoEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool CharacterBackdropEnabled
        {
            get => _characterBackdropEnabled;
            set
            {
                if (_characterBackdropEnabled == value) return;
                _characterBackdropEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActiveCharacterBackdrop));
            }
        }

        public VfxCharacterBackdropOption SelectedCharacterBackdrop
        {
            get => _selectedCharacterBackdrop;
            set
            {
                if (ReferenceEquals(_selectedCharacterBackdrop, value)) return;
                _selectedCharacterBackdrop = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActiveCharacterBackdrop));
            }
        }

        public bool HasCharacterBackdropOptions => CharacterBackdrops.Count > 0;
        public bool HasCharacterSubmeshes => CharacterSubmeshes.Count > 0;
        public bool HasActiveCharacterBackdrop => IsSkinWorkspace && CharacterBackdropEnabled && SelectedCharacterBackdrop != null;
        public double CharacterPositionX { get => _characterPositionX; set { if (_characterPositionX != value) { _characterPositionX = value; OnPropertyChanged(); } } }

        public double CharacterPositionY { get => _characterPositionY; set { if (_characterPositionY != value) { _characterPositionY = value; OnPropertyChanged(); } } }

        public double CharacterPositionZ { get => _characterPositionZ; set { if (_characterPositionZ != value) { _characterPositionZ = value; OnPropertyChanged(); } } }

        public double CharacterRotationX { get => _characterRotationX; set { if (_characterRotationX != value) { _characterRotationX = value; OnPropertyChanged(); } } }

        public double CharacterRotationY { get => _characterRotationY; set { if (_characterRotationY != value) { _characterRotationY = value; OnPropertyChanged(); } } }

        public double CharacterRotationZ { get => _characterRotationZ; set { if (_characterRotationZ != value) { _characterRotationZ = value; OnPropertyChanged(); } } }

        public double CharacterScaleMultiplier
        {
            get => _characterScaleMultiplier;
            set
            {
                double next = double.IsFinite(value) ? Math.Clamp(value, 0.05d, 8d) : 1d;
                if (_characterScaleMultiplier == next) return;
                _characterScaleMultiplier = next;
                OnPropertyChanged();
            }
        }

        internal void NotifyCharacterCollectionsChanged()
        {
            OnPropertyChanged(nameof(HasCharacterBackdropOptions));
            OnPropertyChanged(nameof(HasCharacterSubmeshes));
            OnPropertyChanged(nameof(HasCharacterForms));
        }
    }
}
