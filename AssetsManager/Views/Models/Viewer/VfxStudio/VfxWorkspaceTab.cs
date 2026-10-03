using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    public enum VfxWorkspaceTabKind
    {
        Skin,
        Map
    }

    /// <summary>
    /// One independently addressable VFX Studio workspace document. A Skin tab is a scene of one or
    /// more Character actors; a MAP tab carries its browser node as Payload. The tab only owns navigation
    /// state; heavyweight Champion/MAP resources remain owned by VfxInspectorControl and are swapped
    /// into the single viewport when the tab becomes active.
    /// </summary>
    public sealed class VfxWorkspaceTab : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _key;
        private string _title;
        private string _subtitle;
        private object _payload;
        private VfxSceneActor _focusedActor;

        public VfxWorkspaceTab()
        {
            Actors.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ExtraActorCount));
                OnPropertyChanged(nameof(HasExtraActors));
            };
        }

        public string Key
        {
            get => _key;
            internal set
            {
                if (string.Equals(_key, value, System.StringComparison.Ordinal)) return;
                _key = value;
                OnPropertyChanged();
            }
        }

        public string Title
        {
            get => _title;
            internal set
            {
                if (string.Equals(_title, value, System.StringComparison.Ordinal)) return;
                _title = value;
                OnPropertyChanged();
            }
        }

        public string Subtitle
        {
            get => _subtitle;
            internal set
            {
                if (string.Equals(_subtitle, value, System.StringComparison.Ordinal)) return;
                _subtitle = value;
                OnPropertyChanged();
            }
        }

        public VfxWorkspaceTabKind Kind { get; init; }

        internal object Payload
        {
            get => _payload;
            set
            {
                if (ReferenceEquals(_payload, value)) return;
                _payload = value;
                OnPropertyChanged();
            }
        }

        // Scene-level Skin workspace state. Every Character keeps its own placement and navigation
        // memory in Actors; the single Studio viewport still owns every decoded model, MAP scene and
        // GPU resource.
        internal VfxWorkspaceCameraState CameraState { get; set; }
        internal bool PreserveBackdropFraming { get; set; }
        internal bool CharacterEffectsEnabled { get; set; }
        internal bool MapEffectsEnabled { get; set; }
        internal bool ShadersEnabled { get; set; }
        internal bool StructuresVisible { get; set; }
        internal string BackdropTitle { get; set; }
        internal string BackdropSourceKey { get; set; }
        internal bool CharacterBackdropEnabled { get; set; }
        internal string CharacterBackdropKey { get; set; }
        internal MapVisibilityState CharacterBackdropVisibility { get; set; }

        /// <summary>Characters composed into this Skin scene, in insertion order.</summary>
        public ObservableCollection<VfxSceneActor> Actors { get; } = new();
        internal VfxSceneActor SelectionAnchor { get; set; }

        /// <summary>The actor driven by the Inspector, browser, timeline and gizmo.</summary>
        public VfxSceneActor FocusedActor
        {
            get => _focusedActor;
            internal set
            {
                if (ReferenceEquals(_focusedActor, value)) return;
                if (_focusedActor != null) _focusedActor.IsFocused = false;
                _focusedActor = value;
                if (_focusedActor != null)
                {
                    _focusedActor.IsFocused = true;
                    if (SelectionAnchor == null)
                    {
                        SelectionAnchor = _focusedActor;
                        _focusedActor.IsSelected = true;
                    }
                    Title = BackdropTitle == null ? _focusedActor.Title : $"{BackdropTitle} · {_focusedActor.Title}";
                    Subtitle = _focusedActor.Subtitle;
                }
                OnPropertyChanged();
            }
        }

        /// <summary>The scene every backdrop copy descends from: this tab, or the one it was copied from.</summary>
        internal string OriginSceneKey => BackdropSourceKey ?? Key;

        internal VfxWorkspaceTab CopyForBackdrop(string key, string mapKey, string mapTitle)
        {
            var copy = new VfxWorkspaceTab
            {
                Key = key,
                Kind = VfxWorkspaceTabKind.Skin,
                BackdropTitle = mapTitle,
                CameraState = CameraState,
                PreserveBackdropFraming = true,
                BackdropSourceKey = OriginSceneKey,
                CharacterBackdropEnabled = true,
                CharacterBackdropKey = mapKey,
                CharacterEffectsEnabled = CharacterEffectsEnabled,
                MapEffectsEnabled = MapEffectsEnabled,
                ShadersEnabled = ShadersEnabled,
                StructuresVisible = StructuresVisible
            };
            foreach (VfxSceneActor actor in Actors)
            {
                VfxSceneActor cloned = actor.CopyForBackdrop();
                copy.Actors.Add(cloned);
                if (ReferenceEquals(actor, FocusedActor))
                    copy.FocusedActor = cloned;
            }
            return copy;
        }

        public int ExtraActorCount => Math.Max(0, Actors.Count - 1);
        public bool HasExtraActors => Actors.Count > 1;

        public bool IsSelected
        {
            get => _isSelected;
            internal set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
