using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using AssetsManager.Views.Models;
using AssetsManager.Services.Models;
using AssetsManager.Services.Core;
using System.Threading.Tasks;
using AssetsManager.Utils;

namespace AssetsManager.Views.Controls.Models
{
    /// <summary>
    /// Interaction logic for ModelPanelControl.xaml
    /// </summary>
    public partial class ModelPanelControl : UserControl
    {
        private enum ModelType { Skn, MapGeometry }
        private ModelType _currentMode;

        public SknModelLoadingService SknModelLoadingService { get; set; }
        public MapGeometryLoadingService MapGeometryLoadingService { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        
        public event EventHandler<IAnimationAsset> AnimationReadyForDisplay;
        public event Action<SceneModel> ModelRemovedFromViewport;
        public event EventHandler<IAnimationAsset> AnimationStopRequested;
        public event EventHandler SceneClearRequested;
        public event EventHandler MapGeometryLoadRequested;

        public event Action<SceneModel> ModelReadyForViewport;
        public event Action<RigResource> SkeletonReadyForViewport;
        public event Action SceneSetupRequested;
        public event Action CameraResetRequested;
        public event Action<Visibility> EmptyStateVisibilityChanged;
        public event Action<Visibility> MainContentVisibilityChanged;

        public ListBox MeshesListBoxControl => MeshesListBox;
        public ListBox AnimationsListBoxControl => AnimationsListBox;
        public ListBox ModelsListBoxControl => ModelsListBox;

        private readonly Dictionary<string, IAnimationAsset> _animations = new();
        private readonly ObservableCollection<string> _animationNames = new();
        private readonly ObservableCollection<SceneModel> _loadedModels = new();
        private RigResource _skeleton;
        private SceneModel _sceneModel;

        public ModelPanelControl()
        {
            InitializeComponent();
            AnimationsListBoxControl.ItemsSource = _animationNames;
            ModelsListBoxControl.ItemsSource = _loadedModels;
        }

        private void DeleteModelButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SceneModel modelToDelete)
            {
                _loadedModels.Remove(modelToDelete);
                ModelRemovedFromViewport?.Invoke(modelToDelete);

                if (_loadedModels.Count == 0)
                {
                    _sceneModel = null;
                    _skeleton = null;
                    _animations.Clear();
                    _animationNames.Clear();
                    MeshesListBox.ItemsSource = null;
                    SceneClearRequested?.Invoke(this, EventArgs.Empty);

                    LoadModelButton.IsEnabled = true;

                    if (_currentMode == ModelType.MapGeometry)
                    {
                        LoadModelButton.Content = "Load MapGeometry";
                        LoadAnimationButton.IsEnabled = false;
                    }
                    else
                    {
                        LoadModelButton.Content = "Load Model";
                        LoadAnimationButton.IsEnabled = true;
                    }
                }
            }
        }

        private void LoadModelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMode == ModelType.Skn)
            {
                var openFileDialog = new CommonOpenFileDialog
                {
                    Filters = { new CommonFileDialogFilter("SKN files", "*.skn"), new CommonFileDialogFilter("All files", "*.*") },
                    Title = "Select a SKN File"
                };

                if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    LoadModel(openFileDialog.FileName, false);
                }
            }
            else
            {
                MapGeometryLoadRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        public void LoadInitialModel(string filePath)
        {
            LoadModel(filePath, true);
        }

        public void LoadSkeleton(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            {
                _skeleton = new RigResource(stream);
                SkeletonReadyForViewport?.Invoke(_skeleton);
            }
            LogService.LogDebug($"Loaded skeleton: {Path.GetFileName(filePath)}");
        }

        private void LoadModel(string filePath, bool isInitialLoad)
        {
            _currentMode = ModelType.Skn;
            string sklFilePath = Path.ChangeExtension(filePath, ".skl");
            if (File.Exists(sklFilePath))
            {
                using (var stream = File.OpenRead(sklFilePath))
                {
                    _skeleton = new RigResource(stream);
                    SkeletonReadyForViewport?.Invoke(_skeleton);
                }
            }
            
            _sceneModel = SknModelLoadingService.LoadModel(filePath);
            if (_sceneModel != null)
            {
                if (isInitialLoad)
                {
                    SceneSetupRequested?.Invoke();
                    EmptyStateVisibilityChanged?.Invoke(Visibility.Collapsed);
                    MainContentVisibilityChanged?.Invoke(Visibility.Visible);
                }
                
                ModelReadyForViewport?.Invoke(_sceneModel);
                MeshesListBox.ItemsSource = _sceneModel.Parts;
                
                _loadedModels.Clear();
                _loadedModels.Add(_sceneModel);

                CameraResetRequested?.Invoke();

                LoadModelButton.IsEnabled = false;
                LoadAnimationButton.IsEnabled = true;
            }
        }

        private void LoadAnimationButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("Animation files", "*.anm"), new CommonFileDialogFilter("All files", "*.*") },
                Title = "Select Animation Files",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                foreach (string fileName in openFileDialog.FileNames)
                {
                    LoadAnimation(fileName);
                }
            }
        }

        private void LoadAnimation(string filePath)
        {
            if (_skeleton == null)
            {
                CustomMessageBoxService.ShowWarning("Missing Skeleton", "Please load a skeleton (.skl) file first.");
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
        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string animationName && _animations.TryGetValue(animationName, out var animationAsset))
            {
                AnimationsListBox.SelectedItem = animationName;
                AnimationReadyForDisplay?.Invoke(this, animationAsset);
            }
        }

        public async Task LoadMapGeometry(string filePath, string materialsPath, string gameDataPath)
        {
            _currentMode = ModelType.MapGeometry;
            if (!string.IsNullOrEmpty(materialsPath))
            {
                _sceneModel = await MapGeometryLoadingService.LoadMapGeometry(filePath, materialsPath, gameDataPath);
            }
            else
            {
                _sceneModel = await MapGeometryLoadingService.LoadMapGeometry(filePath, gameDataPath);
            }

            if (_sceneModel != null)
            {
                SceneSetupRequested?.Invoke();
                EmptyStateVisibilityChanged?.Invoke(Visibility.Collapsed);
                MainContentVisibilityChanged?.Invoke(Visibility.Visible);

                ModelReadyForViewport?.Invoke(_sceneModel);
                MeshesListBox.ItemsSource = _sceneModel.Parts;

                _loadedModels.Clear();
                _loadedModels.Add(_sceneModel);

                CameraResetRequested?.Invoke();

                LoadModelButton.IsEnabled = false;
                LoadAnimationButton.IsEnabled = false;
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string animationName && _animations.TryGetValue(animationName, out var animationAsset))
            {
                AnimationStopRequested?.Invoke(this, animationAsset);
            }
        }
    }
}