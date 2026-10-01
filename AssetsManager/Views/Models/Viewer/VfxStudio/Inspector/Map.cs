using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AssetsManager.Services.Viewer.Semantics;

namespace AssetsManager.Views.Models.Viewer
{
    public partial class VfxInspectorModel
    {
        private MapVariantData _selectedMapVariant;
        private MapBrowserNode _selectedMapNode;
        private bool _hasMapPreview;
        private bool _mapParticlesVisible;
        private bool _mapStructuresVisible;

        public ObservableCollection<MapVariantData> MapVariants { get; } = new();
        public ObservableCollection<MapVisibilityLayerOption> MapLayers { get; } = new();
        public ObservableCollection<MapVisibilityStateOption> MapTransformations { get; } = new();
        public ObservableCollection<MapVisibilityStateOption> MapSecondaryStates { get; } = new();
        public ObservableCollection<MapMutatorOption> MapMutators { get; } = new();

        public MapVariantData SelectedMapVariant
        {
            get => _selectedMapVariant;
            set
            {
                if (ReferenceEquals(_selectedMapVariant, value)) return;
                _selectedMapVariant = value;
                OnPropertyChanged();
            }
        }

        public bool HasMultipleMapVariants => MapVariants.Count > 1;
        public bool CanSelectMapVariant => HasMapPreview && HasMultipleMapVariants;
        public bool HasMapLayers => MapLayers.Count > 0;
        public bool HasMapTransformations => MapTransformations.Count > 1;
        public bool HasMapSecondaryStates => MapSecondaryStates.Count > 1;
        public bool HasMapMutators => MapMutators.Count > 0;

        /// <summary>Raised when the user asks for another map state; the owner loads and applies it.</summary>
        internal event Action<MapVisibilityState> MapVisibilityRequested;

        private MapVisibilityState _mapVisibility;
        private bool _isSyncingMapVisibility;
        private MapVisibilityStateOption _selectedMapTransformation;
        private MapVisibilityStateOption _selectedMapSecondaryState;

        internal MapVisibilityState MapVisibility => _mapVisibility;

        public MapVisibilityStateOption SelectedMapTransformation
        {
            get => _selectedMapTransformation;
            set
            {
                if (ReferenceEquals(_selectedMapTransformation, value)) return;
                _selectedMapTransformation = value;
                OnPropertyChanged();
                if (!_isSyncingMapVisibility && value is { IsCustom: false } && _mapVisibility != null)
                    RequestMapVisibility(_mapVisibility.WithFlags(value.Flags));
            }
        }

        public MapVisibilityStateOption SelectedMapSecondaryState
        {
            get => _selectedMapSecondaryState;
            set
            {
                if (ReferenceEquals(_selectedMapSecondaryState, value)) return;
                _selectedMapSecondaryState = value;
                OnPropertyChanged();
                if (!_isSyncingMapVisibility && value != null && _mapVisibility != null)
                    RequestMapVisibility(_mapVisibility.WithSecondaryFlags(value.Flags));
            }
        }

        public int ActiveMapLayerCount => MapLayers.Count(layer => layer.IsEnabled);
        public bool HasMapOnlyWorkspace => HasMapPreview && !HasChampionMesh;

        public bool HasMapPreview
        {
            get => _hasMapPreview;
            internal set
            {
                if (_hasMapPreview == value) return;
                _hasMapPreview = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSelectMapVariant));
                OnPropertyChanged(nameof(HasViewportContentControls));
                OnPropertyChanged(nameof(HasMapOnlyWorkspace));
            }
        }

        public MapBrowserNode SelectedMapNode
        {
            get => _selectedMapNode;
            set
            {
                if (ReferenceEquals(_selectedMapNode, value)) return;
                _selectedMapNode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedMapNode));
                OnPropertyChanged(nameof(ViewportSelectionTitle));
                OnPropertyChanged(nameof(ViewportSelectionDetail));
            }
        }

        public bool HasSelectedMapNode => _selectedMapNode != null;

        internal void SetMapVariants(IEnumerable<MapVariantData> variants)
        {
            MapVariants.Clear();
            foreach (MapVariantData variant in variants ?? Array.Empty<MapVariantData>())
                MapVariants.Add(variant);

            _selectedMapVariant = MapVariantData.Opening(MapVariants);
            OnPropertyChanged(nameof(SelectedMapVariant));
            OnPropertyChanged(nameof(HasMultipleMapVariants));
            OnPropertyChanged(nameof(CanSelectMapVariant));
        }

        /// <summary>
        /// Rebuilds every map-state control for one scene: named transformations and secondary states
        /// from the Map object, mutators from the controller graph and the raw mask bits as fallback.
        /// </summary>
        internal void SetMapVisibility(
            MapSceneVisibility visibility,
            IEnumerable<MapGeometryLayerData> layers,
            MapVisibilityState state)
        {
            _isSyncingMapVisibility = true;
            try
            {
                _mapVisibility = state;
                MapLayers.Clear();
                MapTransformations.Clear();
                MapSecondaryStates.Clear();
                MapMutators.Clear();
                _selectedMapTransformation = null;
                _selectedMapSecondaryState = null;

                if (visibility != null && state != null)
                {
                    MapVisibilityChoices choices = MapVisibilityChoices.Of(visibility, layers);
                    foreach (MapVisibilityLayerChoice layer in choices.Layers)
                        MapLayers.Add(new MapVisibilityLayerOption(layer.Layer, (state.Flags & layer.Layer.Flag) != 0, layer.Label));
                    foreach (MapVisibilityChoice transformation in choices.Transformations)
                        MapTransformations.Add(new MapVisibilityStateOption(transformation.Label, transformation.Flags));
                    foreach (MapVisibilityChoice secondary in choices.SecondaryStates)
                        MapSecondaryStates.Add(new MapVisibilityStateOption(secondary.Label, secondary.Flags));
                    foreach (string mutator in choices.Mutators)
                    {
                        MapMutators.Add(new MapMutatorOption(
                            mutator,
                            MapVisibilityChoices.MutatorLabel(mutator),
                            state.HasMutator(mutator),
                            OnMapMutatorChanged));
                    }
                }

                SyncMapVisibilitySelection();
            }
            finally
            {
                _isSyncingMapVisibility = false;
            }

            OnPropertyChanged(nameof(HasMapLayers));
            OnPropertyChanged(nameof(ActiveMapLayerCount));
            OnPropertyChanged(nameof(HasMapTransformations));
            OnPropertyChanged(nameof(HasMapSecondaryStates));
            OnPropertyChanged(nameof(HasMapMutators));
            OnPropertyChanged(nameof(SelectedMapTransformation));
            OnPropertyChanged(nameof(SelectedMapSecondaryState));
        }

        internal void ClearMapVisibility() =>
            SetMapVisibility(null, Array.Empty<MapGeometryLayerData>(), null);

        /// <summary>Reflects a state in the controls without raising a new request.</summary>
        internal void SyncMapVisibility(MapVisibilityState state)
        {
            if (state == null) return;
            _isSyncingMapVisibility = true;
            try
            {
                _mapVisibility = state;
                SyncMapVisibilitySelection();
            }
            finally
            {
                _isSyncingMapVisibility = false;
            }
            OnPropertyChanged(nameof(ActiveMapLayerCount));
            OnPropertyChanged(nameof(SelectedMapTransformation));
            OnPropertyChanged(nameof(SelectedMapSecondaryState));
        }

        /// <summary>Raw mask bits edited by the user; the transformation selector follows a matching preset.</summary>
        internal void RequestMapLayerFlags(int flags)
        {
            if (_mapVisibility != null)
                RequestMapVisibility(_mapVisibility.WithFlags(flags));
        }

        private void OnMapMutatorChanged()
        {
            if (_isSyncingMapVisibility || _mapVisibility == null)
                return;
            RequestMapVisibility(new MapVisibilityState(
                _mapVisibility.Flags,
                _mapVisibility.SecondaryFlags,
                MapMutators.Where(option => option.IsEnabled).Select(option => option.Name)));
        }

        private void RequestMapVisibility(MapVisibilityState state)
        {
            SyncMapVisibility(state);
            MapVisibilityRequested?.Invoke(state);
        }

        private void SyncMapVisibilitySelection()
        {
            MapVisibilityState state = _mapVisibility;
            if (state == null)
                return;

            foreach (MapVisibilityLayerOption layer in MapLayers)
                layer.IsEnabled = (state.Flags & (1 << layer.Index)) != 0;
            foreach (MapMutatorOption mutator in MapMutators)
                mutator.Sync(state.HasMutator(mutator.Name));

            MapVisibilityStateOption custom = MapTransformations.FirstOrDefault(option => option.IsCustom);
            MapVisibilityStateOption match = MapTransformations.FirstOrDefault(option =>
                !option.IsCustom && option.Flags == state.Flags);
            if (match != null)
            {
                if (custom != null)
                    MapTransformations.Remove(custom);
                _selectedMapTransformation = match;
            }
            else if (MapTransformations.Count > 0)
            {
                if (custom == null)
                {
                    custom = new MapVisibilityStateOption("Custom layers", state.Flags, isCustom: true);
                    MapTransformations.Add(custom);
                }
                _selectedMapTransformation = custom;
            }

            _selectedMapSecondaryState = MapSecondaryStates.FirstOrDefault(option =>
                option.Flags == state.SecondaryFlags);
        }

        public bool MapParticlesVisible
        {
            get => _mapParticlesVisible;
            set { if (_mapParticlesVisible != value) { _mapParticlesVisible = value; OnPropertyChanged(); } }
        }

        public bool MapStructuresVisible
        {
            get => _mapStructuresVisible;
            set { if (_mapStructuresVisible != value) { _mapStructuresVisible = value; OnPropertyChanged(); } }
        }

        public bool IsMapWorkspace => SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Map;
    }
}
