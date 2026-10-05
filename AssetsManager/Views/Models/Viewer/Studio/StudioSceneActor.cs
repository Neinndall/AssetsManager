using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// One Character placed in a Skin workspace scene. It keeps only lightweight identity, placement
    /// and navigation memory; decoded models, animation state and VFX sessions stay owned by the Studio
    /// viewport so a scene can switch its focused actor without reloading any of them.
    /// </summary>
    public sealed class StudioSceneActor : INotifyPropertyChanged
    {
        private bool _isFocused;
        private bool _isSelected;
        private bool _isVisible = true;
        private bool _isLoading;
        private string _statusText;

        internal StudioSceneActor(StudioSkinItem skin)
        {
            Skin = skin ?? throw new ArgumentNullException(nameof(skin));
        }

        internal StudioSkinItem Skin { get; }

        public string Title => string.IsNullOrWhiteSpace(Skin.OwnerName)
            ? Skin.Title
            : $"{Skin.OwnerName} · {Skin.Title}";

        public string Subtitle => Skin.DisplayName ?? Skin.BinPath;

        public bool IsFocused
        {
            get => _isFocused;
            internal set
            {
                if (_isFocused == value) return;
                _isFocused = value;
                OnPropertyChanged();
            }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                OnPropertyChanged();
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

        public bool IsLoading
        {
            get => _isLoading;
            internal set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Short playback summary shown under the actor title (active clip or load state).</summary>
        public string StatusText
        {
            get => _statusText;
            internal set
            {
                if (string.Equals(_statusText, value, StringComparison.Ordinal)) return;
                _statusText = value;
                OnPropertyChanged();
            }
        }

        // Placement in preview space. The focused actor mirrors these values through the Inspector.
        internal double PositionX { get; set; }
        internal double PositionY { get; set; }
        internal double PositionZ { get; set; }
        internal double RotationX { get; set; }
        internal double RotationY { get; set; }
        internal double RotationZ { get; set; }
        internal double ScaleMultiplier { get; set; } = 1d;
        internal bool PlacementCustomized { get; set; }
        internal string PlacedOnKey { get; set; }
        internal Dictionary<uint, bool> SubmeshOverrides { get; } = new();
        internal Dictionary<uint, string> TextureOverrides { get; } = new();
        internal List<SynchronizedAnimationSource> ImportedAnimations { get; } = new();

        /// <summary>Each Character keeps its own transport: a paused actor stays paused off focus.</summary>
        internal bool IsPlaybackPaused { get; set; }
        /// <summary>The actor was left in bind pose, which no remembered clip describes.</summary>
        internal bool ShowsBindPose { get; set; }
        /// <summary>The GAME STATE buffs turned on for this actor; they follow it in and out of focus.</summary>
        internal HashSet<string> EnabledGameStates { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal uint? SelectedCharacterFormPathHash { get; set; }

        // Navigation memory restored when the actor regains focus.
        internal uint? SelectedSystemPathHash { get; set; }
        internal string SelectedAnimationFilePath { get; set; }
        internal uint? SelectedAnimationGraphPathHash { get; set; }
        internal uint? SelectedAnimationOwnerPathHash { get; set; }
        internal uint? SelectedSpellPathHash { get; set; }
        internal float? AnimationParameter { get; set; }

        /// <summary>Copies lightweight actor state into another scene without sharing mutable state or runtime resources.</summary>
        internal StudioSceneActor CopyForBackdrop()
        {
            var copy = new StudioSceneActor(Skin)
            {
                IsSelected = IsSelected,
                IsVisible = IsVisible,
                PositionX = PositionX,
                PositionY = PositionY,
                PositionZ = PositionZ,
                RotationX = RotationX,
                RotationY = RotationY,
                RotationZ = RotationZ,
                ScaleMultiplier = ScaleMultiplier,
                IsPlaybackPaused = IsPlaybackPaused,
                ShowsBindPose = ShowsBindPose,
                SelectedCharacterFormPathHash = SelectedCharacterFormPathHash,
                SelectedSystemPathHash = SelectedSystemPathHash,
                SelectedAnimationFilePath = SelectedAnimationFilePath,
                SelectedAnimationGraphPathHash = SelectedAnimationGraphPathHash,
                SelectedAnimationOwnerPathHash = SelectedAnimationOwnerPathHash,
                SelectedSpellPathHash = SelectedSpellPathHash,
                AnimationParameter = AnimationParameter
            };
            foreach (var pair in SubmeshOverrides)
                copy.SubmeshOverrides.Add(pair.Key, pair.Value);
            foreach (var pair in TextureOverrides) copy.TextureOverrides[pair.Key] = pair.Value;
            copy.ImportedAnimations.AddRange(ImportedAnimations);
            copy.EnabledGameStates.UnionWith(EnabledGameStates);
            return copy;
        }

        internal bool HasSkin(StudioSkinItem skin) => IsSameSkin(Skin, skin);

        internal static bool IsSameSkin(StudioSkinItem left, StudioSkinItem right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (string.IsNullOrWhiteSpace(left?.IdentityPath) || string.IsNullOrWhiteSpace(right?.IdentityPath)) return false;
            return string.Equals(
                Path.GetFullPath(left.IdentityPath),
                Path.GetFullPath(right.IdentityPath),
                StringComparison.OrdinalIgnoreCase);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
