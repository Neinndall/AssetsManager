using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Vector = System.Windows.Vector;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Authoring;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using Microsoft.Win32;

namespace AssetsManager.Views.Controls.Viewer
{
    /// <summary>
    /// Code-behind for the VFX Inspector & Diagnostic Studio.
    /// Provides deep inspection of LoL champion VFX definitions, emitters, .scb meshes, textures, and OpenGL rendering.
    /// </summary>
    public partial class VfxInspectorControl : UserControl
    {
        private readonly VfxInspectorModel _model;
        private Silk.NET.OpenGL.GL _gl;
        private VfxRenderSession _vfxRenderer;
        private VfxLoadingService.Bundle _activeBundle;
        private bool _isCleanedUp;
        private bool _isActive;
        private bool _isGlStarted;
        private bool _discardNextSimulationDelta;
        private bool _isExitPending;
        private bool _isBulkEmitterStateChange;
        private bool _isUpdatingRigControls;
        private bool _isUpdatingChancePinControls;
        private bool _isUpdatingAnimationParameter;
        private VfxSystemDiagnosticItem _pendingSystem;
        private VfxSystemDiagnosticItem _inspectedSystem;
        private VfxSpellBrowserItem _pendingSpell;
        private VfxSpellPreviewPlan _activeSpellPlan;
        private GlMeshRenderer _championMeshRenderer;
        private SceneModel _championModel;
        private AnimationService _championAnimationService;
        private LeagueToolkit.Core.Animation.RigResource _championBindSkeleton;
        private Func<string, uint, Matrix4x4?> _championBindBoneTransformProvider;
        private Matrix4x4[] _championBindSkinningMatrices = Array.Empty<Matrix4x4>();
        private Matrix4x4[] _championBindWorldTransforms = Array.Empty<Matrix4x4>();
        private VfxClipCatalog _clipCatalog;
        private AnimationClipCatalogItem _activeAnimationClip;
        private readonly Dictionary<ModelPart, bool> _animationBasePartVisibility = new();
        private readonly HashSet<uint> _animationBaseHiddenSubmeshes = new();
        private readonly HashSet<uint> _characterAuthoredHiddenSubmeshes = new();
        private double _championAuthoredScale = 1d;
        private bool _isApplyingCharacterViewportState;
        private IReadOnlyList<VfxClipCueEvaluator.VisibilityEntry> _animationVisibilityTimeline =
            Array.Empty<VfxClipCueEvaluator.VisibilityEntry>();
        private VfxLoadingService.Bundle _championBundle;
        private string _animationSearchDirectory;
        private int _championLoadGeneration;
        private System.Threading.CancellationTokenSource _scanCancellation;
        private System.Threading.CancellationTokenSource _binCancellation;
        private System.Threading.CancellationTokenSource _mapCancellation;
        private System.Threading.CancellationTokenSource _mapLayerCancellation;
        private System.Threading.CancellationTokenSource _mapClipCancellation;
        private System.Threading.CancellationTokenSource _animationClipCancellation;
        private MapSceneRuntime _mapSceneRuntime;
        private bool _mapSceneIsCharacterBackdrop;
        private MapBrowserNode _mapBrowserRoot;
        private MapGeometryRenderer _mapGeometryRenderer;
        private MapCharacterRenderer _mapCharacterRenderer;
        private MapParticleRenderer _mapParticleRenderer;
        private MapPostEffectsRenderer _mapPostEffectsRenderer;
        private SkyRenderer _skyRenderer;
        private VfxCubeMapData _genericSkyCube;
        private VfxCubeMapData _mapSkyCube;
        private bool _skyCubeDirty;
        private double _characterAutoRotateDegrees;
        private readonly List<SceneModel> _characterInteractionModels = new();
        private ViewportModelInteractionController _characterInteractionController;
        private MapCharacterRuntimeGroup _activeMapCharacterGroup;
        private MapCharacterData _activeMapCharacterPlacement;
        private AnimationClipDefinition _activeMapCharacterClip;
        private IReadOnlyList<VfxClipCueEvaluator.VisibilityEntry> _mapAnimationVisibilityTimeline =
            Array.Empty<VfxClipCueEvaluator.VisibilityEntry>();
        private double _mapAnimationVisibilityDuration;
        private bool _mapGpuSceneDirty;
        private bool _mapVisibilityDirty;
        private bool _mapLightingDirty;
        private bool _mapTexturesDirty;
        private MapSunPreviewOverride? _mapSunPreviewOverride;
        private bool _hasMapPostEffectsOverride;
        private MapPostEffectsData _mapPostEffectsOverride;
        private MapSsaoPreviewOverride? _mapSsaoPreviewOverride;
        private bool _isUpdatingMapPreviewControls;
        private bool _isSwitchingWorkspaceTab;
        private VfxWorkspaceTab _pendingWorkspaceRestoreTab;
        private readonly List<System.Windows.Shapes.Line> _characterArmatureLines = new();
        private readonly List<TextBlock> _characterJointLabels = new();
        private readonly object _mapTextureUpdateGate = new();
        private readonly Dictionary<string, MapTextureImage> _pendingMapTextureUpdates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MapTextureImage> _pendingMapProgramTextureUpdates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MapTextureImage> _pendingMapLightmapUpdates = new(StringComparer.OrdinalIgnoreCase);
        private MapSceneRuntime _pendingMapTextureRuntime;
        private bool _mapTexturePublishQueued;
        private bool _suppressMapVariantReload;
        private VfxPreviewSurfaceRenderer _previewSurfaceRenderer;
        private PerspectiveCamera _previewPerspectiveCamera;
        private OrthographicCamera _previewOrthographicCamera;
        private bool _isLoadingPreviewPreferences;
        private bool _suppressCameraPresetFit;
        private bool _deferOrbitProjectionSwap;
        private bool _isUpdatingEmitterAuthoringControls;
        private bool _hasTransientEmitterPreview;
        private Vector3? _authoringOriginalTranslation;
        private Vector3? _authoringOriginalRotation;

        private enum MapTextureUpdateKind
        {
            Base,
            Program,
            Lightmap
        }

        private sealed record StandaloneRunMemory(
            int Seed,
            double Playhead,
            float Speed,
            VfxRigSettings RigSettings,
            int[] Muted,
            int[] Soloed);

        private readonly Dictionary<string, StandaloneRunMemory> _standaloneRunMemory =
            new(StringComparer.OrdinalIgnoreCase);

        internal const int StandalonePlaybackSeed = 1337;
        internal const double PreviewLoopMinimumSpan = 1d / 60d;
        private static readonly double[] PlaybackSpeedDetents =
            { 0.05d, 0.1d, 0.25d, 0.5d, 1d, 1.5d, 2d };

        /// <summary>Injected by the host (ViewerWindow) following the peer-controls pattern.</summary>
        public LogService LogService { get; set; }

        /// <summary>Injected by the host and shared with the main Viewer model pipeline.</summary>
        public SknLoadingService SknLoadingService { get; set; }

        /// <summary>Injected by the host and owned by ViewerWindow.</summary>
        public VfxLoadingService VfxLoadingService { get; set; }

        /// <summary>Injected by the host; decoded MAP scenes stay inside the VFX Studio viewport.</summary>
        internal MapViewerSceneService MapViewerSceneService { get; set; }

        /// <summary>Injected by the host; VFX preview display preferences persist here.</summary>
        public AppSettings AppSettings { get; set; }

        public event EventHandler ExitRequested;

        private static readonly Point3D VfxCameraPosition = new(0, 320, 500);
        private static readonly Point3D VfxCameraTarget = new(0, 0, 0);
        private static readonly Vector3D VfxCameraUpDirection = new(0, 1, 0);

        private readonly Viewport3D _dummyViewport = new Viewport3D
        {
            Camera = CreateVfxCamera()
        };
        private CustomCameraController _cameraController;

        internal static PerspectiveCamera CreateVfxCamera()
        {
            return new PerspectiveCamera(
                VfxCameraPosition,
                VfxCameraTarget - VfxCameraPosition,
                VfxCameraUpDirection,
                45);
        }

        public VfxInspectorControl()
        {
            _model = new VfxInspectorModel();
            _previewPerspectiveCamera = (PerspectiveCamera)_dummyViewport.Camera;
            _previewOrthographicCamera = new OrthographicCamera(
                VfxCameraPosition,
                VfxCameraTarget - VfxCameraPosition,
                VfxCameraUpDirection,
                VfxRigMotion.ChampionHeight * 4.0);
            InitializeComponent();
            DataContext = _model;
            _model.PropertyChanged += OnModelPropertyChanged;

            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
            PreviewKeyDown += RunKeys_PreviewKeyDown;
        }

        private VfxSkinItem _browserSkin;

        private sealed record CharacterBackdropSeed(
            MapSceneSource Source,
            int? VisibilityFlags,
            bool ShowParticles,
            bool ShowStructures);

        private CharacterBackdropSeed CaptureActiveBackdropSeed()
        {
            if (_mapSceneRuntime?.Scene?.Source is MapSceneSource loadedSource)
            {
                return new CharacterBackdropSeed(
                    loadedSource,
                    _mapSceneRuntime.VisibilityFlags,
                    _mapSceneRuntime.ShowParticles,
                    _mapSceneRuntime.ShowStructures);
            }

            if (_model.CharacterBackdropEnabled &&
                _model.SelectedCharacterBackdrop?.Source is MapSceneSource selectedBackdrop)
            {
                return new CharacterBackdropSeed(
                    selectedBackdrop,
                    null,
                    _model.MapParticlesVisible,
                    _model.MapStructuresVisible);
            }

            if (_model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Map &&
                _model.SelectedWorkspaceTab.Payload is MapBrowserNode { Kind: MapBrowserNodeKind.MapFile, Payload: MapSceneSource pendingMap })
            {
                return new CharacterBackdropSeed(
                    pendingMap,
                    null,
                    _model.MapParticlesVisible,
                    _model.MapStructuresVisible);
            }

            return null;
        }

        private VfxWorkspaceTab EnsureSkinWorkspaceTab(
            VfxSkinItem skin,
            bool select = true,
            CharacterBackdropSeed inheritedBackdrop = null)
        {
            if (skin == null || string.IsNullOrWhiteSpace(skin.BinPath)) return null;
            string key = $"skin:{Path.GetFullPath(skin.BinPath)}";
            VfxWorkspaceTab tab = _model.WorkspaceTabs.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (tab == null)
            {
                string title = string.IsNullOrWhiteSpace(skin.OwnerName)
                    ? skin.Title
                    : $"{skin.OwnerName} · {skin.Title}";
                tab = new VfxWorkspaceTab
                {
                    Key = key,
                    Title = title,
                    Subtitle = skin.DisplayName ?? skin.BinPath,
                    Kind = VfxWorkspaceTabKind.Skin,
                    Payload = skin
                };
                if (inheritedBackdrop?.Source != null)
                {
                    string backdropKey = VfxInstallationMapCatalog.BackdropKey(inheritedBackdrop.Source);
                    if (!string.IsNullOrWhiteSpace(backdropKey))
                    {
                        tab.CharacterBackdropEnabled = true;
                        tab.CharacterBackdropKey = backdropKey;
                        tab.CharacterBackdropVisibilityFlags = inheritedBackdrop.VisibilityFlags;
                        tab.BackdropParticlesVisible = inheritedBackdrop.ShowParticles;
                        tab.BackdropStructuresVisible = inheritedBackdrop.ShowStructures;
                    }
                }
                _model.WorkspaceTabs.Add(tab);
                _model.NotifyWorkspaceTabsChanged();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    WorkspaceTabsScrollViewer?.ScrollToRightEnd();
                    UpdateWorkspaceTabScrollButtons();
                }));
            }

            if (select)
                SelectWorkspaceTab(tab);
            return tab;
        }

        private static string MapWorkspaceKey(MapSceneSource source)
        {
            string logical = VfxInstallationMapCatalog.BackdropKey(source);
            if (!string.IsNullOrWhiteSpace(logical))
                return $"map:{logical}";
            return $"map:{source?.SelectedMapFilePath ?? string.Empty}";
        }

        private static string MapWorkspaceTitle(MapSceneSource source, string fallback = null)
        {
            string title = Path.GetFileNameWithoutExtension(source?.SelectedMapFilePath);
            if (!string.IsNullOrWhiteSpace(title))
                return title;
            if (!string.IsNullOrWhiteSpace(fallback))
                return fallback;
            string path = source?.Map?.Value ?? "MAP";
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }

        private static MapBrowserNode CreateMapFileNode(MapSceneSource source, string fallbackTitle = null) =>
            new(
                MapWorkspaceTitle(source, fallbackTitle),
                MapBrowserNodeKind.MapFile,
                source?.Map?.Value,
                source);

        private VfxWorkspaceTab EnsureMapWorkspaceTab(MapBrowserNode node, bool select = true)
        {
            if (node?.Kind != MapBrowserNodeKind.MapFile || node.Payload is not MapSceneSource source)
                return null;
            string key = MapWorkspaceKey(source);
            VfxWorkspaceTab tab = _model.WorkspaceTabs.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (tab == null)
            {
                tab = new VfxWorkspaceTab
                {
                    Key = key,
                    Title = MapWorkspaceTitle(source, node.Title),
                    Subtitle = source.SelectedMapFilePath ?? node.Subtitle,
                    Kind = VfxWorkspaceTabKind.Map,
                    Payload = node
                };
                _model.WorkspaceTabs.Add(tab);
                _model.NotifyWorkspaceTabsChanged();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    WorkspaceTabsScrollViewer?.ScrollToRightEnd();
                    UpdateWorkspaceTabScrollButtons();
                }));
            }

            if (select)
                SelectWorkspaceTab(tab);
            return tab;
        }

        private MapSceneSource ResolveMapVariantSource(MapVariantData variant)
        {
            if (variant?.Map == null)
                return null;

            string wantedKey = variant.Map.Value?.Trim().ToLowerInvariant();
            VfxCharacterBackdropOption option = _model.CharacterBackdrops.FirstOrDefault(candidate =>
                string.Equals(
                    VfxInstallationMapCatalog.BackdropKey(candidate?.Source),
                    wantedKey,
                    StringComparison.OrdinalIgnoreCase));
            if (option?.Source != null)
                return option.Source;

            string selectedFile = SelectedMapFileFor(variant.Map, _model.RootPath);
            return new MapSceneSource(variant.Map, selectedFile, _model.RootPath);
        }

        private void RetargetMapWorkspaceTab(VfxWorkspaceTab tab, MapSceneSource source)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Map || source == null)
                return;

            string targetKey = MapWorkspaceKey(source);
            VfxWorkspaceTab duplicate = _model.WorkspaceTabs.FirstOrDefault(candidate =>
                !ReferenceEquals(candidate, tab) &&
                candidate.Kind == VfxWorkspaceTabKind.Map &&
                string.Equals(candidate.Key, targetKey, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                _model.WorkspaceTabs.Remove(duplicate);
                _model.NotifyWorkspaceTabsChanged();
            }

            MapBrowserNode node = CreateMapFileNode(source);
            tab.Key = targetKey;
            tab.Title = MapWorkspaceTitle(source, node.Title);
            tab.Subtitle = source.SelectedMapFilePath ?? source.Map?.Value;
            tab.Payload = node;
            _model.SelectedMapNode = node;
        }

        private void CaptureWorkspaceSelection(VfxWorkspaceTab tab)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Skin || tab.Payload is not VfxSkinItem)
                return;

            // The tree changes SelectedSkin before PropertyChanged reaches us, while the current
            // System/Clip/Spell collections still belong to the previously active tab. Capture those
            // live selections here instead of keying the snapshot off SelectedSkin identity.
            tab.SelectedSystemPathHash = _model.SelectedSystem?.PathHash;
            tab.SelectedAnimationFilePath = _model.SelectedAnimation?.FilePath;
            tab.SelectedAnimationGraphPathHash = _model.SelectedAnimation?.Clip?.GraphPathHash;
            tab.SelectedAnimationOwnerPathHash = _model.SelectedAnimation?.Clip?.OwnerPathHash;
            tab.SelectedSpellPathHash = _model.SelectedSpell?.PathHash;
            tab.AnimationParameter = _model.AnimationParameter;
            CaptureCharacterWorkspaceState(tab);
        }

        private void CaptureCharacterWorkspaceState(VfxWorkspaceTab tab)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Skin) return;
            tab.CharacterEffectsEnabled = _model.CharacterEffectsEnabled;
            tab.CharacterArmatureVisible = _model.ShowCharacterArmature;
            tab.CharacterJointNamesVisible = _model.ShowCharacterJointNames;
            tab.CharacterAutoRotate = _model.CharacterAutoRotate;
            tab.CharacterAutoRotateDegrees = _characterAutoRotateDegrees;
            tab.CharacterTransformGizmoEnabled = _model.CharacterTransformGizmoEnabled;
            tab.CharacterBackdropEnabled = _model.CharacterBackdropEnabled;
            tab.BackdropParticlesVisible = _model.MapParticlesVisible;
            tab.BackdropStructuresVisible = _model.MapStructuresVisible;
            tab.CharacterBackdropKey = VfxInstallationMapCatalog.BackdropKey(_model.SelectedCharacterBackdrop?.Source);
            if (_mapSceneIsCharacterBackdrop && _mapSceneRuntime != null)
                tab.CharacterBackdropVisibilityFlags = _mapSceneRuntime.VisibilityFlags;
            tab.CharacterPositionX = _model.CharacterPositionX;
            tab.CharacterPositionY = _model.CharacterPositionY;
            tab.CharacterPositionZ = _model.CharacterPositionZ;
            tab.CharacterRotationX = _model.CharacterRotationX;
            tab.CharacterRotationY = _model.CharacterRotationY;
            tab.CharacterRotationZ = _model.CharacterRotationZ;
            tab.CharacterScaleMultiplier = _model.CharacterScaleMultiplier;
        }

        private void RestoreCharacterWorkspaceState(VfxWorkspaceTab tab)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Skin) return;
            _isApplyingCharacterViewportState = true;
            try
            {
                _model.CharacterEffectsEnabled = tab.CharacterEffectsEnabled;
                _model.ShowCharacterArmature = tab.CharacterArmatureVisible;
                _model.ShowCharacterJointNames = tab.CharacterJointNamesVisible;
                _model.CharacterAutoRotate = tab.CharacterAutoRotate;
                _characterAutoRotateDegrees = tab.CharacterAutoRotateDegrees;
                _model.CharacterTransformGizmoEnabled = tab.CharacterTransformGizmoEnabled;
                _model.MapParticlesVisible = tab.BackdropParticlesVisible;
                _model.MapStructuresVisible = tab.BackdropStructuresVisible;
                _model.CharacterPositionX = tab.CharacterPositionX;
                _model.CharacterPositionY = tab.CharacterPositionY;
                _model.CharacterPositionZ = tab.CharacterPositionZ;
                _model.CharacterRotationX = tab.CharacterRotationX;
                _model.CharacterRotationY = tab.CharacterRotationY;
                _model.CharacterRotationZ = tab.CharacterRotationZ;
                _model.CharacterScaleMultiplier = tab.CharacterScaleMultiplier;
                _model.SelectedCharacterBackdrop = _model.CharacterBackdrops.FirstOrDefault(option =>
                    !string.IsNullOrWhiteSpace(tab.CharacterBackdropKey) &&
                    string.Equals(VfxInstallationMapCatalog.BackdropKey(option.Source), tab.CharacterBackdropKey, StringComparison.OrdinalIgnoreCase));
                _model.CharacterBackdropEnabled = tab.CharacterBackdropEnabled && _model.SelectedCharacterBackdrop != null;
            }
            finally
            {
                _isApplyingCharacterViewportState = false;
            }
            ApplyCharacterPlacement();
            ApplyEffectiveCharacterSubmeshes();
            RefreshCharacterInteractionTarget();
            EnsureCharacterBackdropRuntime(tab);
        }

        private bool TryAdoptLoadedMapAsCharacterBackdrop(VfxWorkspaceTab tab)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Skin || _mapSceneRuntime?.Scene?.Source == null)
                return false;

            string loadedKey = VfxInstallationMapCatalog.BackdropKey(_mapSceneRuntime.Scene.Source);
            if (!VfxCharacterViewportSemantics.CanAdoptLoadedBackdrop(
                    tab.CharacterBackdropEnabled,
                    tab.CharacterBackdropKey,
                    loadedKey))
            {
                return false;
            }

            ClearMapCharacterClipPreview();
            _mapSceneIsCharacterBackdrop = true;
            _mapSceneRuntime.ShowParticles = tab.BackdropParticlesVisible;
            _mapSceneRuntime.ShowStructures = tab.BackdropStructuresVisible;
            _model.HasMapPreview = true;
            _model.SetMapLayers(
                MapGeometrySemantics.Layers(_mapSceneRuntime.Scene.Geometry),
                _mapSceneRuntime.VisibilityFlags);
            ReplaceMapBrowserRoot(null);
            _mapGpuSceneDirty = true;

            if (tab.CharacterBackdropVisibilityFlags is int wantedFlags &&
                wantedFlags != _mapSceneRuntime.VisibilityFlags)
            {
                _model.SetMapLayerFlags(wantedFlags);
                _ = ApplyMapVisibilityFlagsAsync(wantedFlags);
            }
            else
            {
                ApplyCharacterBackdropOrigin(_mapSceneRuntime.Scene, _mapSceneRuntime.Scene.Source);
            }

            OpenTkControl?.InvalidateVisual();
            return true;
        }

        private void EnsureCharacterBackdropRuntime(VfxWorkspaceTab tab)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Skin ||
                !_model.CharacterBackdropEnabled ||
                _model.SelectedCharacterBackdrop?.Source == null)
            {
                if (_mapSceneIsCharacterBackdrop)
                    CancelMapLoadAndClearScene();
                return;
            }

            if (TryAdoptLoadedMapAsCharacterBackdrop(tab))
                return;

            _ = LoadCharacterBackdropAsync(_model.SelectedCharacterBackdrop);
        }

        private void RestoreWorkspaceSelection(VfxWorkspaceTab tab, string loadedBinPath)
        {
            if (tab?.Kind != VfxWorkspaceTabKind.Skin ||
                tab.Payload is not VfxSkinItem skin ||
                !ReferenceEquals(_model.SelectedWorkspaceTab, tab) ||
                !ReferenceEquals(_model.SelectedSkin, skin) ||
                !string.Equals(Path.GetFullPath(skin.BinPath), Path.GetFullPath(loadedBinPath), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RestoreCharacterWorkspaceState(tab);

            if (tab.SelectedSystemPathHash is uint systemHash)
            {
                VfxSystemDiagnosticItem system = _model.Systems.FirstOrDefault(item => item.PathHash == systemHash);
                if (system != null)
                {
                    _model.IsRawSystemsMode = true;
                    _model.SelectedSystem = system;
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(tab.SelectedAnimationFilePath) ||
                tab.SelectedAnimationOwnerPathHash.HasValue)
            {
                AnimationClipCatalogItem animation = _model.DetectedAnimations.FirstOrDefault(item =>
                    (!tab.SelectedAnimationGraphPathHash.HasValue || item.Clip?.GraphPathHash == tab.SelectedAnimationGraphPathHash) &&
                    (!tab.SelectedAnimationOwnerPathHash.HasValue || item.Clip?.OwnerPathHash == tab.SelectedAnimationOwnerPathHash) &&
                    (string.IsNullOrWhiteSpace(tab.SelectedAnimationFilePath) ||
                     string.Equals(item.FilePath, tab.SelectedAnimationFilePath, StringComparison.OrdinalIgnoreCase)));
                if (animation != null)
                {
                    _model.IsAnimationMode = true;
                    _model.SelectedAnimation = animation;
                    if (tab.AnimationParameter.HasValue &&
                        (_model.AnimationParameter != tab.AnimationParameter || animation.HasParameterValues))
                    {
                        _model.AnimationParameter = tab.AnimationParameter;
                    }
                    return;
                }
            }

            if (tab.SelectedSpellPathHash is uint spellHash)
            {
                VfxSpellBrowserItem spell = FindSpellByPathHash(skin.SpellItems, spellHash);
                if (spell != null)
                {
                    _model.IsAnimationMode = true;
                    _model.SelectedSpell = spell;
                }
            }
        }

        private static VfxSpellBrowserItem FindSpellByPathHash(IEnumerable<object> items, uint pathHash)
        {
            foreach (object item in items ?? Array.Empty<object>())
            {
                if (item is VfxSpellBrowserItem spell && spell.PathHash == pathHash)
                    return spell;
                if (item is VfxBrowserFolder folder)
                {
                    VfxSpellBrowserItem nested = FindSpellByPathHash(folder.Children, pathHash);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private void SelectWorkspaceTab(VfxWorkspaceTab tab)
        {
            if (tab == null) return;
            VfxWorkspaceTab previous = _model.SelectedWorkspaceTab;
            if (!ReferenceEquals(previous, tab))
                CaptureWorkspaceSelection(previous);
            _model.SelectedWorkspaceTab = tab;
        }

        private void ActivateWorkspaceTab(VfxWorkspaceTab tab)
        {
            if (tab == null || _isCleanedUp || ReferenceEquals(_model.SelectedWorkspaceTab, tab)) return;
            _isSwitchingWorkspaceTab = true;
            try
            {
                SelectWorkspaceTab(tab);
                switch (tab.Kind)
                {
                    case VfxWorkspaceTabKind.Skin when tab.Payload is VfxSkinItem skin:
                        _pendingWorkspaceRestoreTab = tab;
                        bool adoptedBackdrop = _mapSceneRuntime != null && TryAdoptLoadedMapAsCharacterBackdrop(tab);
                        if (!adoptedBackdrop)
                            CancelMapLoadAndClearScene();
                        _model.SelectedMapNode = null;
                        if (!ReferenceEquals(_model.SelectedSkin, skin))
                            _model.SelectedSkin = skin;
                        else if (_browserSkin == null)
                            BindBrowserSkin();
                        break;

                    case VfxWorkspaceTabKind.Map when tab.Payload is MapBrowserNode mapNode:
                        _pendingWorkspaceRestoreTab = null;
                        if (_model.SelectedSkin != null || _championModel != null)
                        {
                            ClearLoadedSkinState();
                            _model.SelectedSkin = null;
                        }
                        _model.SelectedMapNode = mapNode;
                        HandleMapBrowserSelection(mapNode);
                        break;
                }
            }
            finally
            {
                _isSwitchingWorkspaceTab = false;
            }
        }

        private void WorkspaceTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left ||
                (sender as FrameworkElement)?.DataContext is not VfxWorkspaceTab tab)
                return;
            ActivateWorkspaceTab(tab);
            e.Handled = true;
        }

        private void CloseWorkspaceTab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not VfxWorkspaceTab tab) return;

            int index = _model.WorkspaceTabs.IndexOf(tab);
            bool wasSelected = ReferenceEquals(_model.SelectedWorkspaceTab, tab);
            if (index < 0) return;
            _model.WorkspaceTabs.RemoveAt(index);
            _model.NotifyWorkspaceTabsChanged();
            Dispatcher.BeginInvoke(new Action(UpdateWorkspaceTabScrollButtons));

            if (!wasSelected) return;
            VfxWorkspaceTab next = _model.WorkspaceTabs.Count == 0
                ? null
                : _model.WorkspaceTabs[Math.Max(0, Math.Min(index, _model.WorkspaceTabs.Count - 1))];
            if (next != null)
            {
                ActivateWorkspaceTab(next);
                return;
            }

            _isSwitchingWorkspaceTab = true;
            try
            {
                _model.SelectedWorkspaceTab = null;
                CancelMapLoadAndClearScene();
                ClearLoadedSkinState();
                _model.SelectedSkin = null;
                _model.SelectedMapNode = null;
            }
            finally
            {
                _isSwitchingWorkspaceTab = false;
            }
        }

        private void WorkspaceTabsScrollLeft_Click(object sender, RoutedEventArgs e)
            => WorkspaceTabsScrollViewer?.ScrollToHorizontalOffset(
                Math.Max(0, WorkspaceTabsScrollViewer.HorizontalOffset - 180));

        private void WorkspaceTabsScrollRight_Click(object sender, RoutedEventArgs e)
            => WorkspaceTabsScrollViewer?.ScrollToHorizontalOffset(
                WorkspaceTabsScrollViewer.HorizontalOffset + 180);

        private void WorkspaceTabsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
            => UpdateWorkspaceTabScrollButtons();

        private void UpdateWorkspaceTabScrollButtons()
        {
            if (WorkspaceTabsScrollViewer == null ||
                WorkspaceTabsScrollLeftButton == null ||
                WorkspaceTabsScrollRightButton == null)
                return;

            const double epsilon = 0.5;
            bool overflows = WorkspaceTabsScrollViewer.ScrollableWidth > epsilon;
            WorkspaceTabsScrollLeftButton.Visibility = overflows && WorkspaceTabsScrollViewer.HorizontalOffset > epsilon
                ? Visibility.Visible
                : Visibility.Collapsed;
            WorkspaceTabsScrollRightButton.Visibility = overflows &&
                                                          WorkspaceTabsScrollViewer.HorizontalOffset < WorkspaceTabsScrollViewer.ScrollableWidth - epsilon
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ClearWorkspaceTabs()
        {
            _model.SelectedWorkspaceTab = null;
            _model.WorkspaceTabs.Clear();
            _model.NotifyWorkspaceTabsChanged();
            Dispatcher.BeginInvoke(new Action(UpdateWorkspaceTabScrollButtons));
        }

        private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VfxInspectorModel.SelectedSkin))
            {
                if (_model.SelectedSkin != null && !_isSwitchingWorkspaceTab)
                {
                    CharacterBackdropSeed inheritedBackdrop = CaptureActiveBackdropSeed();
                    VfxWorkspaceTab skinTab = EnsureSkinWorkspaceTab(
                        _model.SelectedSkin,
                        inheritedBackdrop: inheritedBackdrop);
                    _pendingWorkspaceRestoreTab = skinTab;
                    bool adoptedBackdrop = _mapSceneRuntime != null && TryAdoptLoadedMapAsCharacterBackdrop(skinTab);
                    if (!adoptedBackdrop)
                        CancelMapLoadAndClearScene();
                }
                BindBrowserSkin();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedSystem))
            {
                RequestSystemInspection(_model.SelectedSystem);
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedEmitter))
            {
                LoadEmitterAuthoringControls(_model.SelectedEmitter);
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedAnimation))
            {
                if (_model.SelectedAnimation != null)
                {
                    BeginExclusivePreviewSelection();
                    ConfigureAnimationParameterOptions(_model.SelectedAnimation);
                    _ = PlaySelectedAnimationAsync(_model.SelectedAnimation);
                }
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedSpell))
            {
                if (_model.SelectedSpell != null)
                {
                    BeginExclusivePreviewSelection();
                    RequestSpellPreview(_model.SelectedSpell);
                }
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.SelectedMapVariant) &&
                     !_suppressMapVariantReload &&
                     _mapSceneRuntime != null &&
                     _model.SelectedMapVariant != null)
            {
                ReloadSelectedMapVariant();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.AnimationParameter) &&
                     !_isUpdatingAnimationParameter &&
                     _model.AnimationParameter.HasValue)
            {
                if (_model.SelectedMapNode?.Kind == MapBrowserNodeKind.Clip &&
                    _model.SelectedMapNode.Payload is MapCharacterClipSelection mapClip)
                {
                    _ = PlayMapCharacterClipAsync(mapClip, preservePlayhead: true);
                }
                else if (_model.SelectedAnimation != null)
                {
                    RebuildAnimationsForParameter(_model.AnimationParameter.Value);
                }
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.CharacterBackdropEnabled) ||
                     e.PropertyName == nameof(VfxInspectorModel.SelectedCharacterBackdrop))
            {
                if (!_isApplyingCharacterViewportState)
                    RefreshCharacterBackdrop();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.MapStructuresVisible) ||
                     e.PropertyName == nameof(VfxInspectorModel.MapParticlesVisible))
            {
                if (_mapSceneRuntime != null)
                {
                    _mapSceneRuntime.ShowStructures = _model.MapStructuresVisible;
                    _mapSceneRuntime.ShowParticles = _model.MapParticlesVisible;
                    if (_mapSceneIsCharacterBackdrop && _model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin)
                    {
                        _model.SelectedWorkspaceTab.BackdropStructuresVisible = _model.MapStructuresVisible;
                        _model.SelectedWorkspaceTab.BackdropParticlesVisible = _model.MapParticlesVisible;
                    }
                    OpenTkControl?.InvalidateVisual();
                }
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.CharacterPositionX) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterPositionY) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterPositionZ) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterRotationX) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterRotationY) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterRotationZ) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterScaleMultiplier))
            {
                if (!_isApplyingCharacterViewportState)
                {
                    if (_model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin)
                    {
                        _model.SelectedWorkspaceTab.CharacterPlacementCustomized = true;
                        _model.SelectedWorkspaceTab.CharacterPlacedOnKey = _model.HasActiveCharacterBackdrop
                            ? VfxInstallationMapCatalog.BackdropKey(_model.SelectedCharacterBackdrop?.Source)
                            : null;
                    }
                    ApplyCharacterPlacement();
                }
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.CharacterEffectsEnabled) ||
                     e.PropertyName == nameof(VfxInspectorModel.ShowCharacterArmature) ||
                     e.PropertyName == nameof(VfxInspectorModel.ShowCharacterJointNames) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterAutoRotate) ||
                     e.PropertyName == nameof(VfxInspectorModel.CharacterTransformGizmoEnabled) ||
                     e.PropertyName == nameof(VfxInspectorModel.InspectorVisible))
            {
                if (e.PropertyName == nameof(VfxInspectorModel.CharacterAutoRotate))
                    ApplyCharacterPlacement();
                if (e.PropertyName == nameof(VfxInspectorModel.CharacterTransformGizmoEnabled))
                    RefreshCharacterInteractionTarget();
                OpenTkControl?.InvalidateVisual();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.PreviewCameraPreset))
            {
                if (!_suppressCameraPresetFit)
                    ApplyCameraPreset(_model.PreviewCameraPreset, refit: true);
                SavePreviewDisplayPreferences();
            }
            else if (e.PropertyName == nameof(VfxInspectorModel.ShowPreviewSky) ||
                     e.PropertyName == nameof(VfxInspectorModel.ShowPreviewGrid) ||
                     e.PropertyName == nameof(VfxInspectorModel.ShowPreviewGround) ||
                     e.PropertyName == nameof(VfxInspectorModel.ShowPreviewStage) ||
                     e.PropertyName == nameof(VfxInspectorModel.PreviewViewMode) ||
                     e.PropertyName == nameof(VfxInspectorModel.PreviewWireOverlay) ||
                     e.PropertyName == nameof(VfxInspectorModel.PreviewShaders))
            {
                OpenTkControl?.InvalidateVisual();
                SavePreviewDisplayPreferences();
            }
        }

        private void SyncSelectedMapVariant(MapSceneSource source)
        {
            if (source?.Map == null || _model.MapVariants.Count == 0)
                return;

            MapVariantData variant = _model.MapVariants.FirstOrDefault(candidate =>
                candidate?.Map?.Equals(source.Map) == true ||
                string.Equals(candidate?.Map?.Value, source.Map.Value, StringComparison.OrdinalIgnoreCase));
            if (variant == null || ReferenceEquals(_model.SelectedMapVariant, variant))
                return;

            _suppressMapVariantReload = true;
            try
            {
                _model.SelectedMapVariant = variant;
            }
            finally
            {
                _suppressMapVariantReload = false;
            }
        }

        private void ReloadSelectedMapVariant()
        {
            MapSceneSource source = ResolveMapVariantSource(_model.SelectedMapVariant);
            if (source == null)
                return;

            bool asCharacterBackdrop = _mapSceneIsCharacterBackdrop && _model.IsSkinWorkspace;
            if (asCharacterBackdrop && _model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin)
            {
                VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
                string key = VfxInstallationMapCatalog.BackdropKey(source);
                VfxCharacterBackdropOption option = _model.CharacterBackdrops.FirstOrDefault(candidate =>
                    string.Equals(
                        VfxInstallationMapCatalog.BackdropKey(candidate?.Source),
                        key,
                        StringComparison.OrdinalIgnoreCase));
                if (option == null)
                {
                    option = new VfxCharacterBackdropOption(
                        VfxInstallationMapCatalog.Label(
                            source,
                            projectSource: !string.IsNullOrWhiteSpace(source.SelectedMapFilePath)),
                        source);
                    _model.CharacterBackdrops.Add(option);
                }

                tab.CharacterBackdropEnabled = true;
                tab.CharacterBackdropKey = key;
                tab.CharacterBackdropVisibilityFlags = null;
                _isApplyingCharacterViewportState = true;
                try
                {
                    _model.SelectedCharacterBackdrop = option;
                    _model.CharacterBackdropEnabled = true;
                }
                finally
                {
                    _isApplyingCharacterViewportState = false;
                }
            }
            else if (_model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Map)
            {
                RetargetMapWorkspaceTab(_model.SelectedWorkspaceTab, source);
            }

            _ = LoadDetectedMapAsync(source, asCharacterBackdrop);
        }

        private void RefreshCharacterBackdrop()
        {
            if (!_model.IsSkinWorkspace || !_model.CharacterBackdropEnabled)
            {
                if (_mapSceneIsCharacterBackdrop)
                    CancelMapLoadAndClearScene();
                return;
            }

            if (_model.SelectedCharacterBackdrop == null && _model.CharacterBackdrops.Count > 0)
            {
                _model.SelectedCharacterBackdrop = _model.CharacterBackdrops[0];
                return;
            }

            if (_model.SelectedCharacterBackdrop != null)
                _ = LoadCharacterBackdropAsync(_model.SelectedCharacterBackdrop);
        }

        private Task LoadCharacterBackdropAsync(VfxCharacterBackdropOption option)
        {
            if (option?.Source == null || !_model.IsSkinWorkspace)
                return Task.CompletedTask;

            int? visibilityFlags = null;
            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
            if (tab?.Kind == VfxWorkspaceTabKind.Skin &&
                string.Equals(
                    tab.CharacterBackdropKey,
                    VfxInstallationMapCatalog.BackdropKey(option.Source),
                    StringComparison.OrdinalIgnoreCase))
            {
                visibilityFlags = tab.CharacterBackdropVisibilityFlags;
            }

            return LoadDetectedMapAsync(
                option.Source,
                asCharacterBackdrop: true,
                initialVisibilityFlags: visibilityFlags);
        }

        private void ApplyCharacterBackdropOrigin(MapSceneData scene, MapSceneSource source)
        {
            if (!_model.IsSkinWorkspace || scene?.Geometry == null ||
                _model.SelectedWorkspaceTab?.Kind != VfxWorkspaceTabKind.Skin)
            {
                return;
            }

            int visibilityFlags = _mapSceneRuntime?.VisibilityFlags ?? scene.OpeningVisibilityFlags;
            Vector3? calculatedOrigin = MapGeometrySemantics.CalculateOriginForFlags(scene.Geometry, visibilityFlags);
            if (calculatedOrigin is not Vector3 origin)
                return;

            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
            string sourceKey = VfxInstallationMapCatalog.BackdropKey(source);
            bool hasPinnedPlacement = tab.CharacterPlacementCustomized &&
                string.Equals(tab.CharacterPlacedOnKey, sourceKey, StringComparison.OrdinalIgnoreCase);
            if (hasPinnedPlacement) return;

            _isApplyingCharacterViewportState = true;
            try
            {
                // MAP geometry is mirrored on X by the renderer. Convert the authored engine origin to
                // the same preview-space point before standing the independently rendered Character on it.
                _model.CharacterPositionX = -origin.X;
                _model.CharacterPositionY = origin.Y;
                _model.CharacterPositionZ = origin.Z;
                _model.CharacterRotationX = 0d;
                _model.CharacterRotationY = 0d;
                _model.CharacterRotationZ = 0d;
                tab.CharacterPositionX = _model.CharacterPositionX;
                tab.CharacterPositionY = _model.CharacterPositionY;
                tab.CharacterPositionZ = _model.CharacterPositionZ;
                tab.CharacterRotationX = 0d;
                tab.CharacterRotationY = 0d;
                tab.CharacterRotationZ = 0d;
                tab.CharacterPlacementCustomized = false;
                tab.CharacterPlacedOnKey = sourceKey;
            }
            finally
            {
                _isApplyingCharacterViewportState = false;
            }
            ApplyCharacterPlacement();
        }

        private void ApplyCharacterPlacement()
        {
            if (_championModel == null || !_model.IsSkinWorkspace) return;

            double autoYaw = _model.CharacterAutoRotate ? _characterAutoRotateDegrees : 0d;
            _championModel.PositionX = _model.CharacterPositionX;
            _championModel.PositionY = _model.CharacterPositionY;
            _championModel.PositionZ = _model.CharacterPositionZ;
            _championModel.RotationX = _model.CharacterRotationX;
            _championModel.RotationY = _model.CharacterRotationY + autoYaw;
            _championModel.RotationZ = _model.CharacterRotationZ;
            _championModel.Scale = _championAuthoredScale * _model.CharacterScaleMultiplier;

            // The owner scene already applies authored skinScale to bones/attachment offsets. Only the
            // user placement multiplier belongs in the outer VFX transform, otherwise scale is doubled.
            if (_model.SelectedSystem == null)
            {
                float pitch = (float)(_model.CharacterRotationX * Math.PI / 180d);
                float yaw = (float)((_model.CharacterRotationY + autoYaw) * Math.PI / 180d);
                float roll = (float)(_model.CharacterRotationZ * Math.PI / 180d);
                float scale = (float)_model.CharacterScaleMultiplier;
                Matrix4x4 placement = Matrix4x4.CreateScale(scale) *
                                      Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll) *
                                      Matrix4x4.CreateTranslation(
                                          (float)_model.CharacterPositionX,
                                          (float)_model.CharacterPositionY,
                                          (float)_model.CharacterPositionZ);
                _vfxRenderer?.SetWorldTransform(placement);
            }
            OpenTkControl?.InvalidateVisual();
        }

        private void RefreshCharacterInteractionTarget()
        {
            _characterInteractionModels.Clear();
            if (_championModel != null && _model.IsSkinWorkspace)
                _characterInteractionModels.Add(_championModel);

            if (_characterInteractionController == null)
                return;

            _characterInteractionController.IsEnabled =
                _model.IsSkinWorkspace && _model.CharacterTransformGizmoEnabled;
            _characterInteractionController.SetSelection(
                _characterInteractionModels,
                _characterInteractionModels.FirstOrDefault());
        }

        private void CharacterInteraction_TransformChanged(SceneModel model)
        {
            if (_isApplyingCharacterViewportState ||
                model == null ||
                !ReferenceEquals(model, _championModel) ||
                !_model.IsSkinWorkspace)
            {
                return;
            }

            _isApplyingCharacterViewportState = true;
            try
            {
                _model.CharacterPositionX = model.PositionX;
                _model.CharacterPositionY = model.PositionY;
                _model.CharacterPositionZ = model.PositionZ;

                if (_model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin)
                {
                    VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
                    tab.CharacterPositionX = model.PositionX;
                    tab.CharacterPositionY = model.PositionY;
                    tab.CharacterPositionZ = model.PositionZ;
                    tab.CharacterPlacementCustomized = true;
                    tab.CharacterPlacedOnKey = _model.HasActiveCharacterBackdrop
                        ? VfxInstallationMapCatalog.BackdropKey(_model.SelectedCharacterBackdrop?.Source)
                        : null;
                }
            }
            finally
            {
                _isApplyingCharacterViewportState = false;
            }

            // The shared gizmo owns the SceneModel translation. Re-applying through the Studio placement
            // path keeps attached clip/idle VFX and the per-tab placement in the same world frame.
            ApplyCharacterPlacement();
        }

        private void AdvanceCharacterAutoRotate(float deltaSeconds)
        {
            if (!_model.IsSkinWorkspace || !_model.CharacterAutoRotate || _championModel == null || deltaSeconds <= 0f)
                return;

            // Match the normal Viewer: one calm 30-degree/second orbit. This is a transient layer over
            // the user's authored placement, so disabling Auto Rotate restores the exact manual yaw.
            _characterAutoRotateDegrees = VfxCharacterViewportSemantics.AdvanceAutoRotation(
                _characterAutoRotateDegrees,
                deltaSeconds);
            ApplyCharacterPlacement();
        }

        private void ClearCharacterArmatureOverlay()
        {
            _characterArmatureLines.Clear();
            _characterJointLabels.Clear();
            CharacterArmatureCanvas?.Children.Clear();
        }

        private void UpdateCharacterArmatureOverlay(Matrix4x4 viewProjection)
        {
            if (CharacterArmatureCanvas == null ||
                !_model.IsSkinWorkspace ||
                !_model.ShowCharacterArmature ||
                _championModel?.Skeleton?.Joints == null ||
                _championModel.Skeleton.Joints.Count == 0)
            {
                if (CharacterArmatureCanvas != null)
                    CharacterArmatureCanvas.Visibility = Visibility.Collapsed;
                return;
            }

            var skeleton = _championModel.Skeleton;
            IReadOnlyList<Matrix4x4> pose =
                _championModel.CurrentAnimation != null &&
                _championAnimationService?.WorldBoneTransforms?.Count == skeleton.Joints.Count
                    ? _championAnimationService.WorldBoneTransforms
                    : EnsureChampionBindWorldTransforms(skeleton);
            if (pose == null || pose.Count != skeleton.Joints.Count)
            {
                CharacterArmatureCanvas.Visibility = Visibility.Collapsed;
                return;
            }

            CharacterArmatureCanvas.Visibility = Visibility.Visible;
            Matrix4x4 modelWorld = GlMeshRenderer.CreateWorldMatrix(_championModel, mirrorCharacterX: true);
            double width = Math.Max(1d, OpenTkControl.ActualWidth);
            double height = Math.Max(1d, OpenTkControl.ActualHeight);
            int lineIndex = 0;
            int labelIndex = 0;

            for (int index = 0; index < skeleton.Joints.Count; index++)
            {
                Matrix4x4 bone = pose[index];
                Vector3 world = Vector3.Transform(new Vector3(bone.M41, bone.M42, bone.M43), modelWorld);
                bool childVisible = TryProjectToViewport(world, viewProjection, width, height, out Point child);

                int parentIndex = (int)skeleton.Joints[index].ParentId;
                if (parentIndex >= 0 && parentIndex < skeleton.Joints.Count)
                {
                    Matrix4x4 parentBone = pose[parentIndex];
                    Vector3 parentWorld = Vector3.Transform(
                        new Vector3(parentBone.M41, parentBone.M42, parentBone.M43),
                        modelWorld);
                    bool parentVisible = TryProjectToViewport(parentWorld, viewProjection, width, height, out Point parent);
                    System.Windows.Shapes.Line line = EnsureCharacterArmatureLine(lineIndex++);
                    if (childVisible && parentVisible)
                    {
                        line.X1 = parent.X;
                        line.Y1 = parent.Y;
                        line.X2 = child.X;
                        line.Y2 = child.Y;
                        line.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        line.Visibility = Visibility.Collapsed;
                    }
                }

                if (_model.ShowCharacterJointNames)
                {
                    TextBlock label = EnsureCharacterJointLabel(labelIndex++);
                    if (childVisible)
                    {
                        label.Text = skeleton.Joints[index].Name ?? $"joint {index}";
                        Canvas.SetLeft(label, child.X + 4d);
                        Canvas.SetTop(label, child.Y - 8d);
                        label.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        label.Visibility = Visibility.Collapsed;
                    }
                }
            }

            for (int index = lineIndex; index < _characterArmatureLines.Count; index++)
                _characterArmatureLines[index].Visibility = Visibility.Collapsed;
            for (int index = labelIndex; index < _characterJointLabels.Count; index++)
                _characterJointLabels[index].Visibility = Visibility.Collapsed;
        }

        private IReadOnlyList<Matrix4x4> EnsureChampionBindWorldTransforms(LeagueToolkit.Core.Animation.RigResource skeleton)
        {
            if (!ReferenceEquals(_championBindSkeleton, skeleton) ||
                _championBindWorldTransforms.Length != skeleton.Joints.Count)
            {
                _championBindSkeleton = skeleton;
                _championBindBoneTransformProvider = AnimationService.CreateBindBoneTransformProvider(skeleton);
                _championBindSkinningMatrices = AnimationService.CreateBindSkinningMatrices(skeleton);
                _championBindWorldTransforms = AnimationService.CreateBindWorldTransforms(skeleton);
            }
            return _championBindWorldTransforms;
        }

        private System.Windows.Shapes.Line EnsureCharacterArmatureLine(int index)
        {
            while (_characterArmatureLines.Count <= index)
            {
                var line = new System.Windows.Shapes.Line
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                    StrokeThickness = 1.25,
                    Opacity = 0.9,
                    SnapsToDevicePixels = true
                };
                _characterArmatureLines.Add(line);
                CharacterArmatureCanvas.Children.Add(line);
            }
            return _characterArmatureLines[index];
        }

        private TextBlock EnsureCharacterJointLabel(int index)
        {
            while (_characterJointLabels.Count <= index)
            {
                var label = new TextBlock
                {
                    FontSize = 8.5,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromRgb(224, 242, 254)),
                    Background = new SolidColorBrush(Color.FromArgb(160, 12, 16, 24)),
                    Padding = new Thickness(2, 0, 2, 0),
                    IsHitTestVisible = false
                };
                _characterJointLabels.Add(label);
                CharacterArmatureCanvas.Children.Add(label);
            }
            return _characterJointLabels[index];
        }

        private static bool TryProjectToViewport(
            Vector3 world,
            Matrix4x4 viewProjection,
            double width,
            double height,
            out Point point)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
            if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y) || !float.IsFinite(clip.W) ||
                clip.W <= 1e-5f)
            {
                point = default;
                return false;
            }

            float x = clip.X / clip.W;
            float y = clip.Y / clip.W;
            if (x < -1.15f || x > 1.15f || y < -1.15f || y > 1.15f)
            {
                point = default;
                return false;
            }

            point = new Point((x * 0.5d + 0.5d) * width, (1d - (y * 0.5d + 0.5d)) * height);
            return true;
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            LoadPreviewDisplayPreferences();
            UpdateViewportClip();
            if (_isActive)
            {
                EnsureOpenGlStarted();
            }
        }

        private void LoadPreviewDisplayPreferences()
        {
            StudioParametersSettings viewerSettings = AppSettings?.StudioParameters;
            VfxStudioSettings vfxSettings = AppSettings?.VfxStudio;
            if (viewerSettings == null || vfxSettings == null) return;

            _isLoadingPreviewPreferences = true;
            try
            {
                _model.ShowPreviewSky = viewerSettings.SkyVisible;
                _model.ShowPreviewGrid = viewerSettings.GridVisible;
                _model.ShowPreviewGround = viewerSettings.GroundVisible;
                _model.ShowPreviewStage = vfxSettings.StageVisible;

                if (Enum.TryParse(vfxSettings.CameraPreset, ignoreCase: true, out VfxPreviewCameraPreset cameraPreset))
                    _model.PreviewCameraPreset = cameraPreset;
                if (Enum.TryParse(vfxSettings.ViewMode, ignoreCase: true, out VfxPreviewViewMode viewMode))
                    _model.PreviewViewMode = viewMode;
                _model.PreviewWireOverlay = vfxSettings.WireOverlay;
                _model.PreviewShaders = vfxSettings.ShadersEnabled;
            }
            finally
            {
                _isLoadingPreviewPreferences = false;
            }
        }

        private void SavePreviewDisplayPreferences()
        {
            if (_isLoadingPreviewPreferences || AppSettings == null) return;

            AppSettings.StudioParameters ??= new StudioParametersSettings();
            AppSettings.VfxStudio ??= new VfxStudioSettings();

            AppSettings.StudioParameters.SkyVisible = _model.ShowPreviewSky;
            AppSettings.StudioParameters.GridVisible = _model.ShowPreviewGrid;
            AppSettings.StudioParameters.GroundVisible = _model.ShowPreviewGround;
            AppSettings.VfxStudio.ViewMode = _model.PreviewViewMode.ToString();
            AppSettings.VfxStudio.WireOverlay = _model.PreviewWireOverlay;
            AppSettings.VfxStudio.ShadersEnabled = _model.PreviewShaders;
            AppSettings.VfxStudio.StageVisible = _model.ShowPreviewStage;
            AppSettings.VfxStudio.CameraPreset = _model.PreviewCameraPreset.ToString();
            _ = SavePreviewDisplayPreferencesAsync();
        }

        private async Task SavePreviewDisplayPreferencesAsync()
        {
            try
            {
                await AppSettings.SaveAsync();
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to save VFX preview display preferences.");
            }
        }

        private void ViewportClipGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateViewportClip();
        }

        private void UpdateViewportClip()
        {
            if (ViewportClipGrid == null) return;
            double w = ViewportClipGrid.ActualWidth;
            double h = ViewportClipGrid.ActualHeight;
            if (w > 0 && h > 0)
            {
                ViewportClipGrid.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 10, 10);
            }
        }

        /// <summary>
        /// Activates the VFX viewport when its host view becomes visible.
        /// </summary>
        public void Activate()
        {
            if (_isCleanedUp) return;

            _isActive = true;
            // Reload shared display state on every activation so changes made in Load Project or
            // Chroma Library are reflected immediately when VFX Studio becomes visible.
            LoadPreviewDisplayPreferences();
            // The reference preview starts a fresh RAF clock when content becomes visible, so the
            // first resumed frame advances by zero rather than consuming hidden-tab wall time.
            _discardNextSimulationDelta = true;
            if (!HasSelectedSystemReady())
            {
                RequestSystemInspection(_model.SelectedSystem);
            }

            if (IsLoaded)
            {
                EnsureOpenGlStarted();
                SetRenderLoopRunning(true);
            }
        }

        /// <summary>
        /// Pauses VFX work while the host view is hidden without destroying reusable GPU state.
        /// </summary>
        public void Deactivate()
        {
            _isActive = false;
            _pendingSystem = null;
            // LTK stops the viewport frameloop while hidden. Keep the GL resources alive but stop
            // scheduling empty render callbacks until the Studio becomes visible again.
            SetRenderLoopRunning(false);
        }

        private void SetRenderLoopRunning(bool running)
        {
            if (!_isGlStarted || OpenTkControl == null) return;
            OpenTkControl.RenderContinuously = running;
            if (running)
                OpenTkControl.InvalidateVisual();
        }

        private void EnsureOpenGlStarted()
        {
            if (!_isActive || _isGlStarted || _isCleanedUp || !IsLoaded) return;

            try
            {
                var settings = new OpenTK.Wpf.GLWpfControlSettings
                {
                    MajorVersion = 3,
                    MinorVersion = 3,
                    RenderContinuously = true
                };
                OpenTkControl.Start(settings);
                _isGlStarted = true;
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to initialize the VFX Studio OpenGL viewport.");
                _model.LogMessages.Add($"[ERROR] Failed to initialize the OpenGL viewport: {ex.Message}");
            }
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            Deactivate();
        }

        /// <summary>
        /// Releases all resources owned by this control. The host calls this once when the Viewer
        /// is torn down; repeated calls are safe.
        /// </summary>
        public void Cleanup()
        {
            if (_isCleanedUp) return;

            _isExitPending = false;
            Deactivate();
            _isCleanedUp = true;
            _model.PropertyChanged -= OnModelPropertyChanged;
            _championLoadGeneration++;

            RunReleaseStep("VFX folder scan cancellation", () => _scanCancellation?.Cancel());
            RunReleaseStep("VFX BIN load cancellation", () => _binCancellation?.Cancel());
            RunReleaseStep("MAP scene load cancellation", () => _mapCancellation?.Cancel());
            RunReleaseStep("MAP clip load cancellation", () => _mapClipCancellation?.Cancel());
            RunReleaseStep("Animation clip load cancellation", () => _animationClipCancellation?.Cancel());

            var cameraController = _cameraController;
            _cameraController = null;
            if (cameraController != null)
            {
                cameraController.RotationStarted -= CameraController_RotationStarted;
                cameraController.RotationEnded -= CameraController_RotationEnded;
            }
            RunReleaseStep(nameof(CustomCameraController), () => cameraController?.Dispose());

            var vfxRenderer = _vfxRenderer;
            _vfxRenderer = null;
            RunReleaseStep(nameof(VfxRenderSession), () => vfxRenderer?.Dispose(), gpuBound: true);

            var previewSurfaceRenderer = _previewSurfaceRenderer;
            _previewSurfaceRenderer = null;
            RunReleaseStep(nameof(VfxPreviewSurfaceRenderer), () => previewSurfaceRenderer?.Dispose(), gpuBound: true);

            var mapGeometryRenderer = _mapGeometryRenderer;
            _mapGeometryRenderer = null;
            RunReleaseStep(nameof(MapGeometryRenderer), () => mapGeometryRenderer?.Dispose(), gpuBound: true);

            var mapCharacterRenderer = _mapCharacterRenderer;
            _mapCharacterRenderer = null;
            RunReleaseStep(nameof(MapCharacterRenderer), () => mapCharacterRenderer?.Dispose(), gpuBound: true);

            var mapParticleRenderer = _mapParticleRenderer;
            _mapParticleRenderer = null;
            RunReleaseStep(nameof(MapParticleRenderer), () => mapParticleRenderer?.Dispose(), gpuBound: true);

            var mapPostEffectsRenderer = _mapPostEffectsRenderer;
            _mapPostEffectsRenderer = null;
            RunReleaseStep(nameof(MapPostEffectsRenderer), () => mapPostEffectsRenderer?.Dispose(), gpuBound: true);

            var skyRenderer = _skyRenderer;
            _skyRenderer = null;
            RunReleaseStep(nameof(SkyRenderer), () => skyRenderer?.Dispose(), gpuBound: true);
            _genericSkyCube = null;
            _mapSkyCube = null;
            _skyCubeDirty = false;

            var mapSceneRuntime = _mapSceneRuntime;
            _mapSceneRuntime = null;
            RunReleaseStep(nameof(MapSceneRuntime), () => mapSceneRuntime?.Dispose());

            var championMeshRenderer = _championMeshRenderer;
            _championMeshRenderer = null;
            RunReleaseStep(nameof(GlMeshRenderer), () => championMeshRenderer?.Dispose(), gpuBound: true);

            var championAnimationService = _championAnimationService;
            _championAnimationService = null;
            RunReleaseStep(nameof(AnimationService), () => championAnimationService?.Dispose());

            var characterInteractionController = _characterInteractionController;
            _characterInteractionController = null;
            if (characterInteractionController != null)
            {
                characterInteractionController.TransformChanged -= CharacterInteraction_TransformChanged;
                RunReleaseStep(nameof(ViewportModelInteractionController), characterInteractionController.Dispose);
            }
            _characterInteractionModels.Clear();

            var championModel = _championModel;
            _championModel = null;
            RunReleaseStep("Champion SceneModel", () => championModel?.Dispose());

            var clipCatalog = _clipCatalog;
            _clipCatalog = null;
            RunReleaseStep(nameof(VfxClipCatalog), () => clipCatalog?.Dispose());

            _activeBundle = null;
            _championBundle = null;
            _pendingSystem = null;
            _inspectedSystem = null;

            var gl = _gl;
            _gl = null;
            RunReleaseStep("OpenGL API", () => gl?.Dispose());

            RunReleaseStep(nameof(OpenTkControl), OpenTkControl.Dispose, gpuBound: true);
            _isGlStarted = false;
            ExitRequested = null;

            _model.LogMessages.Add("[GL] VFX Studio resources released.");
        }

        /// <summary>
        /// Runs one disposal step in isolation so a broken component cannot abort the remaining
        /// teardown. GPU-bound steps throw a symbol-loading exception when the OpenGL context is
        /// already destroyed; that is expected because the driver releases every GPU object
        /// created on the context along with it. Any other error is logged and does not stop
        /// the remaining releases.
        /// </summary>
        private void RunReleaseStep(string componentName, Action release, bool gpuBound = false)
        {
            if (release == null) return;

            try
            {
                release();
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException) when (gpuBound)
            {
                // The OpenGL context owns these handles and may have released them already.
            }
            catch (ObjectDisposedException) when (gpuBound)
            {
                // A WPF teardown can dispose the GL context before the control releases its handles.
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to release VFX Studio {componentName}.");
            }
        }

        #region OpenTK OpenGL Viewport Initialization & Rendering

        [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern IntPtr wglGetProcAddress(string procName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string lpszLib);

        private static readonly IntPtr OpenGLModule = LoadLibrary("opengl32.dll");

        private static IntPtr GetOpenGLProcAddress(string procName)
        {
            var addr = wglGetProcAddress(procName);
            if (addr == IntPtr.Zero)
                addr = GetProcAddress(OpenGLModule, procName);
            return addr;
        }

        private void EnsureVfxRenderSession()
        {
            if (_vfxRenderer != null || _gl == null || !_isActive || _isCleanedUp) return;

            var renderer = new VfxRenderSession(LogService, VfxLoadingService);
            try
            {
                renderer.Initialize(_gl);
                _vfxRenderer = renderer;
            }
            catch
            {
                RunReleaseStep(nameof(VfxRenderSession), renderer.Dispose, gpuBound: true);
                throw;
            }
        }

        private void RequestSystemInspection(VfxSystemDiagnosticItem systemItem)
        {
            if (_isCleanedUp) return;
            if (ReferenceEquals(_pendingSystem, systemItem)) return;
            if (ReferenceEquals(_model.SelectedSystem, systemItem) && HasSelectedSystemReady()) return;

            if (_inspectedSystem != null && !ReferenceEquals(_inspectedSystem, systemItem))
                RememberStandaloneRun(_inspectedSystem);

            _inspectedSystem = null;
            _pendingSystem = systemItem;
            if (systemItem == null) return;

            _pendingSpell = null;
            ResetPreviewContextForSelection();
        }

        private void TryInspectPendingSystem()
        {
            if (!_isActive || _isCleanedUp || _gl == null || _pendingSystem == null)
                return;

            VfxSystemDiagnosticItem systemItem = _pendingSystem;
            _pendingSystem = null;

            try
            {
                EnsureVfxRenderSession();
                if (_vfxRenderer == null)
                {
                    return;
                }

                InspectSystem(systemItem);
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to prepare the selected VFX system.");
                _model.LogMessages.Add($"[ERROR] Failed to prepare VFX system: {ex.Message}");
            }
        }

        private void OpenTkControl_Ready()
        {
            try
            {
                if (_isCleanedUp) return;

                _gl = Silk.NET.OpenGL.GL.GetApi(GetOpenGLProcAddress);
                if (_previewSurfaceRenderer == null)
                {
                    BitmapSource groundTexture = SceneElements.LoadSceneTexture(SceneElements.GroundTexturePath, LogService);
                    _previewSurfaceRenderer = new VfxPreviewSurfaceRenderer();
                    _previewSurfaceRenderer.Initialize(_gl, groundTexture);
                }

                if (_championMeshRenderer == null)
                {
                    _championMeshRenderer = new GlMeshRenderer(AppSettings);
                    _championMeshRenderer.Initialize(_gl);
                }

                if (_mapGeometryRenderer == null)
                {
                    _mapGeometryRenderer = new MapGeometryRenderer(AppSettings);
                    _mapGeometryRenderer.Initialize(_gl);
                }
                if (_skyRenderer == null)
                {
                    _skyRenderer = new SkyRenderer();
                    _skyRenderer.Initialize(_gl);
                    _genericSkyCube ??= SkyCubeMapFactory.LoadGeneric(LogService);
                    _skyCubeDirty = true;
                }
                if (_mapCharacterRenderer == null)
                {
                    _mapCharacterRenderer = new MapCharacterRenderer(AppSettings);
                    _mapCharacterRenderer.Initialize(_gl);
                }
                if (_mapParticleRenderer == null)
                {
                    _mapParticleRenderer = new MapParticleRenderer();
                    _mapParticleRenderer.Initialize(_gl);
                }
                _championAnimationService ??= new AnimationService(LogService);

                if (_characterInteractionController == null)
                {
                    _characterInteractionController = new ViewportModelInteractionController(
                        CameraInputSurface,
                        CharacterTransformGizmoCanvas,
                        CharacterGizmoXAxis,
                        CharacterGizmoYAxis,
                        CharacterGizmoZAxis,
                        CharacterGizmoOrigin,
                        () => _dummyViewport.Camera as ProjectionCamera,
                        _characterInteractionModels);
                    _characterInteractionController.TransformChanged += CharacterInteraction_TransformChanged;
                    RefreshCharacterInteractionTarget();
                }

                if (_cameraController == null)
                {
                    _cameraController = new CustomCameraController(_dummyViewport, CameraInputSurface);
                    _cameraController.RotationStarted += CameraController_RotationStarted;
                    _cameraController.RotationEnded += CameraController_RotationEnded;
                    ApplyCameraPreset(_model.PreviewCameraPreset, refit: true);
                }

                _model.LogMessages.Add("[GL] OpenGL viewport, preview surfaces and camera controller initialized successfully.");
                _model.LogMessages.Add(
                    $"[GL] Vendor={_gl.GetStringS(Silk.NET.OpenGL.StringName.Vendor)} | " +
                    $"Renderer={_gl.GetStringS(Silk.NET.OpenGL.StringName.Renderer)} | " +
                    $"OpenGL={_gl.GetStringS(Silk.NET.OpenGL.StringName.Version)} | " +
                    $"GLSL={_gl.GetStringS(Silk.NET.OpenGL.StringName.ShadingLanguageVersion)}");
                _gl.GetInteger(Silk.NET.OpenGL.GLEnum.MaxTextureImageUnits, out int textureUnits);
                _gl.GetInteger(Silk.NET.OpenGL.GLEnum.MaxVertexAttribs, out int vertexAttributes);
                _model.LogMessages.Add(
                    $"[GL] Limits: fragment texture units={textureUnits}, vertex attributes={vertexAttributes}.");

            }
            catch (Exception ex)
            {
                _model.LogMessages.Add($"[ERROR] GL Init failed: {ex.Message}");
            }
        }

        private void OpenTkControl_Render(TimeSpan delta)
        {
            if (_gl == null) return;

            _championMeshRenderer?.ProcessPendingReleases();
            if (_isExitPending)
            {
                // ReleaseCurrentProject clears the CPU/runtime ownership first. Finish the matching
                // GPU teardown here while the OpenGL context is current, then drop any grace-period
                // resources that no longer have an owner because an explicit Studio exit should
                // leave no project or MAP allocation behind.
                ApplyPendingMapGpuState();
                _mapGeometryRenderer?.PurgeReleasedResources();
                _vfxRenderer?.SetSystem(null);
                _isExitPending = false;
                ExitRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!_isActive || !IsVisible) return;

            float dt = ResolveSimulationFrameDelta(delta, _discardNextSimulationDelta);
            _discardNextSimulationDelta = false;
            AdvanceCharacterAutoRotate(dt);

            // Update background clear color matching main viewer (Dark Studio)
            switch (_model.BgMode)
            {
                case "Light":
                    _gl.ClearColor(0.85f, 0.85f, 0.88f, 1.0f);
                    break;
                case "Transparent":
                    _gl.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
                    break;
                default: // Dark Studio
                    _gl.ClearColor(0.08f, 0.09f, 0.12f, 1.0f);
                    break;
            }

            _gl.ClearStencil(0);
            _gl.StencilMask(0xFFu);
            _gl.Clear(
                Silk.NET.OpenGL.ClearBufferMask.ColorBufferBit |
                Silk.NET.OpenGL.ClearBufferMask.DepthBufferBit |
                Silk.NET.OpenGL.ClearBufferMask.StencilBufferBit);

            // Build view/projection matrices from the active preview camera. Orthographic presets
            // use the same camera controller but require their own projection matrix.
            if (_dummyViewport.Camera is not ProjectionCamera camera) return;

            var eye = new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z);
            var lookDir = new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z);
            var target = eye + lookDir;
            var up = new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z);
            var view = Matrix4x4.CreateLookAt(eye, target, up);

            float aspect = (float)Math.Max(1, OpenTkControl.ActualWidth) / (float)Math.Max(1, OpenTkControl.ActualHeight);
            bool hasMapScene = _mapSceneRuntime != null;
            float projectionNear = hasMapScene
                ? ViewerViewportControl.CalculateProjectionNearPlane(lookDir, isMapGeometry: true)
                : VfxPreviewCamera.NearPlane;
            float projectionFar = hasMapScene
                ? ViewerViewportControl.CalculateProjectionFarPlane(lookDir)
                : VfxPreviewCamera.FarPlane;
            Matrix4x4 proj = camera switch
            {
                PerspectiveCamera perspective => Matrix4x4.CreatePerspectiveFieldOfView(
                    (float)(perspective.FieldOfView * (Math.PI / 180.0)),
                    aspect,
                    projectionNear,
                    projectionFar),
                OrthographicCamera orthographic => Matrix4x4.CreateOrthographic(
                    (float)Math.Max(1d, orthographic.Width),
                    (float)Math.Max(1d, orthographic.Width / Math.Max(0.001f, aspect)),
                    projectionNear,
                    projectionFar),
                _ => Matrix4x4.Identity
            };
            var viewProj = view * proj;
            _characterInteractionController?.Update(viewProj);

            // OpenTK has the current context here, so deferred session creation and resource
            // preparation are safe even when WPF selected the system before the GL control was ready.
            TryInspectPendingSystem();
            _vfxRenderer?.ProcessPendingGpuState();
            ApplyPendingSkyGpuState();
            ApplyPendingMapGpuState();
            _mapGeometryRenderer?.ProcessRetainedResources();

            bool characterBackdrop = _mapSceneIsCharacterBackdrop && _model.IsSkinWorkspace;
            if (characterBackdrop)
            {
                AdvanceCurrentVfxPlayback(dt);
                UpdateChampionPoseForFrame();
            }

            // Sky is one Studio display element. MAP scenes supply their authored cubemap when available;
            // otherwise the same renderer falls back to the generic AssetsManager environment.
            if (_model.ShowPreviewSky)
                _skyRenderer?.Render(view, proj);

            // A MAP scene owns the world backdrop. In Character-backdrop mode the selected Skin remains
            // the subject and is composited into the same depth/particle/post-processing frame.
            if (_mapSceneRuntime != null)
            {
                AdvanceMapCharacterClip(dt);
                _mapSceneRuntime.Update(viewProj, dt);
                _mapGeometryRenderer?.Render(
                    viewProj,
                    view,
                    proj,
                    eye,
                    _mapSceneRuntime.SceneTimeSeconds,
                    _model.PreviewViewMode,
                    _model.EffectivePreviewWireOverlay,
                    _model.PreviewShaders);
                if (_mapSceneRuntime.ShowStructures)
                {
                    _mapCharacterRenderer?.Render(
                        _mapSceneRuntime.CharacterGroups,
                        viewProj,
                        view,
                        proj,
                        eye,
                        _mapSceneRuntime.CharacterTimeSeconds,
                        EffectiveMapSun(),
                        _mapSceneRuntime.Hidden,
                        viewMode: _model.PreviewViewMode,
                        wireOverlay: _model.EffectivePreviewWireOverlay,
                        shadersEnabled: _model.PreviewShaders);
                }
                if (characterBackdrop)
                {
                    RenderChampionMesh(viewProj, view, proj, eye);
                    UpdateCharacterArmatureOverlay(viewProj);
                }
                uint mapViewportWidth = (uint)Math.Max(1d, OpenTkControl.ActualWidth);
                uint mapViewportHeight = (uint)Math.Max(1d, OpenTkControl.ActualHeight);
                _mapPostEffectsRenderer?.CaptureSceneDepth(
                    EffectiveMapPostEffects(),
                    EffectiveMapSsao(),
                    mapViewportWidth,
                    mapViewportHeight);

                // LTK has one global particle pass block for the scene. MAP placements and an
                // inspected Character Clip use different coordinate spaces/render owners here, so
                // keep their renderers separate but coordinate the phases: every soft-depth grab
                // happens before particle colour, then all colour/wire draws land before either
                // renderer captures the frame used by distortion.
                bool mapParticlesPrepared = _mapSceneRuntime.ShowParticles &&
                    _mapParticleRenderer?.PrepareRenderFrame(
                        _mapSceneRuntime.Particles.VisibleRuntimes,
                        viewProj,
                        view,
                        mapViewportWidth,
                        mapViewportHeight,
                        _model.PreviewViewMode,
                        _model.EffectivePreviewWireOverlay) == true;

                bool sceneVfxPrepared = false;
                bool shouldDrawSceneVfx = HasSelectedMapClipReady() ||
                    (characterBackdrop && ShouldRenderCharacterVfx());
                if (shouldDrawSceneVfx && _vfxRenderer?.ActiveSystem != null)
                {
                    _vfxRenderer.SetViewportSize(OpenTkControl.ActualWidth, OpenTkControl.ActualHeight);
                    sceneVfxPrepared = _vfxRenderer.PrepareRenderFrame(
                        viewProj,
                        view,
                        _model.PreviewViewMode,
                        _model.EffectivePreviewWireOverlay);
                }

                using IDisposable mapParticleBatch = mapParticlesPrepared
                    ? _mapParticleRenderer.BeginPreparedRenderBatch()
                    : null;
                using IDisposable sceneVfxBatch = sceneVfxPrepared
                    ? _vfxRenderer.BeginPreparedRenderBatch()
                    : null;

                if (mapParticlesPrepared)
                    _mapParticleRenderer.RenderPreparedColorPass();
                if (sceneVfxPrepared)
                    _vfxRenderer.RenderPreparedColorPass();

                // Both captures see the exact same completed colour frame, before any warp draw.
                if (mapParticlesPrepared)
                    _mapParticleRenderer.CapturePreparedDistortionFrame();
                if (sceneVfxPrepared)
                    _vfxRenderer.CapturePreparedDistortionFrame();

                if (mapParticlesPrepared)
                    _mapParticleRenderer.RenderPreparedDistortionPass();
                if (sceneVfxPrepared)
                    _vfxRenderer.RenderPreparedDistortionPass();
            }
            else
                _previewSurfaceRenderer?.Render(
                    viewProj,
                    _model.ShowPreviewGrid,
                    _model.ShowPreviewGround,
                    _model.ShowPreviewStage);

            if (!characterBackdrop)
            {
                AdvanceCurrentVfxPlayback(dt);
                UpdateChampionPoseForFrame();
                RenderChampionMesh(viewProj, view, proj, eye);
                UpdateCharacterArmatureOverlay(viewProj);
            }

            if (_vfxRenderer != null)
            {
                if (_mapSceneRuntime == null && ShouldRenderCharacterVfx())
                {
                    _vfxRenderer.SetViewportSize(OpenTkControl.ActualWidth, OpenTkControl.ActualHeight);
                    _vfxRenderer.Render(viewProj, view, _model.PreviewViewMode, _model.EffectivePreviewWireOverlay);
                }
                _model.LiveParticleCount = _vfxRenderer.LiveParticleCount;
            }
            else
            {
                _model.LiveParticleCount = 0;
            }

            if (_mapSceneRuntime != null)
            {
                _mapPostEffectsRenderer?.Render(
                    EffectiveMapPostEffects(),
                    EffectiveMapSsao(),
                    view,
                    proj,
                    (uint)Math.Max(1d, OpenTkControl.ActualWidth),
                    (uint)Math.Max(1d, OpenTkControl.ActualHeight));
            }

            if (_vfxRenderer != null)
            {
                // Live active particle count per emitter lane (matches LTK Manager liveCount badge)
                foreach (var emitter in _model.Emitters)
                    emitter.ActiveParticleCount = _vfxRenderer.GetEmitterLiveCount(emitter.SourceOrder);
            }

            Dispatcher.InvokeAsync(UpdatePlayheadPosition);
        }

        private void AdvanceCurrentVfxPlayback(float dt)
        {
            // MAP character clips own the same VFX session clock themselves so their pose, cues
            // and ParticleEventData stay on one timeline. Other previews keep the generic session clock.
            if (HasSelectedMapClipReady() ||
                !_model.IsPlaying || _isUserSeeking || _vfxRenderer?.ActiveSystem == null)
            {
                return;
            }

            _vfxRenderer.ActiveSystem.Speed = _model.Speed;
            _vfxRenderer.Update(dt);
            _model.CurrentTime = _vfxRenderer.PlaybackTime;
            if (ShouldRestartPreview(_model.IsPreviewLoopEnabled, _model.CurrentTime, _model.ActiveLoopDuration))
            {
                double loopStart = ResolvePreviewLoopRestart(
                    _model.ActiveLoopStart,
                    _model.ActiveLoopDuration,
                    _model.TotalDuration);
                _vfxRenderer.Seek(loopStart);
                _vfxRenderer.Play();
                _model.CurrentTime = loopStart;
            }
            else if (_model.CurrentTime >= _model.TotalDuration)
            {
                _model.IsPlaying = false;
            }
        }

        private void UpdateChampionPoseForFrame()
        {
            if (_activeMapCharacterClip != null ||
                _championModel == null || _championAnimationService == null)
            {
                return;
            }

            ApplyAnimationClipCues(_model.CurrentTime);
            if (_championModel.CurrentAnimation != null && _championModel.Skeleton != null)
            {
                float animationTime = _activeSpellPlan?.Animation != null
                    ? SpellAnimationTime(_model.CurrentTime, _activeSpellPlan.Animation.Duration)
                    : (float)_model.CurrentTime;
                _championAnimationService.Update(
                    animationTime,
                    _championModel.CurrentAnimation,
                    _championModel.Skeleton,
                    _championModel.SkinnedMesh,
                    _championModel.Parts,
                    _championModel.Name);
                _championModel.SkinningMatrices = _championAnimationService.FinalBoneTransforms;
                _championModel.GpuSkinningData = _championAnimationService.SkinningData;
                _vfxRenderer?.SetOwnerSkinningMatrices(_championAnimationService.FinalBoneTransforms);
                _vfxRenderer?.UpdateBoneTransforms((boneName, boneHash) =>
                {
                    if (!string.IsNullOrEmpty(boneName) &&
                        _championAnimationService.TryGetBoneTransformExactName(boneName, out Matrix4x4 transform))
                    {
                        return transform;
                    }
                    if (boneHash != 0 && _championAnimationService.TryGetBoneTransformFnv(boneHash, out transform))
                        return transform;
                    return null;
                });
            }
            else if (_model.SelectedSystem == null)
            {
                _vfxRenderer?.SetOwnerSkinningMatrices(null);
                _vfxRenderer?.UpdateBoneTransforms(null);
            }
        }

        private void RenderChampionMesh(
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            Matrix4x4 projection,
            Vector3 eye)
        {
            if (_activeMapCharacterClip != null ||
                !_model.ShowChampionMesh ||
                _championModel == null ||
                _championMeshRenderer == null)
            {
                return;
            }

            var lighting = GlMeshRenderer.ReferenceCharacterLighting();
            _championMeshRenderer.Render(
                _championModel,
                viewProjection,
                view,
                projection,
                eye,
                lighting.LightDirection,
                lighting.LightColor,
                lighting.FillDirection,
                lighting.FillColor,
                lighting.AmbientColor,
                _model.PreviewViewMode,
                _model.EffectivePreviewWireOverlay,
                _model.PreviewShaders,
                mirrorCharacterX: true);
        }

        private bool ShouldRenderCharacterVfx()
        {
            if (!_model.IsSkinWorkspace) return true;
            // Explicit System inspection remains visible. The Character Effects switch owns the
            // Skin-driven Clip/Spell/idle effects rather than muting an explicitly opened System.
            return _model.SelectedSystem != null || _model.CharacterEffectsEnabled;
        }

        #endregion

        #region Camera Control

        public void ResetCamera()
        {
            ApplyCameraPreset(_model.PreviewCameraPreset, refit: true);
        }

        private void CameraController_RotationStarted(object sender, EventArgs e)
        {
            if (_model.PreviewCameraPreset == VfxPreviewCameraPreset.Orbit) return;

            // The reference keeps the projection that started the drag. Mark the camera as
            // free Orbit immediately, but defer an orthographic-to-perspective swap until release.
            _deferOrbitProjectionSwap = _dummyViewport.Camera is OrthographicCamera;
            _suppressCameraPresetFit = true;
            try
            {
                _model.PreviewCameraPreset = VfxPreviewCameraPreset.Orbit;
                VfxCameraStand orbit = VfxPreviewCamera.Stand(VfxPreviewCameraPreset.Orbit);
                _cameraController.PerspectiveMinDistance = orbit.Nearest ?? 0d;
                _cameraController.PerspectiveMaxDistance = orbit.Farthest ?? double.PositiveInfinity;
            }
            finally
            {
                _suppressCameraPresetFit = false;
            }
        }

        private void CameraController_RotationEnded(object sender, EventArgs e)
        {
            if (!_deferOrbitProjectionSwap ||
                _cameraController == null ||
                _dummyViewport.Camera is not OrthographicCamera orthographic)
            {
                _deferOrbitProjectionSwap = false;
                return;
            }

            _deferOrbitProjectionSwap = false;
            Point3D target = orthographic.Position + orthographic.LookDirection;
            Vector3D fromTarget = orthographic.Position - target;
            if (fromTarget.Length > 0.001d)
                fromTarget.Normalize();
            else
                fromTarget = new Vector3D(0d, 0d, 1d);

            float aspect = OpenTkControl.ActualHeight > 0d
                ? (float)Math.Max(1d, OpenTkControl.ActualWidth) / (float)OpenTkControl.ActualHeight
                : 1f;
            double reach = VfxPreviewCamera.ReachOfOrthographicWidth(
                (float)Math.Max(1d, orthographic.Width),
                aspect);
            Point3D position = target + fromTarget * reach;

            // Carry the exact target, orientation and visible span into the perspective camera.
            _previewPerspectiveCamera.FieldOfView = VfxPreviewCamera.OrbitFieldOfView;
            _previewPerspectiveCamera.Position = position;
            _previewPerspectiveCamera.LookDirection = target - position;
            _previewPerspectiveCamera.UpDirection = orthographic.UpDirection;
            _cameraController.SetCamera(_previewPerspectiveCamera);
        }

        private void ApplyCameraPreset(VfxPreviewCameraPreset preset, bool refit)
        {
            if (_cameraController == null) return;

            VfxCameraStand stand = VfxPreviewCamera.Stand(preset);
            _cameraController.PerspectiveMinDistance = stand.Nearest ?? 0d;
            _cameraController.PerspectiveMaxDistance = stand.Farthest ?? double.PositiveInfinity;

            ProjectionCamera camera;
            if (stand.Orthographic)
            {
                camera = _previewOrthographicCamera;
            }
            else
            {
                _previewPerspectiveCamera.FieldOfView = stand.FieldOfView;
                camera = _previewPerspectiveCamera;
            }

            _cameraController.SetCamera(camera);
            if (refit)
                FrameCurrentPreview(stand);
        }

        private void FrameCurrentPreview(VfxCameraStand stand)
        {
            if (_cameraController == null) return;

            if (stand.Farthest is float gameReach)
            {
                Vector3 target = CurrentPreviewGround();
                Vector3 position = target + stand.Direction * gameReach;
                _cameraController.SnapTo(
                    new Point3D(position.X, position.Y, position.Z),
                    new Vector3D(target.X - position.X, target.Y - position.Y, target.Z - position.Z),
                    new Vector3D(stand.Up.X, stand.Up.Y, stand.Up.Z));
                return;
            }

            FramePreviewBounds(CurrentPreviewBounds(), stand);
        }

        private VfxDefinitionBounds CurrentPreviewBounds()
        {
            if (_model.IsSkinWorkspace && _championModel != null)
            {
                VfxDefinitionBounds character = CharacterPreviewBounds();
                if (IsFiniteBounds(character)) return character;
            }

            if (_model.IsRawSystemsMode && _model.SelectedSystem?.Definition is { } definition)
            {
                VfxRigSettings settings = _vfxRenderer?.RigSettings ??
                    VfxRigSettings.ForPreset(_model.RigPreset);
                return VfxSystemBounds.Calculate(definition, settings);
            }

            return DefaultPreviewBounds();
        }

        private static bool IsFiniteBounds(VfxDefinitionBounds bounds)
        {
            static bool Finite(Vector3 value) =>
                float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
            return Finite(bounds.Min) && Finite(bounds.Max) &&
                   bounds.Max.X >= bounds.Min.X &&
                   bounds.Max.Y >= bounds.Min.Y &&
                   bounds.Max.Z >= bounds.Min.Z &&
                   (bounds.Max - bounds.Min).LengthSquared() > 1e-8f;
        }

        private VfxDefinitionBounds CharacterPreviewBounds()
        {
            if (_championModel == null) return default;
            Rect3D local = ViewerInteractionService.GetLocalBounds(_championModel);
            if (local.IsEmpty) return default;

            Matrix4x4 world = GlMeshRenderer.CreateWorldMatrix(_championModel, mirrorCharacterX: true);
            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);
            double[] xs = { local.X, local.X + local.SizeX };
            double[] ys = { local.Y, local.Y + local.SizeY };
            double[] zs = { local.Z, local.Z + local.SizeZ };
            foreach (double x in xs)
            foreach (double y in ys)
            foreach (double z in zs)
            {
                Vector3 point = Vector3.Transform(new Vector3((float)x, (float)y, (float)z), world);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            return new VfxDefinitionBounds(min, max);
        }

        private Vector3 CurrentPreviewGround()
        {
            if (_model.IsRawSystemsMode && _model.SelectedSystem?.Definition is { } definition)
            {
                VfxRigSettings settings = _vfxRenderer?.RigSettings ??
                    VfxRigSettings.ForPreset(_model.RigPreset);
                return VfxSystemBounds.Ground(definition, settings);
            }

            return Vector3.Zero;
        }

        private void FramePreviewBounds(VfxDefinitionBounds bounds, VfxCameraStand stand)
        {
            if (_cameraController == null) return;

            var up = new Vector3D(stand.Up.X, stand.Up.Y, stand.Up.Z);
            if (stand.Orthographic)
            {
                VfxOrthographicFrame frame = VfxSystemBounds.FrameOrthographic(
                    bounds,
                    (float)Math.Max(1d, OpenTkControl.ActualWidth),
                    (float)Math.Max(1d, OpenTkControl.ActualHeight),
                    stand.Direction);
                _previewOrthographicCamera.Width = Math.Max(1d, frame.Width);
                var position = new Point3D(frame.Position.X, frame.Position.Y, frame.Position.Z);
                var target = new Point3D(frame.Target.X, frame.Target.Y, frame.Target.Z);
                _cameraController.SnapTo(position, target - position, up);
                return;
            }

            float aspect = OpenTkControl.ActualHeight > 0
                ? (float)Math.Max(1d, OpenTkControl.ActualWidth) / (float)OpenTkControl.ActualHeight
                : 1f;
            VfxCameraFrame perspectiveFrame = VfxSystemBounds.FramePerspective(
                bounds,
                stand.FieldOfView,
                aspect,
                stand.Direction);
            var perspectivePosition = new Point3D(
                perspectiveFrame.Position.X,
                perspectiveFrame.Position.Y,
                perspectiveFrame.Position.Z);
            var perspectiveTarget = new Point3D(
                perspectiveFrame.Target.X,
                perspectiveFrame.Target.Y,
                perspectiveFrame.Target.Z);
            _cameraController.SnapTo(
                perspectivePosition,
                perspectiveTarget - perspectivePosition,
                up);
        }

        private static VfxDefinitionBounds DefaultPreviewBounds()
        {
            float reach = VfxSystemBounds.StandingReach;
            return new VfxDefinitionBounds(
                new Vector3(-reach, 0f, -reach),
                new Vector3(reach, VfxRigMotion.ChampionHeight, reach));
        }

        private void MapSun_Click(object sender, RoutedEventArgs e)
        {
            if (MapSunPopup == null || _mapSceneRuntime == null) return;
            SyncMapPreviewControls();
            MapSunPopup.IsOpen = !MapSunPopup.IsOpen;
        }

        private void MapPost_Click(object sender, RoutedEventArgs e)
        {
            if (MapPostPopup == null || _mapSceneRuntime == null) return;
            SyncMapPreviewControls();
            MapPostPopup.IsOpen = !MapPostPopup.IsOpen;
        }

        private void MapSunReset_Click(object sender, RoutedEventArgs e)
        {
            _mapSunPreviewOverride = null;
            SyncMapPreviewControls();
            MarkMapLightingDirty();
        }

        private void MapPostReset_Click(object sender, RoutedEventArgs e)
        {
            _hasMapPostEffectsOverride = false;
            _mapPostEffectsOverride = null;
            _mapSsaoPreviewOverride = null;
            SyncMapPreviewControls();
            MarkMapLightingDirty();
        }

        private void MapSunControl_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            CommitMapSunControls();

        private void MapSunColor_TextChanged(object sender, TextChangedEventArgs e) =>
            CommitMapSunControls();

        private void MapSsaoControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingMapPreviewControls || _mapSceneRuntime == null || MapSsaoQualityCombo == null)
                return;

            _mapSsaoPreviewOverride = new MapSsaoPreviewOverride(
                MapSsaoEnabledCheck.IsChecked == true,
                new MapSsaoData(
                    MapSsaoQualityCombo.SelectedIndex <= 0 ? 0u : 1u,
                    (float)MapSsaoRadiusSlider.Value,
                    (float)MapSsaoBiasSlider.Value,
                    (float)MapSsaoPowerSlider.Value,
                    (float)MapSsaoIntensitySlider.Value,
                    (float)MapSsaoBufferScaleSlider.Value,
                    MapSsaoEdgeAwareCheck.IsChecked == true));
            MarkMapLightingDirty();
        }

        private void MapPostControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingMapPreviewControls || _mapSceneRuntime == null || MapDepthFogColorTextBox == null)
                return;
            if (!TryParseMapPreviewColor(MapDepthFogColorTextBox.Text, out Vector4 depthColor) ||
                !TryParseMapPreviewColor(MapHeightFogColorTextBox.Text, out Vector4 heightColor))
                return;

            _hasMapPostEffectsOverride = true;
            _mapPostEffectsOverride = new MapPostEffectsData(
                new MapFogData(
                    MapDepthFogEnabledCheck.IsChecked == true,
                    depthColor,
                    (float)MapDepthFogStartSlider.Value,
                    (float)MapDepthFogEndSlider.Value,
                    (float)MapDepthFogMaxSlider.Value),
                new MapFogData(
                    MapHeightFogEnabledCheck.IsChecked == true,
                    heightColor,
                    (float)MapHeightFogStartSlider.Value,
                    (float)MapHeightFogEndSlider.Value,
                    (float)MapHeightFogMaxSlider.Value),
                new MapDepthOfFieldData(
                    MapDofEnabledCheck.IsChecked == true,
                    (float)MapDofFocalSlider.Value,
                    (float)MapDofWidthSlider.Value,
                    (float)MapDofCocSlider.Value));
            MarkMapLightingDirty();
        }

        private void CommitMapSunControls()
        {
            if (_isUpdatingMapPreviewControls || _mapSceneRuntime == null || MapSunColorTextBox == null)
                return;
            if (!TryParseMapPreviewColor(MapSunColorTextBox.Text, out Vector4 color) ||
                !TryParseMapPreviewColor(MapSunSkyColorTextBox.Text, out Vector4 sky) ||
                !TryParseMapPreviewColor(MapSunGroundColorTextBox.Text, out Vector4 ground))
                return;

            _mapSunPreviewOverride = new MapSunPreviewOverride(
                MapPreviewSemantics.SunDirection(
                    (float)MapSunAzimuthSlider.Value,
                    (float)MapSunElevationSlider.Value),
                color,
                (float)MapSunStrengthSlider.Value,
                sky,
                ground,
                (float)MapSunAmbientSlider.Value);
            MarkMapLightingDirty();
        }

        private void SyncMapPreviewControls()
        {
            if (_mapSceneRuntime == null || MapSunAzimuthSlider == null)
                return;

            _isUpdatingMapPreviewControls = true;
            try
            {
                MapSunPreviewOverride sun = _mapSunPreviewOverride ??
                    MapPreviewSemantics.OwnSun(_mapSceneRuntime.Scene.Sun);
                (float azimuth, float elevation) = MapPreviewSemantics.SunAngles(sun.Direction);
                MapSunAzimuthSlider.Value = azimuth;
                MapSunElevationSlider.Value = elevation;
                MapSunStrengthSlider.Value = sun.Strength;
                MapSunAmbientSlider.Value = sun.Ambient;
                MapSunColorTextBox.Text = FormatMapPreviewColor(sun.Color);
                MapSunSkyColorTextBox.Text = FormatMapPreviewColor(sun.SkyColor);
                MapSunGroundColorTextBox.Text = FormatMapPreviewColor(sun.GroundColor);

                MapPostEffectsData post = _hasMapPostEffectsOverride
                    ? _mapPostEffectsOverride ?? MapPreviewSemantics.NoPostEffects
                    : MapPreviewSemantics.OwnPostEffects(_mapSceneRuntime.Scene.PostEffects);
                MapDepthFogEnabledCheck.IsChecked = post.DepthFog.Enabled;
                MapDepthFogColorTextBox.Text = FormatMapPreviewColor(post.DepthFog.Color);
                MapDepthFogStartSlider.Value = post.DepthFog.Start;
                MapDepthFogEndSlider.Value = post.DepthFog.End;
                MapDepthFogMaxSlider.Value = post.DepthFog.MaxIntensity;
                MapHeightFogEnabledCheck.IsChecked = post.HeightFog.Enabled;
                MapHeightFogColorTextBox.Text = FormatMapPreviewColor(post.HeightFog.Color);
                MapHeightFogStartSlider.Value = post.HeightFog.Start;
                MapHeightFogEndSlider.Value = post.HeightFog.End;
                MapHeightFogMaxSlider.Value = post.HeightFog.MaxIntensity;
                MapDofEnabledCheck.IsChecked = post.DepthOfField.Enabled;
                MapDofFocalSlider.Value = post.DepthOfField.FocalDistance;
                MapDofWidthSlider.Value = post.DepthOfField.InFocusWidth;
                MapDofCocSlider.Value = post.DepthOfField.Coc;

                MapSsaoPreviewOverride ssao = _mapSsaoPreviewOverride ??
                    MapPreviewSemantics.OwnSsao(_mapSceneRuntime.Scene.AmbientOcclusion);
                MapSsaoEnabledCheck.IsChecked = ssao.Enabled;
                MapSsaoQualityCombo.SelectedIndex = ssao.Settings.SampleQuality == 0 ? 0 : 1;
                MapSsaoRadiusSlider.Value = ssao.Settings.SampleRadius;
                MapSsaoBiasSlider.Value = ssao.Settings.Bias;
                MapSsaoPowerSlider.Value = ssao.Settings.Power;
                MapSsaoIntensitySlider.Value = ssao.Settings.Intensity;
                MapSsaoBufferScaleSlider.Value = ssao.Settings.BufferScale;
                MapSsaoEdgeAwareCheck.IsChecked = ssao.Settings.EdgeAwareBlur;
            }
            finally
            {
                _isUpdatingMapPreviewControls = false;
            }
        }

        private static bool TryParseMapPreviewColor(string text, out Vector4 value)
        {
            value = Vector4.One;
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                object converted = ColorConverter.ConvertFromString(text.Trim());
                if (converted is not System.Windows.Media.Color color) return false;
                value = new Vector4(
                    color.R / 255f,
                    color.G / 255f,
                    color.B / 255f,
                    color.A / 255f);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static string FormatMapPreviewColor(Vector4 value)
        {
            static byte Channel(float channel) =>
                (byte)Math.Round(Math.Clamp(channel, 0f, 1f) * 255f, MidpointRounding.AwayFromZero);
            return $"#{Channel(value.X):X2}{Channel(value.Y):X2}{Channel(value.Z):X2}";
        }

        private void ResetCharacterPlacement_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedWorkspaceTab?.Kind != VfxWorkspaceTabKind.Skin) return;
            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
            tab.CharacterPlacementCustomized = false;
            _isApplyingCharacterViewportState = true;
            try
            {
                _model.CharacterRotationX = 0d;
                _model.CharacterRotationY = 0d;
                _model.CharacterRotationZ = 0d;
                _model.CharacterScaleMultiplier = 1d;
                if (_model.HasActiveCharacterBackdrop && _mapSceneRuntime?.Scene != null)
                {
                    tab.CharacterPlacedOnKey = VfxInstallationMapCatalog.BackdropKey(_model.SelectedCharacterBackdrop?.Source);
                }
                else
                {
                    tab.CharacterPlacedOnKey = null;
                    _model.CharacterPositionX = 0d;
                    _model.CharacterPositionY = 0d;
                    _model.CharacterPositionZ = 0d;
                }
            }
            finally
            {
                _isApplyingCharacterViewportState = false;
            }

            if (_model.HasActiveCharacterBackdrop && _mapSceneRuntime?.Scene != null)
                ApplyCharacterBackdropOrigin(_mapSceneRuntime.Scene, _model.SelectedCharacterBackdrop?.Source);
            else
                ApplyCharacterPlacement();
            e.Handled = true;
        }

        private void FitCharacter_Click(object sender, RoutedEventArgs e)
        {
            if (_cameraController == null || _championModel == null) return;
            VfxDefinitionBounds bounds = CharacterPreviewBounds();
            if (!IsFiniteBounds(bounds)) return;

            if (_dummyViewport.Camera is ProjectionCamera camera)
            {
                Vector3 look = new((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z);
                Vector3 up = new((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z);
                if (look.LengthSquared() <= 1e-8f) look = -Vector3.UnitZ;
                if (up.LengthSquared() <= 1e-8f) up = Vector3.UnitY;
                var stand = new VfxCameraStand(
                    -Vector3.Normalize(look),
                    Vector3.Normalize(up),
                    camera is PerspectiveCamera perspective ? (float)perspective.FieldOfView : VfxPreviewCamera.OrbitFieldOfView,
                    camera is OrthographicCamera);
                FramePreviewBounds(bounds, stand);
            }
            else
            {
                FramePreviewBounds(bounds, VfxPreviewCamera.Stand(_model.PreviewCameraPreset));
            }
            e.Handled = true;
        }

        private void PreviewShow_Click(object sender, RoutedEventArgs e)
        {
            if (PreviewShowPopup != null)
                PreviewShowPopup.IsOpen = !PreviewShowPopup.IsOpen;
        }

        private void PreviewViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (PreviewViewModePopup != null)
                PreviewViewModePopup.IsOpen = !PreviewViewModePopup.IsOpen;
        }

        private void PreviewCamera_Click(object sender, RoutedEventArgs e)
        {
            if (PreviewCameraPopup != null)
                PreviewCameraPopup.IsOpen = !PreviewCameraPopup.IsOpen;
        }

        private void RigPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                UpdateRigControlValues();
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                btn.ContextMenu.IsOpen = true;
            }
        }



        private void RerollSeed_Click(object sender, RoutedEventArgs e)
        {
            if (_inspectedSystem == null) return;

            RememberStandaloneRun(_inspectedSystem);
            string key = StandaloneRunKey(_inspectedSystem);
            if (_standaloneRunMemory.TryGetValue(key, out StandaloneRunMemory memory))
                _standaloneRunMemory[key] = memory with { Seed = NextPlaybackSeed(memory.Seed) };

            InspectSystem(_inspectedSystem);
        }

        private void SetRigPreset_Click(object sender, RoutedEventArgs e)
        {
            if (!_model.IsRawSystemsMode) return;
            if (sender is not FrameworkElement { Tag: string tagStr } ||
                !Enum.TryParse(tagStr, out VfxRigPreset preset))
            {
                return;
            }

            VfxRigSettings settings = VfxRigSettings.ForPreset(preset) with
            {
                IsLooping = _model.IsPreviewLoopEnabled
            };
            _model.RigPreset = preset;
            if (_vfxRenderer != null)
            {
                _vfxRenderer.RigSettings = settings;
                double duration = ResolveTimelineDuration(_vfxRenderer.RigDuration);
                UpdatePreviewLoopRangeForDuration(duration);
                _model.CurrentTime = _vfxRenderer.PlaybackTime;
                _vfxRenderer.Play();
                _model.IsPlaying = true;
            }
            UpdateRigControlValues();
        }

        private void ApplyRigTuning(VfxRigSettings settings)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null || !_model.IsRawSystemsMode) return;

            _vfxRenderer.RigSettings = settings;
            _model.RigPreset = settings.Preset;

            double duration = ResolveTimelineDuration(_vfxRenderer.RigDuration);
            UpdatePreviewLoopRangeForDuration(duration);

            _model.CurrentTime = _vfxRenderer.PlaybackTime;
            UpdateRigControlValues();
            UpdateTimelineTrackMetrics();
            UpdatePlayheadPosition();
        }

        private void UpdateRigControlValues()
        {
            if (RigHeightSlider == null ||
                RigDistancePanel == null ||
                RigOrbitPanel == null)
            {
                return;
            }

            VfxRigSettings settings = _vfxRenderer?.RigSettings ?? VfxRigSettings.ForPreset(_model.RigPreset);
            try
            {
                _isUpdatingRigControls = true;
                RigHeightSlider.Value = settings.Height;
                RigHeightValueText.Text = $"{Math.Round(settings.Height)} u";

                // Show only the tuning controls used by the selected motion preset.
                RigDistancePanel.Visibility = settings.MotionKind == VfxRigMotionKind.Path
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RigDistanceSlider.Value = settings.FlightRange;
                RigDistanceValueText.Text = $"{Math.Round(settings.FlightRange)} u";
                RigSpeedSlider.Value = settings.FlightSpeed;
                RigSpeedValueText.Text = $"{Math.Round(settings.FlightSpeed)} u/s";

                RigOrbitPanel.Visibility = settings.MotionKind == VfxRigMotionKind.Orbit
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RigRadiusSlider.Value = settings.OrbitRadius;
                RigRadiusValueText.Text = $"{Math.Round(settings.OrbitRadius)} u";
                RigPeriodSlider.Value = settings.OrbitPeriod;
                RigPeriodValueText.Text = $"{settings.OrbitPeriod:F2} s";

                // TimelineLoopToggleButton binds directly to IsPreviewLoopEnabled so
                // Systems, Clips, and Spells share one loop state.
            }
            finally
            {
                _isUpdatingRigControls = false;
            }
        }

        private void RigHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { Height = (float)e.NewValue });
        }

        private void RigDistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { FlightRange = (float)e.NewValue });
        }

        private void RigSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { FlightSpeed = (float)e.NewValue });
        }

        private void RigRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { OrbitRadius = (float)e.NewValue });
        }

        private void RigPeriodSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingRigControls || _vfxRenderer == null) return;
            ApplyRigTuning(_vfxRenderer.RigSettings with { OrbitPeriod = (float)e.NewValue });
        }

        private void TimelineLoopToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingRigControls || sender is not ToggleButton toggleButton) return;
            SetPreviewLoopEnabled(toggleButton.IsChecked == true);
        }

        private void SetPreviewLoopEnabled(bool enabled)
        {
            if (enabled)
            {
                double span = ResolveTimelineDuration(_model.TotalDuration);
                _model.ActiveLoopStart = 0d;
                _model.ActiveLoopDuration = span;
            }

            _model.IsPreviewLoopEnabled = enabled;

            // Standalone Systems also use the rig lifecycle loop. Clips and Spells are
            // replayed by the common timeline loop and do not need a separate engine path.
            if (_model.IsRawSystemsMode && _vfxRenderer != null &&
                _vfxRenderer.RigSettings.IsLooping != enabled)
            {
                _vfxRenderer.RigSettings = _vfxRenderer.RigSettings with { IsLooping = enabled };
            }

            UpdateTimelineTrackMetrics();
            UpdatePlayheadPosition();
        }

        private void ResetPreviewLoopRange(double duration)
        {
            double span = ResolveTimelineDuration(duration);
            _model.TotalDuration = span;
            _model.ActiveLoopStart = 0d;
            _model.ActiveLoopDuration = span;
        }

        private void UpdatePreviewLoopRangeForDuration(double duration)
        {
            double span = ResolveTimelineDuration(duration);
            _model.TotalDuration = span;
            if (!_model.IsPreviewLoopEnabled)
            {
                _model.ActiveLoopStart = 0d;
                _model.ActiveLoopDuration = span;
                return;
            }

            (double from, double to) = ClampPreviewLoop(
                _model.ActiveLoopStart,
                _model.ActiveLoopDuration > 0d ? _model.ActiveLoopDuration : span,
                span);
            _model.ActiveLoopStart = from;
            _model.ActiveLoopDuration = to;
        }

        #endregion

        #region Directory & BIN Scanning

        private void ExitStudio_Click(object sender, RoutedEventArgs e)
        {
            if (_isExitPending) return;

            ReleaseCurrentProject();
            if (_gl != null && _isGlStarted && _isActive && IsVisible)
            {
                // Finish GPU teardown on the render callback while the OpenGL context is current.
                _isExitPending = true;
                return;
            }

            ExitRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ReleaseCurrentProject()
        {
            _scanCancellation?.Cancel();
            _binCancellation?.Cancel();
            ClearWorkspaceTabs();
            CancelMapLoadAndClearScene();
            _mapClipCancellation?.Dispose();
            _mapClipCancellation = null;
            _animationClipCancellation?.Cancel();
            _animationClipCancellation?.Dispose();
            _animationClipCancellation = null;
            _championLoadGeneration++;
            _model.IsPlaying = false;
            _vfxRenderer?.Pause();
            _pendingSystem = null;
            _inspectedSystem = null;
            _pendingSpell = null;
            _activeSpellPlan = null;
            ClearAnimationClipCues();

            if (_championModel != null)
            {
                var championModel = _championModel;
                _championModel = null;
                _championMeshRenderer?.QueueRelease(championModel);
                championModel.CurrentAnimation = null;
                RunReleaseStep("Champion SceneModel", championModel.Dispose);
            }

            RunReleaseStep("Champion animation cache", () => _championAnimationService?.ClearCache());
            var clipCatalog = _clipCatalog;
            _clipCatalog = null;
            RunReleaseStep(nameof(VfxClipCatalog), () => clipCatalog?.Dispose());
            _activeBundle = null;
            _championBundle = null;

            _model.SelectedAnimation = null;
            _model.SelectedSpell = null;
            _model.SelectedSystem = null;
            _model.SelectedSkin = null;
            _model.DetectedAnimations.Clear();
            _model.Systems.Clear();
            _model.SelectedEmitter = null;
            _model.Emitters.Clear();
            _model.Textures.Clear();
            _model.Meshes.Clear();
            _model.DetectedSkins.Clear();
            _model.BrowserRoots.Clear();
            _suppressMapVariantReload = true;
            try
            {
                _model.SetMapVariants(Array.Empty<MapVariantData>());
            }
            finally
            {
                _suppressMapVariantReload = false;
            }
            _model.LogMessages.Clear();
            _model.RootPath = string.Empty;
            _model.SearchQuery = string.Empty;
            _model.EmitterFilterText = string.Empty;
            _model.CurrentTime = 0;
            _model.TotalDuration = 5.0;
            _model.ActiveLoopStart = 0;
            _model.ActiveLoopDuration = 0;
            _model.LiveParticleCount = 0;
            _model.HasChampionMesh = false;
            _model.HasAnySolo = false;
            _model.IsAllMuted = false;
            _model.StatusText = "Ready";
        }

        private void BrowseRoot_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select asset directory root"
            };

            if (dialog.ShowDialog() == true)
                LoadExtractedContainer(dialog.FolderName);
        }

        private void ReloadRoot_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_model.RootPath))
                LoadExtractedContainer(_model.RootPath);
        }

        private void RootPathTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_model.RootPath))
                LoadExtractedContainer(_model.RootPath);
        }

        public void LoadExtractedContainer(string rootFolder)
        {
            if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder) || _isCleanedUp)
                return;

            string fullRoot = Path.GetFullPath(rootFolder);
            _model.RootPath = fullRoot;
            // Folder selection is discovery-only. MAP geometry is loaded exclusively from the
            // unified VFX Studio browser through an explicit MapFile selection.
            _ = ScanRootDirectoryAsync(fullRoot);
        }

        private async Task<bool> ScanRootDirectoryAsync(string rootFolder)
        {
            if (!Directory.Exists(rootFolder)) return false;
            _scanCancellation?.Cancel();
            _scanCancellation = new System.Threading.CancellationTokenSource();
            var operation = _scanCancellation;
            CancelMapLoadAndClearScene();

            // A folder scan is discovery-only. Drop any previously loaded workspace before
            // populating the new catalog so the project opens in a neutral, collapsed state.
            ClearWorkspaceTabs();
            ClearLoadedSkinState();
            _model.SelectedSkin = null;
            _model.DetectedSkins.Clear();
            _model.BrowserRoots.Clear();
            _model.StatusText = "Reading BIN catalog...";
            Func<uint, string> resolveBinEntry = VfxLoadingService == null
                ? null
                : VfxLoadingService.ResolveBinEntryPath;
            Func<ulong, string> resolveGamePath = VfxLoadingService == null
                ? null
                : VfxLoadingService.ResolveGamePath;
            try
            {
                VfxFolderCatalog.BrowserCatalog catalog = await System.Threading.Tasks.Task.Run(
                    () => VfxFolderCatalog.ScanBrowser(
                        rootFolder,
                        operation.Token,
                        resolveBinEntry,
                        LogService),
                    operation.Token);
                IReadOnlyList<MapSceneSource> installationMaps = await System.Threading.Tasks.Task.Run(
                    () => VfxInstallationMapCatalog.Discover(
                        AppSettings,
                        rootFolder,
                        resolveGamePath,
                        operation.Token,
                        LogService),
                    operation.Token);
                if (operation.IsCancellationRequested || _isCleanedUp) return false;
                _model.DetectedSkins.Clear();
                _model.BrowserRoots.Clear();
                _model.CharacterBackdrops.Clear();

                // Project geometry is listed first and wins for the same logical MapPath. Installation
                // choices then fill the rest so a Character-only project can still use Map11/other maps.
                var backdropKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (MapSceneSource mapSource in catalog.MapSources)
                {
                    string key = VfxInstallationMapCatalog.BackdropKey(mapSource);
                    if (string.IsNullOrWhiteSpace(key) || !backdropKeys.Add(key)) continue;
                    _model.CharacterBackdrops.Add(new VfxCharacterBackdropOption(
                        VfxInstallationMapCatalog.Label(mapSource, projectSource: true),
                        mapSource));
                }
                foreach (MapSceneSource mapSource in installationMaps)
                {
                    string key = VfxInstallationMapCatalog.BackdropKey(mapSource);
                    if (string.IsNullOrWhiteSpace(key) || !backdropKeys.Add(key)) continue;
                    _model.CharacterBackdrops.Add(new VfxCharacterBackdropOption(
                        VfxInstallationMapCatalog.Label(mapSource, projectSource: false),
                        mapSource));
                }
                _model.NotifyCharacterCollectionsChanged();
                foreach (VfxSkinItem entry in catalog.Entries)
                {
                    // Never carry expansion state into a freshly discovered project tree.
                    entry.IsExpanded = false;
                    _model.DetectedSkins.Add(entry);
                }
                foreach (VfxBrowserFolder rootNode in catalog.Roots)
                    _model.BrowserRoots.Add(rootNode);

                _suppressMapVariantReload = true;
                try
                {
                    _model.SetMapVariants(catalog.MapVariants);
                }
                finally
                {
                    _suppressMapVariantReload = false;
                }

                VfxBrowserFolder charactersRoot = catalog.Roots.FirstOrDefault(root =>
                    string.Equals(root.Title, "Characters", StringComparison.Ordinal));
                int characterCount = charactersRoot?.Children.OfType<VfxBrowserFolder>().Count() ?? 0;
                _model.StatusText = catalog.MapSources.Count > 0
                    ? $"Found {catalog.MapSources.Count} MapGeometry assets and {catalog.Entries.Count} VFX BIN entries across {characterCount} characters. Select a MAP asset to load it."
                    : $"Found {catalog.Entries.Count} VFX BIN entries across {characterCount} characters.";
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to scan VFX folder.");
                return false;
            }
            finally
            {
                if (ReferenceEquals(_scanCancellation, operation)) _scanCancellation = null;
                operation.Dispose();
            }
        }

        private static string SelectedMapFileFor(MapPath map, string rootFolder)
        {
            if (map == null || string.IsNullOrWhiteSpace(rootFolder))
                return null;

            string geometry = Path.Combine(rootFolder, map.GeometryVirtualPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(geometry))
                return Path.GetFullPath(geometry);

            string materials = Path.Combine(rootFolder, map.MaterialsVirtualPath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(materials) ? Path.GetFullPath(materials) : null;
        }

        private static string MapSourceDisplayName(MapSceneSource source)
        {
            if (!string.IsNullOrWhiteSpace(source?.SelectedMapFilePath))
                return Path.GetFileName(source.SelectedMapFilePath);
            if (!string.IsNullOrWhiteSpace(source?.Map?.Value))
                return source.Map.Value.Replace('\\', '/').Split('/').LastOrDefault() ?? source.Map.Value;
            return "MAP";
        }

        private async Task LoadDetectedMapAsync(
            MapSceneSource source,
            bool asCharacterBackdrop = false,
            int? initialVisibilityFlags = null)
        {
            if (source == null || MapViewerSceneService == null || _isCleanedUp)
                return;

            _mapCancellation?.Cancel();
            _mapCancellation?.Dispose();
            ClearPendingMapTextureUpdates();
            var operation = new System.Threading.CancellationTokenSource();
            _mapCancellation = operation;
            MapSceneRuntime staged = null;
            Task<IReadOnlyList<MapCharacterRuntimeGroup>> characterTask = null;
            Task<MapParticleSceneRuntime> particleTask = null;
            bool characterAssetsAdopted = false;
            bool particleAssetsAdopted = false;

            try
            {
                // Match current LTK MAIN's backdrop flow: publish decoded geometry/material state first.
                // Structure skins, placed VFX and texture waves must not hold the first visible frame.
                staged = await MapViewerSceneService.LoadBackdropAsync(source, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (staged == null)
                {
                    _model.StatusText = $"Unable to load {MapSourceDisplayName(source)}.";
                    return;
                }
                if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation))
                    return;

                if (asCharacterBackdrop && initialVisibilityFlags.HasValue)
                    staged.SetVisibilityFlags(initialVisibilityFlags.Value);

                _mapClipCancellation?.Cancel();
                ClearMapCharacterClipPreview();
                MapSceneRuntime previous = _mapSceneRuntime;
                _mapSceneRuntime = staged;
                _mapSceneIsCharacterBackdrop = asCharacterBackdrop;
                SyncSelectedMapVariant(source);
                _mapSkyCube = null;
                _skyCubeDirty = true;
                staged = null;
                MapSceneRuntime backdrop = _mapSceneRuntime;
                MapSceneData scene = backdrop.Scene;
                backdrop.ShowStructures = _model.MapStructuresVisible;
                backdrop.ShowParticles = _model.MapParticlesVisible;
                _model.HasMapPreview = true;
                _model.SetMapLayers(MapGeometrySemantics.Layers(scene.Geometry), backdrop.VisibilityFlags);
                _mapGpuSceneDirty = true;
                _mapTexturesDirty = false;
                previous?.Dispose();
                if (!asCharacterBackdrop)
                {
                    ReplaceMapBrowserRoot(MapBrowserSemantics.Build(backdrop));
                    SnapMapCamera(scene);
                }
                if (asCharacterBackdrop)
                    ApplyCharacterBackdropOrigin(scene, source);
                _model.StatusText = asCharacterBackdrop
                    ? $"Loaded {MapSourceDisplayName(source)} behind the active Character. Loading backdrop resources..."
                    : $"Loaded {MapSourceDisplayName(source)} backdrop. Loading scene resources...";
                OpenTkControl?.InvalidateVisual();

                // Every secondary MAP resource is independent. One malformed texture, optional sky,
                // Character skin or VFX asset must never cancel the geometry/material scene nor the
                // other resource waves. Cancellation still propagates when this MAP selection is replaced.
                Task<VfxCubeMapData> skyTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadBackdropSkyAsync(source.ProjectRoot, operation.Token),
                    fallback: null,
                    "sky",
                    operation.Token);
                Task<IReadOnlyDictionary<string, MapTextureImage>> previewTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadPreviewTexturesAsync(
                        backdrop,
                        operation.Token,
                        (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Base, key, image)),
                    new Dictionary<string, MapTextureImage>(StringComparer.Ordinal),
                    "base preview textures",
                    operation.Token);
                Task<IReadOnlyDictionary<string, MapTextureImage>> previewProgramTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadPreviewProgramTexturesAsync(
                        backdrop,
                        operation.Token,
                        (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Program, key, image)),
                    new Dictionary<string, MapTextureImage>(StringComparer.Ordinal),
                    "program preview textures",
                    operation.Token);
                Task<IReadOnlyDictionary<string, MapTextureImage>> previewLightmapTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadPreviewLightmapsAsync(
                        backdrop,
                        operation.Token,
                        (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Lightmap, key, image)),
                    new Dictionary<string, MapTextureImage>(StringComparer.OrdinalIgnoreCase),
                    "preview lightmaps",
                    operation.Token);
                characterTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadCharacterAssetsAsync(backdrop, operation.Token),
                    (IReadOnlyList<MapCharacterRuntimeGroup>)Array.Empty<MapCharacterRuntimeGroup>(),
                    "structures",
                    operation.Token);
                particleTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadParticleAssetsAsync(backdrop, operation.Token),
                    new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()),
                    "VFX placements",
                    operation.Token);

                Task previewWaveTask = Task.WhenAll(previewTask, previewProgramTask, previewLightmapTask);
                bool previewFinalized = false;
                bool skyFinalized = false;
                Task<IReadOnlyDictionary<string, MapTextureImage>> fullTextureTask = null;
                Task<IReadOnlyDictionary<string, MapTextureImage>> fullProgramTask = null;
                Task<IReadOnlyDictionary<string, MapTextureImage>> fullLightmapTask = null;

                // LTK lets independent scene resources join as they land. Do not hold Characters or
                // placed VFX behind the complete preview-texture wave; texture callbacks already make
                // the backdrop progressively visible while these tasks finish in parallel.
                while (!previewFinalized || !characterAssetsAdopted || !particleAssetsAdopted || !skyFinalized)
                {
                    var pending = new List<Task>(4);
                    if (!previewFinalized) pending.Add(previewWaveTask);
                    if (!characterAssetsAdopted) pending.Add(characterTask);
                    if (!particleAssetsAdopted) pending.Add(particleTask);
                    if (!skyFinalized) pending.Add(skyTask);
                    Task completed = await Task.WhenAny(pending);

                    if (!skyFinalized && ReferenceEquals(completed, skyTask))
                    {
                        VfxCubeMapData sky = await skyTask;
                        operation.Token.ThrowIfCancellationRequested();
                        if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation) ||
                            !ReferenceEquals(_mapSceneRuntime, backdrop))
                        {
                            return;
                        }

                        _mapSkyCube = sky;
                        _skyCubeDirty = true;
                        skyFinalized = true;
                        OpenTkControl?.InvalidateVisual();
                    }

                    if (!previewFinalized && ReferenceEquals(completed, previewWaveTask))
                    {
                        await previewWaveTask;
                        operation.Token.ThrowIfCancellationRequested();
                        if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation) ||
                            !ReferenceEquals(_mapSceneRuntime, backdrop))
                        {
                            return;
                        }

                        backdrop.SetBackdropTextures(await previewTask);
                        backdrop.SetBackdropProgramTextures(await previewProgramTask);
                        backdrop.SetBackdropLightmaps(await previewLightmapTask);
                        _mapTexturesDirty = true;
                        OpenTkControl?.InvalidateVisual();
                        previewFinalized = true;

                        // Like LTK, only start the sharpening wave once every preview request has
                        // settled. Each full texture still publishes independently as it arrives.
                        fullTextureTask = LoadMapResourceSafelyAsync(
                            MapViewerSceneService.LoadFullTexturesAsync(
                                backdrop,
                                operation.Token,
                                (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Base, key, image)),
                            new Dictionary<string, MapTextureImage>(StringComparer.Ordinal),
                            "full base textures",
                            operation.Token);
                        fullProgramTask = LoadMapResourceSafelyAsync(
                            MapViewerSceneService.LoadFullProgramTexturesAsync(
                                backdrop,
                                operation.Token,
                                (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Program, key, image)),
                            new Dictionary<string, MapTextureImage>(StringComparer.Ordinal),
                            "full program textures",
                            operation.Token);
                        fullLightmapTask = LoadMapResourceSafelyAsync(
                            MapViewerSceneService.LoadFullLightmapsAsync(
                                backdrop,
                                operation.Token,
                                (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Lightmap, key, image)),
                            new Dictionary<string, MapTextureImage>(StringComparer.OrdinalIgnoreCase),
                            "full lightmaps",
                            operation.Token);
                    }

                    if (!characterAssetsAdopted && ReferenceEquals(completed, characterTask))
                    {
                        IReadOnlyList<MapCharacterRuntimeGroup> characters = await characterTask;
                        operation.Token.ThrowIfCancellationRequested();
                        if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation) ||
                            !ReferenceEquals(_mapSceneRuntime, backdrop))
                        {
                            DisposeCharacterGroups(characters);
                            characterAssetsAdopted = true;
                            return;
                        }

                        backdrop.SetCharacterGroups(characters);
                        characterAssetsAdopted = true;
                        bool characterBackdropNow = _mapSceneIsCharacterBackdrop;
                        if (!characterBackdropNow)
                            ReplaceMapBrowserRoot(MapBrowserSemantics.Build(backdrop));
                        _model.StatusText = characterBackdropNow
                            ? $"Character backdrop loaded {backdrop.CharacterGroups.Count} authored MAP structures. Loading remaining resources..."
                            : $"Loaded {backdrop.CharacterGroups.Count} MAP character skins. Loading remaining scene resources...";
                        OpenTkControl?.InvalidateVisual();
                    }

                    if (!particleAssetsAdopted && ReferenceEquals(completed, particleTask))
                    {
                        MapParticleSceneRuntime particles = await particleTask;
                        operation.Token.ThrowIfCancellationRequested();
                        if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation) ||
                            !ReferenceEquals(_mapSceneRuntime, backdrop))
                        {
                            particles?.Dispose();
                            particleAssetsAdopted = true;
                            return;
                        }

                        backdrop.SetParticles(particles);
                        particleAssetsAdopted = true;
                        bool characterBackdropNow = _mapSceneIsCharacterBackdrop;
                        if (!characterBackdropNow)
                            ReplaceMapBrowserRoot(MapBrowserSemantics.Build(backdrop));
                        _model.StatusText = characterBackdropNow
                            ? $"Character backdrop loaded {backdrop.Particles.Runtimes.Count} authored MAP VFX placements. Loading remaining resources..."
                            : $"Loaded {backdrop.Particles.Runtimes.Count} MAP VFX placements. Loading remaining scene resources...";
                        OpenTkControl?.InvalidateVisual();
                    }
                }

                if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation) ||
                    !ReferenceEquals(_mapSceneRuntime, backdrop))
                {
                    return;
                }

                _model.StatusText = _mapSceneIsCharacterBackdrop
                    ? $"Character backdrop {scene.Source.Map.Value} ready."
                    : $"Loaded map {scene.Source.Map.Value}.";
                _model.LogMessages.Add(
                    $"[MAP] Loaded {scene.Geometry.Meshes.Count} backdrop meshes, " +
                    $"{scene.Characters.Count} characters and {scene.Particles.Count} particle placeables.");
                OpenTkControl?.InvalidateVisual();

                if (fullTextureTask != null && fullProgramTask != null && fullLightmapTask != null)
                {
                    await Task.WhenAll(fullTextureTask, fullProgramTask, fullLightmapTask);
                    operation.Token.ThrowIfCancellationRequested();
                    if (!_isCleanedUp && ReferenceEquals(_mapCancellation, operation) &&
                        ReferenceEquals(_mapSceneRuntime, backdrop))
                    {
                        backdrop.SetBackdropTextures(await fullTextureTask);
                        backdrop.SetBackdropProgramTextures(await fullProgramTask);
                        backdrop.SetBackdropLightmaps(await fullLightmapTask);
                        _mapTexturesDirty = true;
                        OpenTkControl?.InvalidateVisual();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                operation.Cancel();
                LogService?.LogError(ex, "Failed to load selected MAP geometry in VFX Studio.");
                _model.StatusText = "Unable to load the selected map scene.";
                _model.LogMessages.Add($"[MAP ERROR] {ex.Message}");
            }
            finally
            {
                staged?.Dispose();
                if (!characterAssetsAdopted && characterTask != null)
                {
                    try
                    {
                        IReadOnlyList<MapCharacterRuntimeGroup> characters = await characterTask;
                        DisposeCharacterGroups(characters);
                    }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                }
                if (!particleAssetsAdopted && particleTask != null)
                {
                    try
                    {
                        MapParticleSceneRuntime particles = await particleTask;
                        particles?.Dispose();
                    }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                }
                if (ReferenceEquals(_mapCancellation, operation))
                    _mapCancellation = null;
                operation.Dispose();
            }
        }

        private async Task<T> LoadMapResourceSafelyAsync<T>(
            Task<T> task,
            T fallback,
            string label,
            System.Threading.CancellationToken cancellationToken)
        {
            try
            {
                return await task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                string resource = string.IsNullOrWhiteSpace(label) ? "resource" : label;
                LogService?.LogError(ex, $"MAP {resource} load failed without cancelling the scene.");
                _model.LogMessages.Add($"[MAP WARNING] {resource}: {ex.Message}");
                return fallback;
            }
        }

        private static void DisposeCharacterGroups(IReadOnlyList<MapCharacterRuntimeGroup> groups)
        {
            if (groups == null) return;
            foreach (MapCharacterRuntimeGroup group in groups)
                group?.Dispose();
        }

        private void QueueMapTextureUpdate(
            MapSceneRuntime runtime,
            MapTextureUpdateKind kind,
            string key,
            MapTextureImage image)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(key) || image == null || _isCleanedUp)
                return;

            bool schedule = false;
            lock (_mapTextureUpdateGate)
            {
                if (!ReferenceEquals(_pendingMapTextureRuntime, runtime))
                {
                    _pendingMapTextureRuntime = runtime;
                    _pendingMapTextureUpdates.Clear();
                    _pendingMapProgramTextureUpdates.Clear();
                    _pendingMapLightmapUpdates.Clear();
                }

                switch (kind)
                {
                    case MapTextureUpdateKind.Base:
                        _pendingMapTextureUpdates[key] = image;
                        break;
                    case MapTextureUpdateKind.Program:
                        _pendingMapProgramTextureUpdates[key] = image;
                        break;
                    case MapTextureUpdateKind.Lightmap:
                        _pendingMapLightmapUpdates[key] = image;
                        break;
                }

                if (!_mapTexturePublishQueued)
                {
                    _mapTexturePublishQueued = true;
                    schedule = true;
                }
            }

            if (schedule && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    new Action(FlushPendingMapTextureUpdates));
            }
        }

        private void FlushPendingMapTextureUpdates()
        {
            MapSceneRuntime runtime;
            Dictionary<string, MapTextureImage> baseTextures;
            Dictionary<string, MapTextureImage> programTextures;
            Dictionary<string, MapTextureImage> lightmaps;

            lock (_mapTextureUpdateGate)
            {
                runtime = _pendingMapTextureRuntime;
                baseTextures = new Dictionary<string, MapTextureImage>(_pendingMapTextureUpdates, StringComparer.Ordinal);
                programTextures = new Dictionary<string, MapTextureImage>(_pendingMapProgramTextureUpdates, StringComparer.Ordinal);
                lightmaps = new Dictionary<string, MapTextureImage>(_pendingMapLightmapUpdates, StringComparer.OrdinalIgnoreCase);
                _pendingMapTextureUpdates.Clear();
                _pendingMapProgramTextureUpdates.Clear();
                _pendingMapLightmapUpdates.Clear();
                _mapTexturePublishQueued = false;
            }

            if (_isCleanedUp || runtime == null || !ReferenceEquals(runtime, _mapSceneRuntime))
                return;

            runtime.MergeBackdropTextures(baseTextures);
            runtime.MergeBackdropProgramTextures(programTextures);
            runtime.MergeBackdropLightmaps(lightmaps);
            if (baseTextures.Count == 0 && programTextures.Count == 0 && lightmaps.Count == 0)
                return;

            _mapTexturesDirty = true;
            OpenTkControl?.InvalidateVisual();
        }

        private void ClearPendingMapTextureUpdates()
        {
            lock (_mapTextureUpdateGate)
            {
                _pendingMapTextureRuntime = null;
                _pendingMapTextureUpdates.Clear();
                _pendingMapProgramTextureUpdates.Clear();
                _pendingMapLightmapUpdates.Clear();
                _mapTexturePublishQueued = false;
            }
        }

        private void CancelMapLoadAndClearScene()
        {
            _mapCancellation?.Cancel();
            _mapCancellation = null;
            _mapLayerCancellation?.Cancel();
            _mapLayerCancellation = null;
            _model.HasMapPreview = false;
            _model.SetMapLayers(Array.Empty<MapGeometryLayerData>(), 0);
            ClearPendingMapTextureUpdates();
            _mapClipCancellation?.Cancel();
            ClearMapCharacterClipPreview();

            MapSceneRuntime previous = _mapSceneRuntime;
            bool wasCharacterBackdrop = _mapSceneIsCharacterBackdrop;
            _mapSceneRuntime = null;
            _mapSceneIsCharacterBackdrop = false;
            _mapSkyCube = null;
            _skyCubeDirty = true;
            if (!wasCharacterBackdrop)
                ReplaceMapBrowserRoot(null);
            _mapGpuSceneDirty = true;
            _mapTexturesDirty = false;
            previous?.Dispose();
        }

        private void ReplaceMapBrowserRoot(MapBrowserNode root)
        {
            _model.SelectedMapNode = null;
            if (_mapBrowserRoot != null)
                _model.BrowserRoots.Remove(_mapBrowserRoot);

            _mapBrowserRoot = root;
            if (root != null)
            {
                _model.BrowserRoots.Insert(0, root);
                RefreshMapBrowserVisibility(root);
            }
        }

        private void ApplyPendingSkyGpuState()
        {
            if (!_skyCubeDirty || _skyRenderer == null)
                return;

            VfxCubeMapData activeSky = _mapSceneRuntime != null && _mapSkyCube?.IsValid == true
                ? _mapSkyCube
                : _genericSkyCube;
            _skyRenderer.SetCube(activeSky);
            _skyCubeDirty = false;
        }

        private void ApplyPendingMapGpuState()
        {
            if (_mapGeometryRenderer == null)
                return;

            if (_mapGpuSceneDirty)
            {
                _mapCharacterRenderer?.Clear();
                _mapParticleRenderer?.Clear();
                if (_mapSceneRuntime != null)
                {
                    _mapGeometryRenderer.LoadScene(_mapSceneRuntime.Scene);
                    _mapGeometryRenderer.SetVisibilityFlags(_mapSceneRuntime.VisibilityFlags);
                    _mapGeometryRenderer.SetPreviewSun(EffectiveMapSun(), _mapSunPreviewOverride);
                    // Backdrop loads publish geometry before texture waves. A preview wave may finish
                    // before the first GL frame, so LoadScene's immutable scene dictionaries can still
                    // be empty here. Rebind the runtime's latest texture state immediately instead of
                    // clearing _mapTexturesDirty and losing an already-completed preview wave.
                    _mapGeometryRenderer.UpdateTextures(_mapSceneRuntime.BackdropTextures);
                    _mapGeometryRenderer.UpdateProgramTextures(_mapSceneRuntime.BackdropProgramTextures);
                    _mapGeometryRenderer.UpdateLightmaps(_mapSceneRuntime.BackdropLightmaps);
                    EnsureMapPostEffectsRenderer();
                }
                else
                {
                    _mapGeometryRenderer.ClearScene();
                    _mapPostEffectsRenderer?.Dispose();
                    _mapPostEffectsRenderer = null;
                }
                _mapGpuSceneDirty = false;
                _mapVisibilityDirty = false;
                _mapLightingDirty = false;
                _mapTexturesDirty = false;
                return;
            }

            if (_mapVisibilityDirty && _mapSceneRuntime != null)
            {
                _mapCharacterRenderer?.Clear();
                _mapParticleRenderer?.Clear();
                _mapGeometryRenderer.SetVisibilityFlags(_mapSceneRuntime.VisibilityFlags);
                _mapVisibilityDirty = false;
            }

            if (_mapLightingDirty && _mapSceneRuntime != null)
            {
                _mapGeometryRenderer.SetPreviewSun(EffectiveMapSun(), _mapSunPreviewOverride);
                EnsureMapPostEffectsRenderer();
                _mapLightingDirty = false;
            }

            if (_mapTexturesDirty && _mapSceneRuntime != null)
            {
                _mapGeometryRenderer.UpdateTextures(_mapSceneRuntime.BackdropTextures);
                _mapGeometryRenderer.UpdateProgramTextures(_mapSceneRuntime.BackdropProgramTextures);
                _mapGeometryRenderer.UpdateLightmaps(_mapSceneRuntime.BackdropLightmaps);
                _mapTexturesDirty = false;
            }
        }

        private MapSunData EffectiveMapSun() =>
            _mapSceneRuntime == null
                ? null
                : MapPreviewSemantics.EffectiveSun(_mapSceneRuntime.Scene.Sun, _mapSunPreviewOverride);

        private MapPostEffectsData EffectiveMapPostEffects() =>
            _mapSceneRuntime == null
                ? null
                : MapPreviewSemantics.EffectivePostEffects(
                    _mapSceneRuntime.Scene.PostEffects,
                    _mapPostEffectsOverride,
                    _hasMapPostEffectsOverride);

        private MapSsaoData EffectiveMapSsao() =>
            _mapSceneRuntime == null
                ? null
                : MapPreviewSemantics.EffectiveSsao(
                    _mapSceneRuntime.Scene.AmbientOcclusion,
                    _mapSsaoPreviewOverride);

        private void EnsureMapPostEffectsRenderer()
        {
            if (_mapSceneRuntime == null)
                return;

            bool needsPostEffects = MapPostEffectsRenderer.DrawsAnything(
                EffectiveMapPostEffects(),
                EffectiveMapSsao());
            if (needsPostEffects && _mapPostEffectsRenderer == null)
            {
                _mapPostEffectsRenderer = new MapPostEffectsRenderer();
                _mapPostEffectsRenderer.Initialize(_gl);
            }
            else if (!needsPostEffects && _mapPostEffectsRenderer != null)
            {
                _mapPostEffectsRenderer.Dispose();
                _mapPostEffectsRenderer = null;
            }
        }

        private void MarkMapLightingDirty()
        {
            _mapLightingDirty = true;
            OpenTkControl?.InvalidateVisual();
        }

        private void SnapMapCamera(MapSceneData scene)
        {
            if (scene?.Geometry == null || _cameraController == null)
                return;

            int visibilityFlags = ReferenceEquals(_mapSceneRuntime?.Scene, scene)
                ? _mapSceneRuntime.VisibilityFlags
                : scene.OpeningVisibilityFlags;
            Vector3? calculatedOrigin = MapGeometrySemantics.CalculateOriginForFlags(scene.Geometry, visibilityFlags);
            if (calculatedOrigin is not Vector3 engineOrigin)
                return;

            if (_dummyViewport.Camera is not PerspectiveCamera)
                _dummyViewport.Camera = _previewPerspectiveCamera;
            _previewPerspectiveCamera.FieldOfView = 45d;

            var target = new Point3D(-engineOrigin.X, engineOrigin.Y + 300f, engineOrigin.Z);
            var direction = new Vector3D(280d, 150d, 400d);
            direction.Normalize();
            double radius = Math.Sqrt(1500d * 1500d + 300d * 300d + 1500d * 1500d);
            double aspect = Math.Max(1d, OpenTkControl.ActualWidth) /
                            Math.Max(1d, OpenTkControl.ActualHeight);
            double distance = ViewerViewportControl.CalculateMapFrameDistance(radius, 45d, aspect);
            Point3D position = target + direction * distance;
            _cameraController.SnapTo(position, target - position, VfxCameraUpDirection);
        }

        private void BindBrowserSkin()
        {
            if (_browserSkin != null)
            {
                _browserSkin.IsExpanded = false;
                foreach (var section in _browserSkin.Sections)
                    section.Items = new ListCollectionView(Array.Empty<object>());
            }
            _browserSkin = _model.SelectedSkin;
            if (_browserSkin == null) return;
            VfxBrowserSection systems = _browserSkin.Sections.FirstOrDefault(section => section.Kind == VfxBrowserSectionKind.Systems);
            VfxBrowserSection clips = _browserSkin.Sections.FirstOrDefault(section => section.Kind == VfxBrowserSectionKind.Clips);
            VfxBrowserSection spells = _browserSkin.Sections.FirstOrDefault(section => section.Kind == VfxBrowserSectionKind.Spells);
            if (systems != null) systems.Items = CollectionViewSource.GetDefaultView(_model.Systems);
            if (clips != null) clips.Items = CollectionViewSource.GetDefaultView(_model.DetectedAnimations);
            if (spells != null) spells.Items = CollectionViewSource.GetDefaultView(_browserSkin.SpellItems);
            _model.IsRawSystemsMode = true;
            LoadBinFile(_browserSkin.BinPath);
        }

        private void BrowserItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem { DataContext: VfxSkinItem skin } &&
                !ReferenceEquals(_model.SelectedSkin, skin))
                _model.SelectedSkin = skin;
        }

        private void VfxBrowser_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not MapBrowserNode)
            {
                _model.SelectedMapNode = null;
                ClearMapCharacterClipPreview();
            }

            switch (e.NewValue)
            {
                case MapBrowserNode mapNode:
                    _model.SelectedMapNode = mapNode;
                    HandleMapBrowserSelection(mapNode);
                    break;
                case VfxSkinItem skin:
                    if (!ReferenceEquals(_model.SelectedSkin, skin))
                    {
                        _model.SelectedSkin = skin;
                    }
                    else
                    {
                        // Returning to the Skin itself clears every explicit child preview. The owner
                        // scene then opens exactly like the Skin viewport: first playable Idle in authored
                        // AnimationGraph order, or bind pose when the graph has no Idle. A standalone
                        // System is never selected implicitly by this path.
                        BeginExclusivePreviewSelection();
                        _model.SelectedSystem = null;
                        _model.SelectedAnimation = null;
                        _model.SelectedSpell = null;
                        _model.IsAnimationMode = true;
                        TrySelectOpeningSkinAnimation();
                    }
                    break;
                case VfxBrowserSection section:
                    if (!ReferenceEquals(_model.SelectedSkin, section.Owner)) _model.SelectedSkin = section.Owner;
                    _model.IsAnimationMode = section.Kind != VfxBrowserSectionKind.Systems;
                    break;
                case VfxSystemDiagnosticItem system:
                    _pendingSpell = null;
                    _model.SelectedSpell = null;
                    _model.SelectedAnimation = null;
                    _model.IsRawSystemsMode = true;
                    _model.SelectedSystem = system;
                    break;
                case AnimationClipCatalogItem animation:
                    _pendingSpell = null;
                    _model.SelectedSpell = null;
                    _model.SelectedSystem = null;
                    _model.IsAnimationMode = true;
                    _model.SelectedAnimation = animation;
                    break;
                case VfxSpellBrowserItem spell:
                    _pendingSpell = spell;
                    if (!ReferenceEquals(_model.SelectedSkin, spell.Owner)) _model.SelectedSkin = spell.Owner;
                    _model.SelectedSystem = null;
                    _model.SelectedAnimation = null;
                    _model.IsAnimationMode = true;
                    _model.SelectedSpell = spell;
                    break;
            }
        }

        private void MapLayerCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as CheckBox)?.DataContext is not MapVisibilityLayerOption layer)
                return;

            layer.IsEnabled = (sender as CheckBox)?.IsChecked == true;
            int flags = 0;
            foreach (MapVisibilityLayerOption option in _model.MapLayers)
            {
                if (option.IsEnabled)
                    flags |= 1 << option.Index;
            }
            _model.SetMapLayerFlags(flags);
            if (_mapSceneIsCharacterBackdrop && _model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin)
                _model.SelectedWorkspaceTab.CharacterBackdropVisibilityFlags = flags;
            _ = ApplyMapVisibilityFlagsAsync(flags);
        }

        private async Task ApplyMapVisibilityFlagsAsync(int flags)
        {
            MapSceneRuntime runtime = _mapSceneRuntime;
            if (runtime == null || MapViewerSceneService == null || runtime.VisibilityFlags == flags || _isCleanedUp)
                return;

            _mapLayerCancellation?.Cancel();
            var operation = new System.Threading.CancellationTokenSource();
            _mapLayerCancellation = operation;
            Task<IReadOnlyList<MapCharacterRuntimeGroup>> characterTask = null;
            Task<MapParticleSceneRuntime> particleTask = null;
            bool charactersAdopted = false;
            bool particlesAdopted = false;

            try
            {
                _model.StatusText = $"Switching MAP layers to 0x{flags:x2}...";
                characterTask = MapViewerSceneService.LoadCharacterAssetsAsync(runtime, flags, operation.Token);
                particleTask = MapViewerSceneService.LoadParticleAssetsAsync(runtime, flags, operation.Token);
                await Task.WhenAll(characterTask, particleTask);
                operation.Token.ThrowIfCancellationRequested();
                if (_isCleanedUp || !ReferenceEquals(_mapLayerCancellation, operation) ||
                    !ReferenceEquals(_mapSceneRuntime, runtime))
                {
                    return;
                }

                ClearMapCharacterClipPreview();
                runtime.SetCharacterGroups(await characterTask);
                charactersAdopted = true;
                runtime.SetParticles(await particleTask);
                particlesAdopted = true;
                runtime.SetVisibilityFlags(flags);
                _model.SetMapLayerFlags(flags);
                if (_mapSceneIsCharacterBackdrop && _model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin)
                {
                    _model.SelectedWorkspaceTab.CharacterBackdropVisibilityFlags = flags;
                }
                else
                {
                    ReplaceMapBrowserRoot(MapBrowserSemantics.Build(runtime));
                }
                _mapVisibilityDirty = true;
                if (_mapSceneIsCharacterBackdrop && _model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin)
                    ApplyCharacterBackdropOrigin(runtime.Scene, runtime.Scene.Source);
                _model.StatusText = $"MAP layers 0x{flags:x2} · {runtime.CharacterGroups.Count} structure skins · {runtime.Particles.Runtimes.Count} VFX placements.";
                OpenTkControl?.InvalidateVisual();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to switch MAP visibility layers to 0x{flags:x2}.");
                _model.SetMapLayerFlags(runtime.VisibilityFlags);
                _model.StatusText = "Unable to switch MAP visibility layers.";
            }
            finally
            {
                if (!charactersAdopted && characterTask?.IsCompletedSuccessfully == true)
                    DisposeCharacterGroups(characterTask.Result);
                if (!particlesAdopted && particleTask?.IsCompletedSuccessfully == true)
                    particleTask.Result?.Dispose();
                if (ReferenceEquals(_mapLayerCancellation, operation))
                    _mapLayerCancellation = null;
                operation.Dispose();
            }
        }

        private void MapBrowserEye_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not MapBrowserNode node ||
                !node.CanHide ||
                _mapSceneRuntime == null)
            {
                return;
            }

            string id = MapBrowserVisibilityId(node);
            if (string.IsNullOrWhiteSpace(id))
                return;

            bool hidden = IsMapBrowserNodeHidden(node, _mapSceneRuntime.Hidden);
            _mapSceneRuntime.SetHidden(id, !hidden);
            RefreshMapBrowserVisibility(_mapBrowserRoot);
            OpenTkControl?.InvalidateVisual();
        }

        private void HandleMapBrowserSelection(MapBrowserNode node)
        {
            if (node == null)
                return;

            if (node.Kind == MapBrowserNodeKind.MapFile && node.Payload is MapSceneSource source)
            {
                if (!_isSwitchingWorkspaceTab)
                {
                    EnsureMapWorkspaceTab(node);
                    if (_model.SelectedSkin != null || _championModel != null)
                    {
                        ClearLoadedSkinState();
                        _model.SelectedSkin = null;
                    }
                }

                if (_mapSceneRuntime?.Scene?.Source?.Map?.Equals(source.Map) == true &&
                    string.Equals(
                        _mapSceneRuntime.Scene.Source.SelectedMapFilePath,
                        source.SelectedMapFilePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (_mapSceneIsCharacterBackdrop)
                    {
                        // The same runtime can become the full MAP workspace without decoding the map again.
                        // Character-backdrop mode intentionally omits the MAP browser root, so restore that owner
                        // state explicitly instead of returning early with a half-switched workspace.
                        _mapSceneIsCharacterBackdrop = false;
                        _mapSceneRuntime.ShowStructures = _model.MapStructuresVisible;
                        _mapSceneRuntime.ShowParticles = _model.MapParticlesVisible;
                        _model.HasMapPreview = true;
                        _model.SetMapLayers(
                            MapGeometrySemantics.Layers(_mapSceneRuntime.Scene.Geometry),
                            _mapSceneRuntime.VisibilityFlags);
                        ReplaceMapBrowserRoot(MapBrowserSemantics.Build(_mapSceneRuntime));
                        _mapGpuSceneDirty = true;
                    }
                    SnapMapCamera(_mapSceneRuntime.Scene);
                    OpenTkControl?.InvalidateVisual();
                    return;
                }

                _model.StatusText = $"Loading {MapSourceDisplayName(source)}...";
                _ = LoadDetectedMapAsync(source);
                return;
            }

            if (_mapSceneRuntime == null)
                return;

            string selectionSummary = !string.IsNullOrWhiteSpace(node.InspectorSummary)
                ? node.InspectorSummary
                : node.Subtitle;
            _model.StatusText = string.IsNullOrWhiteSpace(selectionSummary)
                ? node.Title
                : $"{node.Title} · {selectionSummary}";

            switch (node.Kind)
            {
                case MapBrowserNodeKind.Map:
                case MapBrowserNodeKind.Geometry:
                    _mapGpuSceneDirty = true;
                    SnapMapCamera(_mapSceneRuntime.Scene);
                    OpenTkControl?.InvalidateVisual();
                    break;
                case MapBrowserNodeKind.Chunk when node.Payload is MapOutlineChunkData chunk:
                    MapOutlineItemData firstChunkItem = chunk.Items?.FirstOrDefault(item => item?.IsDrawable == true)
                                                        ?? chunk.Items?.FirstOrDefault();
                    if (firstChunkItem != null)
                        FocusMapBrowserPosition(firstChunkItem.Position);
                    break;
                case MapBrowserNodeKind.Placeable when node.Payload is MapOutlineItemData item:
                    FocusMapBrowserPosition(item.Position);
                    break;
                case MapBrowserNodeKind.CharacterSkin when node.Payload is MapCharacterRuntimeGroup group:
                    if (_activeMapCharacterClip != null)
                        ClearMapCharacterClipPreview();
                    MapCharacterData firstPlacement = group.Placements?.FirstOrDefault();
                    _activeMapCharacterPlacement = firstPlacement;
                    if (firstPlacement != null)
                        FocusMapBrowserPosition(firstPlacement.Placeable.Position);
                    break;
                case MapBrowserNodeKind.CharacterPlacement when node.Payload is MapCharacterData character:
                    if (_activeMapCharacterClip != null)
                        ClearMapCharacterClipPreview();
                    _activeMapCharacterPlacement = character;
                    FocusMapBrowserPosition(character.Placeable.Position);
                    break;
                case MapBrowserNodeKind.Clip when node.Payload is MapCharacterClipSelection selection:
                    ConfigureMapAnimationParameterOptions(selection.Clip);
                    _ = PlayMapCharacterClipAsync(selection);
                    break;
                case MapBrowserNodeKind.ParticleSystem when node.Payload is MapParticleSystemGroupData particleSystem:
                    MapParticleData firstParticle = particleSystem.Particles?.FirstOrDefault(particle => particle?.StartDisabled == false)
                                                    ?? particleSystem.Particles?.FirstOrDefault();
                    if (firstParticle != null)
                        FocusMapBrowserPosition(firstParticle.Position);
                    break;
                case MapBrowserNodeKind.ParticlePlacement when node.Payload is MapParticleData particle:
                    FocusMapBrowserPosition(particle.Position);
                    break;
            }
        }

        private async Task PlayMapCharacterClipAsync(
            MapCharacterClipSelection selection,
            bool preservePlayhead = false)
        {
            if (selection?.Group?.Animation == null || selection.Clip == null || _mapSceneRuntime == null)
                return;

            _mapClipCancellation?.Cancel();
            _mapClipCancellation?.Dispose();
            var operation = new System.Threading.CancellationTokenSource();
            _mapClipCancellation = operation;
            MapSceneRuntime scene = _mapSceneRuntime;

            try
            {
                bool prepared = await selection.Group.Animation.PrepareClipAsync(
                    selection.Group.Asset,
                    selection.Clip,
                    _model.AnimationParameter,
                    operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (!prepared || _isCleanedUp ||
                    !ReferenceEquals(_mapClipCancellation, operation) ||
                    !ReferenceEquals(_mapSceneRuntime, scene))
                {
                    if (!prepared)
                        _model.StatusText = $"{selection.Clip.ClipName ?? "Animation Clip"} · animation asset unavailable.";
                    return;
                }

                MapCharacterData targetPlacement = ResolveMapCharacterPreviewPlacement(selection.Group);
                if (targetPlacement == null)
                    return;

                if (_activeMapCharacterGroup != null && !ReferenceEquals(_activeMapCharacterGroup, selection.Group))
                    _activeMapCharacterGroup.ClearPreviewClip();

                _activeMapCharacterGroup = selection.Group;
                _activeMapCharacterPlacement = targetPlacement;
                _activeMapCharacterClip = selection.Clip;
                selection.Group.SetPreviewClip(selection.Clip, targetPlacement);

                _pendingSystem = null;
                _pendingSpell = null;
                _activeSpellPlan = null;
                _model.SelectedSystem = null;
                _model.SelectedAnimation = null;
                _model.SelectedSpell = null;
                ClearAnimationClipCues();
                ClearCompositeDiagnostics();

                double duration = selection.Group.Animation.PreparedClipDuration(selection.Clip);
                if (!double.IsFinite(duration) || duration <= 0d)
                    duration = Math.Max(0.05d, Math.Max(0f, selection.Clip.EndFrame - selection.Clip.StartFrame) * selection.Clip.TickDuration);
                double resumeAt = preservePlayhead
                    ? Math.Clamp(_model.CurrentTime, 0d, duration)
                    : 0d;
                IEnumerable<uint> initiallyHidden =
                    (selection.Group.Asset?.Skin?.HiddenSubmeshes ?? Array.Empty<string>())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(Fnv1a.HashLower);
                _mapAnimationVisibilityTimeline = VfxClipCueEvaluator.BuildVisibilityTimeline(
                    selection.Group.Animation.PreparedClipCues(selection.Clip),
                    initiallyHidden);
                _mapAnimationVisibilityDuration = duration;

                MapCharacterVfxCatalog catalog = selection.Group.Asset?.Vfx ?? MapCharacterVfxCatalog.Empty;
                IReadOnlyList<AnimationClipDefinition> playlist =
                    selection.Group.Animation.PreparedClipPlaylist(selection.Clip);
                VfxAbilityComposition composition = VfxAbilityCompositionBuilder.BuildTimedPlaylist(
                    selection.Clip,
                    playlist,
                    selection.Group.Animation.PreparedClipStepDurations(selection.Clip),
                    selection.Group.Animation.PreparedClipFrameSeconds(selection.Clip),
                    catalog.Systems,
                    catalog.ResourceMap);
                int resolvedIdleVfx = VfxAbilityCompositionBuilder.CountResolvedIdleEffects(
                    catalog.IdleEffects,
                    catalog.Systems,
                    catalog.ResourceMap);
                bool hasSceneVfx = composition.ResolvedCount > 0 || resolvedIdleVfx > 0;
                IReadOnlyDictionary<uint, VfxSystemDefinition> requiredSystems = hasSceneVfx
                    ? RequiredMapClipSystems(catalog, composition)
                    : new Dictionary<uint, VfxSystemDefinition>();

                bool vfxSessionReady = false;
                string vfxFallback = null;
                _vfxRenderer?.SetSystem(null);
                if (hasSceneVfx)
                {
                    VfxSceneResourceContext resources = null;
                    try
                    {
                        resources = await EnsureMapClipVfxResourcesAsync(
                            selection.Group,
                            scene,
                            requiredSystems,
                            operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogService?.LogWarning($"MAP character clip VFX resources unavailable: {ex.Message}");
                        vfxFallback = "VFX resources unavailable";
                    }

                    if (_isCleanedUp ||
                        !ReferenceEquals(_mapClipCancellation, operation) ||
                        !ReferenceEquals(_mapSceneRuntime, scene))
                    {
                        return;
                    }

                    if (resources != null)
                    {
                        EnsureVfxRenderSession();
                        if (_vfxRenderer != null)
                        {
                            _vfxRenderer.SetWorldTransform(
                                MapCharacterSemantics.VfxWorldTransform(targetPlacement.Transform));
                            int seed = unchecked((int)(selection.Clip.OwnerPathHash ^ targetPlacement.KeyHash));
                            vfxSessionReady = _vfxRenderer.SetAnimationSession(
                                composition,
                                catalog.IdleEffects,
                                catalog.Systems,
                                catalog.ResourceMap,
                                resources.SearchDirectory,
                                seed,
                                duration,
                                catalog.OwnerSceneContext);
                            if (vfxSessionReady)
                            {
                                _vfxRenderer.SetBoneTransformSampler((time, boneName, boneHash) =>
                                    selection.Group.Animation.TrySamplePreparedClipBoneTransform(
                                        selection.Group.Asset,
                                        selection.Clip,
                                        time,
                                        boneName,
                                        boneHash,
                                        out Matrix4x4 transform)
                                        ? transform
                                        : null);
                                _vfxRenderer.Seek(resumeAt);
                                _vfxRenderer.Play();
                            }
                            else
                            {
                                vfxFallback ??= "VFX session unavailable";
                            }
                        }
                    }
                    else
                    {
                        vfxFallback ??= "VFX resources unavailable";
                    }
                }

                if (!vfxSessionReady)
                {
                    _vfxRenderer?.SetSystem(null);
                    _vfxRenderer?.SetWorldTransform(Matrix4x4.Identity);
                    _vfxRenderer?.SetBoneTransformSampler(null);
                    _vfxRenderer?.UpdateBoneTransforms(null);
                    _vfxRenderer?.SetOwnerSkinningMatrices(null);
                }

                ResetPreviewLoopRange(duration);
                _model.CurrentTime = resumeAt;
                SyncMapCharacterClipTime(resumeAt);
                _model.IsPlaying = true;

                FocusMapBrowserPosition(targetPlacement.Placeable.Position);

                string name = string.IsNullOrWhiteSpace(selection.Clip.ClipName)
                    ? $"0x{selection.Clip.OwnerPathHash:x8}"
                    : selection.Clip.ClipName;
                string vfxSummary = vfxSessionReady
                    ? $" · {composition.ResolvedCount} VFX events"
                    : !string.IsNullOrWhiteSpace(vfxFallback)
                        ? $" · animation only ({vfxFallback})"
                        : string.Empty;
                _model.StatusText = $"{name} ({duration:F2}s) · MAP character clip{vfxSummary}.";
                _model.LogMessages.Add($"[MAP CLIP] {name} · {duration:F2}s{vfxSummary}.");
                UpdateTimelineTrackMetrics();
                UpdatePlayheadPosition();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to prepare MAP character animation clip.");
                _model.StatusText = "Unable to play the selected MAP animation clip.";
            }
            finally
            {
                if (ReferenceEquals(_mapClipCancellation, operation))
                    _mapClipCancellation = null;
                operation.Dispose();
            }
        }

        private MapCharacterData ResolveMapCharacterPreviewPlacement(MapCharacterRuntimeGroup group)
        {
            if (group?.Placements == null || group.Placements.Count == 0)
                return null;

            if (_activeMapCharacterPlacement != null)
            {
                MapCharacterData held = group.Placements.FirstOrDefault(placement =>
                    ReferenceEquals(placement, _activeMapCharacterPlacement) ||
                    (placement != null &&
                     placement.ChunkHash == _activeMapCharacterPlacement.ChunkHash &&
                     placement.KeyHash == _activeMapCharacterPlacement.KeyHash));
                if (held != null)
                    return held;
            }

            return group.Placements[0];
        }

        private static IReadOnlyDictionary<uint, VfxSystemDefinition> RequiredMapClipSystems(
            MapCharacterVfxCatalog catalog,
            VfxAbilityComposition composition)
        {
            if (catalog?.Systems == null || catalog.Systems.Count == 0)
                return new Dictionary<uint, VfxSystemDefinition>();

            var roots = new List<VfxSystemDefinition>();
            foreach (VfxCompositionEvent cue in composition?.Events ?? Array.Empty<VfxCompositionEvent>())
                if (cue?.System != null)
                    roots.Add(cue.System);

            foreach (VfxIdleEffectDefinition idle in catalog.IdleEffects ?? Array.Empty<VfxIdleEffectDefinition>())
            {
                if (idle == null || idle.EffectKey == 0 ||
                    catalog.ResourceMap == null ||
                    !catalog.ResourceMap.TryGetValue(idle.EffectKey, out uint systemHash) ||
                    systemHash == 0 ||
                    !catalog.Systems.TryGetValue(systemHash, out VfxSystemDefinition system))
                {
                    continue;
                }
                roots.Add(system);
            }

            return VfxSceneResourceContext.ReachableSystems(
                catalog.Systems,
                catalog.ResourceMap,
                roots);
        }

        private Task<VfxSceneResourceContext> EnsureMapClipVfxResourcesAsync(
            MapCharacterRuntimeGroup group,
            MapSceneRuntime scene,
            IReadOnlyDictionary<uint, VfxSystemDefinition> requiredSystems,
            System.Threading.CancellationToken cancellationToken)
        {
            if (group == null || scene == null || MapViewerSceneService == null)
                return Task.FromResult<VfxSceneResourceContext>(null);

            MapCharacterVfxCatalog catalog = group.Asset?.Vfx ?? MapCharacterVfxCatalog.Empty;
            requiredSystems ??= new Dictionary<uint, VfxSystemDefinition>();
            string projectRoot = scene.Scene.Source?.ProjectRoot;
            var requiredCatalog = new MapCharacterVfxCatalog(
                requiredSystems,
                catalog.ResourceMap,
                Array.Empty<VfxIdleEffectDefinition>(),
                catalog.OwnerSceneContext);
            return group.EnsureVfxResourcesAsync(
                token => MapViewerSceneService.CreateVfxResourcesAsync(requiredCatalog, projectRoot, token),
                (resources, token) => resources.EnsureMaterializedAsync(
                    requiredSystems,
                    catalog.OwnerSceneContext,
                    projectRoot,
                    token),
                cancellationToken);
        }

        private void ClearMapCharacterClipPreview()
        {
            _mapClipCancellation?.Cancel();
            bool ownedVfxRenderer = VfxCharacterViewportSemantics.MapCharacterClipOwnsVfxRenderer(
                _activeMapCharacterClip != null,
                _activeMapCharacterGroup?.PreviewClip != null);
            _activeMapCharacterGroup?.ClearPreviewClip();
            _activeMapCharacterGroup = null;
            _activeMapCharacterPlacement = null;
            _activeMapCharacterClip = null;
            _mapAnimationVisibilityTimeline = Array.Empty<VfxClipCueEvaluator.VisibilityEntry>();
            _mapAnimationVisibilityDuration = 0d;

            // MAP Character Clips share the scene VFX renderer with Skin/System/Spell previews. A MAP
            // backdrop load, layer change or teardown must not clear a Skin-owned session merely because
            // both flows use the same renderer instance.
            if (ownedVfxRenderer)
            {
                _vfxRenderer?.SetSystem(null);
                _vfxRenderer?.SetWorldTransform(Matrix4x4.Identity);
                _vfxRenderer?.UpdateBoneTransforms(null);
                _vfxRenderer?.SetOwnerSkinningMatrices(null);
            }
        }

        private void SyncMapCharacterClipTime(double timeSeconds)
        {
            if (_activeMapCharacterGroup == null || _activeMapCharacterClip == null)
                return;

            double safe = double.IsFinite(timeSeconds) ? Math.Max(0d, timeSeconds) : 0d;
            if (_model.TotalDuration > 0d && double.IsFinite(_model.TotalDuration))
                safe = Math.Min(safe, _model.TotalDuration);
            _activeMapCharacterGroup.PreviewTimeSeconds = (float)safe;
            _activeMapCharacterGroup.Animation.EvaluateClip(
                _activeMapCharacterGroup.Asset,
                _activeMapCharacterClip,
                (float)safe);

            double folded = VfxClipCueEvaluator.FoldedTime(safe, _mapAnimationVisibilityDuration);
            IReadOnlySet<uint> hidden = VfxClipCueEvaluator.HiddenSubmeshesAt(
                _mapAnimationVisibilityTimeline,
                folded);
            _activeMapCharacterGroup.SetPreviewHiddenSubmeshes(hidden);
            _vfxRenderer?.SetOwnerHiddenSubmeshes(hidden);
            UpdateMapCharacterClipVfxPose();
        }

        private void UpdateMapCharacterClipVfxPose()
        {
            if (_vfxRenderer?.ActiveSystem == null ||
                _activeMapCharacterGroup?.Animation == null ||
                _activeMapCharacterClip == null)
            {
                return;
            }

            _vfxRenderer.SetOwnerSkinningMatrices(
                _activeMapCharacterGroup.Animation.PreparedClipSkinningMatrices(_activeMapCharacterClip));
            _vfxRenderer.UpdateBoneTransforms((boneName, boneHash) =>
                _activeMapCharacterGroup.Animation.TryGetPreparedClipBoneTransform(
                    _activeMapCharacterClip,
                    boneName,
                    boneHash,
                    out Matrix4x4 transform)
                    ? transform
                    : null);
        }

        private bool HasSelectedMapClipReady() =>
            _model.SelectedMapNode?.Kind == MapBrowserNodeKind.Clip &&
            _model.SelectedMapNode.Payload is MapCharacterClipSelection selection &&
            ReferenceEquals(selection.Group, _activeMapCharacterGroup) &&
            ReferenceEquals(selection.Clip, _activeMapCharacterClip) &&
            ReferenceEquals(_activeMapCharacterGroup?.PreviewClip, _activeMapCharacterClip);

        private void AdvanceMapCharacterClip(float deltaSeconds)
        {
            if (!HasSelectedMapClipReady())
                return;

            if (_model.IsPlaying && !_isUserSeeking)
            {
                double next;
                if (_vfxRenderer?.ActiveSystem != null)
                {
                    _vfxRenderer.ActiveSystem.Speed = _model.Speed;
                    _vfxRenderer.Update(Math.Max(0f, deltaSeconds));
                    next = _vfxRenderer.PlaybackTime;
                }
                else
                {
                    next = _model.CurrentTime + Math.Max(0f, deltaSeconds) * _model.Speed;
                }

                if (ShouldRestartPreview(_model.IsPreviewLoopEnabled, next, _model.ActiveLoopDuration))
                {
                    next = ResolvePreviewLoopRestart(
                        _model.ActiveLoopStart,
                        _model.ActiveLoopDuration,
                        _model.TotalDuration);
                    _vfxRenderer?.Seek(next);
                    _vfxRenderer?.Play();
                }
                else if (next >= _model.TotalDuration)
                {
                    next = _model.TotalDuration;
                    _vfxRenderer?.Seek(next);
                    _vfxRenderer?.Pause();
                    _model.IsPlaying = false;
                }
                _model.CurrentTime = next;
            }

            SyncMapCharacterClipTime(_model.CurrentTime);
        }

        private void FocusMapBrowserPosition(Vector3 enginePosition)
        {
            if (_cameraController == null)
                return;

            if (_dummyViewport.Camera is not PerspectiveCamera camera)
            {
                _dummyViewport.Camera = _previewPerspectiveCamera;
                camera = _previewPerspectiveCamera;
            }

            var pose = ViewerViewportControl.CalculateMapFocusPose(enginePosition, camera.LookDirection);
            if (pose == null)
                return;

            _cameraController.FlyTo(
                pose.Value.Position,
                pose.Value.LookDirection,
                camera.UpDirection);
        }

        private static string MapBrowserVisibilityId(MapBrowserNode node) =>
            node?.Payload switch
            {
                MapOutlineChunkData chunk => chunk.Id,
                MapOutlineItemData item => item.Id,
                MapCharacterData character => MapOutlineSemantics.ItemId(character.ChunkHash, character.KeyHash),
                MapParticleData particle => MapOutlineSemantics.ItemId(particle.ChunkHash, particle.KeyHash),
                _ => null
            };

        private static bool IsMapBrowserNodeHidden(MapBrowserNode node, IReadOnlySet<string> hidden) =>
            node?.Payload switch
            {
                MapOutlineChunkData chunk => hidden?.Contains(chunk.Id) == true,
                MapOutlineItemData item => MapOutlineSemantics.IsHidden(hidden, item.ChunkHash, item.KeyHash),
                MapCharacterData character => MapOutlineSemantics.IsHidden(hidden, character.ChunkHash, character.KeyHash),
                MapParticleData particle => MapOutlineSemantics.IsHidden(hidden, particle.ChunkHash, particle.KeyHash),
                _ => false
            };

        private void RefreshMapBrowserVisibility(MapBrowserNode node)
        {
            if (node == null || _mapSceneRuntime == null)
                return;

            if (node.CanHide)
                node.IsHidden = IsMapBrowserNodeHidden(node, _mapSceneRuntime.Hidden);

            foreach (object child in node.Children)
            {
                if (child is MapBrowserNode mapChild)
                    RefreshMapBrowserVisibility(mapChild);
            }
        }

        private void ClearLoadedSkinState()
        {
            _binCancellation?.Cancel();
            _animationClipCancellation?.Cancel();
            _animationClipCancellation?.Dispose();
            _animationClipCancellation = null;
            _model.IsPlaying = false;
            _vfxRenderer?.Pause();
            if (_inspectedSystem != null)
                RememberStandaloneRun(_inspectedSystem);
            _pendingSystem = null;
            _inspectedSystem = null;
            _pendingSpell = null;
            _activeSpellPlan = null;
            _activeBundle = null;
            _championLoadGeneration++;
            ClearAnimationClipCues();
            _model.SelectedAnimation = null;
            _model.SelectedSpell = null;
            _model.SetAnimationParameterOptions(Array.Empty<float>(), null);
            _model.DetectedAnimations.Clear();
            _vfxRenderer?.SetSystem(null);

            if (_championModel != null)
            {
                SceneModel championModel = _championModel;
                _championModel = null;
                RefreshCharacterInteractionTarget();
                _championMeshRenderer?.QueueRelease(championModel);
                RunReleaseStep("Champion SceneModel", championModel.Dispose);
            }

            _championBundle = null;
            _championAuthoredScale = 1d;
            _characterAuthoredHiddenSubmeshes.Clear();
            InvalidateChampionBindPose();
            _model.HasChampionMesh = false;
            _model.HasCharacterSkeleton = false;
            RebuildCharacterSubmeshOptions();
            ClearCharacterArmatureOverlay();
            RunReleaseStep("Champion animation cache", () => _championAnimationService?.ClearCache());

            VfxClipCatalog clipCatalog = _clipCatalog;
            _clipCatalog = null;
            RunReleaseStep(nameof(VfxClipCatalog), () => clipCatalog?.Dispose());

            _model.SelectedSystem = null;
            _model.Systems.Clear();
            _model.SelectedEmitter = null;
            _model.Emitters.Clear();
            _model.Textures.Clear();
            _model.Meshes.Clear();
            _model.HasAnySolo = false;
            _model.IsAllMuted = false;
            _model.CurrentTime = 0;
            _model.TotalDuration = 5.0;
            _model.ActiveLoopStart = 0;
            _model.ActiveLoopDuration = 0;
        }

        private async void LoadBinFile(string binFilePath)
        {
            if (!File.Exists(binFilePath)) return;

            ClearLoadedSkinState();
            _binCancellation = new System.Threading.CancellationTokenSource();
            var operation = _binCancellation;
            VfxWorkspaceTab restoreTab = _pendingWorkspaceRestoreTab;

            try
            {
                _model.LogMessages.Add($"[BIN] Loading BIN definitions from: {Path.GetFileName(binFilePath)}");

                var bundle = await VfxLoadingService.LoadAsync(binFilePath, LogService, operation.Token);
                if (operation.IsCancellationRequested || _isCleanedUp) return;
                _activeBundle = bundle;

                // AnimationGraph metadata is independent from the preview mesh. Populate the
                // picker immediately; ANM payloads are decoded only when a clip/spell needs one.
                BindAnimationCatalog(_model.RootPath);

                foreach (var (hash, sysDef) in _activeBundle.Systems)
                {
                    string name = sysDef.Name ?? $"VFX_0x{hash:X8}";

                    if (!HasPlayableEmitters(sysDef))
                    {
                        continue;
                    }

                    VfxEmitterDefinition[] playableEmitters = sysDef.Emitters
                        .Where(emitter => !emitter.Disabled)
                        .ToArray();
                    var item = new VfxSystemDiagnosticItem
                    {
                        Name = name,
                        PathHash = hash,
                        Definition = sysDef,
                        EmitterCount = playableEmitters.Length,
                        TextureCount = playableEmitters.Count(e =>
                            !string.IsNullOrWhiteSpace(e.TexturePath) ||
                            !string.IsNullOrWhiteSpace(e.TextureMultPath) ||
                            !string.IsNullOrWhiteSpace(e.ParticleColorTexturePath) ||
                            !string.IsNullOrWhiteSpace(e.PaletteDefinition?.PaletteTexturePath)),
                        MeshCount = playableEmitters.Count(e => e.IsMeshPrimitive)
                    };
                    _model.Systems.Add(item);
                }

                _model.LogMessages.Add($"[BIN SUCCESS] Extracted {_model.Systems.Count} VFX systems.");
                _model.StatusText = $"Loaded {_model.Systems.Count} systems from {Path.GetFileName(binFilePath)}.";

                if (restoreTab != null && ReferenceEquals(_pendingWorkspaceRestoreTab, restoreTab))
                {
                    _pendingWorkspaceRestoreTab = null;
                    RestoreWorkspaceSelection(restoreTab, binFilePath);
                }

                // Selecting a skin loads its owner model and browser data only. A VFX system
                // starts exclusively from an explicit System selection in the browser.
                TryLoadChampionModelAsync(_model.RootPath);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                    LogService?.LogError(ex, "Failed to load VFX BIN.");
                _model.StatusText = "Unable to load this BIN.";
                _model.LogMessages.Add($"[ERROR] Failed to load BIN: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_binCancellation, operation)) _binCancellation = null;
                operation.Dispose();
            }
        }

        internal static bool HasPlayableEmitters(VfxSystemDefinition definition)
            => definition?.Emitters.Any(emitter => !emitter.Disabled) == true;

        #endregion

        #region System & Emitter Diagnostics

        private string StandaloneRunKey(VfxSystemDiagnosticItem systemItem)
        {
            string document = _activeBundle?.PrimaryBinPath ?? _model.RootPath ?? string.Empty;
            return $"{document}|{systemItem?.PathHash ?? 0:X8}";
        }

        private void RememberStandaloneRun(VfxSystemDiagnosticItem systemItem)
        {
            if (systemItem == null) return;

            _standaloneRunMemory[StandaloneRunKey(systemItem)] = new StandaloneRunMemory(
                Seed: _model.PlaybackSeed,
                Playhead: _model.CurrentTime,
                Speed: _model.Speed,
                RigSettings: (_vfxRenderer?.RigSettings ?? VfxRigSettings.ForPreset(_model.RigPreset)) with { IsLooping = false },
                Muted: _model.Emitters.Where(emitter => emitter.IsMuted).Select(emitter => emitter.SourceOrder).ToArray(),
                Soloed: _model.Emitters.Where(emitter => emitter.IsSolo).Select(emitter => emitter.SourceOrder).ToArray());
        }

        private StandaloneRunMemory RecallStandaloneRun(VfxSystemDiagnosticItem systemItem)
            => systemItem != null &&
               _standaloneRunMemory.TryGetValue(StandaloneRunKey(systemItem), out StandaloneRunMemory memory)
                ? memory
                : null;

        internal static double RememberedPlayhead(double playhead, double span)
        {
            double safe = double.IsFinite(playhead) ? Math.Max(0d, playhead) : 0d;
            return span > 0d && double.IsFinite(span) ? Math.Min(safe, span) : safe;
        }

        internal static int NextPlaybackSeed(int seed) => unchecked(seed + 1);

        private void InspectSystem(VfxSystemDiagnosticItem systemItem)
        {
            var def = systemItem.Definition;
            if (def == null) return;
            _pendingSpell = null;
            _activeSpellPlan = null;

            StandaloneRunMemory remembered = RecallStandaloneRun(systemItem);
            int playbackSeed = remembered?.Seed ?? StandalonePlaybackSeed;
            float playbackSpeed = remembered?.Speed ?? 1f;
            VfxRigSettings rigSettings =
                (remembered?.RigSettings ?? VfxRigSettings.ForPreset(VfxRigPreset.Still)) with
                {
                    IsLooping = _model.IsPreviewLoopEnabled
                };
            VfxRigPreset rigPreset = rigSettings.Preset;
            HashSet<int> muted = remembered?.Muted?.ToHashSet() ?? new HashSet<int>();
            HashSet<int> soloed = remembered?.Soloed?.ToHashSet() ?? new HashSet<int>();

            _model.SelectedEmitter = null;
            _model.Emitters.Clear();
            _model.Textures.Clear();
            _model.Meshes.Clear();
            _model.LogMessages.Add($"[INSPECT] Selected VFX: {systemItem.Name} (Hash: 0x{systemItem.PathHash:X8})");

            string searchDir = _model.RootPath;
            if (!string.IsNullOrEmpty(searchDir) && File.Exists(searchDir))
            {
                searchDir = Path.GetDirectoryName(searchDir) ?? searchDir;
            }

            // 1. Prepare playback in OpenGL Viewport
            var systemModel = new VfxSystemModel
            {
                Name = systemItem.Name,
                Definition = def,
                SystemCatalog = _activeBundle?.Systems ?? new Dictionary<uint, VfxSystemDefinition>(),
                ResourceMap = _activeBundle?.ResourceMap ?? new Dictionary<uint, uint>(),
                SearchDirectory = searchDir,
                OwnerSceneContext = _activeBundle?.OwnerSceneContext,
                PlaybackSeed = playbackSeed,
                TotalDuration = VfxDurationCalculator.SystemSpan(def),
                Speed = playbackSpeed
            };

            _model.CurrentTime = 0;
            _model.PlaybackSeed = playbackSeed;
            string playbackContext = "standalone system";
            _vfxRenderer?.SetVfxSystem(systemModel);
            ApplyChampionBindPose();
            if (_vfxRenderer != null)
            {
                _vfxRenderer.RigSettings = rigSettings;
                _model.RigPreset = rigPreset;
            }
            else
            {
                _model.RigPreset = rigPreset;
            }
            SetPlaybackSpeed(playbackSpeed);

            double rigDuration = _vfxRenderer?.RigDuration ?? VfxRigMotion.RunLength(_model.RigPreset, def);
            double timelineMax = ResolveTimelineDuration(rigDuration);
            ResetPreviewLoopRange(timelineMax);

            // 2. Audit Emitters
            for (int emitterIndex = 0; emitterIndex < def.Emitters.Count; emitterIndex++)
            {
                var emitter = def.Emitters[emitterIndex];
                if (emitter.Disabled) continue;

                string texPath = emitter.TexturePath;
                string meshPath = emitter.MeshPath;

                BitmapSource tex = string.IsNullOrEmpty(texPath) ? null : VfxLoadingService.ResolveTexture(texPath, searchDir);
                var mesh = emitter.IsMeshPrimitive
                    ? VfxLoadingService.ResolveMesh(meshPath, searchDir)
                    : null;

                (string textureStatus, Brush textureStatusBrush) = DescribeTextureStatus(emitter, tex);

                string primKind = emitter.IsMeshPrimitive
                    ? "MESH"
                    : (emitter.IsGroundLayer ? "GROUND" : (emitter.Trail != null ? "TRAIL" : "QUAD"));

                var emitterDiagnostic = new VfxEmitterDiagnosticItem
                {
                    Name = string.IsNullOrWhiteSpace(emitter.Name) ? "Emitter" : emitter.Name,
                    SourceOrder = emitterIndex,
                    IsEnabled = true,
                    IsSolo = soloed.Contains(emitterIndex),
                    IsMuted = muted.Contains(emitterIndex),
                    PrimitiveKindName = primKind,
                    EmitterDef = emitter,
                    ImagePreview = tex,
                    TexturePath = texPath ?? "N/A",
                    TextureSources = DescribeTextureSources(emitter),
                    TextureStatus = textureStatus,
                    TextureStatusBrush = textureStatusBrush,
                    MeshPath = emitter.IsMeshPrimitive ? (meshPath ?? "N/A") : "N/A",
                    MeshStatus = emitter.IsMeshPrimitive ? (mesh != null ? "Resolved" : "MISSING") : "N/A",
                    MeshStatusBrush = emitter.IsMeshPrimitive ? (mesh != null ? Brushes.LightGreen : Brushes.OrangeRed) : Brushes.Gray,
                    BlendMode = GetBlendModeName(emitter.BlendMode),
                    TexDiv = $"{emitter.TexDiv.X} x {emitter.TexDiv.Y}",
                    IsMeshPrimitive = emitter.IsMeshPrimitive,
                    DisableBackfaceCull = emitter.RenderState?.DisableBackfaceCull ?? false
                };

                emitterDiagnostic.OnEnabledChanged += (item, enabled) =>
                {
                    _vfxRenderer?.SetEmitterVisibility(item.SourceOrder, enabled);
                    _model.LogMessages.Add($"[EMITTER TOGGLE] {item.Name} set to {(enabled ? "ENABLED" : "DISABLED")}");
                };
                emitterDiagnostic.OnVisibilityStateChanged += item =>
                {
                    if (!_isBulkEmitterStateChange)
                        UpdateEmittersVisibility();
                };

                _model.Emitters.Add(emitterDiagnostic);

                // Add to texture audit
                if (!string.IsNullOrEmpty(texPath) && !_model.Textures.Any(t => t.AuthoredPath == texPath))
                {
                    _model.Textures.Add(new VfxTextureDiagnosticItem
                    {
                        AuthoredPath = texPath,
                        ResolvedPath = tex != null ? "Resolved on disk" : "Missing",
                        Status = tex != null ? "OK" : "MISSING",
                        StatusBrush = tex != null ? Brushes.LightGreen : Brushes.Red,
                        Width = tex?.PixelWidth ?? 0,
                        Height = tex?.PixelHeight ?? 0,
                        ImagePreview = tex,
                        TexDiv = $"{emitter.TexDiv.X}x{emitter.TexDiv.Y}"
                    });
                }

                // Add to mesh audit
                if (emitter.IsMeshPrimitive && !string.IsNullOrEmpty(meshPath) && !_model.Meshes.Any(m => m.AuthoredPath == meshPath))
                {
                    _model.Meshes.Add(new VfxMeshDiagnosticItem
                    {
                        AuthoredPath = meshPath,
                        ResolvedPath = mesh != null ? "Loaded" : "Missing",
                        Status = mesh != null ? "OK" : "MISSING",
                        StatusBrush = mesh != null ? Brushes.LightGreen : Brushes.Red,
                        VertexCount = mesh?.Positions != null ? mesh.Value.Positions.Length / 3 : 0,
                        FaceCount = mesh?.Indices != null ? mesh.Value.Indices.Length / 3 : 0,
                        Format = meshPath.EndsWith(".scb", StringComparison.OrdinalIgnoreCase) ? "SCB" : (meshPath.EndsWith(".sco", StringComparison.OrdinalIgnoreCase) ? "SCO" : "SKN")
                    });
                }
            }

            UpdateEmittersVisibility();

            double restoredTime = remembered == null
                ? 0d
                : RememberedPlayhead(remembered.Playhead, timelineMax);
            _vfxRenderer?.Seek(restoredTime);
            if (_vfxRenderer?.ActiveSystem != null)
                _model.CurrentTime = _vfxRenderer.PlaybackTime;
            else
                _model.CurrentTime = restoredTime;
            _vfxRenderer?.Play();
            _model.IsPlaying = true;

            TryLoadChampionModelAsync(searchDir);

            UpdateTimelineTrackMetrics();
            UpdatePlayheadPosition();

            _model.StatusText = $"{systemItem.Name} · {playbackContext}.";
            _inspectedSystem = systemItem;
        }

        private async void TryLoadChampionModelAsync(string searchDir)
        {
            if (string.IsNullOrEmpty(searchDir)) return;
            if (_championModel != null && ReferenceEquals(_championBundle, _activeBundle))
            {
                TryPlayPendingSpell();
                return;
            }
            int generation = ++_championLoadGeneration;
            var bundle = _activeBundle;
            try
            {
                string authored = _activeBundle?.OwnerSceneContext?.MeshPath;
                string sknPath = ResolveSknPath(authored, searchDir);

                if (!string.IsNullOrEmpty(sknPath) && File.Exists(sknPath) && SknLoadingService != null)
                {
                    var loaded = await SknLoadingService.LoadModelWithSkinBin(
                        sknPath,
                        bundle?.PrimaryBinPath,
                        searchDir);
                    if (generation != _championLoadGeneration || !ReferenceEquals(bundle, _activeBundle) || _isCleanedUp)
                    {
                        loaded?.Dispose();
                        return;
                    }
                    if (loaded != null)
                    {
                        var oldModel = _championModel;
                        _championModel = loaded;
                        _championBundle = bundle;
                        RefreshCharacterInteractionTarget();
                        // Keep the owner mesh and its joint anchors in authored skinScale space. User
                        // placement is an outer multiplier so attached VFX do not receive skinScale twice.
                        _championAuthoredScale = _activeBundle?.OwnerSceneContext is { SkinScale: > 0f } owner
                            ? owner.SkinScale
                            : 1d;
                        _championModel.Scale = _championAuthoredScale;
                        IReadOnlyList<uint> initialHidden =
                            _activeBundle?.OwnerSceneContext?.InitialHiddenSubmeshHashes ?? Array.Empty<uint>();
                        ApplyOwnerSubmeshVisibility(initialHidden);
                        if (oldModel != null)
                        {
                            _championMeshRenderer?.QueueRelease(oldModel);
                            oldModel.Dispose();
                        }
                        _model.HasChampionMesh = true;
                        RebuildCharacterSubmeshOptions();

                        // Ensure skeleton is loaded
                        if (_championModel.Skeleton == null)
                        {
                            string sklPath = ResolveSklPath(_activeBundle?.OwnerSceneContext?.SkeletonPath, sknPath, searchDir);
                            if (!string.IsNullOrEmpty(sklPath) && File.Exists(sklPath))
                            {
                                using var sklStream = File.OpenRead(sklPath);
                                _championModel.Skeleton = new LeagueToolkit.Core.Animation.RigResource(sklStream);
                            }
                        }

                        _model.HasCharacterSkeleton = _championModel.Skeleton?.Joints?.Count > 0;

                        if (_championModel.GpuSkinningData == null &&
                            _championModel.Skeleton != null &&
                            _championModel.SkinnedMesh != null)
                        {
                            _championModel.GpuSkinningData = GpuSkinningData.TryCreate(
                                _championModel.Skeleton,
                                _championModel.SkinnedMesh,
                                _championModel.Parts,
                                out string skinningFailure);
                            if (_championModel.GpuSkinningData == null)
                            {
                                _model.LogMessages.Add(
                                    $"[CHAMPION MESH] GPU skinning unavailable: {skinningFailure ?? "Unsupported skin data."}");
                            }
                        }

                        int boneCount = _championModel.Skeleton?.Joints?.Count ?? 0;
                        ApplyCharacterPlacement();
                        _model.LogMessages.Add($"[CHAMPION MESH] Model loaded for VFX studio: {Path.GetFileName(sknPath)} (Skeleton: {(boneCount > 0 ? $"{boneCount} bones" : "None")})");
                        InvalidateChampionBindPose();
                        if (_model.SelectedSystem != null && _model.SelectedAnimation == null && _model.SelectedSpell == null)
                            ApplyChampionBindPose();

                        // The catalog may already be available from BIN load. Rebuild only when needed.
                        // A restored explicit System/Clip/Spell keeps ownership; otherwise the Skin opens
                        // on its first playable Idle, matching the authored AnimationGraph order.
                        if (_model.DetectedAnimations.Count == 0)
                            BindAnimationCatalog(searchDir);
                        if (_model.SelectedAnimation != null)
                        {
                            _ = PlaySelectedAnimationAsync(_model.SelectedAnimation);
                        }
                        else if (_model.SelectedSystem == null && _model.SelectedSpell == null)
                        {
                            TrySelectOpeningSkinAnimation();
                        }
                        else
                        {
                            TryPlayPendingSpell();
                        }
                        return;
                    }
                }
                _model.HasChampionMesh = false;
            }
            catch (Exception ex)
            {
                LogService?.LogDebug($"Champion mesh not loaded: {ex.Message}");
                _model.HasChampionMesh = false;
            }
        }

        private void BindAnimationCatalog(string searchDir)
        {
            ClearAnimationClipCues();
            _model.SelectedAnimation = null;
            _model.DetectedAnimations.Clear();
            _animationSearchDirectory = searchDir;
            _isUpdatingAnimationParameter = true;
            try
            {
                _model.SetAnimationParameterOptions(Array.Empty<float>(), null);
            }
            finally
            {
                _isUpdatingAnimationParameter = false;
            }

            if (_championModel != null) _championModel.CurrentAnimation = null;
            _clipCatalog?.Dispose();
            _clipCatalog = new VfxClipCatalog();
            if (_activeBundle == null || VfxLoadingService == null)
                return;

            foreach (AnimationClipCatalogItem item in BuildAnimationCatalog(null))
                _model.DetectedAnimations.Add(item);
            _model.LogMessages.Add($"[ANIMATIONS] Loaded {_model.DetectedAnimations.Count} authored clips.");
        }

        private IReadOnlyList<AnimationClipCatalogItem> BuildAnimationCatalog(float? parameter)
        {
            if (_clipCatalog == null || _activeBundle == null || VfxLoadingService == null)
                return Array.Empty<AnimationClipCatalogItem>();

            return _clipCatalog.BuildMetadata(
                _activeBundle,
                path => VfxLoadingService.ResolveAssetPath(path, _animationSearchDirectory, ".anm"),
                parameter);
        }

        private bool TrySelectOpeningSkinAnimation()
        {
            // Explicit child previews always win. This method only owns the unclaimed Skin scene.
            if (_model.SelectedSystem != null ||
                _model.SelectedAnimation != null ||
                _model.SelectedSpell != null)
            {
                return false;
            }

            AnimationClipCatalogItem opening = VfxClipCatalog.OpeningClip(_model.DetectedAnimations);
            if (opening == null)
                return false;

            _model.IsAnimationMode = true;
            _model.SelectedAnimation = opening;
            return true;
        }

        private void ConfigureAnimationParameterOptions(AnimationClipCatalogItem item)
        {
            _isUpdatingAnimationParameter = true;
            try
            {
                IReadOnlyList<float> values = item?.ParameterValues ?? Array.Empty<float>();
                _model.SetAnimationParameterOptions(
                    values,
                    values.Count > 1 ? item?.ParameterValue : _model.AnimationParameter);
            }
            finally
            {
                _isUpdatingAnimationParameter = false;
            }
        }

        private void ConfigureMapAnimationParameterOptions(AnimationClipDefinition clip)
        {
            _isUpdatingAnimationParameter = true;
            try
            {
                IReadOnlyList<float> values = AnimationGraphPlayback.ParameterValues(clip);
                float? selected = values.Count > 1
                    ? AnimationGraphPlayback.NearestParameter(
                        values,
                        clip?.ParametricValues?.FirstOrDefault() ?? values[0])
                    : null;
                _model.SetAnimationParameterOptions(values, selected);
            }
            finally
            {
                _isUpdatingAnimationParameter = false;
            }
        }

        private void RebuildAnimationsForParameter(float parameter)
        {
            AnimationClipCatalogItem selected = _model.SelectedAnimation;
            if (selected?.Clip == null) return;

            uint selectedGraph = selected.Clip.GraphPathHash;
            uint selectedClip = selected.Clip.OwnerPathHash;
            IReadOnlyList<AnimationClipCatalogItem> rebuilt = BuildAnimationCatalog(parameter);
            if (rebuilt.Count == 0) return;

            // Rebuilding parameter choices is metadata-only; the chosen playlist is decoded
            // when the replacement selection starts playback.
            _model.DetectedAnimations.Clear();
            foreach (AnimationClipCatalogItem item in rebuilt)
                _model.DetectedAnimations.Add(item);

            AnimationClipCatalogItem replacement = rebuilt.FirstOrDefault(item =>
                item.Clip?.GraphPathHash == selectedGraph &&
                item.Clip?.OwnerPathHash == selectedClip);
            if (replacement != null)
                _model.SelectedAnimation = replacement;
        }

        private string ResolveSklPath(string authoredPath, string sknPath, string searchDir)
        {
            if (!string.IsNullOrEmpty(authoredPath))
                return VfxLoadingService?.ResolveAssetPath(authoredPath, searchDir, ".skl");
            string sameName = Path.ChangeExtension(sknPath, ".skl");
            return File.Exists(sameName) ? sameName : null;
        }

        private async Task PlaySelectedAnimationAsync(AnimationClipCatalogItem selectedItem)
        {
            if (selectedItem == null || _activeBundle == null || _clipCatalog == null) return;
            if (_championModel == null)
            {
                _model.StatusText = $"{selectedItem.DisplayName} · waiting for character model.";
                TryLoadChampionModelAsync(_model.RootPath);
                return;
            }

            _animationClipCancellation?.Cancel();
            var operation = new System.Threading.CancellationTokenSource();
            _animationClipCancellation = operation;
            VfxClipCatalog catalog = _clipCatalog;
            VfxLoadingService.Bundle bundle = _activeBundle;

            try
            {
                _model.StatusText = $"{selectedItem.DisplayName} · loading animation...";
                AnimationClipCatalogItem animItem = await catalog.PrepareAsync(
                    selectedItem,
                    bundle,
                    path => VfxLoadingService.ResolveAssetPath(path, _animationSearchDirectory, ".anm"),
                    LogService,
                    operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (_isCleanedUp || animItem == null ||
                    !ReferenceEquals(operation, _animationClipCancellation) ||
                    !ReferenceEquals(catalog, _clipCatalog) ||
                    !ReferenceEquals(bundle, _activeBundle) ||
                    !SameAnimationClip(selectedItem, _model.SelectedAnimation) ||
                    _championModel == null)
                {
                    if (animItem == null && ReferenceEquals(selectedItem, _model.SelectedAnimation))
                        _model.StatusText = $"{selectedItem.DisplayName} · animation asset unavailable.";
                    return;
                }

                _pendingSpell = null;
                _activeSpellPlan = null;

                // Animation Clip playback can trigger several VFX systems over time, so the
                // standalone emitter audit from a previously selected System is not meaningful here.
                ClearCompositeDiagnostics();

                _championModel.CurrentAnimation = animItem.AnimationAsset;
                _championModel.AnimationTime = 0;
                _model.CurrentTime = 0;

                double dur = animItem.Duration > 0 ? animItem.Duration : 3.0;
                ResetPreviewLoopRange(dur);

                string searchDir = ResolvePreviewSearchDirectory();

                EnsureVfxRenderSession();
                ConfigureAnimationClipCues(animItem);
                if (_vfxRenderer != null)
                {
                    _championAnimationService?.Update(0, animItem.AnimationAsset, _championModel.Skeleton,
                        _championModel.SkinnedMesh, _championModel.Parts, _championModel.Name);
                    _vfxRenderer.SetBoneTransformSampler((time, name, hash) =>
                        _championAnimationService != null &&
                        _championAnimationService.TrySampleBoneTransform((float)time, name, hash, out var transform)
                            ? transform : null);
                    int seed = HashCode.Combine(animItem.Name, bundle.Systems.Count);
                    _vfxRenderer.SetAnimationSession(
                        animItem.Composition,
                        bundle.IdleEffects,
                        bundle.Systems,
                        bundle.ResourceMap,
                        searchDir,
                        seed,
                        dur,
                        bundle.OwnerSceneContext);
                    _vfxRenderer.SetOwnerSkinningMatrices(_championAnimationService?.FinalBoneTransforms);
                    _vfxRenderer.Play();
                }

                int resolvedIdleVfx = VfxAbilityCompositionBuilder.CountResolvedIdleEffects(
                    bundle.IdleEffects,
                    bundle.Systems,
                    bundle.ResourceMap);
                _model.IsPlaying = true;
                _model.StatusText = $"{animItem.DisplayName} ({dur:F2}s) · {(animItem.HasVfx ? animItem.VfxSummary : "Animation only")}";
                _model.LogMessages.Add($"[PLAY ANIMATION] {animItem.DisplayName} ({dur:F2}s) with {(animItem.Composition?.ResolvedCount ?? 0)} VFX events & {resolvedIdleVfx} resolved idle auras.");
                UpdateTimelineTrackMetrics();
                UpdatePlayheadPosition();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (_isCleanedUp || !ReferenceEquals(catalog, _clipCatalog)) { }
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to prepare AnimationGraph clip: {selectedItem.DisplayName}");
                if (ReferenceEquals(selectedItem, _model.SelectedAnimation))
                    _model.StatusText = $"{selectedItem.DisplayName} · animation load failed.";
            }
            finally
            {
                if (ReferenceEquals(_animationClipCancellation, operation))
                    _animationClipCancellation = null;
                operation.Dispose();
            }
        }

        private static bool SameAnimationClip(AnimationClipCatalogItem left, AnimationClipCatalogItem right)
            => left?.Clip != null && right?.Clip != null &&
               left.Clip.GraphPathHash == right.Clip.GraphPathHash &&
               left.Clip.OwnerPathHash == right.Clip.OwnerPathHash &&
               left.ParameterValue == right.ParameterValue;

        private void ClearCompositeDiagnostics()
        {
            _model.SelectedEmitter = null;
            _model.Emitters.Clear();
            _model.Textures.Clear();
            _model.Meshes.Clear();
            _model.HasAnySolo = false;
            _model.IsAllMuted = false;
        }

        private void CancelTimedPreviewPreparation()
        {
            _animationClipCancellation?.Cancel();
            // The async owner disposes its CTS in finally. Clearing the shared slot here makes
            // a stale continuation fail the ReferenceEquals guard without disposing its Token
            // while PrepareAsync may still be unwinding on that token.
            _animationClipCancellation = null;
        }

        private void ResetPreviewContextForSelection()
        {
            CancelTimedPreviewPreparation();
            _activeSpellPlan = null;
            _model.IsPlaying = false;
            _vfxRenderer?.Pause();
            _vfxRenderer?.SetSystem(null);
            ClearAnimationClipCues();
            ClearCompositeDiagnostics();
            _model.CurrentTime = 0d;

            _isUpdatingAnimationParameter = true;
            try
            {
                _model.SetAnimationParameterOptions(Array.Empty<float>(), null);
            }
            finally
            {
                _isUpdatingAnimationParameter = false;
            }

            // A context switch must not leave the old Clip pose driving the owner. While the
            // next Clip/Spell loads, on a neutral Skin selection, or for a standalone System,
            // the Champion returns to bind pose and owner-joint attachments resolve from it.
            ResetChampionToBindPose();
        }

        private void BeginExclusivePreviewSelection()
        {
            if (_inspectedSystem != null)
                RememberStandaloneRun(_inspectedSystem);
            _pendingSystem = null;
            _inspectedSystem = null;
            _pendingSpell = null;
            ResetPreviewContextForSelection();
        }

        private string ResolvePreviewSearchDirectory()
        {
            string searchDir = _model.RootPath;
            return !string.IsNullOrEmpty(searchDir) && File.Exists(searchDir)
                ? Path.GetDirectoryName(searchDir) ?? searchDir
                : searchDir;
        }

        private bool IsActiveSpellSkin(VfxSpellBrowserItem spell)
        {
            if (spell?.Owner == null || _activeBundle == null) return false;
            return string.Equals(
                Path.GetFullPath(_activeBundle.PrimaryBinPath ?? string.Empty),
                Path.GetFullPath(spell.Owner.BinPath ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);
        }

        private void RequestSpellPreview(VfxSpellBrowserItem spell)
        {
            if (spell == null) return;
            _pendingSpell = spell;

            if (!ReferenceEquals(_model.SelectedSkin, spell.Owner))
            {
                _model.SelectedSkin = spell.Owner;
                return;
            }

            if (!IsActiveSpellSkin(spell)) return;

            if (_championModel == null || !ReferenceEquals(_championBundle, _activeBundle))
            {
                TryLoadChampionModelAsync(_model.RootPath);
                return;
            }

            _ = PlaySelectedSpellAsync(spell);
        }

        private void TryPlayPendingSpell()
        {
            VfxSpellBrowserItem spell = _pendingSpell;
            if (spell == null || !ReferenceEquals(_model.SelectedSpell, spell)) return;
            if (!ReferenceEquals(_model.SelectedSkin, spell.Owner) ||
                !IsActiveSpellSkin(spell) ||
                _championModel == null ||
                !ReferenceEquals(_championBundle, _activeBundle))
            {
                return;
            }

            _ = PlaySelectedSpellAsync(spell);
        }

        private async Task PlaySelectedSpellAsync(VfxSpellBrowserItem spell)
        {
            if (spell == null || _activeBundle == null || _championModel == null) return;
            _pendingSpell = null;

            if (spell.Availability != VfxSpellAvailability.Supported)
            {
                _activeSpellPlan = null;
                _model.IsPlaying = false;
                _vfxRenderer?.Pause();
                _model.StatusText = $"{spell.Name} · {spell.AvailabilityText}.";
                return;
            }

            _animationClipCancellation?.Cancel();
            var operation = new System.Threading.CancellationTokenSource();
            _animationClipCancellation = operation;
            VfxClipCatalog catalog = _clipCatalog;
            VfxLoadingService.Bundle bundle = _activeBundle;

            try
            {
                AnimationClipCatalogItem[] clips = _model.DetectedAnimations.ToArray();
                AnimationClipCatalogItem requestedAnimation =
                    VfxSpellPreviewComposer.ResolveAnimation(spell.Preview?.AnimationName, clips);
                if (requestedAnimation != null)
                {
                    if (catalog == null)
                        return;
                    _model.StatusText = $"{spell.Name} · loading cast animation...";
                    AnimationClipCatalogItem prepared = await catalog.PrepareAsync(
                        requestedAnimation,
                        bundle,
                        path => VfxLoadingService.ResolveAssetPath(path, _animationSearchDirectory, ".anm"),
                        LogService,
                        operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    if (prepared == null)
                    {
                        _activeSpellPlan = null;
                        _model.IsPlaying = false;
                        _model.StatusText = $"{spell.Name} · cast animation unavailable.";
                        return;
                    }

                    for (int index = 0; index < clips.Length; index++)
                    {
                        if (SameAnimationClip(clips[index], requestedAnimation))
                        {
                            clips[index] = prepared;
                            break;
                        }
                    }
                }

                if (_isCleanedUp || operation.IsCancellationRequested ||
                    !ReferenceEquals(operation, _animationClipCancellation) ||
                    !ReferenceEquals(bundle, _activeBundle) ||
                    !ReferenceEquals(spell, _model.SelectedSpell) ||
                    _championModel == null)
                {
                    return;
                }

                EnsureVfxRenderSession();
                Func<uint, string> resolveSystemPath = VfxLoadingService == null
                    ? null
                    : hash => VfxLoadingService.ResolveBinEntryPath(hash);
                VfxSpellPreviewPlan plan = VfxSpellPreviewComposer.Build(
                    spell,
                    bundle,
                    clips,
                    ResolveSpellLaunchFrame,
                    resolveSystemPath);
                _activeSpellPlan = plan;
                if (plan.Availability != VfxSpellAvailability.Supported)
                {
                    _model.IsPlaying = false;
                    _vfxRenderer?.Pause();
                    _model.StatusText = $"{spell.Name} · {plan.Status}";
                    return;
                }

                ClearAnimationClipCues();
                ClearCompositeDiagnostics();
                _model.CurrentTime = 0d;

                AnimationClipCatalogItem animation = plan.Animation;
                _championModel.CurrentAnimation = animation?.AnimationAsset;
                _championModel.AnimationTime = 0d;
                if (animation?.AnimationAsset != null &&
                    _championAnimationService != null &&
                    _championModel.Skeleton != null)
                {
                    _championAnimationService.SetJointSnapCues(Array.Empty<AnimationJointSnapCue>());
                    _championAnimationService.Update(
                        0f,
                        animation.AnimationAsset,
                        _championModel.Skeleton,
                        _championModel.SkinnedMesh,
                        _championModel.Parts,
                        _championModel.Name);
                    _championModel.SkinningMatrices = _championAnimationService.FinalBoneTransforms;
                    _championModel.GpuSkinningData = _championAnimationService.SkinningData;
                }
                else
                {
                    _championModel.SkinningMatrices = null;
                }

                string searchDir = ResolvePreviewSearchDirectory();

                bool ready = _vfxRenderer?.SetSpellSession(
                    plan.Steps,
                    bundle.Systems,
                    bundle.ResourceMap,
                    searchDir,
                    animation?.Duration ?? 0d,
                    bundle.OwnerSceneContext) == true;
                if (_vfxRenderer != null)
                {
                    _vfxRenderer.SetOwnerSkinningMatrices(_championModel.SkinningMatrices);
                    if (ready) _vfxRenderer.Play();
                }

                double duration = _vfxRenderer?.RigDuration ?? Math.Max(
                    animation?.Duration ?? 0d,
                    plan.Arrival + VfxSpellPreviewComposer.ImpactDuration);
                ResetPreviewLoopRange(duration);
                _model.IsPlaying = ready;
                _model.StatusText = $"{spell.Name} · {plan.Status} · release {plan.Release:F2}s / arrival {plan.Arrival:F2}s";
                _model.LogMessages.Add($"[PLAY SPELL] {spell.ObjectPath} · {plan.Status}.");
                UpdateTimelineTrackMetrics();
                UpdatePlayheadPosition();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (_isCleanedUp || !ReferenceEquals(catalog, _clipCatalog)) { }
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to prepare spell preview: {spell.Name}");
                if (ReferenceEquals(spell, _model.SelectedSpell))
                    _model.StatusText = $"{spell.Name} · preview load failed.";
            }
            finally
            {
                if (ReferenceEquals(_animationClipCancellation, operation))
                    _animationClipCancellation = null;
                operation.Dispose();
            }
        }

        private (Vector3 Origin, Vector3 Forward)? ResolveSpellLaunchFrame(
            AnimationClipCatalogItem animation,
            double time,
            string boneName)
        {
            if (_championModel?.Skeleton == null) return null;

            Matrix4x4 boneTransform = Matrix4x4.Identity;
            Matrix4x4 rootTransform = Matrix4x4.Identity;
            bool hasRootTransform;
            bool hasLaunchBone = !string.IsNullOrWhiteSpace(boneName);

            if (animation?.AnimationAsset != null && _championAnimationService != null)
            {
                _championAnimationService.Update(
                    0f,
                    animation.AnimationAsset,
                    _championModel.Skeleton,
                    _championModel.SkinnedMesh,
                    _championModel.Parts,
                    _championModel.Name);
                float sampleTime = SpellAnimationTime(time, animation.Duration);
                if (hasLaunchBone &&
                    !_championAnimationService.TrySampleBoneTransform(
                        sampleTime,
                        boneName,
                        Fnv1a.HashLower(boneName),
                        out boneTransform))
                {
                    return null;
                }

                hasRootTransform = _championAnimationService.TrySampleRootTransform(
                    sampleTime,
                    out rootTransform);
            }
            else
            {
                if (hasLaunchBone &&
                    !AnimationService.TryGetBindBoneTransform(
                        _championModel.Skeleton,
                        boneName,
                        out boneTransform))
                {
                    return null;
                }

                hasRootTransform = AnimationService.TryGetBindRootTransform(
                    _championModel.Skeleton,
                    out rootTransform);
            }

            float skinScale = _activeBundle?.OwnerSceneContext is { SkinScale: > 0f } context
                ? context.SkinScale
                : 1f;
            Vector3 origin = hasLaunchBone
                ? VfxRenderSession.PrepareBoneAnchorTransform(
                    boneTransform,
                    Vector3.Zero,
                    skinScale).Translation
                : Vector3.Zero;
            Vector3 forward = hasRootTransform
                ? Vector3.TransformNormal(Vector3.UnitX, rootTransform)
                : Vector3.UnitX;
            return (origin, forward);
        }

        internal static float SpellAnimationTime(double time, float duration)
        {
            if (!(duration > 0f) || !float.IsFinite(duration) || !double.IsFinite(time)) return 0f;
            return (float)Math.Clamp(time, 0d, Math.Max(0d, duration - 0.000001d));
        }

        private void ConfigureAnimationClipCues(AnimationClipCatalogItem clip)
        {
            ClearAnimationClipCues();
            if (clip == null || _championModel == null) return;

            _activeAnimationClip = clip;
            foreach (ModelPart part in _championModel.Parts)
                _animationBasePartVisibility[part] = part.IsVisible;
            foreach (uint hash in _activeBundle?.OwnerSceneContext?.InitialHiddenSubmeshHashes ?? Array.Empty<uint>())
                _animationBaseHiddenSubmeshes.Add(hash);

            _animationVisibilityTimeline = VfxClipCueEvaluator.BuildVisibilityTimeline(
                clip.TimedCues,
                _animationBaseHiddenSubmeshes);
            _championAnimationService?.SetJointSnapCues(
                clip.TimedCues.OfType<AnimationJointSnapCue>().ToArray());
            ApplyAnimationClipCues(0d);
        }

        private void ApplyAnimationClipCues(double time)
        {
            if (_activeAnimationClip == null || _championModel == null) return;

            double folded = VfxClipCueEvaluator.FoldedTime(time, _activeAnimationClip.Duration);
            IReadOnlySet<uint> hidden = VfxClipCueEvaluator.HiddenSubmeshesAt(
                _animationVisibilityTimeline,
                folded);
            ApplyOwnerSubmeshVisibility(hidden);
        }

        private void ApplyOwnerSubmeshVisibility(IEnumerable<uint> hiddenHashes)
        {
            _characterAuthoredHiddenSubmeshes.Clear();
            foreach (uint hash in hiddenHashes ?? Array.Empty<uint>())
                _characterAuthoredHiddenSubmeshes.Add(hash);
            ApplyEffectiveCharacterSubmeshes();
        }

        private void ApplyEffectiveCharacterSubmeshes()
        {
            if (_championModel == null) return;
            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin
                ? _model.SelectedWorkspaceTab
                : null;
            var effectiveHidden = new HashSet<uint>();
            foreach (ModelPart part in _championModel.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Name)) continue;
                uint hash = Fnv1a.HashLower(part.Name);
                bool inheritedVisible = !_characterAuthoredHiddenSubmeshes.Contains(hash);
                bool manualVisible = false;
                bool overridden = tab != null && tab.CharacterSubmeshOverrides.TryGetValue(hash, out manualVisible);
                bool visible = VfxCharacterViewportSemantics.ResolveSubmeshVisibility(
                    inheritedVisible,
                    overridden,
                    manualVisible);
                if (part.IsVisible != visible) part.IsVisible = visible;
                if (!visible) effectiveHidden.Add(hash);

                VfxCharacterSubmeshOption option = _model.CharacterSubmeshes.FirstOrDefault(item => item.NameHash == hash);
                option?.Sync(visible, overridden);
            }
            _vfxRenderer?.SetOwnerHiddenSubmeshes(effectiveHidden);
        }

        private void RebuildCharacterSubmeshOptions()
        {
            foreach (VfxCharacterSubmeshOption existing in _model.CharacterSubmeshes)
                existing.VisibilityChanged -= CharacterSubmesh_VisibilityChanged;
            _model.CharacterSubmeshes.Clear();

            if (_championModel != null)
            {
                foreach (ModelPart part in _championModel.Parts)
                {
                    if (string.IsNullOrWhiteSpace(part?.Name)) continue;
                    var option = new VfxCharacterSubmeshOption(part.Name, part.IsVisible);
                    option.VisibilityChanged += CharacterSubmesh_VisibilityChanged;
                    _model.CharacterSubmeshes.Add(option);
                }
            }
            _model.NotifyCharacterCollectionsChanged();
            ApplyEffectiveCharacterSubmeshes();
        }

        private void CharacterSubmesh_VisibilityChanged(object sender, EventArgs e)
        {
            if (sender is not VfxCharacterSubmeshOption option ||
                _model.SelectedWorkspaceTab?.Kind != VfxWorkspaceTabKind.Skin)
            {
                return;
            }
            _model.SelectedWorkspaceTab.CharacterSubmeshOverrides[option.NameHash] = option.IsVisible;
            ApplyEffectiveCharacterSubmeshes();
            OpenTkControl?.InvalidateVisual();
        }

        private void ResetCharacterSubmeshOverrides_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedWorkspaceTab?.Kind != VfxWorkspaceTabKind.Skin) return;
            _model.SelectedWorkspaceTab.CharacterSubmeshOverrides.Clear();
            ApplyEffectiveCharacterSubmeshes();
            OpenTkControl?.InvalidateVisual();
        }

        private void ClearAnimationClipCues()
        {
            if (_championModel != null)
            {
                foreach (var (part, visible) in _animationBasePartVisibility)
                {
                    if (_championModel.Parts.Contains(part) && part.IsVisible != visible)
                        part.IsVisible = visible;
                }
            }

            _activeAnimationClip = null;
            _animationBasePartVisibility.Clear();
            _animationBaseHiddenSubmeshes.Clear();
            _animationVisibilityTimeline = Array.Empty<VfxClipCueEvaluator.VisibilityEntry>();
            _championAnimationService?.SetJointSnapCues(Array.Empty<AnimationJointSnapCue>());
            ApplyOwnerSubmeshVisibility(
                _activeBundle?.OwnerSceneContext?.InitialHiddenSubmeshHashes ?? Array.Empty<uint>());
        }

        private void ResetChampionToBindPose()
        {
            _vfxRenderer?.SetBoneTransformSampler(null);
            if (_championModel == null)
            {
                _vfxRenderer?.SetOwnerSkinningMatrices(null);
                _vfxRenderer?.UpdateBoneTransforms(null);
                return;
            }

            _championModel.CurrentAnimation = null;
            _championModel.AnimationTime = 0d;
            _championModel.IsAnimationPaused = true;
            ApplyChampionBindPose();
        }

        private void ApplyChampionBindPose()
        {
            var skeleton = _championModel?.Skeleton;
            if (skeleton?.Joints == null || skeleton.Joints.Count == 0)
            {
                if (_championModel != null) _championModel.SkinningMatrices = null;
                _vfxRenderer?.SetOwnerSkinningMatrices(null);
                _vfxRenderer?.UpdateBoneTransforms(null);
                return;
            }

            if (!ReferenceEquals(_championBindSkeleton, skeleton) || _championBindBoneTransformProvider == null)
            {
                _championBindSkeleton = skeleton;
                _championBindBoneTransformProvider = AnimationService.CreateBindBoneTransformProvider(skeleton);
                _championBindSkinningMatrices = AnimationService.CreateBindSkinningMatrices(skeleton);
                _championBindWorldTransforms = AnimationService.CreateBindWorldTransforms(skeleton);
            }

            _championModel.SkinningMatrices = _championBindSkinningMatrices;
            _vfxRenderer?.SetOwnerSkinningMatrices(_championBindSkinningMatrices);
            _vfxRenderer?.UpdateBoneTransforms(_championBindBoneTransformProvider);
        }

        private void InvalidateChampionBindPose()
        {
            _championBindSkeleton = null;
            _championBindBoneTransformProvider = null;
            _championBindSkinningMatrices = Array.Empty<Matrix4x4>();
            _championBindWorldTransforms = Array.Empty<Matrix4x4>();
        }

        private string ResolveSknPath(string authoredPath, string searchDir)
            => VfxLoadingService?.ResolveAssetPath(authoredPath, searchDir, ".skn");

        private void EmitterLane_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: VfxEmitterDiagnosticItem item }) return;
            _model.SelectedEmitter = item;
            Focus();
        }

        private void LoadEmitterAuthoringControls(VfxEmitterDiagnosticItem item)
        {
            CancelTransientEmitterPreview();
            _isUpdatingEmitterAuthoringControls = true;
            try
            {
                if (item?.EmitterDef == null)
                {
                    SetEmitterTransformText(Vector3.Zero, Vector3.Zero);
                    if (EmitterAuthoringStatusText != null)
                        EmitterAuthoringStatusText.Text = "Select an emitter to author it";
                    if (EmitterForceSummaryText != null)
                        EmitterForceSummaryText.Text = string.Empty;
                    _model.ForceAuthoringItems.Clear();
                    _model.CurveAuthoringItems.Clear();
                    _model.SelectedCurveAuthoringItem = null;
                    _authoringOriginalTranslation = null;
                    _authoringOriginalRotation = null;
                    return;
                }

                _authoringOriginalTranslation = item.EmitterDef.TranslationOverride;
                _authoringOriginalRotation = item.EmitterDef.RotationOverride;
                SetEmitterTransformText(
                    _authoringOriginalTranslation ?? Vector3.Zero,
                    _authoringOriginalRotation ?? Vector3.Zero);
                UpdateEmitterForceSummary(item.EmitterDef);
                RebuildForceAuthoringItems(item.EmitterDef);
                RebuildCurveAuthoringItems(item.EmitterDef);
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = "Edit values to preview · Enter saves · Esc reverts";
            }
            finally
            {
                _isUpdatingEmitterAuthoringControls = false;
            }
        }

        private void SetEmitterTransformText(Vector3 translation, Vector3 rotation)
        {
            if (EmitterTranslationXTextBox == null) return;
            EmitterTranslationXTextBox.Text = AuthoringNumber(translation.X);
            EmitterTranslationYTextBox.Text = AuthoringNumber(translation.Y);
            EmitterTranslationZTextBox.Text = AuthoringNumber(translation.Z);
            EmitterRotationXTextBox.Text = AuthoringNumber(rotation.X);
            EmitterRotationYTextBox.Text = AuthoringNumber(rotation.Y);
            EmitterRotationZTextBox.Text = AuthoringNumber(rotation.Z);
        }

        private static string AuthoringNumber(float value)
            => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryAuthoringNumber(TextBox textBox, out float value)
            => TryAuthoringNumber(textBox?.Text, out value);

        private static bool TryAuthoringNumber(string text, out float value)
        {
            text = text?.Trim();
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && float.IsFinite(value))
                return true;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
        }

        private bool TryReadEmitterTransforms(out Vector3 translation, out Vector3 rotation)
        {
            translation = default;
            rotation = default;
            if (!TryAuthoringNumber(EmitterTranslationXTextBox, out float tx) ||
                !TryAuthoringNumber(EmitterTranslationYTextBox, out float ty) ||
                !TryAuthoringNumber(EmitterTranslationZTextBox, out float tz) ||
                !TryAuthoringNumber(EmitterRotationXTextBox, out float rx) ||
                !TryAuthoringNumber(EmitterRotationYTextBox, out float ry) ||
                !TryAuthoringNumber(EmitterRotationZTextBox, out float rz))
            {
                return false;
            }

            translation = new Vector3(tx, ty, tz);
            rotation = new Vector3(rx, ry, rz);
            return true;
        }

        private bool HasEmitterTransformChanges(Vector3 translation, Vector3 rotation)
            => translation != (_authoringOriginalTranslation ?? Vector3.Zero) ||
               rotation != (_authoringOriginalRotation ?? Vector3.Zero);

        private void EmitterTransform_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingEmitterAuthoringControls ||
                _model.SelectedEmitter?.EmitterDef == null ||
                _inspectedSystem?.Definition == null)
            {
                return;
            }

            if (!TryReadEmitterTransforms(out Vector3 translation, out Vector3 rotation))
            {
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = "Invalid transform value";
                return;
            }

            if (!HasEmitterTransformChanges(translation, rotation))
            {
                CancelTransientEmitterPreview();
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = "No transform changes";
                return;
            }

            VfxEmitterDiagnosticItem selected = _model.SelectedEmitter;
            int sourceOrder = selected.SourceOrder;
            VfxSystemDefinition baseDefinition = _inspectedSystem.Definition;
            if ((uint)sourceOrder >= (uint)baseDefinition.Emitters.Count) return;

            VfxEmitterDefinition[] emitters = baseDefinition.Emitters.ToArray();
            emitters[sourceOrder] = emitters[sourceOrder] with
            {
                TranslationOverride = translation,
                RotationOverride = rotation
            };
            VfxSystemDefinition preview = baseDefinition with { Emitters = emitters };
            if (_vfxRenderer?.SwapStandaloneDefinition(preview) == true)
            {
                _hasTransientEmitterPreview = true;
                UpdateEmittersVisibility();
                _model.CurrentTime = _vfxRenderer.PlaybackTime;
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = "Previewing unsaved transform";
            }
        }

        private void EmitterTransform_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplyEmitterTransforms();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelTransientEmitterPreview();
                LoadEmitterAuthoringControls(_model.SelectedEmitter);
            }
        }

        private void ApplyEmitterTransforms_Click(object sender, RoutedEventArgs e)
            => ApplyEmitterTransforms();

        private bool ApplyEmitterTransforms()
        {
            VfxEmitterDiagnosticItem selected = _model.SelectedEmitter;
            if (selected?.EmitterDef == null || _inspectedSystem?.Definition == null)
                return false;
            if (!TryReadEmitterTransforms(out Vector3 translation, out Vector3 rotation))
            {
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = "Cannot save: one or more values are invalid";
                return false;
            }

            Vector3 originalTranslation = _authoringOriginalTranslation ?? Vector3.Zero;
            Vector3 originalRotation = _authoringOriginalRotation ?? Vector3.Zero;
            bool translationChanged = translation != originalTranslation;
            bool rotationChanged = rotation != originalRotation;
            if (!translationChanged && !rotationChanged)
            {
                CancelTransientEmitterPreview();
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = "No transform changes to save";
                return true;
            }

            if (_activeBundle?.SystemSources.TryGetValue(_inspectedSystem.PathHash, out string sourceBin) != true)
            {
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = "Source BIN for this system is unavailable";
                return false;
            }

            bool saved = VfxEmitterAuthoringService.TryWriteTransforms(
                sourceBin,
                _inspectedSystem.PathHash,
                selected.SourceOrder,
                translationChanged ? translation : null,
                rotationChanged ? rotation : null,
                out VfxSystemDefinition updated,
                out string error);
            if (!saved)
            {
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = $"Save failed: {error}";
                return false;
            }

            AcceptAuthoredSystem(updated);
            _authoringOriginalTranslation = updated.Emitters[selected.SourceOrder].TranslationOverride;
            _authoringOriginalRotation = updated.Emitters[selected.SourceOrder].RotationOverride;
            _hasTransientEmitterPreview = false;
            if (EmitterAuthoringStatusText != null)
                EmitterAuthoringStatusText.Text = $"Saved to {Path.GetFileName(sourceBin)}";
            return true;
        }

        private void CancelTransientEmitterPreview()
        {
            if (!_hasTransientEmitterPreview) return;
            _hasTransientEmitterPreview = false;
            if (_inspectedSystem?.Definition != null &&
                _vfxRenderer?.SwapStandaloneDefinition(_inspectedSystem.Definition) == true)
            {
                UpdateEmittersVisibility();
                _model.CurrentTime = _vfxRenderer.PlaybackTime;
            }
        }

        private void AcceptAuthoredSystem(VfxSystemDefinition updated)
        {
            if (updated == null || _inspectedSystem == null) return;
            _inspectedSystem.Definition = updated;
            if (_activeBundle?.Systems != null)
                _activeBundle.Systems[updated.PathHash] = updated;

            foreach (VfxEmitterDiagnosticItem diagnostic in _model.Emitters)
            {
                if ((uint)diagnostic.SourceOrder < (uint)updated.Emitters.Count)
                    diagnostic.EmitterDef = updated.Emitters[diagnostic.SourceOrder];
            }

            _vfxRenderer?.SwapStandaloneDefinition(updated);
            UpdateEmittersVisibility();
            if (_vfxRenderer?.ActiveSystem != null)
                _model.CurrentTime = _vfxRenderer.PlaybackTime;
            UpdateEmitterForceSummary(_model.SelectedEmitter?.EmitterDef);
            RebuildForceAuthoringItems(_model.SelectedEmitter?.EmitterDef);
            RebuildCurveAuthoringItems(_model.SelectedEmitter?.EmitterDef);
        }

        private void RebuildForceAuthoringItems(VfxEmitterDefinition emitter)
        {
            _model.ForceAuthoringItems.Clear();
            VfxFieldCollectionDefinition fields = emitter?.Fields;
            if (fields == null) return;

            for (int index = 0; index < (fields.Acceleration?.Count ?? 0); index++)
            {
                VfxAccelerationField field = fields.Acceleration[index];
                var item = ForceItem(VfxEmitterForceKind.Acceleration, index, "Acceleration");
                item.Properties.Add(ForceVector(
                    item,
                    VfxEmitterForceProperty.Acceleration,
                    "Acceleration",
                    field.Acceleration.Constant,
                    canAnimate: true,
                    hasCurve: HasCurve(field.Acceleration.Times, field.Acceleration.Values)));
                item.Properties.Add(ForceBool(item, VfxEmitterForceProperty.LocalSpace, "Local Space", field.LocalSpace));
                _model.ForceAuthoringItems.Add(item);
            }

            for (int index = 0; index < (fields.Attraction?.Count ?? 0); index++)
            {
                VfxAttractionField field = fields.Attraction[index];
                var item = ForceItem(VfxEmitterForceKind.Attraction, index, "Attraction");
                item.Properties.Add(ForceVector(item, VfxEmitterForceProperty.Position, "Center", field.Position.Constant, true, HasCurve(field.Position.Times, field.Position.Values)));
                item.Properties.Add(ForceScalar(item, VfxEmitterForceProperty.Radius, "Radius", field.Radius.Constant, true, HasCurve(field.Radius.Times, field.Radius.Values)));
                item.Properties.Add(ForceScalar(item, VfxEmitterForceProperty.Acceleration, "Acceleration", field.Acceleration.Constant, true, HasCurve(field.Acceleration.Times, field.Acceleration.Values)));
                _model.ForceAuthoringItems.Add(item);
            }

            for (int index = 0; index < (fields.Noise?.Count ?? 0); index++)
            {
                VfxNoiseField field = fields.Noise[index];
                var item = ForceItem(VfxEmitterForceKind.Noise, index, "Noise");
                item.Properties.Add(ForceVector(item, VfxEmitterForceProperty.Position, "Center", field.Position.Constant, true, HasCurve(field.Position.Times, field.Position.Values)));
                item.Properties.Add(ForceScalar(item, VfxEmitterForceProperty.Radius, "Radius", field.Radius.Constant, true, HasCurve(field.Radius.Times, field.Radius.Values)));
                item.Properties.Add(ForceScalar(item, VfxEmitterForceProperty.Frequency, "Frequency", field.Frequency.Constant, true, HasCurve(field.Frequency.Times, field.Frequency.Values)));
                item.Properties.Add(ForceScalar(item, VfxEmitterForceProperty.VelocityDelta, "Velocity Δ", field.VelocityDelta.Constant, true, HasCurve(field.VelocityDelta.Times, field.VelocityDelta.Values)));
                item.Properties.Add(ForceVector(item, VfxEmitterForceProperty.AxisFraction, "Axis Weights", field.AxisFraction, false, false));
                _model.ForceAuthoringItems.Add(item);
            }

            for (int index = 0; index < (fields.Drag?.Count ?? 0); index++)
            {
                VfxDragField field = fields.Drag[index];
                var item = ForceItem(VfxEmitterForceKind.Drag, index, "Drag");
                item.Properties.Add(ForceVector(item, VfxEmitterForceProperty.Position, "Center", field.Position.Constant, true, HasCurve(field.Position.Times, field.Position.Values)));
                item.Properties.Add(ForceScalar(item, VfxEmitterForceProperty.Radius, "Radius", field.Radius.Constant, true, HasCurve(field.Radius.Times, field.Radius.Values)));
                item.Properties.Add(ForceScalar(item, VfxEmitterForceProperty.Strength, "Strength", field.Strength.Constant, true, HasCurve(field.Strength.Times, field.Strength.Values)));
                _model.ForceAuthoringItems.Add(item);
            }

            for (int index = 0; index < (fields.Orbital?.Count ?? 0); index++)
            {
                VfxOrbitalField field = fields.Orbital[index];
                var item = ForceItem(VfxEmitterForceKind.Orbital, index, "Orbital");
                item.Properties.Add(ForceVector(item, VfxEmitterForceProperty.Direction, "Direction", field.Direction.Constant, true, HasCurve(field.Direction.Times, field.Direction.Values)));
                item.Properties.Add(ForceBool(item, VfxEmitterForceProperty.LocalSpace, "Local Space", field.LocalSpace));
                _model.ForceAuthoringItems.Add(item);
            }
        }

        private void RebuildCurveAuthoringItems(VfxEmitterDefinition emitter)
        {
            string wanted = _model.SelectedCurveAuthoringItem?.Identity;
            _model.SelectedCurveAuthoringItem = null;
            _model.CurveAuthoringItems.Clear();
            if (emitter == null) return;

            AddCurve("Rate", "rate", emitter.Rate);
            AddCurve("Particle Lifetime", "particleLifetime", emitter.ParticleLifetime);
            AddCurve("Birth Scale", "birthScale0", emitter.BirthScale);
            AddCurve("Scale Over Life", "scale0", emitter.ScaleOverLife ?? VfxCurve3.Const(Vector3.One));
            AddCurve("Birth Color", "birthColor", emitter.BirthColor);
            AddCurve("Color Over Life", "color", emitter.ColorOverLife ?? VfxCurve4.Const(Vector4.One));
            AddCurve("Birth Velocity", "birthVelocity", emitter.BirthVelocity ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Velocity Over Life", "velocity", emitter.VelocityOverLife ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("World Acceleration", "worldAcceleration", emitter.Acceleration ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Birth Acceleration", "birthAcceleration", emitter.BirthAcceleration ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Acceleration Over Life", "acceleration", emitter.AccelerationOverLife ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Birth Orbital Velocity", "birthOrbitalVelocity", emitter.BirthOrbitalVelocity ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Birth Drag", "birthDrag", emitter.BirthDrag ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Drag Over Life", "drag", emitter.DragOverLife ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Birth Rotation", "birthRotation0", emitter.BirthRotation ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Rotation Over Life", "rotation0", emitter.RotationOverLife ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Rotation 1", "rotation1", emitter.Rotation1 ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Birth Rotational Velocity", "birthRotationalVelocity0", emitter.BirthRotationalVelocity ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Birth Rotational Acceleration", "birthRotationalAcceleration", emitter.BirthRotationalAcceleration ?? VfxCurve3.Const(Vector3.Zero));
            AddCurve("Emitter Position", "emitterPosition", emitter.EmitterPosition);
            AddCurve("Birth Frame Rate", "birthFrameRate", emitter.BirthFrameRate ?? VfxCurveF.Const(1f));
            AddCurve("Birth Scale 1", "birthScale1", emitter.BirthScale1 ?? VfxCurve3.Const(Vector3.One));
            AddCurve("Bind Weight", "bindWeight", emitter.BindWeight ?? VfxCurveF.Zero);
            AddCurve("Birth UV Offset", "birthUVOffset", emitter.BirthUvOffset ?? VfxCurve2.Const(Vector2.Zero));
            AddCurve("UV Scale", "uvScale", emitter.UvScale ?? VfxCurve2.Const(Vector2.One));
            AddCurve("UV Rotation", "uvRotation", emitter.UvRotation ?? VfxCurveF.Zero);
            AddCurve("Birth UV Scroll", "birthUvScrollRate", emitter.BirthUvScrollRateCurve ?? VfxCurve2.Const(Vector2.Zero));
            AddCurve("Particle UV Scroll", "particleUVScrollRate", emitter.ParticleUvScrollRate ?? VfxCurve2.Const(Vector2.Zero));
            AddCurve("Birth UV Rotate", "birthUvRotateRate", emitter.BirthUvRotateRate ?? VfxCurveF.Zero);
            AddCurve("Particle UV Rotate", "particleUVRotateRate", emitter.ParticleUvRotateRate ?? VfxCurveF.Zero);
            AddCurve("Mult Birth UV Offset", "birthUVOffsetMult", emitter.TextureMultBirthUvOffset ?? VfxCurve2.Const(Vector2.Zero));
            AddCurve("Mult Birth UV Scroll", "birthUvScrollRateMult", emitter.TextureMultBirthUvScrollRate ?? VfxCurve2.Const(Vector2.Zero));
            AddCurve("Mult Particle UV Scroll", "ParticleIntegratedUvScrollMult", emitter.TextureMultParticleUvScroll ?? VfxCurve2.Const(Vector2.Zero));
            AddCurve("Mult UV Scale", "uvScaleMult", emitter.TextureMultUvScale ?? VfxCurve2.Const(Vector2.One));
            AddCurve("Mult UV Rotation", "UvRotationMult", emitter.TextureMultUvRotation ?? VfxCurveF.Zero);
            AddCurve("Mult Birth UV Rotate", "birthUvRotateRateMult", emitter.TextureMultBirthUvRotateRate ?? VfxCurveF.Zero);
            AddCurve("Mult Particle UV Rotate", "ParticleIntegratedUvRotateMult", emitter.TextureMultParticleUvRotate ?? VfxCurveF.Zero);
            AddForceCurves(emitter.Fields);

            _model.SelectedCurveAuthoringItem = !string.IsNullOrEmpty(wanted)
                ? _model.CurveAuthoringItems.FirstOrDefault(item => item.Identity == wanted)
                : _model.CurveAuthoringItems.FirstOrDefault();
            _model.SelectedCurveAuthoringItem ??= _model.CurveAuthoringItems.FirstOrDefault();
        }

        private void AddCurve(string name, string fieldName, VfxCurveF curve)
        {
            Vector4 constant = new(curve.Constant, 0f, 0f, 0f);
            Vector4[] values = curve.Values?.Select(value => new Vector4(value, 0f, 0f, 0f)).ToArray();
            AddCurve(name, fieldName, VfxEmitterCurveFamily.Scalar, 1, constant, curve.Times, values);
        }

        private void AddCurve(string name, string fieldName, VfxCurve2 curve)
        {
            Vector4 constant = new(curve.Constant, 0f, 0f);
            Vector4[] values = curve.Values?.Select(value => new Vector4(value, 0f, 0f)).ToArray();
            AddCurve(name, fieldName, VfxEmitterCurveFamily.Vector2, 2, constant, curve.Times, values);
        }

        private void AddCurve(string name, string fieldName, VfxCurve3 curve)
        {
            Vector4 constant = new(curve.Constant, 0f);
            Vector4[] values = curve.Values?.Select(value => new Vector4(value, 0f)).ToArray();
            AddCurve(name, fieldName, VfxEmitterCurveFamily.Vector3, 3, constant, curve.Times, values);
        }

        private void AddCurve(string name, string fieldName, VfxCurve4 curve)
            => AddCurve(name, fieldName, VfxEmitterCurveFamily.Vector4, 4, curve.Constant, curve.Times, curve.Values);

        private void AddCurve(
            string name,
            string fieldName,
            VfxEmitterCurveFamily family,
            int components,
            Vector4 constant,
            float[] times,
            Vector4[] values)
        {
            AddCurveItem(
                identity: $"emitter:{Fnv1a.HashLower(fieldName):x8}",
                name,
                fieldName,
                Fnv1a.HashLower(fieldName),
                family,
                components,
                constant,
                times,
                values,
                isForceCurve: false,
                default,
                -1,
                default);
        }

        private void AddCurveItem(
            string identity,
            string name,
            string fieldName,
            uint propertyHash,
            VfxEmitterCurveFamily family,
            int components,
            Vector4 constant,
            float[] times,
            Vector4[] values,
            bool isForceCurve,
            VfxEmitterForceKind forceKind,
            int forceIndex,
            VfxEmitterForceProperty forceProperty)
        {
            bool keyed = HasCurve(times, values);
            var item = new VfxCurveAuthoringItem
            {
                Identity = identity,
                Name = name,
                FieldName = fieldName,
                PropertyHash = propertyHash,
                Family = family,
                ComponentCount = components,
                Constant = constant,
                HasCurve = keyed,
                IsForceCurve = isForceCurve,
                ForceKind = forceKind,
                ForceIndex = forceIndex,
                ForceProperty = forceProperty
            };
            if (keyed)
            {
                int count = Math.Min(times.Length, values.Length);
                for (int index = 0; index < count; index++)
                {
                    Vector4 value = values[index];
                    item.Keys.Add(new VfxCurveKeyAuthoringItem
                    {
                        Owner = item,
                        KeyIndex = index,
                        TimeText = AuthoringNumber(times[index]),
                        XText = AuthoringNumber(value.X),
                        YText = components >= 2 ? AuthoringNumber(value.Y) : string.Empty,
                        ZText = components >= 3 ? AuthoringNumber(value.Z) : string.Empty,
                        WText = components >= 4 ? AuthoringNumber(value.W) : string.Empty
                    });
                }
            }
            _model.CurveAuthoringItems.Add(item);
        }

        private void AddForceCurves(VfxFieldCollectionDefinition fields)
        {
            if (fields == null) return;

            for (int index = 0; index < (fields.Acceleration?.Count ?? 0); index++)
            {
                VfxAccelerationField field = fields.Acceleration[index];
                AddForceCurve(VfxEmitterForceKind.Acceleration, index, VfxEmitterForceProperty.Acceleration,
                    "Acceleration", "acceleration", field.Acceleration);
            }
            for (int index = 0; index < (fields.Attraction?.Count ?? 0); index++)
            {
                VfxAttractionField field = fields.Attraction[index];
                AddForceCurve(VfxEmitterForceKind.Attraction, index, VfxEmitterForceProperty.Position,
                    "Center", "Position", field.Position);
                AddForceCurve(VfxEmitterForceKind.Attraction, index, VfxEmitterForceProperty.Radius,
                    "Radius", "radius", field.Radius);
                AddForceCurve(VfxEmitterForceKind.Attraction, index, VfxEmitterForceProperty.Acceleration,
                    "Acceleration", "acceleration", field.Acceleration);
            }
            for (int index = 0; index < (fields.Noise?.Count ?? 0); index++)
            {
                VfxNoiseField field = fields.Noise[index];
                AddForceCurve(VfxEmitterForceKind.Noise, index, VfxEmitterForceProperty.Position,
                    "Center", "Position", field.Position);
                AddForceCurve(VfxEmitterForceKind.Noise, index, VfxEmitterForceProperty.Radius,
                    "Radius", "radius", field.Radius);
                AddForceCurve(VfxEmitterForceKind.Noise, index, VfxEmitterForceProperty.Frequency,
                    "Frequency", "frequency", field.Frequency);
                AddForceCurve(VfxEmitterForceKind.Noise, index, VfxEmitterForceProperty.VelocityDelta,
                    "Velocity Δ", "velocityDelta", field.VelocityDelta);
            }
            for (int index = 0; index < (fields.Drag?.Count ?? 0); index++)
            {
                VfxDragField field = fields.Drag[index];
                AddForceCurve(VfxEmitterForceKind.Drag, index, VfxEmitterForceProperty.Position,
                    "Center", "Position", field.Position);
                AddForceCurve(VfxEmitterForceKind.Drag, index, VfxEmitterForceProperty.Radius,
                    "Radius", "radius", field.Radius);
                AddForceCurve(VfxEmitterForceKind.Drag, index, VfxEmitterForceProperty.Strength,
                    "Strength", "strength", field.Strength);
            }
            for (int index = 0; index < (fields.Orbital?.Count ?? 0); index++)
            {
                VfxOrbitalField field = fields.Orbital[index];
                AddForceCurve(VfxEmitterForceKind.Orbital, index, VfxEmitterForceProperty.Direction,
                    "Direction", "direction", field.Direction);
            }
        }

        private void AddForceCurve(
            VfxEmitterForceKind kind,
            int forceIndex,
            VfxEmitterForceProperty property,
            string label,
            string fieldName,
            VfxCurveF curve)
        {
            if (!HasCurve(curve.Times, curve.Values)) return;
            Vector4 constant = new(curve.Constant, 0f, 0f, 0f);
            Vector4[] values = curve.Values.Select(value => new Vector4(value, 0f, 0f, 0f)).ToArray();
            AddCurveItem(
                $"force:{kind}:{forceIndex}:{property}",
                $"Force · {kind} {forceIndex + 1} · {label}",
                $"{kind}[{forceIndex}].{fieldName}",
                Fnv1a.HashLower(fieldName),
                VfxEmitterCurveFamily.Scalar,
                1,
                constant,
                curve.Times,
                values,
                true,
                kind,
                forceIndex,
                property);
        }

        private void AddForceCurve(
            VfxEmitterForceKind kind,
            int forceIndex,
            VfxEmitterForceProperty property,
            string label,
            string fieldName,
            VfxCurve3 curve)
        {
            if (!HasCurve(curve.Times, curve.Values)) return;
            Vector4 constant = new(curve.Constant, 0f);
            Vector4[] values = curve.Values.Select(value => new Vector4(value, 0f)).ToArray();
            AddCurveItem(
                $"force:{kind}:{forceIndex}:{property}",
                $"Force · {kind} {forceIndex + 1} · {label}",
                $"{kind}[{forceIndex}].{fieldName}",
                Fnv1a.HashLower(fieldName),
                VfxEmitterCurveFamily.Vector3,
                3,
                constant,
                curve.Times,
                values,
                true,
                kind,
                forceIndex,
                property);
        }

        private static bool HasCurve<T>(float[] times, T[] values)
            => times is { Length: > 0 } && values is { Length: > 0 };

        private static VfxForceAuthoringItem ForceItem(VfxEmitterForceKind kind, int index, string title)
            => new()
            {
                Kind = kind,
                ForceIndex = index,
                Title = $"{title} {index + 1}"
            };

        private static VfxForcePropertyAuthoringItem ForceScalar(
            VfxForceAuthoringItem owner,
            VfxEmitterForceProperty property,
            string label,
            float value,
            bool canAnimate,
            bool hasCurve)
            => new()
            {
                ForceKind = owner.Kind,
                ForceIndex = owner.ForceIndex,
                Property = property,
                Label = label,
                ValueKind = VfxForceAuthoringValueKind.Scalar,
                CanAnimate = canAnimate,
                HasCurve = hasCurve,
                XText = AuthoringNumber(value)
            };

        private static VfxForcePropertyAuthoringItem ForceVector(
            VfxForceAuthoringItem owner,
            VfxEmitterForceProperty property,
            string label,
            Vector3 value,
            bool canAnimate,
            bool hasCurve)
            => new()
            {
                ForceKind = owner.Kind,
                ForceIndex = owner.ForceIndex,
                Property = property,
                Label = label,
                ValueKind = VfxForceAuthoringValueKind.Vector3,
                CanAnimate = canAnimate,
                HasCurve = hasCurve,
                XText = AuthoringNumber(value.X),
                YText = AuthoringNumber(value.Y),
                ZText = AuthoringNumber(value.Z)
            };

        private static VfxForcePropertyAuthoringItem ForceBool(
            VfxForceAuthoringItem owner,
            VfxEmitterForceProperty property,
            string label,
            bool value)
            => new()
            {
                ForceKind = owner.Kind,
                ForceIndex = owner.ForceIndex,
                Property = property,
                Label = label,
                ValueKind = VfxForceAuthoringValueKind.Boolean,
                CanAnimate = false,
                HasCurve = false,
                BoolValue = value
            };

        private void UpdateEmitterForceSummary(VfxEmitterDefinition emitter)
        {
            if (EmitterForceSummaryText == null) return;
            VfxFieldCollectionDefinition fields = emitter?.Fields;
            if (fields == null)
            {
                EmitterForceSummaryText.Text = "none";
                return;
            }

            int acceleration = fields.Acceleration?.Count ?? 0;
            int attraction = fields.Attraction?.Count ?? 0;
            int noise = fields.Noise?.Count ?? 0;
            int drag = fields.Drag?.Count ?? 0;
            int orbital = fields.Orbital?.Count ?? 0;
            EmitterForceSummaryText.Text = $"A{acceleration} · T{attraction} · N{noise} · D{drag} · O{orbital}";
        }

        private void AddEmitterForce_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag } ||
                !Enum.TryParse(tag, ignoreCase: true, out VfxEmitterForceKind kind) ||
                _model.SelectedEmitter is not { } selected ||
                _inspectedSystem?.Definition == null)
            {
                return;
            }

            if (!ApplyEmitterTransforms()) return;
            if (_activeBundle?.SystemSources.TryGetValue(_inspectedSystem.PathHash, out string sourceBin) != true)
            {
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = "Source BIN for this system is unavailable";
                return;
            }

            if (!VfxEmitterAuthoringService.TryAddForce(
                    sourceBin,
                    _inspectedSystem.PathHash,
                    selected.SourceOrder,
                    kind,
                    out VfxSystemDefinition updated,
                    out string error))
            {
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = $"Force add failed: {error}";
                return;
            }

            AcceptAuthoredSystem(updated);
            if (EmitterAuthoringStatusText != null)
                EmitterAuthoringStatusText.Text = $"Added {kind} force";
        }

        private void ForceValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: VfxForcePropertyAuthoringItem property }) return;
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitForceProperty(property);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                RebuildForceAuthoringItems(_model.SelectedEmitter?.EmitterDef);
                Focus();
            }
        }

        private void ForceBool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: VfxForcePropertyAuthoringItem property })
                CommitForceProperty(property);
        }

        private bool CommitForceProperty(VfxForcePropertyAuthoringItem property)
        {
            if (property == null || _model.SelectedEmitter is not { } selected || _inspectedSystem?.Definition == null)
                return false;
            if (!ApplyEmitterTransforms()) return false;
            if (_activeBundle?.SystemSources.TryGetValue(_inspectedSystem.PathHash, out string sourceBin) != true)
            {
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = "Source BIN for this system is unavailable";
                return false;
            }

            bool saved;
            VfxSystemDefinition updated;
            string error;
            switch (property.ValueKind)
            {
                case VfxForceAuthoringValueKind.Scalar:
                    if (!TryAuthoringNumber(property.XText, out float scalar))
                    {
                        if (EmitterAuthoringStatusText != null)
                            EmitterAuthoringStatusText.Text = $"Invalid {property.Label} value";
                        return false;
                    }
                    saved = VfxEmitterAuthoringService.TryWriteForceScalar(
                        sourceBin,
                        _inspectedSystem.PathHash,
                        selected.SourceOrder,
                        property.ForceKind,
                        property.ForceIndex,
                        property.Property,
                        scalar,
                        out updated,
                        out error);
                    break;

                case VfxForceAuthoringValueKind.Vector3:
                    if (!TryAuthoringNumber(property.XText, out float x) ||
                        !TryAuthoringNumber(property.YText, out float y) ||
                        !TryAuthoringNumber(property.ZText, out float z))
                    {
                        if (EmitterAuthoringStatusText != null)
                            EmitterAuthoringStatusText.Text = $"Invalid {property.Label} vector";
                        return false;
                    }
                    saved = VfxEmitterAuthoringService.TryWriteForceVector(
                        sourceBin,
                        _inspectedSystem.PathHash,
                        selected.SourceOrder,
                        property.ForceKind,
                        property.ForceIndex,
                        property.Property,
                        new Vector3(x, y, z),
                        out updated,
                        out error);
                    break;

                case VfxForceAuthoringValueKind.Boolean:
                    saved = VfxEmitterAuthoringService.TryWriteForceBool(
                        sourceBin,
                        _inspectedSystem.PathHash,
                        selected.SourceOrder,
                        property.ForceKind,
                        property.ForceIndex,
                        property.Property,
                        property.BoolValue,
                        out updated,
                        out error);
                    break;

                default:
                    return false;
            }

            if (!saved)
            {
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = $"Force save failed: {error}";
                return false;
            }

            AcceptAuthoredSystem(updated);
            if (EmitterAuthoringStatusText != null)
                EmitterAuthoringStatusText.Text = $"Saved {property.Label}";
            return true;
        }

        private void RemoveEmitterForce_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: VfxForceAuthoringItem force } ||
                _model.SelectedEmitter is not { } selected ||
                _inspectedSystem?.Definition == null)
            {
                return;
            }
            if (!ApplyEmitterTransforms()) return;
            if (_activeBundle?.SystemSources.TryGetValue(_inspectedSystem.PathHash, out string sourceBin) != true)
                return;

            if (!VfxEmitterAuthoringService.TryRemoveForce(
                    sourceBin,
                    _inspectedSystem.PathHash,
                    selected.SourceOrder,
                    force.Kind,
                    force.ForceIndex,
                    out VfxSystemDefinition updated,
                    out string error))
            {
                if (EmitterAuthoringStatusText != null)
                    EmitterAuthoringStatusText.Text = $"Force remove failed: {error}";
                return;
            }

            AcceptAuthoredSystem(updated);
            if (EmitterAuthoringStatusText != null)
                EmitterAuthoringStatusText.Text = $"Removed {force.Title}";
        }

        private void ActivateCurve_Click(object sender, RoutedEventArgs e)
        {
            VfxCurveAuthoringItem curve = _model.SelectedCurveAuthoringItem;
            if (curve == null || curve.HasCurve || curve.IsForceCurve || _model.SelectedEmitter is not { } selected || _inspectedSystem == null)
                return;
            if (!ApplyEmitterTransforms()) return;
            if (!TryGetAuthoringSource(out string sourceBin)) return;

            if (!VfxEmitterAuthoringService.TryActivateCurve(
                    sourceBin,
                    _inspectedSystem.PathHash,
                    selected.SourceOrder,
                    curve.PropertyHash,
                    curve.Family,
                    curve.Constant,
                    out VfxSystemDefinition updated,
                    out string error))
            {
                SetAuthoringStatus($"Curve activation failed: {error}");
                return;
            }

            string identity = curve.Identity;
            AcceptAuthoredSystem(updated);
            SelectCurve(identity, 0);
            SetAuthoringStatus($"Activated {curve.Name} curve");
        }

        private void CurveKey_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: VfxCurveKeyAuthoringItem key }) return;
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitCurveKey(key);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                string identity = key.Owner.Identity;
                int index = key.KeyIndex;
                RebuildCurveAuthoringItems(_model.SelectedEmitter?.EmitterDef);
                SelectCurve(identity, index);
                Focus();
            }
        }

        private bool CommitCurveKey(VfxCurveKeyAuthoringItem key)
        {
            if (key?.Owner == null || _model.SelectedEmitter is not { } selected || _inspectedSystem == null)
                return false;
            if (!TryReadCurveKey(key, out float time, out Vector4 value))
            {
                SetAuthoringStatus($"Invalid key values for {key.Owner.Name}");
                return false;
            }
            if (!IsCurveKeyTimeWithinNeighbors(key.Owner, key.KeyIndex, time))
            {
                SetAuthoringStatus($"Key time for {key.Owner.Name} must stay between its neighboring keys");
                return false;
            }
            if (!ApplyEmitterTransforms()) return false;
            if (!TryGetAuthoringSource(out string sourceBin)) return false;

            bool saved = key.Owner.IsForceCurve
                ? VfxEmitterAuthoringService.TryWriteForceCurveKey(
                    sourceBin,
                    _inspectedSystem.PathHash,
                    selected.SourceOrder,
                    key.Owner.ForceKind,
                    key.Owner.ForceIndex,
                    key.Owner.ForceProperty,
                    key.KeyIndex,
                    time,
                    value,
                    out VfxSystemDefinition updated,
                    out string error)
                : VfxEmitterAuthoringService.TryWriteCurveKey(
                    sourceBin,
                    _inspectedSystem.PathHash,
                    selected.SourceOrder,
                    key.Owner.PropertyHash,
                    key.Owner.Family,
                    key.KeyIndex,
                    time,
                    value,
                    out updated,
                    out error);
            if (!saved)
            {
                SetAuthoringStatus($"Curve save failed: {error}");
                return false;
            }

            string identity = key.Owner.Identity;
            int keyIndex = key.KeyIndex;
            AcceptAuthoredSystem(updated);
            SelectCurve(identity, keyIndex);
            SetAuthoringStatus($"Saved {key.Owner.Name} key {keyIndex + 1}");
            return true;
        }

        private void AddCurveKey_Click(object sender, RoutedEventArgs e)
        {
            VfxCurveAuthoringItem curve = _model.SelectedCurveAuthoringItem;
            if (curve == null || !curve.HasCurve || _model.SelectedEmitter is not { } selected || _inspectedSystem == null)
                return;
            if (!TrySuggestCurveKey(curve, _model.SelectedCurveKeyAuthoringItem, out int index, out float time, out Vector4 value))
            {
                SetAuthoringStatus("Cannot derive a valid curve key from the current values");
                return;
            }
            if (!ApplyEmitterTransforms()) return;
            if (!TryGetAuthoringSource(out string sourceBin)) return;

            bool saved = curve.IsForceCurve
                ? VfxEmitterAuthoringService.TryInsertForceCurveKey(
                    sourceBin,
                    _inspectedSystem.PathHash,
                    selected.SourceOrder,
                    curve.ForceKind,
                    curve.ForceIndex,
                    curve.ForceProperty,
                    index,
                    time,
                    value,
                    out VfxSystemDefinition updated,
                    out string error)
                : VfxEmitterAuthoringService.TryInsertCurveKey(
                    sourceBin,
                    _inspectedSystem.PathHash,
                    selected.SourceOrder,
                    curve.PropertyHash,
                    curve.Family,
                    index,
                    time,
                    value,
                    out updated,
                    out error);
            if (!saved)
            {
                SetAuthoringStatus($"Add key failed: {error}");
                return;
            }

            string identity = curve.Identity;
            AcceptAuthoredSystem(updated);
            SelectCurve(identity, index);
            SetAuthoringStatus($"Added {curve.Name} key at {time:0.###}");
        }

        private void RemoveCurveKey_Click(object sender, RoutedEventArgs e)
        {
            VfxCurveKeyAuthoringItem key = sender is FrameworkElement { DataContext: VfxCurveKeyAuthoringItem row }
                ? row
                : _model.SelectedCurveKeyAuthoringItem;
            if (key?.Owner == null || _model.SelectedEmitter is not { } selected || _inspectedSystem == null)
                return;
            if (!ApplyEmitterTransforms()) return;
            if (!TryGetAuthoringSource(out string sourceBin)) return;

            bool saved = key.Owner.IsForceCurve
                ? VfxEmitterAuthoringService.TryRemoveForceCurveKeys(
                    sourceBin,
                    _inspectedSystem.PathHash,
                    selected.SourceOrder,
                    key.Owner.ForceKind,
                    key.Owner.ForceIndex,
                    key.Owner.ForceProperty,
                    new[] { key.KeyIndex },
                    out VfxSystemDefinition updated,
                    out string error)
                : VfxEmitterAuthoringService.TryRemoveCurveKeys(
                    sourceBin,
                    _inspectedSystem.PathHash,
                    selected.SourceOrder,
                    key.Owner.PropertyHash,
                    new[] { key.KeyIndex },
                    out updated,
                    out error);
            if (!saved)
            {
                SetAuthoringStatus($"Remove key failed: {error}");
                return;
            }

            string identity = key.Owner.Identity;
            int next = Math.Max(0, key.KeyIndex - 1);
            AcceptAuthoredSystem(updated);
            SelectCurve(identity, next);
            SetAuthoringStatus($"Removed {key.Owner.Name} key {key.KeyIndex + 1}");
        }

        private bool TryReadCurveKey(VfxCurveKeyAuthoringItem key, out float time, out Vector4 value)
        {
            time = 0f;
            value = default;
            if (key?.Owner == null || !TryAuthoringNumber(key.TimeText, out time) ||
                !TryAuthoringNumber(key.XText, out float x))
            {
                return false;
            }

            float y = 0f;
            float z = 0f;
            float w = 0f;
            if (key.Owner.ComponentCount >= 2 && !TryAuthoringNumber(key.YText, out y)) return false;
            if (key.Owner.ComponentCount >= 3 && !TryAuthoringNumber(key.ZText, out z)) return false;
            if (key.Owner.ComponentCount >= 4 && !TryAuthoringNumber(key.WText, out w)) return false;
            value = new Vector4(x, y, z, w);
            return true;
        }

        internal static bool IsCurveKeyTimeWithinNeighbors(
            VfxCurveAuthoringItem curve,
            int keyIndex,
            float time)
        {
            if (curve == null || keyIndex < 0 || keyIndex >= curve.Keys.Count || !float.IsFinite(time))
                return false;
            if (keyIndex > 0 &&
                TryCurveRow(curve.Keys[keyIndex - 1], out float previous, out _) &&
                time < previous)
            {
                return false;
            }
            if (keyIndex + 1 < curve.Keys.Count &&
                TryCurveRow(curve.Keys[keyIndex + 1], out float next, out _) &&
                time > next)
            {
                return false;
            }
            return true;
        }

        private static bool TrySuggestCurveKey(
            VfxCurveAuthoringItem curve,
            VfxCurveKeyAuthoringItem selected,
            out int insertionIndex,
            out float time,
            out Vector4 value)
        {
            insertionIndex = 0;
            time = 0f;
            value = curve?.Constant ?? Vector4.Zero;
            if (curve == null) return false;
            if (curve.Keys.Count == 0) return true;

            int selectedIndex = selected?.Owner == curve
                ? Math.Clamp(selected.KeyIndex, 0, curve.Keys.Count - 1)
                : 0;
            if (!TryCurveRow(curve.Keys[selectedIndex], out float currentTime, out _)) return false;
            if (selectedIndex + 1 < curve.Keys.Count)
            {
                if (!TryCurveRow(curve.Keys[selectedIndex + 1], out float nextTime, out _)) return false;
                time = (currentTime + nextTime) * 0.5f;
            }
            else if (currentTime < 1f)
            {
                time = (currentTime + 1f) * 0.5f;
            }
            else
            {
                time = currentTime + 0.25f;
            }

            value = SampleCurveRows(curve, time);
            insertionIndex = curve.Keys.Count;
            for (int index = 0; index < curve.Keys.Count; index++)
            {
                if (!TryCurveRow(curve.Keys[index], out float keyTime, out _)) return false;
                if (keyTime > time)
                {
                    insertionIndex = index;
                    break;
                }
            }
            return true;
        }

        private static Vector4 SampleCurveRows(VfxCurveAuthoringItem curve, float time)
        {
            if (curve.Keys.Count == 0) return curve.Constant;
            if (!TryCurveRow(curve.Keys[0], out float firstTime, out Vector4 first)) return curve.Constant;
            if (time <= firstTime) return first;

            for (int index = 1; index < curve.Keys.Count; index++)
            {
                if (!TryCurveRow(curve.Keys[index], out float rightTime, out Vector4 right)) continue;
                if (time > rightTime)
                {
                    firstTime = rightTime;
                    first = right;
                    continue;
                }
                float span = rightTime - firstTime;
                if (Math.Abs(span) <= 1e-7f) return right;
                float amount = Math.Clamp((time - firstTime) / span, 0f, 1f);
                return Vector4.Lerp(first, right, amount);
            }
            return first;
        }

        private static bool TryCurveRow(VfxCurveKeyAuthoringItem key, out float time, out Vector4 value)
        {
            time = 0f;
            value = default;
            if (key?.Owner == null || !TryAuthoringNumber(key.TimeText, out time) ||
                !TryAuthoringNumber(key.XText, out float x)) return false;
            float y = 0f;
            float z = 0f;
            float w = 0f;
            if (key.Owner.ComponentCount >= 2 && !TryAuthoringNumber(key.YText, out y)) return false;
            if (key.Owner.ComponentCount >= 3 && !TryAuthoringNumber(key.ZText, out z)) return false;
            if (key.Owner.ComponentCount >= 4 && !TryAuthoringNumber(key.WText, out w)) return false;
            value = new Vector4(x, y, z, w);
            return true;
        }

        private void SelectCurve(string identity, int keyIndex)
        {
            VfxCurveAuthoringItem curve = _model.CurveAuthoringItems.FirstOrDefault(item => item.Identity == identity);
            _model.SelectedCurveAuthoringItem = curve;
            if (curve == null || curve.Keys.Count == 0)
            {
                _model.SelectedCurveKeyAuthoringItem = null;
                return;
            }
            int safe = Math.Clamp(keyIndex, 0, curve.Keys.Count - 1);
            _model.SelectedCurveKeyAuthoringItem = curve.Keys[safe];
        }

        private bool TryGetAuthoringSource(out string sourceBin)
        {
            sourceBin = null;
            if (_inspectedSystem != null &&
                _activeBundle?.SystemSources.TryGetValue(_inspectedSystem.PathHash, out sourceBin) == true)
            {
                return true;
            }
            SetAuthoringStatus("Source BIN for this system is unavailable");
            return false;
        }

        private void SetAuthoringStatus(string status)
        {
            if (EmitterAuthoringStatusText != null)
                EmitterAuthoringStatusText.Text = status ?? string.Empty;
        }

        private void EmitterSolo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: VfxEmitterDiagnosticItem item })
                ToggleEmitterSolo(item);
        }

        private void EmitterMute_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: VfxEmitterDiagnosticItem item })
                ToggleEmitterMuted(item);
        }

        private void ToggleEmitterSolo(VfxEmitterDiagnosticItem item)
        {
            if (item == null) return;
            _vfxRenderer?.SetAllEmittersVisibility(true);
            item.IsSolo = !item.IsSolo;
        }

        private void ToggleEmitterMuted(VfxEmitterDiagnosticItem item)
        {
            if (item == null) return;
            _vfxRenderer?.SetAllEmittersVisibility(true);
            item.IsMuted = !item.IsMuted;
        }

        private void ToggleSoloAll_Click(object sender, RoutedEventArgs e)
        {
            if (_model.Emitters.Count == 0) return;
            bool newSolo = !_model.Emitters.All(emitter => emitter.IsSolo);
            _vfxRenderer?.SetAllEmittersVisibility(true);
            try
            {
                _isBulkEmitterStateChange = true;
                foreach (VfxEmitterDiagnosticItem emitter in _model.Emitters)
                    emitter.IsSolo = newSolo;
            }
            finally
            {
                _isBulkEmitterStateChange = false;
            }
            UpdateEmittersVisibility();
        }

        private void ToggleMuteAll_Click(object sender, RoutedEventArgs e)
        {
            if (_model.Emitters.Count == 0) return;
            bool newMute = !_model.Emitters.All(emitter => emitter.IsMuted);
            try
            {
                _isBulkEmitterStateChange = true;
                foreach (VfxEmitterDiagnosticItem emitter in _model.Emitters)
                    emitter.IsMuted = newMute;
            }
            finally
            {
                _isBulkEmitterStateChange = false;
            }

            _vfxRenderer?.SetAllEmittersVisibility(!newMute);
            UpdateEmittersVisibility();
        }

        private void UpdateEmittersVisibility()
        {
            bool hasSolo = _model.Emitters.Any(em => em.IsSolo);
            _model.HasAnySolo = hasSolo;

            foreach (var emitter in _model.Emitters)
            {
                bool visible;
                if (hasSolo)
                {
                    visible = emitter.IsSolo && !emitter.IsMuted;
                }
                else
                {
                    visible = !emitter.IsMuted;
                }
                emitter.IsEnabled = visible;
                _vfxRenderer?.SetEmitterVisibility(emitter.SourceOrder, visible);
            }

            _model.IsAllMuted = _model.Emitters.Count > 0 && _model.Emitters.All(em => em.IsMuted);
        }

        private void Replay_Click(object sender, RoutedEventArgs e) => Play_Click(sender, e);

        private void ResetCamera_Click(object sender, RoutedEventArgs e)
        {
            ResetCamera();
        }

        #region Timeline Deck Mechanics

        private void TracksCanvasContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTimelineTrackMetrics();
            UpdatePlayheadPosition();
        }

        private const double TimelineLiveColumnWidth = 40d;

        private double GetTimelineTrackWidth()
            => TracksCanvasContainer == null
                ? 0d
                : Math.Max(0d, TracksCanvasContainer.ActualWidth - TimelineLiveColumnWidth);

        private void UpdateTimelineTrackMetrics()
        {
            if (_model == null || TracksCanvasContainer == null) return;
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;

            // Soft, refined translucent slate-blue palette matching LTK-Manager timeline reference
            Brush[] fillPalette = new Brush[]
            {
                new SolidColorBrush(Color.FromArgb(60, 59, 130, 246)),  // Soft Accent Blue
                new SolidColorBrush(Color.FromArgb(60, 14, 165, 233)),  // Soft Sky Blue
                new SolidColorBrush(Color.FromArgb(60, 99, 102, 241)),  // Soft Indigo
                new SolidColorBrush(Color.FromArgb(60, 45, 212, 191)),  // Soft Teal
                new SolidColorBrush(Color.FromArgb(60, 168, 85, 247))   // Soft Purple
            };
            Brush[] borderPalette = new Brush[]
            {
                new SolidColorBrush(Color.FromArgb(160, 59, 130, 246)),
                new SolidColorBrush(Color.FromArgb(160, 14, 165, 233)),
                new SolidColorBrush(Color.FromArgb(160, 99, 102, 241)),
                new SolidColorBrush(Color.FromArgb(160, 45, 212, 191)),
                new SolidColorBrush(Color.FromArgb(160, 168, 85, 247))
            };

            foreach (var b in fillPalette) b.Freeze();
            foreach (var b in borderPalette) b.Freeze();

            int idx = 1;
            foreach (var emitter in _model.Emitters)
            {
                emitter.IndexNumber = idx;
                emitter.TrackBrush = fillPalette[(idx - 1) % fillPalette.Length];
                emitter.TrackBorderBrush = borderPalette[(idx - 1) % borderPalette.Length];

                double delay = emitter.EmitterDef?.TimeBeforeFirstEmission ?? 0;
                VfxEmitterDefinition definition = emitter.EmitterDef;
                double partLife = definition == null ? 1.5 : GetMaximumParticleLifetime(definition);
                double duration = definition?.IsSingleParticle == true
                    ? partLife
                    : definition?.EmitterLifetime is { } emitterLife
                        ? emitterLife + partLife
                        : Math.Max(0, totalDur - delay);
                var metrics = CalculateEmitterTrackMetrics(delay, duration, totalDur, availableWidth);

                emitter.TrackMargin = new Thickness(metrics.BarLeft, 0, 0, 0);
                emitter.TrackWidth = metrics.BarWidth;

                // Yellow Keyframe Marker Dot for Emission Delay
                if (delay > 0.05)
                {
                    emitter.HasDelay = true;
                    emitter.DelayTime = delay;
                    emitter.DelayMarkerMargin = new Thickness(metrics.MarkerLeft, 0, 0, 0);
                }
                else
                {
                    emitter.HasDelay = false;
                    emitter.DelayTime = 0;
                    emitter.DelayMarkerMargin = new Thickness(0);
                }

                idx++;
            }
        }

        internal static (double BarLeft, double BarWidth, double MarkerLeft) CalculateEmitterTrackMetrics(
            double delay,
            double duration,
            double totalDuration,
            double availableWidth)
        {
            double safeTotal = Math.Max(0.001, totalDuration);
            double safeWidth = Math.Max(0, availableWidth);
            double barLeft = Math.Clamp(Math.Max(0, delay) / safeTotal * safeWidth, 0, safeWidth);
            double rawWidth = Math.Max(0, duration) / safeTotal * safeWidth;
            double remainingWidth = Math.Max(0, safeWidth - barLeft);
            double barWidth = Math.Min(Math.Max(remainingWidth > 0 ? 2 : 0, rawWidth), remainingWidth);
            double markerLeft = Math.Clamp(barLeft - 4, 0, Math.Max(0, safeWidth - 8));
            return (barLeft, barWidth, markerLeft);
        }

        private static double GetMaximumParticleLifetime(VfxEmitterDefinition emitter)
            => VfxDurationCalculator.GetMaximumParticleLifetime(emitter);

        private void UpdatePlayheadPosition()
        {
            if (_model == null || TracksCanvasContainer == null || PlayheadLine == null) return;
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;
            double ratio = Math.Clamp(_model.CurrentTime / totalDur, 0.0, 1.0);
            double posX = ratio * availableWidth;

            PlayheadLine.X1 = posX;
            PlayheadLine.X2 = posX;
            if (PlayheadHandle != null)
                Canvas.SetLeft(PlayheadHandle, posX - 5);

            if (LoopBoundaryLine != null && LoopBoundaryHandle != null &&
                LoopStartLine != null && LoopStartHandle != null && LoopRangeBand != null)
            {
                (double from, double to) = ClampPreviewLoop(
                    _model.ActiveLoopStart,
                    _model.ActiveLoopDuration > 0 ? _model.ActiveLoopDuration : totalDur,
                    totalDur);
                double startPosX = from / totalDur * availableWidth;
                double endPosX = to / totalDur * availableWidth;

                LoopStartLine.X1 = startPosX;
                LoopStartLine.X2 = startPosX;
                Canvas.SetLeft(LoopStartHandle, startPosX - 7);

                LoopBoundaryLine.X1 = endPosX;
                LoopBoundaryLine.X2 = endPosX;
                Canvas.SetLeft(LoopBoundaryHandle, endPosX - 7);

                Canvas.SetLeft(LoopRangeBand, startPosX);
                LoopRangeBand.Width = Math.Max(0d, endPosX - startPosX);
            }
        }

        internal static bool ShouldRestartPreview(bool enabled, double currentTime, double boundary)
            => enabled && boundary > 0 && currentTime >= boundary;

        internal static (double From, double To) ClampPreviewLoop(double from, double to, double span)
        {
            double safeSpan = double.IsFinite(span) ? Math.Max(PreviewLoopMinimumSpan, span) : PreviewLoopMinimumSpan;
            double safeTo = Math.Clamp(double.IsFinite(to) ? to : safeSpan, PreviewLoopMinimumSpan, safeSpan);
            double safeFrom = Math.Clamp(
                double.IsFinite(from) ? from : 0d,
                0d,
                Math.Max(0d, safeTo - PreviewLoopMinimumSpan));
            return (safeFrom, safeTo);
        }

        internal static double ResolvePreviewLoopRestart(double from, double to, double span)
            => ClampPreviewLoop(from, to, span).From;

        internal static double ResolveTimelineDuration(double playbackDuration)
            => double.IsFinite(playbackDuration) && playbackDuration > 0
                ? Math.Max(0.05, playbackDuration)
                : 10.0;

        private bool _isDraggingLoopStart;
        private bool _isDraggingLoopBoundary;

        private void LoopStartHandle_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingLoopStart = true;
            ((UIElement)sender).CaptureMouse();
            UpdateLoopStartFromMouse(e.GetPosition(TracksCanvasContainer).X);
            e.Handled = true;
        }

        private void LoopStartHandle_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingLoopStart && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateLoopStartFromMouse(e.GetPosition(TracksCanvasContainer).X);
                e.Handled = true;
            }
        }

        private void LoopStartHandle_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingLoopStart) return;
            _isDraggingLoopStart = false;
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        private void LoopBoundaryHandle_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingLoopBoundary = true;
            ((UIElement)sender).CaptureMouse();
            UpdateLoopBoundaryFromMouse(e.GetPosition(TracksCanvasContainer).X);
            e.Handled = true;
        }

        private void LoopBoundaryHandle_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingLoopBoundary && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateLoopBoundaryFromMouse(e.GetPosition(TracksCanvasContainer).X);
                e.Handled = true;
            }
        }

        private void LoopBoundaryHandle_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingLoopBoundary)
            {
                _isDraggingLoopBoundary = false;
                ((UIElement)sender).ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void UpdateLoopStartFromMouse(double mouseX)
        {
            if (_model == null || TracksCanvasContainer == null) return;
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;
            double requested = Math.Clamp(mouseX / availableWidth, 0d, 1d) * totalDur;
            (double from, double to) = ClampPreviewLoop(
                requested,
                _model.ActiveLoopDuration > 0 ? _model.ActiveLoopDuration : totalDur,
                totalDur);

            _model.ActiveLoopStart = Math.Round(from, 3);
            _model.ActiveLoopDuration = Math.Round(to, 3);
            _model.IsPreviewLoopEnabled = true;
            UpdatePlayheadPosition();
        }

        private void UpdateLoopBoundaryFromMouse(double mouseX)
        {
            if (_model == null || TracksCanvasContainer == null) return;
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;
            double requested = Math.Clamp(mouseX / availableWidth, 0d, 1d) * totalDur;
            (double from, double to) = ClampPreviewLoop(_model.ActiveLoopStart, requested, totalDur);

            _model.ActiveLoopStart = Math.Round(from, 3);
            _model.ActiveLoopDuration = Math.Round(to, 3);
            _model.IsPreviewLoopEnabled = true;
            UpdatePlayheadPosition();
        }

        private bool _isTimelineDragging;

        private void TimelineGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingLoopStart || _isDraggingLoopBoundary) return;
            if (e.OriginalSource is FrameworkElement fe &&
                (fe == LoopStartHandle || fe == LoopBoundaryHandle || fe == LoopBoundaryCanvas ||
                 fe == LoopStartLine || fe == LoopBoundaryLine || fe == LoopRangeBand)) return;
            _isTimelineDragging = true;
            UpdateSeekFromTimeline(e.GetPosition(TracksCanvasContainer).X);
        }

        private void TimelineGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isTimelineDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateSeekFromTimeline(e.GetPosition(TracksCanvasContainer).X);
            }
        }

        private void TimelineGrid_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isTimelineDragging = false;
        }

        private void UpdateSeekFromTimeline(double mouseX)
        {
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0 || _model == null) return;

            double ratio = Math.Clamp(mouseX / availableWidth, 0.0, 1.0);
            double seekTime = ratio * _model.TotalDuration;

            _model.CurrentTime = seekTime;
            SyncMapCharacterClipTime(seekTime);
            _vfxRenderer?.Seek(seekTime);
        }

        private void StepBack_Click(object sender, RoutedEventArgs e)
            => StepPlayback(-1);

        private void StepForward_Click(object sender, RoutedEventArgs e)
            => StepPlayback(1);

        private void StepPlayback(int frames)
        {
            if (_model == null || frames == 0) return;

            _vfxRenderer?.Pause();
            _model.IsPlaying = false;

            double newTime = PlaybackStepTarget(
                _model.CurrentTime,
                frames,
                _model.TotalDuration);
            _model.CurrentTime = newTime;
            SyncMapCharacterClipTime(newTime);
            _vfxRenderer?.Seek(newTime);
            UpdatePlayheadPosition();
        }

        internal static double PlaybackStepTarget(double currentTime, int frames, double span)
        {
            const double frame = 1d / 60d;
            double target = Math.Max(0d, currentTime + frames * frame);
            return span > 0d && double.IsFinite(span)
                ? Math.Min(target, span)
                : target;
        }

        private void RunKeys_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ShouldIgnoreRunHotkey(Keyboard.FocusedElement as DependencyObject)) return;

            ModifierKeys modifiers = Keyboard.Modifiers;
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
            bool shift = (modifiers & ModifierKeys.Shift) != 0;

            bool handled = true;
            switch (e.Key)
            {
                case Key.Space:
                    PlayPauseToggle_Click(this, new RoutedEventArgs());
                    break;
                case Key.Left:
                    StepPlayback(shift ? -6 : -1);
                    break;
                case Key.Right:
                    StepPlayback(shift ? 6 : 1);
                    break;
                case Key.Home:
                    RestartPlayback();
                    break;
                case Key.F:
                    ResetCamera();
                    break;
                case Key.S when _model.IsRawSystemsMode && _model.SelectedEmitter != null:
                    ToggleEmitterSolo(_model.SelectedEmitter);
                    break;
                case Key.M when _model.IsRawSystemsMode && _model.SelectedEmitter != null:
                    ToggleEmitterMuted(_model.SelectedEmitter);
                    break;
                case Key.OemOpenBrackets:
                    SetPlaybackSpeed(PlaybackSpeedDetent(_model.Speed, -1));
                    break;
                case Key.OemCloseBrackets:
                    SetPlaybackSpeed(PlaybackSpeedDetent(_model.Speed, 1));
                    break;
                default:
                    handled = false;
                    break;
            }

            if (handled) e.Handled = true;
        }

        private static bool ShouldIgnoreRunHotkey(DependencyObject focused)
            => focused is TextBoxBase or PasswordBox or ComboBox or Slider or ButtonBase or
               Selector or TreeView or MenuItem;

        private void RestartPlayback()
        {
            if (_model == null) return;
            bool wasPlaying = _model.IsPlaying;
            _vfxRenderer?.Seek(0d);
            _model.CurrentTime = 0d;
            SyncMapCharacterClipTime(0d);
            if (wasPlaying) _vfxRenderer?.Play();
            UpdatePlayheadPosition();
        }

        private void PlayPauseToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_model == null) return;

            if (_model.IsPlaying)
            {
                _vfxRenderer?.Pause();
                _model.IsPlaying = false;
                return;
            }

            if (TryPlaySelectedTimedPreview(restartWhenPlaying: false))
                return;

            if (_model.SelectedSystem != null)
            {
                if (!HasSelectedSystemReady())
                {
                    RequestSystemInspection(_model.SelectedSystem);
                }
                else if (_model.CurrentTime >= _model.TotalDuration)
                {
                    _vfxRenderer.Stop();
                    _model.CurrentTime = 0;
                    _vfxRenderer.Seek(0);
                    _vfxRenderer.Play();
                    _model.IsPlaying = true;
                }
                else
                {
                    _vfxRenderer.Play();
                    _model.IsPlaying = true;
                }
            }
        }

        private void EmitterFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyEmitterFilter();
        }

        private void ClearEmitterFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_model != null)
            {
                _model.EmitterFilterText = string.Empty;
            }
            ApplyEmitterFilter();
        }

        private void ApplyEmitterFilter()
        {
            if (_model == null) return;
            string filter = _model.EmitterFilterText?.Trim() ?? string.Empty;
            var view = CollectionViewSource.GetDefaultView(_model.Emitters);
            if (view != null)
            {
                if (string.IsNullOrWhiteSpace(filter))
                {
                    view.Filter = null;
                }
                else
                {
                    view.Filter = obj =>
                    {
                        if (obj is VfxEmitterDiagnosticItem item)
                        {
                            return (item.Name != null && item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                                   item.IndexNumber.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                   (item.PrimitiveKindName != null && item.PrimitiveKindName.Contains(filter, StringComparison.OrdinalIgnoreCase));
                        }
                        return false;
                    };
                }
            }
        }

        private const double RulerLoopDragThreshold = 4d;
        private bool _isRulerDragging;
        private bool _isRulerCreatingLoop;
        private double _rulerPressX;

        private void Ruler_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TracksCanvasContainer == null || _model == null) return;

            double mouseX = e.GetPosition(TracksCanvasContainer).X;
            _rulerPressX = mouseX;
            _isRulerDragging = true;
            _isRulerCreatingLoop = false;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void Ruler_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isRulerDragging || e.LeftButton != MouseButtonState.Pressed || TracksCanvasContainer == null)
                return;

            double mouseX = e.GetPosition(TracksCanvasContainer).X;
            if (!_isRulerCreatingLoop &&
                _model.IsPreviewLoopEnabled &&
                Math.Abs(mouseX - _rulerPressX) >= RulerLoopDragThreshold)
            {
                _isRulerCreatingLoop = true;
            }

            if (_isRulerCreatingLoop)
                UpdateLoopRangeFromRuler(_rulerPressX, mouseX);

            e.Handled = true;
        }

        private void Ruler_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isRulerDragging || TracksCanvasContainer == null) return;

            double mouseX = e.GetPosition(TracksCanvasContainer).X;
            if (_isRulerCreatingLoop)
                UpdateLoopRangeFromRuler(_rulerPressX, mouseX);
            else
                UpdateSeekFromTimeline(mouseX);

            _isRulerDragging = false;
            _isRulerCreatingLoop = false;
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        private double TimelineTimeFromX(double mouseX)
        {
            double availableWidth = GetTimelineTrackWidth();
            if (availableWidth <= 0 || _model == null) return 0d;

            double ratio = Math.Clamp(mouseX / availableWidth, 0d, 1d);
            return ratio * _model.TotalDuration;
        }

        private void UpdateLoopRangeFromRuler(double anchorX, double currentX)
        {
            if (_model == null) return;

            double totalDuration = Math.Max(PreviewLoopMinimumSpan, _model.TotalDuration);
            double from = Math.Min(TimelineTimeFromX(anchorX), TimelineTimeFromX(currentX));
            double to = Math.Max(TimelineTimeFromX(anchorX), TimelineTimeFromX(currentX));
            if (to - from < PreviewLoopMinimumSpan)
            {
                to = Math.Min(totalDuration, from + PreviewLoopMinimumSpan);
                if (to - from < PreviewLoopMinimumSpan)
                    from = Math.Max(0d, to - PreviewLoopMinimumSpan);
            }

            _model.ActiveLoopStart = Math.Round(from, 3);
            _model.ActiveLoopDuration = Math.Round(to, 3);
            _model.IsPreviewLoopEnabled = true;
            UpdatePlayheadPosition();
        }

        #endregion

        private static string GetBlendModeName(int blendMode) => VfxBlendModes.Describe(blendMode);

        private static string DescribeTextureSources(VfxEmitterDefinition emitter)
        {
            if (emitter is null) return "N/A";

            var sources = new List<string>();
            bool hasVisualTexture = !string.IsNullOrWhiteSpace(emitter.TexturePath) ||
                                    !string.IsNullOrWhiteSpace(emitter.TextureMultPath);
            AddTextureSource(sources, "Base", emitter.TexturePath);
            AddTextureSource(sources, "Mult", emitter.TextureMultPath);
            AddTextureSource(sources, "Distortion", emitter.Distortion?.NormalMapTexturePath);
            AddTextureSource(sources, "Erosion", emitter.AlphaErosion?.TexturePath);
            AddTextureSource(sources, "Reflection", emitter.Reflection?.TexturePath);
            AddTextureSource(sources, "Palette", emitter.PaletteDefinition?.PaletteTexturePath);
            if (!hasVisualTexture || !string.Equals(
                    emitter.ParticleColorTexturePath,
                    "ASSETS/Shared/Particles/DefaultColorOverlifetime.dds",
                    StringComparison.OrdinalIgnoreCase))
            {
                AddTextureSource(sources, "Color LUT", emitter.ParticleColorTexturePath);
            }
            return sources.Count == 0 ? "N/A" : string.Join(" | ", sources);
        }

        private static (string Status, Brush Brush) DescribeTextureStatus(
            VfxEmitterDefinition emitter,
            BitmapSource texture)
        {
            if (texture != null) return ("Resolved", Brushes.LightGreen);
            if (!string.IsNullOrWhiteSpace(emitter.TexturePath)) return ("MISSING", Brushes.OrangeRed);
            if (!string.IsNullOrWhiteSpace(emitter.TextureMultPath)) return ("Mult stage", Brushes.DarkOrange);
            if (!string.IsNullOrWhiteSpace(emitter.Distortion?.NormalMapTexturePath))
                return ("Distortion stage", Brushes.DarkOrange);
            if (!string.IsNullOrWhiteSpace(emitter.ParticleColorTexturePath))
                return ("LUT only", Brushes.DarkOrange);
            return ("None", Brushes.Gray);
        }

        private static void AddTextureSource(List<string> sources, string role, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                sources.Add($"{role}: {path}");
        }

        private void SearchQuery_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = _model.SearchQuery?.Trim() ?? "";

            var animView = CollectionViewSource.GetDefaultView(_model.DetectedAnimations);
            if (animView != null)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    animView.Filter = null;
                }
                else
                {
                    animView.Filter = obj =>
                    {
                        if (obj is AnimationClipCatalogItem item)
                        {
                            return item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || (!string.IsNullOrEmpty(item.VfxSummary) && item.VfxSummary.Contains(query, StringComparison.OrdinalIgnoreCase));
                        }
                        return false;
                    };
                }
            }

            var view = CollectionViewSource.GetDefaultView(_model.Systems);
            if (view != null)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    view.Filter = null;
                }
                else
                {
                    view.Filter = obj =>
                    {
                        if (obj is VfxSystemDiagnosticItem item)
                        {
                            return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                        }
                        return false;
                    };
                }
            }
        }

        #endregion

        #region Viewport Playback Control Events

        private bool HasSelectedSystemReady()
            => _model.SelectedSystem != null &&
               _pendingSystem == null &&
               ReferenceEquals(_inspectedSystem, _model.SelectedSystem) &&
               _vfxRenderer?.ActiveSystem != null;

        private bool HasSelectedSpellReady()
            => _model.SelectedSpell != null &&
               _activeSpellPlan?.Availability == VfxSpellAvailability.Supported &&
               _vfxRenderer?.ActiveSystem != null;

        private bool HasSelectedAnimationReady()
            => _model.IsAnimationMode &&
               _model.SelectedAnimation != null &&
               SameAnimationClip(_activeAnimationClip, _model.SelectedAnimation) &&
               _activeAnimationClip?.AnimationAsset != null &&
               _vfxRenderer?.ActiveSystem != null;

        private bool TryPlaySelectedTimedPreview(bool restartWhenPlaying)
        {
            bool ended = _model.CurrentTime >= _model.TotalDuration;
            bool restart = ended || (restartWhenPlaying && _model.IsPlaying);
            if (_model.SelectedMapNode?.Kind == MapBrowserNodeKind.Clip &&
                _model.SelectedMapNode.Payload is MapCharacterClipSelection mapClip)
            {
                if (!HasSelectedMapClipReady())
                    _ = PlayMapCharacterClipAsync(mapClip);
                else
                {
                    if (restart)
                    {
                        _model.CurrentTime = 0d;
                        _vfxRenderer?.Seek(0d);
                        SyncMapCharacterClipTime(0d);
                    }
                    _model.IsPlaying = true;
                    _vfxRenderer?.Play();
                }
                return true;
            }

            if (_model.SelectedSpell != null)
            {
                if (!HasSelectedSpellReady())
                    RequestSpellPreview(_model.SelectedSpell);
                else
                    ResumeTimedPreview(restart);
                return true;
            }

            if (_model.IsAnimationMode && _model.SelectedAnimation != null)
            {
                if (!HasSelectedAnimationReady())
                    _ = PlaySelectedAnimationAsync(_model.SelectedAnimation);
                else
                    ResumeTimedPreview(restart);
                return true;
            }

            return false;
        }

        private void ResumeTimedPreview(bool restartFromBeginning)
        {
            if (restartFromBeginning)
            {
                _model.CurrentTime = 0d;
                SyncMapCharacterClipTime(0d);
                _vfxRenderer?.Seek(0d);
            }
            _model.IsPlaying = true;
            _vfxRenderer?.Play();
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (TryPlaySelectedTimedPreview(restartWhenPlaying: true))
                return;

            if (_model.SelectedSystem != null)
            {
                if (!HasSelectedSystemReady())
                {
                    RequestSystemInspection(_model.SelectedSystem);
                }
                else
                {
                    _vfxRenderer.Stop();
                    _model.CurrentTime = 0;
                    _vfxRenderer.Seek(0);
                    _vfxRenderer.Play();
                    _model.IsPlaying = true;
                }
            }
        }

        private void StopResume_Click(object sender, RoutedEventArgs e)
        {
            if (_model.IsPlaying)
            {
                _vfxRenderer?.Pause();
                _model.IsPlaying = false;
            }
            else
            {
                if (TryPlaySelectedTimedPreview(restartWhenPlaying: false))
                    return;

                if (_model.SelectedSystem != null)
                {
                    if (!HasSelectedSystemReady())
                    {
                        RequestSystemInspection(_model.SelectedSystem);
                    }
                    else if (_vfxRenderer.PlaybackTime >= _model.TotalDuration)
                    {
                        _vfxRenderer.Stop();
                        _vfxRenderer.Play();
                        _model.IsPlaying = true;
                    }
                    else
                    {
                        _vfxRenderer.Play();
                        _model.IsPlaying = true;
                    }
                }
            }
        }

        private bool _isUserSeeking;

        private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_model.IsPlaying || _isUserSeeking)
            {
                _model.CurrentTime = e.NewValue;
                SyncMapCharacterClipTime(e.NewValue);
                _vfxRenderer?.Seek(e.NewValue);
            }
        }

        private void TimeSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = true;
        }

        private void TimeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = false;
        }

        private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_model == null) return;
            if (SpeedComboBox?.SelectedItem is ComboBoxItem item &&
                float.TryParse(item.Tag?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out float speed))
            {
                SetPlaybackSpeed(speed, updateControl: false);
            }
        }

        private void ChancePinSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isUpdatingChancePinControls ||
                _model?.HasStandaloneSystem != true ||
                _vfxRenderer == null ||
                ChancePinSlider == null)
            {
                return;
            }

            // Clicking the slider at its neutral 0.50 position must still create a pin even when
            // ValueChanged does not fire because the thumb was already resting there.
            ApplyPinnedBirthChance((float)ChancePinSlider.Value);
        }

        private void ChancePinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingChancePinControls ||
                !IsLoaded ||
                _model?.HasStandaloneSystem != true ||
                _vfxRenderer == null)
            {
                return;
            }

            ApplyPinnedBirthChance((float)e.NewValue);
        }

        private void ChancePinClear_Click(object sender, RoutedEventArgs e)
        {
            if (_vfxRenderer == null) return;
            _vfxRenderer.SetPinnedBirthChance(null);

            _isUpdatingChancePinControls = true;
            try
            {
                if (ChancePinSlider != null)
                {
                    ChancePinSlider.Value = 0.5d;
                    ChancePinSlider.Opacity = 0.5d;
                }
                if (ChancePinValueText != null) ChancePinValueText.Text = string.Empty;
                if (ChancePinClearButton != null) ChancePinClearButton.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _isUpdatingChancePinControls = false;
            }
        }

        private void ApplyPinnedBirthChance(float chance)
        {
            chance = Math.Clamp(chance, 0f, 1f);
            _vfxRenderer?.SetPinnedBirthChance(chance);
            if (ChancePinSlider != null) ChancePinSlider.Opacity = 1d;
            if (ChancePinValueText != null)
                ChancePinValueText.Text = chance.ToString("F2", CultureInfo.InvariantCulture);
            if (ChancePinClearButton != null) ChancePinClearButton.Visibility = Visibility.Visible;
        }

        private void SetPlaybackSpeed(double speed, bool updateControl = true)
        {
            float normalized = VfxRenderSession.NormalizePlaybackSpeed(speed);
            _model.Speed = normalized;
            if (_vfxRenderer?.ActiveSystem != null)
                _vfxRenderer.ActiveSystem.Speed = normalized;

            if (!updateControl || SpeedComboBox == null) return;
            for (int index = 0; index < SpeedComboBox.Items.Count; index++)
            {
                if (SpeedComboBox.Items[index] is not ComboBoxItem item ||
                    !double.TryParse(
                        item.Tag?.ToString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double candidate))
                {
                    continue;
                }

                if (Math.Abs(candidate - normalized) <= 1e-6)
                {
                    SpeedComboBox.SelectedIndex = index;
                    break;
                }
            }
        }

        internal static float ResolveSimulationFrameDelta(TimeSpan delta, bool discard)
            => discard ? 0f : (float)Math.Max(0d, delta.TotalSeconds);

        internal static double PlaybackSpeedDetent(double speed, int direction)
        {
            if (direction >= 0)
            {
                foreach (double detent in PlaybackSpeedDetents)
                    if (detent > speed + 1e-6) return detent;
                return PlaybackSpeedDetents[^1];
            }

            for (int index = PlaybackSpeedDetents.Length - 1; index >= 0; index--)
                if (PlaybackSpeedDetents[index] < speed - 1e-6) return PlaybackSpeedDetents[index];
            return PlaybackSpeedDetents[0];
        }

        private void BgMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_model == null) return;
            if (BgComboBox?.SelectedItem is ComboBoxItem item)
            {
                _model.BgMode = item.Content?.ToString() ?? "Dark";
            }
        }

        private void CopyDebugReport_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedSystem == null)
            {
                MessageBox.Show("Selecciona primero un sistema VFX de la lista para generar el reporte de depuración.", "VFX Inspector", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# INFORME COMPLETO DE DIAGNÓSTICO DE VISUALIZACIÓN VFX");
            sb.AppendLine($"Fecha/Hora: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Sistema VFX: {_model.SelectedSystem.Name}");
            sb.AppendLine($"Ruta Partícula: {_model.SelectedSystem.Definition?.ParticlePath ?? "N/A"}");
            sb.AppendLine($"Hash de Ruta: 0x{_model.SelectedSystem.Definition?.PathHash ?? 0:X8}");
            sb.AppendLine($"Duración Calculada: {_model.TotalDuration:F2} s");
            sb.AppendLine($"Emisores Totales: {_model.Emitters.Count}");
            sb.AppendLine($"Texturas Cargadas: {_model.Textures.Count}");
            sb.AppendLine();

            sb.AppendLine("## EMISORES Y PROPIEDADES DE RENDERIZADO");
            int idx = 1;
            foreach (var emitter in _model.Emitters)
            {
                var d = emitter.EmitterDef;
                sb.AppendLine($"### Emisor {idx++}: {emitter.Name}");
                sb.AppendLine($"  - Estado: {(emitter.IsEnabled ? "ACTIVO" : "DESACTIVADO")}");
                sb.AppendLine($"  - Modo Mezcla (BlendMode): {emitter.BlendMode} (Valor Original BIN: {d?.BlendMode})");
                sb.AppendLine($"  - Tipo Primitiva: {(d?.IsMeshPrimitive == true ? "MALLA 3D (.scb/.sco)" : (d?.IsGroundLayer == true ? "CAPA SUELO 3D" : "QUAD BILLBOARD 2D"))}");
                sb.AppendLine($"  - Malla 3D Ruta: {emitter.MeshPath} (Estado GPU: {emitter.MeshStatus})");
                sb.AppendLine($"  - Textura Principal: {emitter.TexturePath} (Estado GPU: {emitter.TextureStatus})");
                sb.AppendLine($"  - Textura Multiplicadora: {d?.TextureMultPath ?? "N/A"}");
                sb.AppendLine($"  - Textura Color Lookup: {d?.ParticleColorTexturePath ?? "N/A"}");
                sb.AppendLine($"  - Textura Paleta: {d?.PaletteDefinition?.PaletteTexturePath ?? "N/A"}");
                sb.AppendLine($"  - Rejilla Atlas (TexDiv): {emitter.TexDiv}");
                if (d != null)
                {
                    var bs = d.BirthScale.Constant;
                    sb.AppendLine($"  - Escala Inicial (BirthScale): X={bs.X:F1}, Y={bs.Y:F1}, Z={bs.Z:F1}");
                    sb.AppendLine($"  - Bucle Infinito (IsLoop): {d.IsLoop}");
                    sb.AppendLine($"  - Emisor Único (IsSingleParticle): {d.IsSingleParticle}");
                    sb.AppendLine($"  - Flags Orientación: OrientadoDirección={d.IsDirectionOriented}, CuadriláteroArbitrario={d.IsArbitraryQuad}, Terreno={d.IsFollowingTerrain}, Suelo={d.IsGroundLayer}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## TEXTURAS EN MEMORIA GPU");
            foreach (var tex in _model.Textures)
            {
                sb.AppendLine($"  - {tex.AuthoredPath} => [{tex.Width}x{tex.Height}] ({tex.Status})");
            }

            string reportText = sb.ToString();
            Clipboard.SetText(reportText);
            _model.LogMessages.Add("[DEBUG EXPORT] Reporte completo de depuración copiado al Portapapeles.");
            MessageBox.Show("¡Reporte de Depuración Completo copiado al Portapapeles de Windows!\n\nPuedes pegarlo directamente en la conversación para que analicemos cualquier anomalía visual.", "VFX Inspector", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

    }
}
