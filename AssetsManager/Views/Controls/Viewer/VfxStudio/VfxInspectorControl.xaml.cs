using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;

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
        private string _championSknPath;
        private string _championSearchDir;
        // GearSkinUpgrade whose skinMeshProperties the installed model was loaded with; 0 for the skin's own.
        private uint _championFormPathHash;
        // Like LTK's Skin preview, a freshly installed Character is not drawn until the clip it opens
        // on has its pose, instead of flashing the bind pose while the ANM is prepared.
        private bool _championAwaitingFirstPose;
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
        // The map geometry's depth before structures and characters draw, where planar projections land.
        private AssetsManager.Services.Viewer.Rendering.Core.GlSceneCapture _terrainDepthCapture;
        private uint _frameTerrainDepth;
        private uint _frameTerrainWidth;
        private uint _frameTerrainHeight;
        private FxaaPostEffectsRenderer _fxaaRenderer;
        private CheckerboardBackgroundRenderer _checkerboardBackgroundRenderer;
        private SmaaPostEffectsRenderer _smaaRenderer;
        private SkyRenderer _skyRenderer;
        private VfxCubeMapData _genericSkyCube;
        private VfxCubeMapData _mapSkyCube;
        private bool _skyCubeDirty;
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
        private PreviewSurfaceRenderer _previewSurfaceRenderer;
        private PerspectiveCamera _previewPerspectiveCamera;
        private OrthographicCamera _previewOrthographicCamera;
        private bool _isLoadingPreviewPreferences;
        private bool _suppressCameraPresetFit;
        private bool _deferOrbitProjectionSwap;

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
            _model.MapVisibilityRequested += OnMapVisibilityRequested;

            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
            IsVisibleChanged += OnControlVisibilityChanged;
            PreviewKeyDown += RunKeys_PreviewKeyDown;
        }

        private VfxSkinItem _browserSkin;

    }
}
