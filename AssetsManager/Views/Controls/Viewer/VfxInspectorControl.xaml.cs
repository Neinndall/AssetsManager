using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Vector = System.Windows.Vector;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;
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
        private IReadOnlyList<VfxAbilityComposition> _abilityCompositions = Array.Empty<VfxAbilityComposition>();
        private bool _isCleanedUp;
        private bool _isActive;
        private bool _isGlStarted;
        private VfxSystemDiagnosticItem _pendingSystem;
        private VfxSystemDiagnosticItem _inspectedSystem;

        /// <summary>Injected by the host (ViewerWindow) following the peer-controls pattern.</summary>
        public LogService LogService { get; set; }

        /// <summary>Injected by the host and owned by ViewerWindow.</summary>
        public VfxLoadingService VfxLoadingService { get; set; }

        // VFX Studio dedicated camera framing (elevated 3/4 perspective looking down at origin Y=0)
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
            InitializeComponent();
            DataContext = _model;
            _model.PropertyChanged += OnModelPropertyChanged;

            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
        }

        private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VfxInspectorModel.SelectedSystem))
            {
                RequestSystemInspection(_model.SelectedSystem);
            }
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            if (_isActive)
            {
                EnsureOpenGlStarted();
            }
        }

        /// <summary>
        /// Activates the VFX viewport when its host view becomes visible.
        /// </summary>
        public void Activate()
        {
            if (_isCleanedUp) return;

            _isActive = true;
            if (!HasSelectedSystemReady())
            {
                RequestSystemInspection(_model.SelectedSystem);
            }

            if (IsLoaded)
            {
                EnsureOpenGlStarted();
            }
        }

        /// <summary>
        /// Pauses VFX work while the host view is hidden without destroying reusable GPU state.
        /// </summary>
        public void Deactivate()
        {
            _isActive = false;
            _pendingSystem = null;
            _model.IsPlaying = false;
            _vfxRenderer?.Pause();
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

            Deactivate();
            _isCleanedUp = true;
            _model.PropertyChanged -= OnModelPropertyChanged;

            var cameraController = _cameraController;
            _cameraController = null;
            RunReleaseStep(nameof(CustomCameraController), () => cameraController?.Dispose());

            var vfxRenderer = _vfxRenderer;
            _vfxRenderer = null;
            RunReleaseStep(nameof(VfxRenderSession), () => vfxRenderer?.Dispose(), gpuBound: true);

            var gridRenderer = _gridRenderer;
            _gridRenderer = null;
            RunReleaseStep(nameof(GridRenderer), () => gridRenderer?.Dispose(), gpuBound: true);

            var gl = _gl;
            _gl = null;
            RunReleaseStep("OpenGL API", () => gl?.Dispose());

            RunReleaseStep(nameof(OpenTkControl), OpenTkControl.Dispose, gpuBound: true);
            _isGlStarted = false;

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
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to release VFX Studio {componentName}.");
            }
        }

        private VfxLoadingService RequireLoadingService()
            => VfxLoadingService ?? throw new InvalidOperationException(
                "VfxInspectorControl requires a VfxLoadingService from ViewerWindow.");

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

        private GridRenderer _gridRenderer;

        private void EnsureVfxRenderSession()
        {
            if (_vfxRenderer != null || _gl == null || !_isActive || _isCleanedUp) return;

            var renderer = new VfxRenderSession(LogService, RequireLoadingService());
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

            _inspectedSystem = null;
            _pendingSystem = systemItem;
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
                if (_gridRenderer == null)
                {
                    _gridRenderer = new GridRenderer();
                    _gridRenderer.Initialize(_gl, false);
                }

                if (_cameraController == null)
                {
                    _cameraController = new CustomCameraController(_dummyViewport, OpenTkControl);
                }

                _model.LogMessages.Add("[GL] OpenGL viewport, camera controller & 3D grid initialized successfully.");
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
            if (_gl == null || !_isActive || !IsVisible) return;

            float dt = (float)delta.TotalSeconds;
            if (dt <= 0 || dt > 0.5f) dt = 1f / 60f;

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

            // Build View/Projection matrices directly from CustomCameraController's PerspectiveCamera
            var camera = _dummyViewport.Camera as PerspectiveCamera;
            if (camera == null) return;

            var eye = new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z);
            var lookDir = new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z);
            var target = eye + lookDir;
            var up = new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z);
            var view = Matrix4x4.CreateLookAt(eye, target, up);

            float fovRadians = (float)(camera.FieldOfView * (Math.PI / 180.0));
            float aspect = (float)Math.Max(1, OpenTkControl.ActualWidth) / (float)Math.Max(1, OpenTkControl.ActualHeight);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(fovRadians, aspect, 1f, 10000f);
            var viewProj = view * proj;

            // OpenTK has the current context here, so deferred session creation and resource
            // preparation are safe even when WPF selected the system before the GL control was ready.
            TryInspectPendingSystem();

            // Render 3D Ground Grid (matching main viewer)
            _gridRenderer?.Render(viewProj);

            if (_vfxRenderer == null)
            {
                _model.LiveParticleCount = 0;
                return;
            }

            _vfxRenderer.SetViewportSize(OpenTkControl.ActualWidth, OpenTkControl.ActualHeight);
            if (_model.IsPlaying && !_isUserSeeking)
            {
                _vfxRenderer.Update(dt);
                if (_vfxRenderer.ActiveSystem != null)
                    _model.CurrentTime = _vfxRenderer.ActiveSystem.CurrentTime;
                if (ShouldRestartPreview(
                    _model.IsPreviewLoopEnabled,
                    _model.CurrentTime,
                    _model.ActiveLoopDuration))
                {
                    _model.CurrentTime = 0;
                    _vfxRenderer.Seek(0);
                    _vfxRenderer.Play();
                }
                else if (_model.CurrentTime >= _model.TotalDuration)
                    _model.IsPlaying = false;
            }
            _vfxRenderer.Render(viewProj, view);

            _model.LiveParticleCount = _vfxRenderer.LiveParticleCount;
            Dispatcher.InvokeAsync(UpdatePlayheadPosition);
        }

        #endregion

        #region Camera Control

        public void ResetCamera()
        {
            _cameraController?.FlyTo(
                VfxCameraPosition,
                VfxCameraTarget - VfxCameraPosition,
                VfxCameraUpDirection);
        }

        #endregion

        #region Directory & BIN Scanning

        private void BrowseRoot_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select asset directory root"
            };

            if (dialog.ShowDialog() == true)
            {
                _model.RootPath = dialog.FolderName;
                ScanRootDirectory(dialog.FolderName);
            }
        }

        private void ReloadRoot_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_model.RootPath))
                ScanRootDirectory(_model.RootPath);
        }

        private void RootPathTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_model.RootPath))
                ScanRootDirectory(_model.RootPath);
        }

        private void ScanRootDirectory(string rootFolder)
        {
            if (!Directory.Exists(rootFolder)) return;

            try
            {
                _model.DetectedSkins.Clear();
                _model.Systems.Clear();
                _abilityCompositions = Array.Empty<VfxAbilityComposition>();

                var binFiles = Directory.GetFiles(rootFolder, "*.bin", SearchOption.AllDirectories);
                
                // Filter strictly for skin BINs named skin0.bin, skin1.bin, skin10.bin, etc. inside /skins/ folder
                var skinBinFiles = binFiles
                    .Where(b => b.Contains($"{Path.DirectorySeparatorChar}skins{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                                b.Contains("/skins/", StringComparison.OrdinalIgnoreCase))
                    .Where(b => Path.GetFileNameWithoutExtension(b).StartsWith("skin", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var skinItems = new List<VfxSkinItem>();

                foreach (var binPath in skinBinFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(binPath);
                    int skinIndex = ExtractSkinIndex(fileName);
                    string displayName = skinIndex == 0 ? "Skin Base (skin0)" : (skinIndex > 0 ? $"Skin {skinIndex} ({fileName})" : fileName);

                    skinItems.Add(new VfxSkinItem
                    {
                        DisplayName = displayName,
                        BinPath = binPath,
                        SkinIndex = skinIndex < 0 ? 999 : skinIndex
                    });
                }

                // Sort by skin index (0, 1, 2...)
                foreach (var item in skinItems.OrderBy(s => s.SkinIndex))
                {
                    _model.DetectedSkins.Add(item);
                }

                // Fallback: If no skin*.bin files were found (e.g. non-standard folder), show general BIN files cleanly
                if (_model.DetectedSkins.Count == 0)
                {
                    foreach (var binPath in binFiles.OrderBy(b => b))
                    {
                        string fileName = Path.GetFileName(binPath);
                        _model.DetectedSkins.Add(new VfxSkinItem
                        {
                            DisplayName = fileName,
                            BinPath = binPath,
                            SkinIndex = 0
                        });
                    }
                }

                // Auto-select Skin Base (index 0) or first skin item
                var preferredSkin = _model.DetectedSkins.FirstOrDefault(s => s.SkinIndex == 0) 
                                 ?? _model.DetectedSkins.FirstOrDefault();

                if (preferredSkin != null)
                {
                    _model.SelectedSkin = preferredSkin;
                }

                _model.StatusText = $"Scanned {_model.DetectedSkins.Count} skins in {Path.GetFileName(rootFolder)}.";
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to scan ROOT directory");
            }
        }

        private static int ExtractSkinIndex(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return -1;
            if (fileName.StartsWith("skin", StringComparison.OrdinalIgnoreCase))
            {
                string numPart = fileName.Substring(4);
                if (int.TryParse(numPart, out int skinId)) return skinId;
            }
            string digits = new string(fileName.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int val) ? val : -1;
        }

        private void BinSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_model.SelectedSkin != null)
            {
                LoadBinFile(_model.SelectedSkin.BinPath);
            }
        }

        private void LoadBinFile(string binFilePath)
        {
            if (!File.Exists(binFilePath)) return;

            try
            {
                _model.SelectedSystem = null;
                _model.Systems.Clear();
                _abilityCompositions = Array.Empty<VfxAbilityComposition>();
                _model.LogMessages.Add($"[BIN] Loading BIN definitions from: {Path.GetFileName(binFilePath)}");

                _activeBundle = RequireLoadingService().Load(binFilePath, LogService);

                foreach (var (hash, sysDef) in _activeBundle.Systems)
                {
                    string name = sysDef.Name ?? $"VFX_0x{hash:X8}";

                    // Filter out internal MATH, script, helper, dummy and 0-emitter non-visual systems
                    if (name.StartsWith("MATH_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Math_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("script_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("helper_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("dummy_", StringComparison.OrdinalIgnoreCase) ||
                        !HasPlayableEmitters(sysDef))
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

                _abilityCompositions = VfxAbilityCompositionBuilder.BuildAll(
                    _activeBundle.EventSequences.Values,
                    _activeBundle.Systems,
                    _activeBundle.ResourceMap);

                _model.LogMessages.Add($"[BIN SUCCESS] Extracted {_model.Systems.Count} VFX systems.");
                _model.StatusText = $"Loaded {_model.Systems.Count} systems from {Path.GetFileName(binFilePath)}.";

                if (_model.Systems.Count > 0)
                {
                    _model.SelectedSystem = _model.Systems.FirstOrDefault(s => s.Name.Contains("Samira", StringComparison.OrdinalIgnoreCase))
                                          ?? _model.Systems.First();
                }
            }
            catch (Exception ex)
            {
                _abilityCompositions = Array.Empty<VfxAbilityComposition>();
                _model.LogMessages.Add($"[ERROR] Failed to load BIN: {ex.Message}");
            }
        }

        internal static bool HasPlayableEmitters(VfxSystemDefinition definition)
            => definition?.Emitters.Any(emitter => !emitter.Disabled) == true;

        internal static double CalculatePlaybackDuration(VfxSystemDefinition definition)
            => VfxDurationCalculator.Calculate(definition);

        #endregion

        #region System & Emitter Diagnostics

        private void InspectSystem(VfxSystemDiagnosticItem systemItem)
        {
            var def = systemItem.Definition;
            if (def == null) return;

            _model.Emitters.Clear();
            _model.Textures.Clear();
            _model.Meshes.Clear();
            _model.LogMessages.Add($"[INSPECT] Selected VFX: {systemItem.Name} (Hash: 0x{systemItem.PathHash:X8})");

            string searchDir = _model.RootPath;
            if (!string.IsNullOrEmpty(searchDir) && File.Exists(searchDir))
            {
                searchDir = Path.GetDirectoryName(searchDir) ?? searchDir;
            }

            int playbackSeed = HashCode.Combine(def.PathHash, systemItem.Name);
            double playbackDuration = VfxDurationCalculator.CalculatePreview(
                def,
                playbackSeed,
                _activeBundle?.Systems,
                _activeBundle?.ResourceMap);

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
                TotalDuration = playbackDuration,
                Speed = _model.Speed
            };

            _model.CurrentTime = 0;
            string playbackContext = "standalone system";
            IReadOnlyList<VfxAbilityComposition> linkedCompositions = VfxAbilityCompositionBuilder.FindContainingSystem(
                systemItem.PathHash,
                _abilityCompositions);
            bool configuredComposition = false;
            if (linkedCompositions.Count == 1 && _activeBundle is not null && _vfxRenderer is not null)
            {
                VfxAbilityComposition composition = linkedCompositions[0];
                configuredComposition = _vfxRenderer.SetAbilityComposition(
                    composition,
                    _activeBundle.Systems,
                    _activeBundle.ResourceMap,
                    searchDir,
                    HashCode.Combine(playbackSeed, composition.SequencePathHash),
                    _activeBundle.OwnerSceneContext);
                if (configuredComposition)
                {
                    playbackDuration = _vfxRenderer.ActiveSystem.TotalDuration;
                    playbackContext = $"authored sequence 0x{composition.SequencePathHash:X8}";
                    _model.LogMessages.Add(
                        $"[COMPOSITION AUTO] {systemItem.Name} belongs to one authored sequence; " +
                        $"playing all {composition.ResolvedCount} resolved events.");
                }
            }
            else if (linkedCompositions.Count > 1)
            {
                _model.LogMessages.Add(
                    $"[COMPOSITION AMBIGUOUS] {systemItem.Name} belongs to {linkedCompositions.Count} authored sequences; " +
                    "standalone playback retained because no unique authored sequence can be selected automatically.");
            }
            else
            {
                _model.LogMessages.Add(
                    $"[COMPOSITION STANDALONE] {systemItem.Name} has no explicit ParticleEventData composition reference.");
            }

            if (!configuredComposition)
                _vfxRenderer?.SetVfxSystem(systemModel);

            double timelineMax = ResolveTimelineDuration(playbackDuration);
            _model.ActiveLoopDuration = double.IsFinite(playbackDuration) && playbackDuration > 0
                ? playbackDuration
                : timelineMax;
            _model.TotalDuration = timelineMax;
            _vfxRenderer?.Play();
            _model.IsPlaying = true;

            // 2. Audit Emitters
            for (int emitterIndex = 0; emitterIndex < def.Emitters.Count; emitterIndex++)
            {
                var emitter = def.Emitters[emitterIndex];
                if (emitter.Disabled) continue;

                string texPath = emitter.TexturePath;
                string meshPath = emitter.MeshPath;

                BitmapSource tex = string.IsNullOrEmpty(texPath) ? null : RequireLoadingService().ResolveTexture(texPath, searchDir);
                var mesh = emitter.IsMeshPrimitive
                    ? RequireLoadingService().ResolveMesh(meshPath, searchDir)
                    : null;

                (string textureStatus, Brush textureStatusBrush) = DescribeTextureStatus(emitter, tex);

                var emitterDiagnostic = new VfxEmitterDiagnosticItem
                {
                    Name = emitter.Name ?? "Emitter",
                    SourceOrder = emitterIndex,
                    IsEnabled = true,
                    EmitterDef = emitter,
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

            UpdateTimelineTrackMetrics();
            UpdatePlayheadPosition();

            _model.StatusText = $"{systemItem.Name} · {playbackContext}.";
            _inspectedSystem = systemItem;
        }

        #region Timeline Deck Mechanics

        private void TracksCanvasContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTimelineTrackMetrics();
            UpdatePlayheadPosition();
        }

        private void UpdateTimelineTrackMetrics()
        {
            if (_model == null || TracksCanvasContainer == null) return;
            double availableWidth = TracksCanvasContainer.ActualWidth;
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;

            Brush[] palette = new Brush[]
            {
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00E676")), // Teal / Green
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00D1FF")), // Cyan
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5C85FF")), // Slate Blue
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B35")), // Coral / Orange
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD600")), // Amber / Gold
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A855F7")), // Purple
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EC4899"))  // Pink
            };

            int idx = 1;
            foreach (var emitter in _model.Emitters)
            {
                emitter.IndexNumber = idx;
                emitter.TrackBrush = palette[(idx - 1) % palette.Length];

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

                // Yellow Keyframe Marker Dot for Emission Delay (Matching linea_tiempo.png)
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
            double availableWidth = TracksCanvasContainer.ActualWidth;
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;
            double ratio = Math.Clamp(_model.CurrentTime / totalDur, 0.0, 1.0);
            double posX = ratio * availableWidth;

            PlayheadLine.X1 = posX;
            PlayheadLine.X2 = posX;
            Canvas.SetLeft(PlayheadHandle, posX - 4);

            if (LoopBoundaryLine != null && LoopBoundaryHandle != null)
            {
                double loopDur = _model.ActiveLoopDuration > 0 ? _model.ActiveLoopDuration : totalDur;
                double loopRatio = Math.Clamp(loopDur / totalDur, 0.0, 1.0);
                double loopPosX = loopRatio * availableWidth;

                LoopBoundaryLine.X1 = loopPosX;
                LoopBoundaryLine.X2 = loopPosX;
                Canvas.SetLeft(LoopBoundaryHandle, loopPosX - 7);
            }
        }

        internal static bool ShouldRestartPreview(bool enabled, double currentTime, double boundary)
            => enabled && boundary > 0 && currentTime >= boundary;

        internal static double ResolveTimelineDuration(double playbackDuration)
            => double.IsFinite(playbackDuration) && playbackDuration > 0
                ? Math.Max(0.05, playbackDuration)
                : 10.0;

        private bool _isDraggingLoopBoundary;

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

        private void UpdateLoopBoundaryFromMouse(double mouseX)
        {
            if (_model == null || TracksCanvasContainer == null) return;
            double availableWidth = TracksCanvasContainer.ActualWidth;
            if (availableWidth <= 0) return;

            double totalDur = _model.TotalDuration > 0 ? _model.TotalDuration : 3.0;
            double ratio = Math.Clamp(mouseX / availableWidth, 0.02, 1.0);
            double newLoopDur = Math.Round(ratio * totalDur, 2);

            _model.ActiveLoopDuration = Math.Max(0.05, newLoopDur);
            _model.IsPreviewLoopEnabled = true;
            UpdatePlayheadPosition();
        }

        private bool _isTimelineDragging;

        private void TimelineGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingLoopBoundary) return;
            if (e.OriginalSource is FrameworkElement fe && (fe == LoopBoundaryHandle || fe == LoopBoundaryCanvas || fe == LoopBoundaryLine)) return;
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
            double availableWidth = TracksCanvasContainer.ActualWidth;
            if (availableWidth <= 0 || _model == null) return;

            double ratio = Math.Clamp(mouseX / availableWidth, 0.0, 1.0);
            double seekTime = ratio * _model.TotalDuration;

            _model.CurrentTime = seekTime;
            _vfxRenderer?.Seek(seekTime);
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
            var view = CollectionViewSource.GetDefaultView(_model.Systems);
            if (view == null) return;

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

        #endregion

        #region Viewport Playback Control Events

        private bool HasSelectedSystemReady()
            => _model.SelectedSystem != null &&
               _pendingSystem == null &&
               ReferenceEquals(_inspectedSystem, _model.SelectedSystem) &&
               _vfxRenderer?.ActiveSystem != null;

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedSystem != null)
            {
                if (!HasSelectedSystemReady())
                {
                    RequestSystemInspection(_model.SelectedSystem);
                }
                else
                {
                    _vfxRenderer.Stop();
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
                if (_model.SelectedSystem != null)
                {
                    if (!HasSelectedSystemReady())
                    {
                        RequestSystemInspection(_model.SelectedSystem);
                    }
                    else if (_vfxRenderer.ActiveSystem.CurrentTime >= _model.TotalDuration)
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
            if (_vfxRenderer?.ActiveSystem == null) return;
            if (!_model.IsPlaying || _isUserSeeking)
            {
                _vfxRenderer.Seek(e.NewValue);
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
                _model.Speed = speed;
                if (_vfxRenderer?.ActiveSystem != null)
                {
                    _vfxRenderer.ActiveSystem.Speed = speed;
                }
            }
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
                    sb.AppendLine($"  - Usa Relación Aspecto (UseTextureAspect): {d.UseTextureAspect}");
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

        #region Table Last Column Auto-Stretch (Hash Lab Technique)

        private bool _isUpdatingTableColumns;

        private void TableListView_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListView listView)
            {
                Dispatcher.BeginInvoke(() => UpdateLastColumnWidth(listView), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void TableListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ListView listView)
            {
                UpdateLastColumnWidth(listView);
            }
        }

        private void TableListView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is ListView listView && e.NewValue is true)
            {
                Dispatcher.BeginInvoke(() => UpdateLastColumnWidth(listView), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void TableColumnHeader_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isUpdatingTableColumns && sender is GridViewColumnHeader header)
            {
                var listView = FindAncestor<ListView>(header);
                if (listView != null)
                {
                    UpdateLastColumnWidth(listView);
                }
            }
        }

        private void UpdateLastColumnWidth(ListView listView)
        {
            if (_isUpdatingTableColumns || listView?.View is not GridView gridView || gridView.Columns.Count < 2)
                return;

            var scrollViewer = FindScrollViewer(listView);
            if (scrollViewer == null || scrollViewer.ViewportWidth <= 0)
                return;

            double precedingWidth = 0;
            for (int i = 0; i < gridView.Columns.Count - 1; i++)
            {
                precedingWidth += gridView.Columns[i].ActualWidth;
            }

            double lastWidth = Math.Max(80, scrollViewer.ViewportWidth - precedingWidth);
            var lastColumn = gridView.Columns[gridView.Columns.Count - 1];

            if (Math.Abs(lastColumn.Width - lastWidth) < 0.5)
                return;

            _isUpdatingTableColumns = true;
            try
            {
                lastColumn.Width = lastWidth;
            }
            finally
            {
                _isUpdatingTableColumns = false;
            }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null && current is not T)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            return current as T;
        }

        private static ScrollViewer FindScrollViewer(DependencyObject element)
        {
            if (element is ScrollViewer scrollViewer) return scrollViewer;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var result = FindScrollViewer(VisualTreeHelper.GetChild(element, i));
                if (result != null) return result;
            }
            return null;
        }

        #endregion
    }
}
