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
        private readonly List<SceneModel> _diffOverlayScenes = new();
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

            // Initial focus on origin
            Loaded += (s, e) => ResetCharacterCameras();

            if (OldViewport.OpenTkControl != null)
            {
                OldViewport.OpenTkControl.Opacity = 0;
            }
            if (NewViewport.OpenTkControl != null)
            {
                NewViewport.OpenTkControl.Opacity = 0;
            }

            CompositionTarget.Rendering += OnDiffRendering;
        }

        private int _oldRenderedFrames = 0;
        private int _newRenderedFrames = 0;

        private void OnDiffRendering(object sender, EventArgs e)
        {
            if (OldViewport.OpenTkControl != null && OldViewport.OpenTkControl.IsLoaded)
            {
                OldViewport.OpenTkControl.InvalidateVisual();
                if (_oldRenderedFrames < 2 && OldViewport.OpenTkControl.Framebuffer > 0)
                {
                    _oldRenderedFrames++;
                    if (_oldRenderedFrames >= 2)
                    {
                        OldViewport.OpenTkControl.Opacity = 1.0;
                    }
                }
            }

            if (NewViewport.OpenTkControl != null && NewViewport.OpenTkControl.IsLoaded)
            {
                NewViewport.OpenTkControl.InvalidateVisual();
                if (_newRenderedFrames < 2 && NewViewport.OpenTkControl.Framebuffer > 0)
                {
                    _newRenderedFrames++;
                    if (_newRenderedFrames >= 2)
                    {
                        NewViewport.OpenTkControl.Opacity = 1.0;
                    }
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            CompositionTarget.Rendering -= OnDiffRendering;

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

            // Clean up viewports: rendering loops, cameras and controllers
            OldViewport.Cleanup();
            NewViewport.Cleanup();

            // Clear lists and scenes
            _partItems.Clear();
            foreach (var overlay in _diffOverlayScenes)
            {
                overlay.Dispose();
            }
            _diffOverlayScenes.Clear();
            _addedGeometryCache.Clear();
            _removedGeometryCache.Clear();
            if (_oldScene != null)
            {
                _oldScene.Dispose();
                _oldScene = null;
            }
            if (_newScene != null)
            {
                _newScene.Dispose();
                _newScene = null;
            }
            if (_combinedNewScene != null)
            {
                _combinedNewScene.Dispose();
                _combinedNewScene = null;
            }
        }

        private void ResetCharacterCameras()
        {
            _isSyncing = true;
            try
            {
                if (NewViewport != null && _newScene != null)
                {
                    NewViewport.ResetCamera(false);
                    if (OldViewport != null)
                    {
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
            finally
            {
                _isSyncing = false;
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
                    tgtP.FieldOfView = srcP.FieldOfView;
                else if (srcCam is OrthographicCamera srcO && tgtCam is OrthographicCamera tgtO)
                    tgtO.Width = srcO.Width;
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
            if (_combinedNewScene != null)
            {
                _combinedNewScene.Dispose();
                _combinedNewScene = null;
            }
            foreach (var overlay in _diffOverlayScenes)
            {
                overlay.Dispose();
            }
            _diffOverlayScenes.Clear();
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
                        OldViewport.AddModel(_combinedNewScene);
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
            try
            {
                string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".skn");
                File.WriteAllBytes(tempFile, data);
                var scene = await _sknLoadingService.LoadModel(tempFile);
                if (scene != null)
                {
                    scene.Name = Path.GetFileNameWithoutExtension(path);
                    scene.PositionY = SceneElements.GroundLevel;
                }
                try { File.Delete(tempFile); } catch { }
                return scene;
            }
            catch (Exception ex)
            {
                if (_logService != null)
                {
                    _logService.LogError(ex, $"[3D-DIFF] [{label}] Failed to load model: {path}");
                }
                return null;
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

        private void AddDiffOverlay(ViewerViewportControl viewport, string name, MeshGeometry3D mesh, System.Numerics.Vector4 tint, float alphaCutoff)
        {
            var part = new ModelPart(name, new GeometryModel3D(mesh, null))
            {
                ColorTint = tint,
                AlphaCutoff = alphaCutoff
            };
            var scene = new SceneModel
            {
                Name = name,
                IsVisible = true,
                PositionY = SceneElements.GroundLevel
            };
            scene.AddPart(part);
            viewport.AddModel(scene);
            _diffOverlayScenes.Add(scene);
        }

        private void UpdateVisualHighlighting()
        {
            if (_oldScene == null && _newScene == null) return;

            bool isGhostMode = OldViewport.IsGhostModeChecked;
            bool isCombined = OldViewport.IsCombinedModeChecked;

            // Clear old overlays
            foreach (var overlay in _diffOverlayScenes)
            {
                OldViewport.RemoveModel(overlay);
                NewViewport.RemoveModel(overlay);
                overlay.Dispose();
            }
            _diffOverlayScenes.Clear();

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
                        AddDiffOverlay(NewViewport, "AddedOverlay_" + newPart.Name, addedMesh, new System.Numerics.Vector4(0f, 1f, 0f, 1f), 0.5f);
                        if (isCombined)
                        {
                            AddDiffOverlay(OldViewport, "CombinedAddedOverlay_" + newPart.Name, addedMesh, new System.Numerics.Vector4(0f, 1f, 0f, 1f), 0.5f);
                        }
                    }

                    // Check cache for newly deleted geometry pieces inside this modified part
                    if (_removedGeometryCache.TryGetValue(newPart.Name, out var removedMesh))
                    {
                        AddDiffOverlay(OldViewport, "RemovedOverlay_" + newPart.Name, removedMesh, new System.Numerics.Vector4(1f, 0f, 0f, 0.8f), 0f);
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

        private bool ArePartsEqual(ModelPart p1, ModelPart p2)
        {
            var m1 = p1.Geometry.Geometry as MeshGeometry3D;
            var m2 = p2.Geometry.Geometry as MeshGeometry3D;

            if (m1 == null || m2 == null) return m1 == m2;

            if (m1.Positions.Count != m2.Positions.Count || m1.TriangleIndices.Count != m2.TriangleIndices.Count)
                return false;

            // Compare actual vertex positions with epsilon
            for (int i = 0; i < m1.Positions.Count; i++)
            {
                var pt1 = m1.Positions[i];
                var pt2 = m2.Positions[i];
                if (Math.Abs(pt1.X - pt2.X) > 1e-5 || Math.Abs(pt1.Y - pt2.Y) > 1e-5 || Math.Abs(pt1.Z - pt2.Z) > 1e-5)
                    return false;
            }

            // Compare triangle indices
            for (int i = 0; i < m1.TriangleIndices.Count; i++)
            {
                if (m1.TriangleIndices[i] != m2.TriangleIndices[i])
                    return false;
            }

            return true;
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

        private void MeshPartVisibility_Changed(object sender, RoutedEventArgs e)
        {
            UpdateVisualHighlighting();
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
                    diffBrush = new SolidColorBrush(Colors.Green);
                    item.StatusText = "New mesh part";
                }
                else if (oldPart != null && newPart == null)
                {
                    diffBrush = new SolidColorBrush(Colors.Red);
                    item.StatusText = "Removed mesh part";
                }
                else if (oldPart != null && newPart != null)
                {
                    if (!ArePartsEqual(oldPart, newPart))
                    {
                        diffBrush = new SolidColorBrush(Colors.DodgerBlue);
                        item.StatusText = "Modified mesh part";
                    }
                    else
                    {
                        diffBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100));
                        item.StatusText = "Unchanged mesh part";
                    }
                }
                else
                {
                    diffBrush = Brushes.Transparent;
                    item.StatusText = string.Empty;
                }

                diffBrush.Freeze();
                item.DiffColorBrush = diffBrush;

                _partItems.Add(item);
            }

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

        private int GetOrCreateVertex(int origIdx, Point3D pos, MeshGeometry3D srcMesh, MeshGeometry3D dstMesh, Dictionary<int, int> map)
        {
            if (map.TryGetValue(origIdx, out int newIdx))
                return newIdx;

            int idx = dstMesh.Positions.Count;
            dstMesh.Positions.Add(pos);
            
            if (srcMesh.Normals != null && srcMesh.Normals.Count > origIdx)
                dstMesh.Normals.Add(srcMesh.Normals[origIdx]);
            if (srcMesh.TextureCoordinates != null && srcMesh.TextureCoordinates.Count > origIdx)
                dstMesh.TextureCoordinates.Add(srcMesh.TextureCoordinates[origIdx]);

            map[origIdx] = idx;
            return idx;
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
                var addedMesh = ExtractDifferenceMesh(oldMesh, newMesh);
                if (addedMesh != null)
                {
                    addedMesh.Freeze();
                    _addedGeometryCache[newPart.Name] = addedMesh;
                }

                // 2. Detect removed triangles (New -> Old)
                var removedMesh = ExtractDifferenceMesh(newMesh, oldMesh);
                if (removedMesh != null)
                {
                    removedMesh.Freeze();
                    _removedGeometryCache[newPart.Name] = removedMesh;
                }
            }
        }

        private MeshGeometry3D ExtractDifferenceMesh(MeshGeometry3D sourceMesh, MeshGeometry3D targetMesh)
        {
            var sourcePoints = new HashSet<VertexKey>();
            foreach (var pos in sourceMesh.Positions)
            {
                sourcePoints.Add(new VertexKey(pos));
            }

            var diffMesh = new MeshGeometry3D();
            var diffMap = new Dictionary<int, int>();

            for (int i = 0; i < targetMesh.TriangleIndices.Count; i += 3)
            {
                int i1 = targetMesh.TriangleIndices[i];
                int i2 = targetMesh.TriangleIndices[i + 1];
                int i3 = targetMesh.TriangleIndices[i + 2];

                var p1 = targetMesh.Positions[i1];
                var p2 = targetMesh.Positions[i2];
                var p3 = targetMesh.Positions[i3];

                bool isDiff = !sourcePoints.Contains(new VertexKey(p1)) || 
                              !sourcePoints.Contains(new VertexKey(p2)) || 
                              !sourcePoints.Contains(new VertexKey(p3));

                if (isDiff)
                {
                    int n1 = GetOrCreateVertex(i1, p1, targetMesh, diffMesh, diffMap);
                    int n2 = GetOrCreateVertex(i2, p2, targetMesh, diffMesh, diffMap);
                    int n3 = GetOrCreateVertex(i3, p3, targetMesh, diffMesh, diffMap);

                    diffMesh.TriangleIndices.Add(n1);
                    diffMesh.TriangleIndices.Add(n2);
                    diffMesh.TriangleIndices.Add(n3);
                }
            }

            return diffMesh.TriangleIndices.Count > 0 ? diffMesh : null;
        }

        private struct VertexKey : IEquatable<VertexKey>
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;

            public VertexKey(Point3D point)
            {
                X = (int)Math.Round(point.X * 10.0);
                Y = (int)Math.Round(point.Y * 10.0);
                Z = (int)Math.Round(point.Z * 10.0);
            }

            public bool Equals(VertexKey other)
            {
                return X == other.X && Y == other.Y && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                return obj is VertexKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(X, Y, Z);
            }
        }
    }

    public class MeshPartDiffItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public SolidColorBrush DiffColorBrush { get; set; }
        public string StatusText { get; set; }

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
