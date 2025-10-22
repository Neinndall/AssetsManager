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
using Material.Icons;
using System.Threading.Tasks;
using AssetsManager.Utils;

namespace AssetsManager.Views.Controls.Models
{
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

            Unloaded += (s, e) => Cleanup();
        }

        public void Cleanup()
        {
            ResetScene();
        }

        public void ResetScene()
        {
            // 1. Limpiar animaciones
            _animations.Clear();
            _animationNames.Clear();
            
            // 2. CRÍTICO: Liberar recursos de TODOS los modelos
            foreach (var model in _loadedModels)
            {
                model?.Dispose();  // Libera texturas, geometrías y materiales
            }
            _loadedModels.Clear();
            
            // 3. Limpiar referencias
            _sceneModel?.Dispose();
            _sceneModel = null;
            _skeleton = null;
            
            // 4. Limpiar UI
            MeshesListBox.ItemsSource = null;
            AnimationsListBox.SelectedItem = null;
            ModelsListBox.SelectedItem = null;

            LoadModelButton.IsEnabled = true;
            LoadChromaModelButton.IsEnabled = true;

            if (_currentMode == ModelType.MapGeometry)
            {
                LoadModelIcon.Kind = MaterialIconKind.Map;
                LoadModelButton.ToolTip = "Load MapGeometry";
                LoadAnimationButton.IsEnabled = false;
                LoadChromaModelButton.IsEnabled = false;
            }
            else
            {
                LoadModelIcon.Kind = MaterialIconKind.CubeOutline;
                LoadModelButton.ToolTip = "Load Model";
                LoadAnimationButton.IsEnabled = true;
            }
        }

        private void DeleteModelButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SceneModel modelToDelete)
            {
                // Liberar recursos del modelo antes de eliminarlo
                modelToDelete?.Dispose();
                
                _loadedModels.Remove(modelToDelete);
                ModelRemovedFromViewport?.Invoke(modelToDelete);

                if (_loadedModels.Count == 0)
                {
                    ResetScene();
                    SceneClearRequested?.Invoke(this, EventArgs.Empty);
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
                    Title = "Select a skn file"
                };

                if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ProcessModelLoading(openFileDialog.FileName, null, false);
                }
            }
            else
            {
                MapGeometryLoadRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void LoadChromaModelButton_Click(object sender, RoutedEventArgs e)
        {
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
                    Title = "Select the Texture Folder for the Chroma"
                };

                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ProcessModelLoading(openFileDialog.FileName, folderBrowserDialog.FileName, false);
                }
            }
        }

        public void LoadInitialModel(string filePath)
        {
            ProcessModelLoading(filePath, null, true);
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

        public void ProcessModelLoading(string modelPath, string texturePath, bool isInitialLoad)
        {
            _currentMode = ModelType.Skn;
            LoadModelIcon.Kind = MaterialIconKind.CubeOutline;
            LoadModelButton.ToolTip = "Load Model";

            string sklFilePath = Path.ChangeExtension(modelPath, ".skl");
            if (File.Exists(sklFilePath))
            {
                using (var stream = File.OpenRead(sklFilePath))
                {
                    _skeleton = new RigResource(stream);
                    SkeletonReadyForViewport?.Invoke(_skeleton);
                }
            }

            _sceneModel?.Dispose();

            if (string.IsNullOrEmpty(texturePath))
            {
                _sceneModel = SknModelLoadingService.LoadModel(modelPath);
            }
            else
            {
                _sceneModel = SknModelLoadingService.LoadModel(modelPath, texturePath);
            }

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

                foreach (var model in _loadedModels)
                {
                    model?.Dispose();
                }
                _loadedModels.Clear();
                _loadedModels.Add(_sceneModel);

                CameraResetRequested?.Invoke();

                LoadModelButton.IsEnabled = false;
                LoadChromaModelButton.IsEnabled = false;
                LoadAnimationButton.IsEnabled = true;
            }
        }

        private void LoadAnimationButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("Animation files", "*.anm"), new CommonFileDialogFilter("All files", "*.*") },
                Title = "Select animation files",
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
            if (sender is Button button && button.Tag is string animationName && 
                _animations.TryGetValue(animationName, out var animationAsset))
            {
                AnimationsListBox.SelectedItem = animationName;
                AnimationReadyForDisplay?.Invoke(this, animationAsset);
            }
        }

        public async Task LoadMapGeometry(string filePath, string materialsPath, string gameDataPath)
        {
            _currentMode = ModelType.MapGeometry;
            LoadModelIcon.Kind = MaterialIconKind.Map;
            LoadModelButton.ToolTip = "Load MapGeometry";

            // 🆕 Dispose del modelo anterior
            _sceneModel?.Dispose();

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

                // Limpiar y disponer modelos antiguos en la colección
                foreach (var model in _loadedModels)
                {
                    model?.Dispose();
                }
                _loadedModels.Clear();
                _loadedModels.Add(_sceneModel);

                CameraResetRequested?.Invoke();

                LoadModelButton.IsEnabled = false;
                LoadAnimationButton.IsEnabled = false;
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string animationName && 
                _animations.TryGetValue(animationName, out var animationAsset))
            {
                AnimationStopRequested?.Invoke(this, animationAsset);
            }
        }
    }
}