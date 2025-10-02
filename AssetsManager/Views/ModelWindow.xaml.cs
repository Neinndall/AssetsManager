using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Views.Camera;
using AssetsManager.Services.Core;
using AssetsManager.Services.Models;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models;

namespace AssetsManager.Views
{
    public partial class ModelWindow : UserControl
    {
        private readonly ModelLoadingService _modelLoadingService;
        private readonly LogService _logService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly CustomCameraController _cameraController;
        private SceneModel _sceneModel;

        private readonly Dictionary<string, IAnimationAsset> _animations = new();
        private readonly ObservableCollection<string> _animationNames = new();
        private readonly ObservableCollection<SceneModel> _loadedModels = new();
        private RigResource _skeleton;

        public ModelWindow(ModelLoadingService modelLoadingService, LogService logService, CustomMessageBoxService customMessageBoxService)
        {
            InitializeComponent();
            _modelLoadingService = modelLoadingService;
            _logService = logService;
            _customMessageBoxService = customMessageBoxService;
            _cameraController = new CustomCameraController(ViewportControl.Viewport);

            ViewportControl.LogService = _logService;

            // ItemSources
            PanelControl.AnimationsListBoxControl.ItemsSource = _animationNames;
            PanelControl.ModelsListBoxControl.ItemsSource = _loadedModels;
            
            // Event Subscriptions
            PanelControl.AnimationFileLoaded += PanelControl_AnimationFileLoaded;
            PanelControl.ModelFileLoaded += PanelControl_ModelFileLoaded;
            PanelControl.ModelDeleted += PanelControl_ModelDeleted;
            PanelControl.AnimationSelected += AnimationsListBox_SelectionChanged;
            PanelControl.AnimationStopRequested += PanelControl_AnimationStopRequested;
        }

        private void PanelControl_AnimationStopRequested(object sender, System.EventArgs e)
        {
            ViewportControl.TogglePauseResume();
        }

        private void SetupScene()
        {
            var ground = SceneElements.CreateGroundPlane(path => _modelLoadingService.LoadTexture(path), _logService.LogError);
            var sky = SceneElements.CreateSidePlanes(path => _modelLoadingService.LoadTexture(path), _logService.LogError);

            ViewportControl.Viewport.Children.Add(ground);
            ViewportControl.Viewport.Children.Add(sky);
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("3D Model Files", "*.skn;*.skl"), new CommonFileDialogFilter("All Files", "*.*") }
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var extension = Path.GetExtension(openFileDialog.FileName).ToLower();
                if (extension == ".skl")
                {
                    using (var stream = File.OpenRead(openFileDialog.FileName))
                    {
                        _skeleton = new RigResource(stream);
                        ViewportControl.SetSkeleton(_skeleton);
                    }
                    _logService.LogDebug($"Loaded skeleton: {Path.GetFileName(openFileDialog.FileName)}");
                }
                else if (extension == ".skn")
                {
                    LoadModel(openFileDialog.FileName, true);
                }
            }
        }

        private void PanelControl_AnimationFileLoaded(object sender, string filePath)
        {
            LoadAnimation(filePath);
        }

        private void PanelControl_ModelFileLoaded(object sender, string filePath)
        {
            LoadModel(filePath, false);
        }

        private void PanelControl_ModelDeleted(object sender, SceneModel modelToDelete)
        {
            if (modelToDelete == null) return;

            _loadedModels.Remove(modelToDelete);
            ViewportControl.Viewport.Children.Remove(modelToDelete.RootVisual);

            // If no models are left, just reset the state but keep the view active.
            if (_loadedModels.Count == 0)
            {
                _sceneModel = null;
                _skeleton = null;
                _animations.Clear();
                _animationNames.Clear();
                PanelControl.MeshesListBoxControl.ItemsSource = null;
            }
        }

        private void LoadModel(string filePath, bool isInitialLoad)
        {
            string sklFilePath = Path.ChangeExtension(filePath, ".skl");
            if (File.Exists(sklFilePath))
            {
                using (var stream = File.OpenRead(sklFilePath))
                {
                    _skeleton = new RigResource(stream);
                    ViewportControl.SetSkeleton(_skeleton);
                }
            }
            
            _sceneModel = _modelLoadingService.LoadModel(filePath);
            if (_sceneModel != null)
            {
                if (isInitialLoad)
                {
                    SetupScene();
                    EmptyStatePanel.Visibility = Visibility.Collapsed;
                    MainContentGrid.Visibility = Visibility.Visible;
                }
                
                ViewportControl.SetModel(_sceneModel);
                PanelControl.MeshesListBoxControl.ItemsSource = _sceneModel.Parts;
                
                _loadedModels.Clear();
                _loadedModels.Add(_sceneModel);

                ViewportControl.ResetCamera();
            }
        }

        private void LoadAnimation(string filePath)
        {
            if (_skeleton == null)
            {
                _customMessageBoxService.ShowWarning("Missing Skeleton", "Please load a skeleton (.skl) file first.");
                return;
            }
            using (var stream = File.OpenRead(filePath))
            {
                var animationAsset = AnimationAsset.Load(stream);
                var animationName = Path.GetFileNameWithoutExtension(filePath);

                if (!_animations.ContainsKey(animationName))
                {
                    _animations[animationName] = animationAsset;
                    _animationNames.Add(animationName);
                }
            }
        }

        private void AnimationsListBox_SelectionChanged(object sender, string selectedAnimationName)
        {
            if (selectedAnimationName != null && _animations.TryGetValue(selectedAnimationName, out var animationAsset))
            {
                ViewportControl.SetAnimation(animationAsset);
            }
        }
    }
}
