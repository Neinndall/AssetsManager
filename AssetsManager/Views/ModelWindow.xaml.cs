using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services.Core;
using AssetsManager.Services.Models;
using AssetsManager.Views.Helpers;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;
using System.Threading.Tasks;

namespace AssetsManager.Views
{
    public partial class ModelWindow : UserControl
    {
        private readonly SknModelLoadingService _sknModelLoadingService;
        private readonly MapGeometryLoadingService _mapGeometryLoadingService;
        private readonly LogService _logService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly CustomCameraController _cameraController;
        private bool _isMapGeometry = false;
        private ModelVisual3D _groundVisual;
        private ModelVisual3D _skyVisual;

        private readonly AppSettings _appSettings;

        public ModelWindow(SknModelLoadingService sknModelLoadingService, MapGeometryLoadingService mapGeometryLoadingService, LogService logService, CustomMessageBoxService customMessageBoxService, AppSettings appSettings)
        {
            InitializeComponent();
            _sknModelLoadingService = sknModelLoadingService;
            _mapGeometryLoadingService = mapGeometryLoadingService;
            _logService = logService;
            _customMessageBoxService = customMessageBoxService;
            _appSettings = appSettings;
            _cameraController = new CustomCameraController(ViewportControl.Viewport);

            // Inject services into controls
            ViewportControl.LogService = _logService;
            PanelControl.SknModelLoadingService = _sknModelLoadingService;
            PanelControl.MapGeometryLoadingService = _mapGeometryLoadingService;
            PanelControl.LogService = _logService;
            PanelControl.CustomMessageBoxService = _customMessageBoxService;

            // Scene events
            PanelControl.SceneClearRequested += OnSceneClearRequested;
            PanelControl.SceneSetupRequested += SetupScene;
            PanelControl.CameraResetRequested += () => ViewportControl.ResetCamera();
            PanelControl.EmptyStateVisibilityChanged += (visibility) => EmptyStatePanel.Visibility = visibility;
            PanelControl.MainContentVisibilityChanged += (visibility) => MainContentGrid.Visibility = visibility;

            // Model events
            PanelControl.ModelReadyForViewport += (model) => {
                model.RootVisual.Transform = new TranslateTransform3D(0, SceneElements.GroundLevel, 0);
                ViewportControl.SetModel(model);
            };
            PanelControl.ModelRemovedFromViewport += (model) => ViewportControl.Viewport.Children.Remove(model.RootVisual);
            PanelControl.SkeletonReadyForViewport += (skeleton) => ViewportControl.SetSkeleton(skeleton);
            PanelControl.MapGeometryLoadRequested += (s, e) => OpenGeometryFile_Click(s, null);

            // Animation events
            PanelControl.AnimationReadyForDisplay += (s, anim) => ViewportControl.SetAnimation(anim);
            PanelControl.AnimationStopRequested += (s, animAsset) => ViewportControl.TogglePauseResume(animAsset);

            // Viewport events
            ViewportControl.SkyboxVisibilityChanged += OnSkyboxVisibilityChanged;

            Unloaded += (s, e) => {
                CleanupResources();
            };
        }

        private void OnSceneClearRequested(object sender, EventArgs e)
        {
            ViewportControl.ResetScene();
            ViewportControl.ResetCamera();
        }

        private void OnSkyboxVisibilityChanged(object sender, bool isVisible)
        {
            if (_skyVisual == null) return;

            if (isVisible && !ViewportControl.Viewport.Children.Contains(_skyVisual))
            {
                ViewportControl.Viewport.Children.Add(_skyVisual);
            }
            else if (!isVisible && ViewportControl.Viewport.Children.Contains(_skyVisual))
            {
                ViewportControl.Viewport.Children.Remove(_skyVisual);
            }
        }

        public void CleanupResources()
        {
            // Limpiar el controlador de la cámara para desuscribir eventos
            _cameraController?.Dispose();

            // Desuscribir eventos del ViewportControl
            if (ViewportControl != null)
            {
                ViewportControl.SkyboxVisibilityChanged -= OnSkyboxVisibilityChanged;
            }

            // Limpiar viewport
            ViewportControl?.Cleanup();
            
            // Limpiar ground y sky
            if (_groundVisual != null)
            {
                ViewportControl.Viewport.Children.Remove(_groundVisual);
                _groundVisual = null;
            }
            
            if (_skyVisual != null)
            {
                ViewportControl.Viewport.Children.Remove(_skyVisual);
                _skyVisual = null;
            }
            
            // Limpiar panel
            PanelControl?.Cleanup();
        }

        private void ViewportControl_MaximizeClicked(object sender, bool isMaximized)
        {
            if (isMaximized)
            {
                MainGridSplitter.Visibility = Visibility.Collapsed;
                PanelControl.Visibility = Visibility.Collapsed;
                Grid.SetColumnSpan(ViewportControl, 3);
            }
            else
            {
                MainGridSplitter.Visibility = Visibility.Visible;
                PanelControl.Visibility = Visibility.Visible;
                Grid.SetColumnSpan(ViewportControl, 1);
            }
        }

        private void SetupScene()
        {
            // If we are loading a map, stop here.
            if (_isMapGeometry)
            {
                return;
            }

            // Otherwise, create and add them
            _groundVisual = SceneElements.CreateGroundPlane(path => LoadSceneTexture(path), _logService.LogError, _appSettings.CustomFloorTexturePath);
            _skyVisual = SceneElements.CreateSidePlanes(path => LoadSceneTexture(path), _logService.LogError);

            ViewportControl.Viewport.Children.Add(_groundVisual);
            ViewportControl.Viewport.Children.Add(_skyVisual);
        }

        private BitmapSource LoadSceneTexture(string path)
        {
            try
            {
                // Check if the path is a file path (not a pack URI)
                if (File.Exists(path))
                {
                    using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        return TextureUtils.LoadTexture(fileStream, Path.GetExtension(path), 1024);
                    }
                }
                else
                {
                    // Assume it's a pack URI for resource loading
                    using (Stream resourceStream = Application.GetResourceStream(new Uri(path)).Stream)
                    {
                        // Resize scene textures to a maximum of 1024x1024 for a balance of quality and memory.
                        return TextureUtils.LoadTexture(resourceStream, Path.GetExtension(path), 1024);
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to load scene texture: {path}");
                return null;
            }
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            _isMapGeometry = false;
            DefaultEmptyStateContent.Visibility = Visibility.Visible;
            LoadingStateContent.Visibility = Visibility.Collapsed;
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("3D Model Files", "*.skn;*.skl"), new CommonFileDialogFilter("All Files", "*.*") },
                Title = "Select a skn file"
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var extension = Path.GetExtension(openFileDialog.FileName).ToLower();
                if (extension == ".skl")
                {
                    PanelControl.LoadSkeleton(openFileDialog.FileName);
                }
                else if (extension == ".skn")
                {
                    PanelControl.LoadInitialModel(openFileDialog.FileName);
                }
            }
        }

        private void OpenChromaFile_Click(object sender, RoutedEventArgs e)
        {
            _isMapGeometry = false;
            DefaultEmptyStateContent.Visibility = Visibility.Visible;
            LoadingStateContent.Visibility = Visibility.Collapsed;

            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("SKN files", "*.skn"), new CommonFileDialogFilter("All files", "*.*") },
                Title = "Select a skn file for the chroma"
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var folderBrowserDialog = new CommonOpenFileDialog
                {
                    IsFolderPicker = true,
                    Title = "Select the texture folder for the chroma"
                };

                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    PanelControl.ProcessModelLoading(openFileDialog.FileName, folderBrowserDialog.FileName, true);
                }
            }
        }

        private async void OpenGeometryFile_Click(object sender, RoutedEventArgs e)
        {
            _isMapGeometry = true;

            try
            {
                var openMapGeoDialog = new CommonOpenFileDialog
                {
                    Filters = { new CommonFileDialogFilter("MapGeometry Files", "*.mapgeo"), new CommonFileDialogFilter("All Files", "*.*") },
                    Title = "Select a mapgeo file"
                };

                if (openMapGeoDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    string mapGeoPath = openMapGeoDialog.FileName;
                    string materialsBinPath = Path.ChangeExtension(mapGeoPath, ".materials.bin");

                    if (!File.Exists(materialsBinPath))
                    {
                        var openMaterialsBinDialog = new CommonOpenFileDialog
                        {
                            Filters = { new CommonFileDialogFilter("Materials files", "*.materials.bin"), new CommonFileDialogFilter("All files", "*.*") },
                            Title = "Select a materials.bin file",
                            InitialDirectory = Path.GetDirectoryName(mapGeoPath)
                        };

                        if (openMaterialsBinDialog.ShowDialog() == CommonFileDialogResult.Ok)
                        {
                            materialsBinPath = openMaterialsBinDialog.FileName;
                        }
                        else
                        {
                            _customMessageBoxService.ShowWarning("Materials.bin Not Selected", "Map geometry cannot be loaded without the materials.bin file.");
                            return;
                        }
                    }

                    var openGameDataDialog = new CommonOpenFileDialog
                    {
                        IsFolderPicker = true,
                        Title = "Select map root for textures"
                    };

                    if (openGameDataDialog.ShowDialog() == CommonFileDialogResult.Ok)
                    {
                        DefaultEmptyStateContent.Visibility = Visibility.Collapsed;
                        LoadingStateContent.Visibility = Visibility.Visible;
                        await PanelControl.LoadMapGeometry(mapGeoPath, materialsBinPath, openGameDataDialog.FileName);
                    }
                    else
                    {
                        _customMessageBoxService.ShowWarning("Game Data Path Not Selected", "Map geometry cannot be loaded without the game data root folder.");
                    }
                }
            }
            finally
            {
                DefaultEmptyStateContent.Visibility = Visibility.Visible;
                LoadingStateContent.Visibility = Visibility.Collapsed;
            }
        }
    }
}