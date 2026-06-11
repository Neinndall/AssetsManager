using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer;
using AssetsManager.Utils;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Views.Dialogs
{
    public partial class SknDiffWindow : HudWindow
    {
        private readonly SknLoadingService _sknLoadingService;
        private readonly LogService _logService;
        
        private SceneModel _oldScene;
        private SceneModel _newScene;

        public LoadingDiffWindow LoadingWindow { get; set; }

        public SknDiffWindow(SknLoadingService sknLoadingService, LogService logService)
        {
            InitializeComponent();
            _sknLoadingService = sknLoadingService;
            _logService = logService;
            
            _logService.Log("[3D-DIFF] SknDiffWindow initialized.");

            // Inject services into viewports
            OldViewport.LogService = logService;
            NewViewport.LogService = logService;

            // Sync cameras
            OldViewport.Viewport3D.Camera.Changed += (s, e) => SyncCameras(OldViewport, NewViewport);
            NewViewport.Viewport3D.Camera.Changed += (s, e) => SyncCameras(NewViewport, OldViewport);

            // Initial focus on origin
            Dispatcher.BeginInvoke(new Action(() => ResetCharacterCameras()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ResetCharacterCameras()
        {
            var position = new Point3D(0, 150, 500);
            var lookDir = new Vector3D(0, -150, -500);
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

        public async Task LoadAndDisplayDiffAsync(byte[] oldData, byte[] newData, string oldPath, string newPath)
        {
            _logService.Log($"[3D-DIFF] Loading mesh comparison for: {Path.GetFileName(newPath)}");

            OldFileNameLabel.Text = Path.GetFileName(oldPath) ?? "None";
            NewFileNameLabel.Text = Path.GetFileName(newPath) ?? "None";

            OldViewport.ClearModels();
            NewViewport.ClearModels();

            if (oldData != null)
            {
                _oldScene = await LoadModelFromBytesAsync(oldData, oldPath, "OLD");
                if (_oldScene != null) OldViewport.AddModel(_oldScene);
            }

            if (newData != null)
            {
                _newScene = await LoadModelFromBytesAsync(newData, newPath, "NEW");
                if (_newScene != null) NewViewport.AddModel(_newScene);
            }

            CompareModels();
        }

        private async Task<SceneModel> LoadModelFromBytesAsync(byte[] data, string path, string label)
        {
            try
            {
                string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".skn");
                File.WriteAllBytes(tempFile, data);
                var scene = await _sknLoadingService.LoadModel(tempFile);
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

            VertexDeltaLabel.Text = $"{(newVertices - oldVertices):+0;-0;0}";
            FaceDeltaLabel.Text = $"{(newIndices - oldIndices) / 3:+0;-0;0}";

            UpdateVisualHighlighting();
        }

        private void UpdateVisualHighlighting()
        {
            if (_oldScene == null || _newScene == null) return;

            bool isGhostMode = GhostModeToggle.IsChecked == true;

            foreach (var newPart in _newScene.Parts)
            {
                var oldPart = _oldScene.Parts.FirstOrDefault(p => p.Name == newPart.Name);
                if (oldPart == null)
                {
                    // [NEW]
                    HighlightPart(newPart, Colors.Green, isGhostMode ? 0.7 : 1.0);
                }
                else if (!ArePartsEqual(oldPart, newPart))
                {
                    // [MODIFIED]
                    HighlightPart(newPart, Colors.DodgerBlue, isGhostMode ? 0.6 : 1.0);
                    HighlightPart(oldPart, Colors.DodgerBlue, isGhostMode ? 0.6 : 1.0);
                }
                else
                {
                    // [UNCHANGED]
                    if (isGhostMode)
                        HighlightPart(newPart, Color.FromRgb(120, 120, 130), 0.15); // Transparent Ghost
                    else
                        HighlightPart(newPart, Color.FromRgb(100, 100, 100), 1.0);  // Solid Technical Gray
                    
                    if (isGhostMode)
                        HighlightPart(oldPart, Color.FromRgb(120, 120, 130), 0.15);
                    else
                        HighlightPart(oldPart, Color.FromRgb(100, 100, 100), 1.0);
                }
            }

            foreach (var oldPart in _oldScene.Parts)
            {
                if (!_newScene.Parts.Any(p => p.Name == oldPart.Name))
                {
                    // [REMOVED]
                    HighlightPart(oldPart, Colors.Red, isGhostMode ? 0.2 : 0.5);
                }
            }
        }

        private bool ArePartsEqual(ModelPart p1, ModelPart p2)
        {
            var m1 = (MeshGeometry3D)p1.Geometry.Geometry;
            var m2 = (MeshGeometry3D)p2.Geometry.Geometry;
            return m1.Positions.Count == m2.Positions.Count && m1.TriangleIndices.Count == m2.TriangleIndices.Count;
        }

        private void HighlightPart(ModelPart part, Color color, double opacity = 1.0)
        {
            var brush = new SolidColorBrush(color) { Opacity = opacity };
            
            // Create a professional material group with Specular highlights for depth
            var materialGroup = new MaterialGroup();
            materialGroup.Children.Add(new DiffuseMaterial(brush));
            
            // Add a subtle specular highlight to see the "volume" and edges of the mesh
            materialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), 200, 200, 200)), 40.0));

            part.Geometry.Material = materialGroup;
            part.Geometry.BackMaterial = materialGroup;
        }

        private void ResetCameras_Click(object sender, RoutedEventArgs e)
        {
            ResetCharacterCameras();
        }

        private void GhostModeToggle_Click(object sender, RoutedEventArgs e)
        {
            UpdateVisualHighlighting();
        }
    }
}
