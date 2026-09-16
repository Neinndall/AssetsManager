using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Utils;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Views.Models.Dialogs.Controls;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Views.Dialogs
{
    public partial class SknDiffWindow : HudWindow
    {
        private readonly SknLoadingService _sknLoadingService;
        private readonly LogService _logService;
        
        private SceneModel _oldScene;
        private SceneModel _newScene;
        private SceneModel _combinedNewScene;
        private readonly List<MeshPartDiffItem> _partItems = new();
        private bool _isBulkUpdatingMeshParts;
        private readonly List<SceneModel> _diffOverlayScenes = new();
        private readonly Dictionary<SceneModel, string> _diffOverlayOwners = new();
        private readonly Dictionary<SceneModel, ViewerViewportControl> _diffOverlayViewports = new();
        private readonly Dictionary<string, MeshGeometry3D> _addedGeometryCache = new();
        private readonly Dictionary<string, MeshGeometry3D> _removedGeometryCache = new();

        private EventHandler _oldCameraChangedHandler;
        private EventHandler _newCameraChangedHandler;

        public LoadingDiffWindow LoadingWindow { get; set; }

        public SknDiffWindow(SknLoadingService sknLoadingService, LogService logService)
        {
            InitializeComponent();
            _sknLoadingService = sknLoadingService;
            _logService = logService;
            
            
            // Inject services into viewports
            OldViewport.LogService = logService;
            NewViewport.LogService = logService;

            // Expand the toolbars by default
            OldViewport.ViewModel.IsToolbarVisible = true;
            NewViewport.ViewModel.IsToolbarVisible = true;

            // Wire up diff toolbar events
            OldViewport.CombinedModeToggled += Viewport_CombinedModeToggled;
            NewViewport.CombinedModeToggled += Viewport_CombinedModeToggled;

            OldViewport.AutoRotateToggled += Viewport_AutoRotateToggled;
            NewViewport.AutoRotateToggled += Viewport_AutoRotateToggled;

            OldViewport.MeshPartsToggled += Viewport_MeshPartsToggled;
            NewViewport.MeshPartsToggled += Viewport_MeshPartsToggled;

            OldViewport.GhostModeToggled += Viewport_GhostModeToggled;
            NewViewport.GhostModeToggled += Viewport_GhostModeToggled;

            OldViewport.ResetCamerasClicked += Viewport_ResetCamerasClicked;
            NewViewport.ResetCamerasClicked += Viewport_ResetCamerasClicked;

            // Sync the expanding/collapsing of the toolbars between the two viewports
            OldViewport.ViewModel.PropertyChanged += ViewportViewModel_PropertyChanged;
            NewViewport.ViewModel.PropertyChanged += ViewportViewModel_PropertyChanged;

            // Sync cameras
            _oldCameraChangedHandler = (s, e) => SyncCameras(OldViewport, NewViewport);
            _newCameraChangedHandler = (s, e) => SyncCameras(NewViewport, OldViewport);
            OldViewport.Viewport3D.Camera.Changed += _oldCameraChangedHandler;
            NewViewport.Viewport3D.Camera.Changed += _newCameraChangedHandler;

            // Initial focus on origin and smooth loading handover
            Loaded += SknDiffWindow_Loaded;
        }

        private void SknDiffWindow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Restore explicit title-bar dragging only for the mesh analyzer dialog.
            Point position = e.GetPosition(this);
            if (position.Y < 0 || position.Y > 36)
                return;

            // Keep the standard title-bar double-click behavior for maximize / restore.
            if (e.ClickCount == 2 && ShowMaximizeButton)
            {
                if (WindowState == WindowState.Maximized)
                    SystemCommands.RestoreWindow(this);
                else
                    SystemCommands.MaximizeWindow(this);
            }
            else if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && WindowState == WindowState.Normal)
            {
                DragMove();
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // OLD owns the OpenGL context and NEW reuses it. GLWpfControl explicitly
            // supports ContextToUse for multiple controls, which keeps framebuffer and
            // scene resource creation on one context throughout the comparison window.
            OldViewport.EnsureOpenTkStarted();
            NewViewport.EnsureOpenTkStarted(OldViewport.OpenTkContext);
        }

        private async void SknDiffWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= SknDiffWindow_Loaded;
            ResetCharacterCameras();

            // Keep the loading handover tied to an actual OpenGL frame from both sides.
            OldViewport.RequestRender();
            NewViewport.RequestRender();
            try
            {
                await Task.WhenAll(
                        OldViewport.WaitForFirstRenderedFrameAsync(),
                        NewViewport.WaitForFirstRenderedFrameAsync())
                    .WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                // Keep the live scheduler running so the viewports can recover on a later frame.
                _logService.LogWarning("Initial OpenGL frame timed out; keeping the live render scheduler active.");
            }

            // Move focus to the analyzer only after both viewport surfaces had a chance to render.
            Activate();
            Focus();

            // Close the loading handover once the analyzer is ready for interaction.
            if (LoadingWindow != null)
            {
                try
                {
                    LoadingWindow.Close();
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, "Failed to close loading window after initial render.");
                }
                LoadingWindow = null;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // Close any remaining loading handover before releasing comparison resources.
            if (LoadingWindow != null)
            {
                try
                {
                    LoadingWindow.Close();
                }
                catch (Exception ex)
                {
                    // Report shutdown cleanup failures without interrupting the window close path.
                    _logService.LogError(ex, "Failed to close loading window during shutdown.");
                }
                LoadingWindow = null;
            }

            // Unwire diff toolbar events
            OldViewport.CombinedModeToggled -= Viewport_CombinedModeToggled;
            NewViewport.CombinedModeToggled -= Viewport_CombinedModeToggled;

            OldViewport.AutoRotateToggled -= Viewport_AutoRotateToggled;
            NewViewport.AutoRotateToggled -= Viewport_AutoRotateToggled;

            OldViewport.MeshPartsToggled -= Viewport_MeshPartsToggled;
            NewViewport.MeshPartsToggled -= Viewport_MeshPartsToggled;

            OldViewport.GhostModeToggled -= Viewport_GhostModeToggled;
            NewViewport.GhostModeToggled -= Viewport_GhostModeToggled;

            OldViewport.ResetCamerasClicked -= Viewport_ResetCamerasClicked;
            NewViewport.ResetCamerasClicked -= Viewport_ResetCamerasClicked;

            OldViewport.ViewModel.PropertyChanged -= ViewportViewModel_PropertyChanged;
            NewViewport.ViewModel.PropertyChanged -= ViewportViewModel_PropertyChanged;

            if (OldViewport.Viewport3D.Camera != null && _oldCameraChangedHandler != null)
                OldViewport.Viewport3D.Camera.Changed -= _oldCameraChangedHandler;
            if (NewViewport.Viewport3D.Camera != null && _newCameraChangedHandler != null)
                NewViewport.Viewport3D.Camera.Changed -= _newCameraChangedHandler;

            _oldCameraChangedHandler = null;
            _newCameraChangedHandler = null;

            // NEW borrows OLD's OpenGL context, so dispose the dependent viewport first.
            NewViewport.Cleanup();
            OldViewport.Cleanup();

            _partItems.Clear();
            _diffOverlayScenes.Clear();
            _diffOverlayOwners.Clear();
            _diffOverlayViewports.Clear();
            _addedGeometryCache.Clear();
            _removedGeometryCache.Clear();
            _oldScene = null;
            _newScene = null;
            _combinedNewScene = null;
        }

        private void ResetCharacterCameras()
        {
            if (NewViewport != null && _newScene != null)
            {
                NewViewport.ResetCamera(false);
                if (OldViewport != null)
                {
                    if (_oldScene != null)
                    {
                        OldViewport.ResetCamera(false);
                    }
                    SyncCameras(NewViewport, OldViewport);
                }
            }
            else if (OldViewport != null && _oldScene != null)
            {
                OldViewport.ResetCamera(false);
                if (NewViewport != null)
                {
                    SyncCameras(OldViewport, NewViewport);
                }
            }
            else
            {
                if (OldViewport != null) OldViewport.ResetCamera(false);
                if (NewViewport != null) NewViewport.ResetCamera(false);
            }
        }

        private static void UpdateViewportSceneDisplay(ViewerViewportControl viewport, SceneModel scene, string path)
        {
            int count = 0;
            if (scene != null)
            {
                count = 1;
            }
            string name = string.Empty;
            if (!string.IsNullOrEmpty(path))
            {
                name = Path.GetFileName(path);
            }
            viewport.ViewModel.UpdateSceneDisplay(count, name);
        }

        private bool _isSyncing = false;
        private void SyncCameras(ViewerViewportControl source, ViewerViewportControl target)
        {
            if (_isSyncing || source.Viewport3D.Camera == null || target.Viewport3D.Camera == null) return;
            
            _isSyncing = true;
            try
            {
                var srcCam = (ProjectionCamera)source.Viewport3D.Camera;
                var tgtCam = (ProjectionCamera)target.Viewport3D.Camera;
                
                tgtCam.Position = srcCam.Position;
                tgtCam.LookDirection = srcCam.LookDirection;
                tgtCam.UpDirection = srcCam.UpDirection;
                
                if (srcCam is PerspectiveCamera srcP && tgtCam is PerspectiveCamera tgtP)
                {
                    tgtP.FieldOfView = srcP.FieldOfView;
                }
                else if (srcCam is OrthographicCamera srcO && tgtCam is OrthographicCamera tgtO)
                {
                    tgtO.Width = srcO.Width;
                }
            }
            finally
            {
                _isSyncing = false;
            }
        }

        public async Task LoadAndDisplayDiffAsync(byte[] oldData, byte[] newData, string oldPath, string newPath, LoadingDiffWindow loadingWindow = null)
        {
            string oldDisplayName = "None";
            if (!string.IsNullOrEmpty(oldPath))
            {
                oldDisplayName = Path.GetFileName(oldPath);
            }
            OldFileNameLabel.Text = oldDisplayName;

            string newDisplayName = "None";
            if (!string.IsNullOrEmpty(newPath))
            {
                newDisplayName = Path.GetFileName(newPath);
            }
            NewFileNameLabel.Text = newDisplayName;

            OldViewport.ClearModels();
            NewViewport.ClearModels();
            _oldScene = null;
            _newScene = null;
            _combinedNewScene = null;
            _diffOverlayScenes.Clear();
            _diffOverlayOwners.Clear();
            _diffOverlayViewports.Clear();
            _addedGeometryCache.Clear();
            _removedGeometryCache.Clear();

            if (oldData != null)
            {
                if (loadingWindow != null) await loadingWindow.SetStateAndRenderAsync(DiffLoadingState.ParsingOldModel);
                _oldScene = await LoadModelFromBytesAsync(oldData, oldPath, "OLD");
                if (_oldScene != null) OldViewport.AddModel(_oldScene);
            }

            if (newData != null)
            {
                if (loadingWindow != null) await loadingWindow.SetStateAndRenderAsync(DiffLoadingState.ParsingNewModel);
                _newScene = await LoadModelFromBytesAsync(newData, newPath, "NEW");
                if (_newScene != null) NewViewport.AddModel(_newScene);

                if (_oldScene != null)
                {
                    _combinedNewScene = await LoadModelFromBytesAsync(newData, newPath, "COMBINED_NEW");
                    if (_combinedNewScene != null)
                    {
                        _combinedNewScene.IsVisible = false;
                        OldViewport.AddAuxiliaryModel(_combinedNewScene);
                    }
                }
            }

            if (loadingWindow != null) await loadingWindow.SetStateAndRenderAsync(DiffLoadingState.Comparing3DGeometry);
            CompareModels();
            BuildMeshPartsList();
            ResetCharacterCameras();

            UpdateViewportSceneDisplay(OldViewport, _oldScene, oldPath);
            UpdateViewportSceneDisplay(NewViewport, _newScene, newPath);
        }

        private async Task<SceneModel> LoadModelFromBytesAsync(byte[] data, string path, string label)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".skn");
            try
            {
                File.WriteAllBytes(tempFile, data);
                var scene = await _sknLoadingService.LoadModel(tempFile);
                if (scene != null)
                {
                    scene.Name = Path.GetFileNameWithoutExtension(path);
                    scene.PositionY = SceneElements.GroundLevel;
                }
                return scene;
            }
            catch (Exception ex)
            {
                // Preserve the side label so model-loading failures remain easy to correlate.
                _logService.LogError(ex, $"[{label}] Failed to load model: {path}");
                return null;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
                catch (Exception ex)
                {
                    // Keep temporary-file cleanup failures visible without hiding the original load result.
                    _logService.LogError(ex, $"[{label}] Failed to remove temporary model: {tempFile}");
                }
            }
        }

        private void CompareModels()
        {
            if (_oldScene == null && _newScene == null)
            {
                VertexDeltaLabel.Text = "0";
                FaceDeltaLabel.Text = "0";
                return;
            }

            if (_oldScene == null && _newScene != null)
            {
                int newVertices = _newScene.SkinnedMesh.VerticesView.VertexCount;
                int newIndices = _newScene.SkinnedMesh.Indices.Count;
                VertexDeltaLabel.Text = $"{newVertices:N0} (New)";
                FaceDeltaLabel.Text = $"{(newIndices / 3):N0} (New)";
                UpdateVisualHighlighting();
                return;
            }

            if (_oldScene != null && _newScene == null)
            {
                int oldVertices = _oldScene.SkinnedMesh.VerticesView.VertexCount;
                int oldIndices = _oldScene.SkinnedMesh.Indices.Count;
                VertexDeltaLabel.Text = $"{oldVertices:N0} (Removed)";
                FaceDeltaLabel.Text = $"{(oldIndices / 3):N0} (Removed)";
                UpdateVisualHighlighting();
                return;
            }

            int oldVerticesCount = _oldScene.SkinnedMesh.VerticesView.VertexCount;
            int newVerticesCount = _newScene.SkinnedMesh.VerticesView.VertexCount;
            int oldIndicesCount = _oldScene.SkinnedMesh.Indices.Count;
            int newIndicesCount = _newScene.SkinnedMesh.Indices.Count;

            VertexDeltaLabel.Text = $"{newVerticesCount:N0} ({(newVerticesCount - oldVerticesCount):+0;-0;0})";
            FaceDeltaLabel.Text = $"{(newIndicesCount / 3):N0} ({((newIndicesCount - oldIndicesCount) / 3):+0;-0;0})";

            PrecalculateGeometryDiffs();
            UpdateVisualHighlighting();
        }

        private bool IsPartUserVisible(string partName)
        {
            var item = _partItems.FirstOrDefault(i => i.Name == partName);
            if (item != null)
            {
                return item.IsVisible;
            }
            return true;
        }

        private void AddDiffOverlay(ViewerViewportControl viewport, string ownerPartName, string name, MeshGeometry3D mesh, System.Numerics.Vector4 tint, float alphaCutoff)
        {
            bool isVisible = IsPartUserVisible(ownerPartName);
            var part = new ModelPart(name, new GeometryModel3D(mesh, null))
            {
                ColorTint = tint,
                AlphaCutoff = alphaCutoff,
                IsVisible = isVisible
            };
            var scene = new SceneModel
            {
                Name = name,
                IsVisible = isVisible,
                PositionY = SceneElements.GroundLevel
            };
            scene.AddPart(part);
            viewport.AddAuxiliaryModel(scene);
            _diffOverlayScenes.Add(scene);
            _diffOverlayOwners[scene] = ownerPartName;
            _diffOverlayViewports[scene] = viewport;
        }

        private void UpdateVisualHighlighting()
        {
            if (_oldScene == null && _newScene == null) return;

            bool isGhostMode = OldViewport.IsGhostModeChecked;
            bool isCombined = OldViewport.IsCombinedModeChecked;

            // Clear old overlays without disturbing the primary diff scenes.
            foreach (var overlay in _diffOverlayScenes)
            {
                if (_diffOverlayViewports.TryGetValue(overlay, out ViewerViewportControl viewport))
                    viewport.RemoveModel(overlay);
                else
                    overlay.Dispose();
            }
            _diffOverlayScenes.Clear();
            _diffOverlayOwners.Clear();
            _diffOverlayViewports.Clear();

            if (_oldScene == null && _newScene != null)
            {
                double greenOpacity = 1.0;
                if (isGhostMode)
                {
                    greenOpacity = 0.7;
                }
                foreach (var newPart in _newScene.Parts)
                {
                    newPart.IsVisible = IsPartUserVisible(newPart.Name);
                    HighlightPart(newPart, Colors.Green, greenOpacity);
                }
                return;
            }

            if (_oldScene != null && _newScene == null)
            {
                double redOpacity = 0.5;
                if (isGhostMode)
                {
                    redOpacity = 0.2;
                }
                foreach (var oldPart in _oldScene.Parts)
                {
                    oldPart.IsVisible = IsPartUserVisible(oldPart.Name);
                    HighlightPart(oldPart, Colors.Red, redOpacity);
                }
                return;
            }

            double newPartGreenOpacity = 1.0;
            if (isGhostMode)
            {
                newPartGreenOpacity = 0.7;
            }
            double modifiedBlueOpacity = 1.0;
            if (isGhostMode)
            {
                modifiedBlueOpacity = 0.6;
            }
            Color unchangedColor = Color.FromRgb(100, 100, 100);
            double unchangedOpacity = 1.0;
            if (isGhostMode)
            {
                unchangedColor = Color.FromRgb(120, 120, 130);
                unchangedOpacity = 0.15;
            }

            foreach (var newPart in _newScene.Parts)
            {
                var oldPart = _oldScene.Parts.FirstOrDefault(p => p.Name == newPart.Name);
                bool userVisible = IsPartUserVisible(newPart.Name);

                if (oldPart == null)
                {
                    // [NEW]
                    newPart.IsVisible = userVisible;
                    HighlightPart(newPart, Colors.Green, newPartGreenOpacity);
                }
                else if (!ArePartsEqual(oldPart, newPart))
                {
                    // [MODIFIED]
                    newPart.IsVisible = userVisible;
                    oldPart.IsVisible = userVisible;
                    HighlightPart(newPart, Colors.DodgerBlue, modifiedBlueOpacity);
                    HighlightPart(oldPart, Colors.DodgerBlue, modifiedBlueOpacity);

                    // Check cache for newly added geometry pieces inside this modified part (e.g. piercings)
                    if (_addedGeometryCache.TryGetValue(newPart.Name, out var addedMesh))
                    {
                        AddDiffOverlay(NewViewport, newPart.Name, "AddedOverlay_" + newPart.Name, addedMesh, new System.Numerics.Vector4(0f, 1f, 0f, 1f), 0.5f);
                        if (isCombined)
                        {
                            AddDiffOverlay(OldViewport, newPart.Name, "CombinedAddedOverlay_" + newPart.Name, addedMesh, new System.Numerics.Vector4(0f, 1f, 0f, 1f), 0.5f);
                        }
                    }

                    // Check cache for newly deleted geometry pieces inside this modified part
                    if (_removedGeometryCache.TryGetValue(newPart.Name, out var removedMesh))
                    {
                        AddDiffOverlay(OldViewport, newPart.Name, "RemovedOverlay_" + newPart.Name, removedMesh, new System.Numerics.Vector4(1f, 0f, 0f, 0.8f), 0f);
                    }
                }
                else
                {
                    // [UNCHANGED]
                    newPart.IsVisible = userVisible;
                    HighlightPart(newPart, unchangedColor, unchangedOpacity);
                    
                    // In combined mode, hide the old unchanged part to avoid Z-fighting
                    oldPart.IsVisible = userVisible && !isCombined;
                    if (!isCombined)
                    {
                        HighlightPart(oldPart, unchangedColor, unchangedOpacity);
                    }
                }
            }

            double oldPartRedOpacity = 0.5;
            if (isGhostMode)
            {
                oldPartRedOpacity = 0.2;
            }

            foreach (var oldPart in _oldScene.Parts)
            {
                if (!_newScene.Parts.Any(p => p.Name == oldPart.Name))
                {
                    // [REMOVED]
                    oldPart.IsVisible = IsPartUserVisible(oldPart.Name);
                    HighlightPart(oldPart, Colors.Red, oldPartRedOpacity);
                }
            }

            if (_combinedNewScene != null)
            {
                _combinedNewScene.IsVisible = isCombined;
                if (isCombined)
                {
                    foreach (var combPart in _combinedNewScene.Parts)
                    {
                        var matchingPart = _newScene.Parts.FirstOrDefault(p => p.Name == combPart.Name);
                        if (matchingPart != null)
                        {
                            combPart.IsVisible = matchingPart.IsVisible;
                            combPart.ColorTint = matchingPart.ColorTint;
                            combPart.AlphaCutoff = matchingPart.AlphaCutoff;
                        }
                    }
                }
            }
        }

        private static bool ArePartsEqual(ModelPart p1, ModelPart p2)
        {
            var m1 = p1?.Geometry?.Geometry as MeshGeometry3D;
            var m2 = p2?.Geometry?.Geometry as MeshGeometry3D;
            return SknMeshDiffAnalyzer.AreEquivalent(m1, m2);
        }

        private void HighlightPart(ModelPart part, Color color, double opacity = 1.0)
        {
            part.ColorTint = new System.Numerics.Vector4(color.R / 255f, color.G / 255f, color.B / 255f, (float)opacity);
            float alphaCutoff = 0.5f;
            if (opacity < 1.0)
            {
                alphaCutoff = 0f;
            }
            part.AlphaCutoff = alphaCutoff;
        }

        private bool _isSyncingToolbarVisibility = false;
        private void ViewportViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewerViewportModel.IsToolbarVisible))
            {
                if (_isSyncingToolbarVisibility) return;
                _isSyncingToolbarVisibility = true;
                try
                {
                    var isVisible = ((ViewerViewportModel)sender).IsToolbarVisible;
                    OldViewport.ViewModel.IsToolbarVisible = isVisible;
                    NewViewport.ViewModel.IsToolbarVisible = isVisible;
                }
                finally
                {
                    _isSyncingToolbarVisibility = false;
                }
            }
        }

        private bool _isSyncingToggles = false;

        private void Viewport_CombinedModeToggled(object sender, bool isChecked)
        {
            if (_isSyncingToggles) return;
            _isSyncingToggles = true;
            try
            {
                OldViewport.IsCombinedModeChecked = isChecked;
                NewViewport.IsCombinedModeChecked = isChecked;
            }
            finally
            {
                _isSyncingToggles = false;
            }

            UpdateViewMode();
        }

        private void Viewport_AutoRotateToggled(object sender, bool isChecked)
        {
            if (_isSyncingToggles) return;
            _isSyncingToggles = true;
            try
            {
                OldViewport.IsAutoRotateChecked = isChecked;
                NewViewport.IsAutoRotateChecked = isChecked;
            }
            finally
            {
                _isSyncingToggles = false;
            }

            OldViewport.ViewModel.IsAutoRotateActive = isChecked;
            NewViewport.ViewModel.IsAutoRotateActive = isChecked;
        }

        private void Viewport_MeshPartsToggled(object sender, bool isChecked)
        {
            if (_isSyncingToggles) return;
            _isSyncingToggles = true;
            try
            {
                OldViewport.IsMeshPartsChecked = isChecked;
                NewViewport.IsMeshPartsChecked = isChecked;
            }
            finally
            {
                _isSyncingToggles = false;
            }

            MeshVisibilityPanel.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Viewport_GhostModeToggled(object sender, bool isChecked)
        {
            if (_isSyncingToggles) return;
            _isSyncingToggles = true;
            try
            {
                OldViewport.IsGhostModeChecked = isChecked;
                NewViewport.IsGhostModeChecked = isChecked;
            }
            finally
            {
                _isSyncingToggles = false;
            }

            UpdateVisualHighlighting();
        }

        private void Viewport_ResetCamerasClicked(object sender, EventArgs e)
        {
            ResetCharacterCameras();
        }

        private void UpdateViewMode()
        {
            if (_oldScene == null || _newScene == null) return;

            bool isCombined = OldViewport.IsCombinedModeChecked;

            if (isCombined)
            {
                if (_combinedNewScene != null)
                {
                    _combinedNewScene.IsVisible = true;
                }

                // Collapse NewViewport and GridSplitter
                NewViewportContainer.Visibility = Visibility.Collapsed;
                ViewportSplitter.Visibility = Visibility.Collapsed;

                // Span OldViewport to fill all columns
                Grid.SetColumnSpan(OldViewportContainer, 3);

                OldViewport.ViewModel.UpdateSceneDisplay(2, OldFileNameLabel.Text);
            }
            else
            {
                if (_combinedNewScene != null)
                {
                    _combinedNewScene.IsVisible = false;
                }

                // Restore span and visibility
                Grid.SetColumnSpan(OldViewportContainer, 1);
                NewViewportContainer.Visibility = Visibility.Visible;
                ViewportSplitter.Visibility = Visibility.Visible;

                // Sync cameras immediately to ensure alignment
                SyncCameras(OldViewport, NewViewport);

                int oldCount = 0;
                if (_oldScene != null)
                {
                    oldCount = 1;
                }
                OldViewport.ViewModel.UpdateSceneDisplay(oldCount, OldFileNameLabel.Text);
            }

            // Update highlighting to hide unchanged parts of _oldScene in combined mode
            UpdateVisualHighlighting();
        }

        private void CloseMeshPanel_Click(object sender, RoutedEventArgs e)
        {
            _isSyncingToggles = true;
            try
            {
                OldViewport.IsMeshPartsChecked = false;
                NewViewport.IsMeshPartsChecked = false;
            }
            finally
            {
                _isSyncingToggles = false;
            }
            MeshVisibilityPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowAllMeshParts_Click(object sender, RoutedEventArgs e) =>
            SetAllMeshPartsVisibility(true);

        private void HideAllMeshParts_Click(object sender, RoutedEventArgs e) =>
            SetAllMeshPartsVisibility(false);

        private void SetAllMeshPartsVisibility(bool isVisible)
        {
            _isBulkUpdatingMeshParts = true;
            try
            {
                foreach (MeshPartDiffItem item in _partItems)
                    item.IsVisible = isVisible;
            }
            finally
            {
                _isBulkUpdatingMeshParts = false;
            }

            ApplyMeshPartVisibility();
        }

        private void MeshPartVisibility_Changed(object sender, RoutedEventArgs e)
        {
            if (_isBulkUpdatingMeshParts) return;
            ApplyMeshPartVisibility();
        }

        private void ApplyMeshPartVisibility()
        {
            if (_oldScene != null)
            {
                foreach (ModelPart part in _oldScene.Parts)
                    part.IsVisible = IsPartUserVisible(part.Name);
            }

            if (_newScene != null)
            {
                foreach (ModelPart part in _newScene.Parts)
                    part.IsVisible = IsPartUserVisible(part.Name);
            }

            if (_combinedNewScene != null)
            {
                foreach (ModelPart part in _combinedNewScene.Parts)
                    part.IsVisible = IsPartUserVisible(part.Name);
            }

            foreach (var pair in _diffOverlayOwners)
                pair.Key.IsVisible = IsPartUserVisible(pair.Value);
        }

        private void BuildMeshPartsList()
        {
            _partItems.Clear();

            if (_oldScene == null && _newScene == null) return;

            var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_oldScene != null)
            {
                foreach (var part in _oldScene.Parts) allNames.Add(part.Name);
            }
            if (_newScene != null)
            {
                foreach (var part in _newScene.Parts) allNames.Add(part.Name);
            }

            foreach (var name in allNames.OrderBy(n => n))
            {
                var item = new MeshPartDiffItem { Name = name, IsVisible = true };

                ModelPart oldPart = null;
                if (_oldScene != null)
                {
                    oldPart = _oldScene.Parts.FirstOrDefault(p => p.Name == name);
                }

                ModelPart newPart = null;
                if (_newScene != null)
                {
                    newPart = _newScene.Parts.FirstOrDefault(p => p.Name == name);
                }

                SolidColorBrush diffBrush;
                if (oldPart == null && newPart != null)
                {
                    int triangles = SknMeshDiffAnalyzer.GetTriangleCount(newPart.Geometry?.Geometry as MeshGeometry3D);
                    diffBrush = new SolidColorBrush(Colors.Green);
                    item.DeltaText = $"+{triangles:N0}";
                    item.StatusLabel = "NEW";
                    item.SortRank = 0;
                    item.StatusText = $"New mesh part — {triangles:N0} triangles only in NEW";
                }
                else if (oldPart != null && newPart == null)
                {
                    int triangles = SknMeshDiffAnalyzer.GetTriangleCount(oldPart.Geometry?.Geometry as MeshGeometry3D);
                    diffBrush = new SolidColorBrush(Colors.Red);
                    item.DeltaText = $"-{triangles:N0}";
                    item.StatusLabel = "REMOVED";
                    item.SortRank = 0;
                    item.StatusText = $"Removed mesh part — {triangles:N0} triangles only in OLD";
                }
                else if (oldPart != null && newPart != null)
                {
                    if (!ArePartsEqual(oldPart, newPart))
                    {
                        int addedTriangles = _addedGeometryCache.TryGetValue(name, out MeshGeometry3D addedGeometry)
                            ? SknMeshDiffAnalyzer.GetTriangleCount(addedGeometry)
                            : 0;
                        int removedTriangles = _removedGeometryCache.TryGetValue(name, out MeshGeometry3D removedGeometry)
                            ? SknMeshDiffAnalyzer.GetTriangleCount(removedGeometry)
                            : 0;

                        diffBrush = new SolidColorBrush(Colors.DodgerBlue);
                        item.DeltaText = $"+{addedTriangles:N0}/-{removedTriangles:N0}";
                        item.StatusLabel = "MODIFIED";
                        item.SortRank = 0;
                        item.StatusText = $"Modified mesh part — {addedTriangles:N0} NEW-only triangles, {removedTriangles:N0} OLD-only triangles";
                    }
                    else
                    {
                        diffBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100));
                        item.StatusLabel = "UNCHANGED";
                        item.SortRank = 1;
                        item.StatusText = "Unchanged mesh part";
                    }
                }
                else
                {
                    diffBrush = Brushes.Transparent;
                    item.StatusLabel = "UNKNOWN";
                    item.SortRank = 1;
                    item.StatusText = string.Empty;
                }

                diffBrush.Freeze();
                item.DiffColorBrush = diffBrush;

                _partItems.Add(item);
            }

            _partItems.Sort((left, right) =>
            {
                int rankComparison = left.SortRank.CompareTo(right.SortRank);
                return rankComparison != 0
                    ? rankComparison
                    : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            });

            int changedCount = _partItems.Count(item => item.SortRank == 0);
            MeshPartsSummaryText.Text = $"{_partItems.Count:N0} parts · {changedCount:N0} changed";

            MeshPartsItemsControl.ItemsSource = null;
            MeshPartsItemsControl.ItemsSource = _partItems;
        }

        private bool _isDraggingMeshPanel = false;
        private Point _meshPanelDragStart;
        private double _meshPanelInitialX;
        private double _meshPanelInitialY;

        private void MeshPanelHeader_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var grid = sender as Grid;
            if (grid == null) return;

            _isDraggingMeshPanel = true;
            _meshPanelDragStart = e.GetPosition(this);
            _meshPanelInitialX = MeshPanelTranslation.X;
            _meshPanelInitialY = MeshPanelTranslation.Y;

            grid.CaptureMouse();
        }

        private void MeshPanelHeader_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDraggingMeshPanel) return;

            var grid = sender as Grid;
            if (grid == null) return;

            Point currentPoint = e.GetPosition(this);
            double deltaX = currentPoint.X - _meshPanelDragStart.X;
            double deltaY = currentPoint.Y - _meshPanelDragStart.Y;

            MeshPanelTranslation.X = _meshPanelInitialX + deltaX;
            MeshPanelTranslation.Y = _meshPanelInitialY + deltaY;
        }

        private void MeshPanelHeader_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_isDraggingMeshPanel) return;

            _isDraggingMeshPanel = false;
            var grid = sender as Grid;
            if (grid != null)
            {
                grid.ReleaseMouseCapture();
            }
        }

        private void PrecalculateGeometryDiffs()
        {
            _addedGeometryCache.Clear();
            _removedGeometryCache.Clear();

            if (_oldScene == null || _newScene == null) return;

            foreach (var newPart in _newScene.Parts)
            {
                var oldPart = _oldScene.Parts.FirstOrDefault(p => p.Name == newPart.Name);
                if (oldPart == null || ArePartsEqual(oldPart, newPart)) continue;

                MeshGeometry3D newMesh = null;
                if (newPart.Geometry != null)
                {
                    newMesh = newPart.Geometry.Geometry as MeshGeometry3D;
                }
                MeshGeometry3D oldMesh = null;
                if (oldPart.Geometry != null)
                {
                    oldMesh = oldPart.Geometry.Geometry as MeshGeometry3D;
                }
                if (newMesh == null || oldMesh == null) continue;

                // 1. Detect added triangles (Old -> New)
                var addedMesh = SknMeshDiffAnalyzer.ExtractDifferenceMesh(oldMesh, newMesh);
                if (addedMesh != null)
                {
                    addedMesh.Freeze();
                    _addedGeometryCache[newPart.Name] = addedMesh;
                }

                // 2. Detect removed triangles (New -> Old)
                var removedMesh = SknMeshDiffAnalyzer.ExtractDifferenceMesh(newMesh, oldMesh);
                if (removedMesh != null)
                {
                    removedMesh.Freeze();
                    _removedGeometryCache[newPart.Name] = removedMesh;
                }
            }
        }

    }

    internal static class SknMeshDiffAnalyzer
    {
        private const double PositionScale = 100000.0;

        internal static MeshGeometry3D ExtractDifferenceMesh(MeshGeometry3D sourceMesh, MeshGeometry3D targetMesh)
        {
            if (sourceMesh == null || targetMesh == null) return null;

            Dictionary<TriangleKey, int> sourceTriangles = BuildTriangleCounts(sourceMesh);
            var diffMesh = new MeshGeometry3D();
            var diffMap = new Dictionary<int, int>();

            for (int i = 0; i + 2 < targetMesh.TriangleIndices.Count; i += 3)
            {
                int i1 = targetMesh.TriangleIndices[i];
                int i2 = targetMesh.TriangleIndices[i + 1];
                int i3 = targetMesh.TriangleIndices[i + 2];
                TriangleKey key = TriangleKey.Create(
                    targetMesh.Positions[i1],
                    targetMesh.Positions[i2],
                    targetMesh.Positions[i3]);

                if (sourceTriangles.TryGetValue(key, out int remaining) && remaining > 0)
                {
                    if (remaining == 1)
                        sourceTriangles.Remove(key);
                    else
                        sourceTriangles[key] = remaining - 1;
                    continue;
                }

                int n1 = GetOrCreateVertex(i1, targetMesh, diffMesh, diffMap);
                int n2 = GetOrCreateVertex(i2, targetMesh, diffMesh, diffMap);
                int n3 = GetOrCreateVertex(i3, targetMesh, diffMesh, diffMap);
                diffMesh.TriangleIndices.Add(n1);
                diffMesh.TriangleIndices.Add(n2);
                diffMesh.TriangleIndices.Add(n3);
            }

            return diffMesh.TriangleIndices.Count > 0 ? diffMesh : null;
        }

        internal static int GetTriangleCount(MeshGeometry3D mesh) =>
            mesh?.TriangleIndices?.Count / 3 ?? 0;

        internal static bool AreEquivalent(MeshGeometry3D left, MeshGeometry3D right)
        {
            if (left == null || right == null) return left == right;
            if (GetTriangleCount(left) != GetTriangleCount(right)) return false;

            Dictionary<TriangleKey, int> leftTriangles = BuildTriangleCounts(left);
            Dictionary<TriangleKey, int> rightTriangles = BuildTriangleCounts(right);
            if (leftTriangles.Count != rightTriangles.Count) return false;

            foreach ((TriangleKey key, int count) in leftTriangles)
            {
                if (!rightTriangles.TryGetValue(key, out int rightCount) || rightCount != count)
                    return false;
            }
            return true;
        }

        private static Dictionary<TriangleKey, int> BuildTriangleCounts(MeshGeometry3D mesh)
        {
            var result = new Dictionary<TriangleKey, int>();
            for (int i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3)
            {
                int i1 = mesh.TriangleIndices[i];
                int i2 = mesh.TriangleIndices[i + 1];
                int i3 = mesh.TriangleIndices[i + 2];
                TriangleKey key = TriangleKey.Create(
                    mesh.Positions[i1],
                    mesh.Positions[i2],
                    mesh.Positions[i3]);
                result.TryGetValue(key, out int count);
                result[key] = count + 1;
            }
            return result;
        }

        private static int GetOrCreateVertex(
            int sourceIndex,
            MeshGeometry3D sourceMesh,
            MeshGeometry3D targetMesh,
            Dictionary<int, int> map)
        {
            if (map.TryGetValue(sourceIndex, out int mappedIndex))
                return mappedIndex;

            int index = targetMesh.Positions.Count;
            targetMesh.Positions.Add(sourceMesh.Positions[sourceIndex]);
            if (sourceMesh.Normals != null && sourceMesh.Normals.Count > sourceIndex)
                targetMesh.Normals.Add(sourceMesh.Normals[sourceIndex]);
            if (sourceMesh.TextureCoordinates != null && sourceMesh.TextureCoordinates.Count > sourceIndex)
                targetMesh.TextureCoordinates.Add(sourceMesh.TextureCoordinates[sourceIndex]);
            map[sourceIndex] = index;
            return index;
        }

        private readonly struct VertexKey : IEquatable<VertexKey>, IComparable<VertexKey>
        {
            private readonly long _x;
            private readonly long _y;
            private readonly long _z;

            private VertexKey(Point3D point)
            {
                _x = (long)Math.Round(point.X * PositionScale, MidpointRounding.AwayFromZero);
                _y = (long)Math.Round(point.Y * PositionScale, MidpointRounding.AwayFromZero);
                _z = (long)Math.Round(point.Z * PositionScale, MidpointRounding.AwayFromZero);
            }

            internal static VertexKey Create(Point3D point) => new(point);

            public int CompareTo(VertexKey other)
            {
                int result = _x.CompareTo(other._x);
                if (result != 0) return result;
                result = _y.CompareTo(other._y);
                return result != 0 ? result : _z.CompareTo(other._z);
            }

            public bool Equals(VertexKey other) =>
                _x == other._x && _y == other._y && _z == other._z;

            public override bool Equals(object obj) => obj is VertexKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(_x, _y, _z);
        }

        private readonly struct TriangleKey : IEquatable<TriangleKey>
        {
            private readonly VertexKey _a;
            private readonly VertexKey _b;
            private readonly VertexKey _c;

            private TriangleKey(VertexKey a, VertexKey b, VertexKey c)
            {
                _a = a;
                _b = b;
                _c = c;
            }

            internal static TriangleKey Create(Point3D p1, Point3D p2, Point3D p3)
            {
                VertexKey a = VertexKey.Create(p1);
                VertexKey b = VertexKey.Create(p2);
                VertexKey c = VertexKey.Create(p3);
                if (a.CompareTo(b) > 0) (a, b) = (b, a);
                if (b.CompareTo(c) > 0) (b, c) = (c, b);
                if (a.CompareTo(b) > 0) (a, b) = (b, a);
                return new TriangleKey(a, b, c);
            }

            public bool Equals(TriangleKey other) =>
                _a.Equals(other._a) && _b.Equals(other._b) && _c.Equals(other._c);

            public override bool Equals(object obj) => obj is TriangleKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(_a, _b, _c);
        }
    }

    public class MeshPartDiffItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public SolidColorBrush DiffColorBrush { get; set; }
        public string StatusText { get; set; }
        public string StatusLabel { get; set; } = string.Empty;
        public string DeltaText { get; set; } = string.Empty;
        public int SortRank { get; set; } = 1;

        private bool _isVisible = true;
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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
