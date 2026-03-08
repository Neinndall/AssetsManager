using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer;
using AssetsManager.Views.Helpers;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;
using System.Threading.Tasks;
using LeagueToolkit.Core.Animation;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views
{
    public partial class ViewerWindow : UserControl
    {
        private readonly SknLoadingService _sknLoadingService;
        private readonly ScoLoadingService _scoLoadingService;
        private readonly MapGeometryLoadingService _mapGeometryLoadingService;
        private readonly LogService _logService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly CustomCameraController _cameraController;
        private bool _isMapGeometry = false;
        private ModelVisual3D _groundVisual;
        private ModelVisual3D _skyVisual;

        private readonly AppSettings _appSettings;

        public ViewerWindow(SknLoadingService sknLoadingService, ScoLoadingService scoLoadingService, MapGeometryLoadingService mapGeometryLoadingService, LogService logService, CustomMessageBoxService customMessageBoxService, AppSettings appSettings)
        {
            InitializeComponent();
            _sknLoadingService = sknLoadingService;
            _scoLoadingService = scoLoadingService;
            _mapGeometryLoadingService = mapGeometryLoadingService;
            _logService = logService;
            _customMessageBoxService = customMessageBoxService;
            _appSettings = appSettings;
            _cameraController = new CustomCameraController(ViewportControl.Viewport);

            // Inject services into controls
            ViewportControl.LogService = _logService;
            PanelControl.SknLoadingService = _sknLoadingService;
            PanelControl.ScoLoadingService = _scoLoadingService;
            PanelControl.MapGeometryLoadingService = _mapGeometryLoadingService;
            PanelControl.LogService = _logService;
            PanelControl.CustomMessageBoxService = _customMessageBoxService;
            
            // Link Panel to Viewport directly for Studio controls
            PanelControl.Viewport = ViewportControl;
            ViewportControl.Panel = PanelControl;

            // Scene events
            ViewportControl.SceneSetupRequested += SetupScene;
            ViewportControl.MapGeometryLoadRequested += OnMapGeometryLoadRequested;
            PanelControl.EmptyStateVisibilityChanged += OnEmptyStateVisibilityChanged;
            PanelControl.MainContentVisibilityChanged += OnMainContentVisibilityChanged;

            Unloaded += (s, e) =>
            {
                CleanupResources();
            };
        }

        // Event handlers extraídos de lambdas
        private void OnEmptyStateVisibilityChanged(Visibility visibility) => EmptyStatePanel.Visibility = visibility;
        private void OnMainContentVisibilityChanged(Visibility visibility) => MainContentGrid.Visibility = visibility;
        private void OnMapGeometryLoadRequested() => OpenGeometryFile_Click(null, null);

        public void CleanupResources()
        {
            // 1. CRÍTICO: Desuscribirse de TODOS los eventos para evitar memory leaks
            if (PanelControl != null)
            {
                PanelControl.EmptyStateVisibilityChanged -= OnEmptyStateVisibilityChanged;
                PanelControl.MainContentVisibilityChanged -= OnMainContentVisibilityChanged;
            }

            if (ViewportControl != null)
            {
                ViewportControl.SceneSetupRequested -= SetupScene;
                ViewportControl.MapGeometryLoadRequested -= OnMapGeometryLoadRequested;
            }

            // 2. Limpiar el controlador de la cámara
            _cameraController?.Dispose();

            // 3. Limpiar viewport
            ViewportControl?.Cleanup();

            // 4. Limpiar ground y sky
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

            // 5. Limpiar panel
            PanelControl?.Cleanup();
        }

        private void ViewportControl_MaximizeClicked(object sender, bool isMaximized)
        {
            if (isMaximized)
            {
                MainGridSplitter.Visibility = Visibility.Collapsed;
                PanelControl.Visibility = Visibility.Collapsed;
                Grid.SetColumnSpan(ViewportControl, 3);
                ViewportControl.Margin = new Thickness(0, 0, 4, 0);
            }
            else
            {
                MainGridSplitter.Visibility = Visibility.Visible;
                PanelControl.Visibility = Visibility.Visible;
                Grid.SetColumnSpan(ViewportControl, 1);
                ViewportControl.Margin = new Thickness(0);
            }
        }

        private void SetupScene()
        {
            // If we are loading a map, stop here.
            if (_isMapGeometry)
            {
                return;
            }

            // Cleanup previous environment if exists
            if (_groundVisual != null && ViewportControl.Viewport.Children.Contains(_groundVisual))
                ViewportControl.Viewport.Children.Remove(_groundVisual);
            
            if (_skyVisual != null && ViewportControl.Viewport.Children.Contains(_skyVisual))
                ViewportControl.Viewport.Children.Remove(_skyVisual);

            // Otherwise, create and add them
            _groundVisual = SceneElements.CreateGroundPlane(path => LoadSceneTexture(path), _logService.LogError, _appSettings.CustomFloorTexturePath);
            _skyVisual = SceneElements.CreateSidePlanes(path => LoadSceneTexture(path), _logService.LogError);

            ViewportControl.Viewport.Children.Add(_groundVisual);
            ViewportControl.Viewport.Children.Add(_skyVisual);
            
            ViewportControl.RegisterEnvironment(_skyVisual, _groundVisual);
            
            // Re-apply current environment settings from panel
            PanelControl?.UpdateEnvironment();
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
                        return TextureUtils.LoadTexture(fileStream, Path.GetExtension(path), 2048);
                    }
                }
                else
                {
                    // Assume it's a pack URI for resource loading
                    using (Stream resourceStream = Application.GetResourceStream(new Uri(path)).Stream)
                    {
                        // Resize scene textures to a maximum of 2048x2048 for a balance of quality and memory.
                        return TextureUtils.LoadTexture(resourceStream, Path.GetExtension(path), 2048);
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
                Filters = { 
                    new CommonFileDialogFilter("3D Model Files", "*.skn;*.skl;*.sco;*.scb"), 
                    new CommonFileDialogFilter("All Files", "*.*") 
                },
                Title = "Select a model file"
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var extension = Path.GetExtension(openFileDialog.FileName).ToLower();
                if (extension == ".skl")
                {
                    PanelControl.LoadSkeleton(openFileDialog.FileName);
                }
                else if (extension == ".skn" || extension == ".sco" || extension == ".scb")
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
                            _customMessageBoxService.ShowWarning("Warning", "Map geometry cannot be loaded without the materials.bin file.", Window.GetWindow(this));
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
                        _customMessageBoxService.ShowWarning("Warning", "MapGeometry cannot be loaded without the game data root folder.", Window.GetWindow(this));
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
