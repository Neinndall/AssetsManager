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
using HelixToolkit.Wpf;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer;
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
        private readonly List<MeshPartDiffItem> _partItems = new();
        private readonly List<ModelVisual3D> _diffOverlays = new();
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
            Dispatcher.BeginInvoke(new Action(() => ResetCharacterCameras()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

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

            // Dispose viewports to clean up rendering loops, cameras, controllers
            OldViewport.Dispose();
            NewViewport.Dispose();

            // Clear lists and scenes
            _partItems.Clear();
            _diffOverlays.Clear();
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
        }

        private void ResetCharacterCameras()
        {
            var position = new Point3D(0, 130, 280);
            var lookDir = new Vector3D(0, -40, -280);
            var upDir = new Vector3D(0, 1, 0);

            _isSyncing = true;
            try
            {
                OldViewport.Viewport3D.SetView(position, lookDir, upDir, 0);
                NewViewport.Viewport3D.SetView(position, lookDir, upDir, 0);
            }
            finally
            {
                _isSyncing = false;
            }
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
            OldFileNameLabel.Text = Path.GetFileName(oldPath) ?? "None";
            NewFileNameLabel.Text = Path.GetFileName(newPath) ?? "None";

            OldViewport.ClearModels();
            NewViewport.ClearModels();
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
            }

            if (loadingWindow != null) await loadingWindow.SetStateAndRenderAsync(DiffLoadingState.Comparing3DGeometry);
            CompareModels();
            BuildMeshPartsList();
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
                }
                try { File.Delete(tempFile); } catch { }
                return scene;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"[3D-DIFF] [{label}] Failed to load model: {path}");
                return null;
            }
        }

        private void CompareModels()
        {
            if (_oldScene == null || _newScene == null) return;

            int oldVertices = _oldScene.SkinnedMesh.VerticesView.VertexCount;
            int newVertices = _newScene.SkinnedMesh.VerticesView.VertexCount;
            int oldIndices = _oldScene.SkinnedMesh.Indices.Count;
            int newIndices = _newScene.SkinnedMesh.Indices.Count;

            VertexDeltaLabel.Text = $"{newVertices} ({(newVertices - oldVertices):+0;-0;0})";
            FaceDeltaLabel.Text = $"{newIndices / 3} ({((newIndices - oldIndices) / 3):+0;-0;0})";

            PrecalculateGeometryDiffs();
            UpdateVisualHighlighting();
        }

        private void UpdateVisualHighlighting()
        {
            if (_oldScene == null || _newScene == null) return;

            bool isGhostMode = OldViewport.IsGhostModeChecked;
            bool isCombined = OldViewport.IsCombinedModeChecked;

            // Clear old overlays
            foreach (var overlay in _diffOverlays)
            {
                if (OldViewport.Viewport3D.Children.Contains(overlay))
                    OldViewport.Viewport3D.Children.Remove(overlay);
                if (NewViewport.Viewport3D.Children.Contains(overlay))
                    NewViewport.Viewport3D.Children.Remove(overlay);
            }
            _diffOverlays.Clear();

            foreach (var newPart in _newScene.Parts)
            {
                var oldPart = _oldScene.Parts.FirstOrDefault(p => p.Name == newPart.Name);
                var partItem = _partItems.FirstOrDefault(i => i.Name == newPart.Name);
                bool userVisible = partItem?.IsVisible ?? true;

                if (oldPart == null)
                {
                    // [NEW]
                    newPart.IsVisible = userVisible;
                    HighlightPart(newPart, Colors.Green, isGhostMode ? 0.7 : 1.0);
                }
                else if (!ArePartsEqual(oldPart, newPart))
                {
                    // [MODIFIED]
                    newPart.IsVisible = userVisible;
                    oldPart.IsVisible = userVisible;
                    HighlightPart(newPart, Colors.DodgerBlue, isGhostMode ? 0.6 : 1.0);
                    HighlightPart(oldPart, Colors.DodgerBlue, isGhostMode ? 0.6 : 1.0);

                    // Check cache for newly added geometry pieces inside this modified part (e.g. piercings)
                    if (_addedGeometryCache.TryGetValue(newPart.Name, out var addedMesh))
                    {
                        var greenBrush = new SolidColorBrush(Colors.Green) { Opacity = 1.0 };
                        greenBrush.Freeze();
                        var greenMaterial = new MaterialGroup();
                        greenMaterial.Children.Add(new DiffuseMaterial(greenBrush));
                        greenMaterial.Children.Add(new SpecularMaterial(Brushes.White, 40.0));
                        greenMaterial.Freeze();

                        var addedModel = new GeometryModel3D(addedMesh, greenMaterial) { BackMaterial = greenMaterial };
                        var addedVisual = new ModelVisual3D { Content = addedModel };

                        NewViewport.Viewport3D.Children.Add(addedVisual);
                        _diffOverlays.Add(addedVisual);

                        if (isCombined)
                        {
                            var combinedAddedVisual = new ModelVisual3D { Content = addedModel };
                            OldViewport.Viewport3D.Children.Add(combinedAddedVisual);
                            _diffOverlays.Add(combinedAddedVisual);
                        }
                    }

                    // Check cache for newly deleted geometry pieces inside this modified part
                    if (_removedGeometryCache.TryGetValue(newPart.Name, out var removedMesh))
                    {
                        var redBrush = new SolidColorBrush(Colors.Red) { Opacity = 0.8 };
                        redBrush.Freeze();
                        var redMaterial = new MaterialGroup();
                        redMaterial.Children.Add(new DiffuseMaterial(redBrush));
                        redMaterial.Children.Add(new SpecularMaterial(Brushes.White, 40.0));
                        redMaterial.Freeze();

                        var removedModel = new GeometryModel3D(removedMesh, redMaterial) { BackMaterial = redMaterial };
                        var removedVisual = new ModelVisual3D { Content = removedModel };

                        OldViewport.Viewport3D.Children.Add(removedVisual);
                        _diffOverlays.Add(removedVisual);
                    }
                }
                else
                {
                    // [UNCHANGED]
                    newPart.IsVisible = userVisible;
                    if (isGhostMode)
                        HighlightPart(newPart, Color.FromRgb(120, 120, 130), 0.15); // Transparent Ghost
                    else
                        HighlightPart(newPart, Color.FromRgb(100, 100, 100), 1.0);  // Solid Technical Gray
                    
                    // In combined mode, hide the old unchanged part to avoid Z-fighting
                    oldPart.IsVisible = userVisible && !isCombined;
                    if (!isCombined)
                    {
                        if (isGhostMode)
                            HighlightPart(oldPart, Color.FromRgb(120, 120, 130), 0.15);
                        else
                            HighlightPart(oldPart, Color.FromRgb(100, 100, 100), 1.0);
                    }
                }
            }

            foreach (var oldPart in _oldScene.Parts)
            {
                if (!_newScene.Parts.Any(p => p.Name == oldPart.Name))
                {
                    // [REMOVED]
                    var partItem = _partItems.FirstOrDefault(i => i.Name == oldPart.Name);
                    bool userVisible = partItem?.IsVisible ?? true;

                    oldPart.IsVisible = userVisible;
                    HighlightPart(oldPart, Colors.Red, isGhostMode ? 0.2 : 0.5);
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
            var brush = new SolidColorBrush(color) { Opacity = opacity };
            brush.Freeze();
            
            // Create a professional material group with Specular highlights for depth
            var materialGroup = new MaterialGroup();
            materialGroup.Children.Add(new DiffuseMaterial(brush));
            
            // Add a subtle specular highlight to see the "volume" and edges of the mesh
            var specBrush = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), 200, 200, 200));
            specBrush.Freeze();
            materialGroup.Children.Add(new SpecularMaterial(specBrush, 40.0));
            materialGroup.Freeze();

            part.Geometry.Material = materialGroup;
            part.Geometry.BackMaterial = materialGroup;
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
                // Remove _newScene from NewViewport, add to OldViewport
                NewViewport.Viewport3D.Children.Remove(_newScene.RootVisual);
                if (!OldViewport.Viewport3D.Children.Contains(_newScene.RootVisual))
                {
                    OldViewport.Viewport3D.Children.Add(_newScene.RootVisual);
                }

                // Collapse NewViewport and GridSplitter
                NewViewportContainer.Visibility = Visibility.Collapsed;
                ViewportSplitter.Visibility = Visibility.Collapsed;

                // Span OldViewport to fill all columns
                Grid.SetColumnSpan(OldViewportContainer, 3);
            }
            else
            {
                // Remove _newScene from OldViewport, add back to NewViewport
                OldViewport.Viewport3D.Children.Remove(_newScene.RootVisual);
                if (!NewViewport.Viewport3D.Children.Contains(_newScene.RootVisual))
                {
                    NewViewport.Viewport3D.Children.Add(_newScene.RootVisual);
                }

                // Restore span and visibility
                Grid.SetColumnSpan(OldViewportContainer, 1);
                NewViewportContainer.Visibility = Visibility.Visible;
                ViewportSplitter.Visibility = Visibility.Visible;

                // Sync cameras immediately to ensure alignment
                SyncCameras(OldViewport, NewViewport);
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

                var oldPart = _oldScene?.Parts.FirstOrDefault(p => p.Name == name);
                var newPart = _newScene?.Parts.FirstOrDefault(p => p.Name == name);

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

                var newMesh = newPart.Geometry?.Geometry as MeshGeometry3D;
                var oldMesh = oldPart.Geometry?.Geometry as MeshGeometry3D;
                if (newMesh == null || oldMesh == null) continue;

                // 1. Detect added triangles (New geometry inside modified mesh)
                var oldPoints = new HashSet<VertexKey>();
                foreach (var pos in oldMesh.Positions)
                {
                    oldPoints.Add(new VertexKey(pos));
                }

                var addedMesh = new MeshGeometry3D();
                var addedMap = new Dictionary<int, int>();

                for (int i = 0; i < newMesh.TriangleIndices.Count; i += 3)
                {
                    int i1 = newMesh.TriangleIndices[i];
                    int i2 = newMesh.TriangleIndices[i + 1];
                    int i3 = newMesh.TriangleIndices[i + 2];

                    var p1 = newMesh.Positions[i1];
                    var p2 = newMesh.Positions[i2];
                    var p3 = newMesh.Positions[i3];

                    bool isNew = !oldPoints.Contains(new VertexKey(p1)) || 
                                 !oldPoints.Contains(new VertexKey(p2)) || 
                                 !oldPoints.Contains(new VertexKey(p3));

                    if (isNew)
                    {
                        int n1 = GetOrCreateVertex(i1, p1, newMesh, addedMesh, addedMap);
                        int n2 = GetOrCreateVertex(i2, p2, newMesh, addedMesh, addedMap);
                        int n3 = GetOrCreateVertex(i3, p3, newMesh, addedMesh, addedMap);

                        addedMesh.TriangleIndices.Add(n1);
                        addedMesh.TriangleIndices.Add(n2);
                        addedMesh.TriangleIndices.Add(n3);
                    }
                }

                if (addedMesh.TriangleIndices.Count > 0)
                {
                    addedMesh.Freeze();
                    _addedGeometryCache[newPart.Name] = addedMesh;
                }

                // 2. Detect removed triangles (Deleted geometry inside modified mesh)
                var newPoints = new HashSet<VertexKey>();
                foreach (var pos in newMesh.Positions)
                {
                    newPoints.Add(new VertexKey(pos));
                }

                var removedMesh = new MeshGeometry3D();
                var removedMap = new Dictionary<int, int>();

                for (int i = 0; i < oldMesh.TriangleIndices.Count; i += 3)
                {
                    int i1 = oldMesh.TriangleIndices[i];
                    int i2 = oldMesh.TriangleIndices[i + 1];
                    int i3 = oldMesh.TriangleIndices[i + 2];

                    var p1 = oldMesh.Positions[i1];
                    var p2 = oldMesh.Positions[i2];
                    var p3 = oldMesh.Positions[i3];

                    bool isDeleted = !newPoints.Contains(new VertexKey(p1)) || 
                                     !newPoints.Contains(new VertexKey(p2)) || 
                                     !newPoints.Contains(new VertexKey(p3));

                    if (isDeleted)
                    {
                        int n1 = GetOrCreateVertex(i1, p1, oldMesh, removedMesh, removedMap);
                        int n2 = GetOrCreateVertex(i2, p2, oldMesh, removedMesh, removedMap);
                        int n3 = GetOrCreateVertex(i3, p3, oldMesh, removedMesh, removedMap);

                        removedMesh.TriangleIndices.Add(n1);
                        removedMesh.TriangleIndices.Add(n2);
                        removedMesh.TriangleIndices.Add(n3);
                    }
                }

                if (removedMesh.TriangleIndices.Count > 0)
                {
                    removedMesh.Freeze();
                    _removedGeometryCache[newPart.Name] = removedMesh;
                }
            }
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
