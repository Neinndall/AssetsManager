using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AssetsManager.Utils.Framework;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer;
using AssetsManager.Services.Core;
using Material.Icons;
using System.Threading.Tasks;
using AssetsManager.Utils;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Helpers;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Linq;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class ViewerPanelControl : UserControl
    {
        private readonly ViewerPanelModel _viewModel;
        public ViewerPanelModel ViewModel => _viewModel;

        private enum ViewerType { Skn, MapGeometry }
        private ViewerType _currentMode;

        public SknLoadingService SknLoadingService { get; set; }
        public ScoLoadingService ScoLoadingService { get; set; }
        public MapGeometryLoadingService MapGeometryLoadingService { get; set; }
        public ChromaScannerService ChromaScannerService { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }

        // Peer Controls (Direct communication)
        public ViewerViewportControl Viewport { get; set; }
        public ChromaSelectionControl ChromaGallery { get; set; }

        public event Action<Visibility> EmptyStateVisibilityChanged;
        public event Action<Visibility> MainContentVisibilityChanged;

        public ObservableRangeCollection<AnimationModel> AnimationModels => _viewModel.AnimationModels;

        private AnimationModel _currentlyPlayingAnimation;

        public ViewerPanelControl()
        {
            _viewModel = new ViewerPanelModel();
            DataContext = _viewModel;

            InitializeComponent();

            _viewModel.MainContentRequested += () => 
            {
                EmptyStateVisibilityChanged?.Invoke(Visibility.Collapsed);
                MainContentVisibilityChanged?.Invoke(Visibility.Visible);
            };

            _viewModel.EmptyStateRequested += () => 
            {
                MainContentVisibilityChanged?.Invoke(Visibility.Collapsed);
                EmptyStateVisibilityChanged?.Invoke(Visibility.Visible);
            };

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewerPanelModel.SelectedModel))
                {
                    HandleSelectedModelChanged();
                }
            };

            Unloaded += (s, e) => Cleanup();
        }

        private void HandleSelectedModelChanged()
        {
            var selectedModel = _viewModel.SelectedModel;
            if (selectedModel == null)
            {
                _viewModel.SelectedModelParts = null;
                _viewModel.AnimationModels.Clear();
                _viewModel.SelectedAnimation = null;
                return;
            }

            _viewModel.SelectedAnimation = null; // Limpiar selección previa
            Viewport?.SetActiveModel(selectedModel);
            _viewModel.SelectedModelParts = selectedModel.Parts;

            if (selectedModel.Animations != null)
            {
                var animModels = selectedModel.Animations.Select(a => new AnimationModel(a));
                _viewModel.AnimationModels.ReplaceRange(animModels);
            }
            else
            {
                _viewModel.AnimationModels.Clear();
            }

            PositionXLock.IsChecked = false;
            PositionYLock.IsChecked = false;
            PositionZLock.IsChecked = false;
            RotationXLock.IsChecked = false;
            RotationYLock.IsChecked = false;
            RotationZLock.IsChecked = false;
            ScaleLock.IsChecked = false;
        }

        public void Cleanup()
        {
            ResetScene();
            Viewport = null;
            ChromaGallery = null;
        }

        public void ResetScene()
        {
            // 1. CRÍTICO: Liberar recursos de TODOS los modelos
            foreach (var model in _viewModel.LoadedModels)
            {
                model?.Dispose();
            }
            _viewModel.LoadedModels.Clear();

            _currentlyPlayingAnimation = null;

            _viewModel.SelectedModelParts = null; // MVVM Cleanup
            _viewModel.AnimationModels.Clear();
            _viewModel.SelectedModel = null;

            LoadModelButton.IsEnabled = true;
            LoadChromaModelButton.IsEnabled = true;

            ViewModel.IsMapMode = (_currentMode == ViewerType.MapGeometry);
            if (!ViewModel.IsMapMode)
            {
                LoadAnimationButton.IsEnabled = true;
            }
        }

        private void DeleteModelButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SceneModel modelToDelete)
            {
                modelToDelete?.Dispose();
                _viewModel.LoadedModels.Remove(modelToDelete);
                Viewport?.RemoveModel(modelToDelete);

                if (_viewModel.LoadedModels.Count == 0)
                {
                    ResetScene();
                    Viewport?.ResetScene();
                    Viewport?.ResetCamera();
                }
                else
                {
                    ModelsListBox.SelectedIndex = 0;
                }
            }
        }

        private void LoadModelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMode == ViewerType.Skn)
            {
                var openFileDialog = new CommonOpenFileDialog
                {
                    Filters = { 
                        new CommonFileDialogFilter("SKN files", "*.skn"), 
                        new CommonFileDialogFilter("SCO/SCB files", "*.sco;*.scb"),
                        new CommonFileDialogFilter("All files", "*.*") 
                    },
                    Title = "Select a model file"
                };

                if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ProcessModelLoading(openFileDialog.FileName, null, false);
                }
            }
            else
            {
                Viewport?.HandleMapGeometryLoadRequest();
            }
        }

        private void LoadChromaModelButton_Click(object sender, RoutedEventArgs e)
        {
            var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select the skins folder of the character"
            };

            if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                HandleChromaGalleryRequest(folderBrowserDialog.FileName);
            }
        }

        /// <summary>
        /// Orchestrates the opening of the Chroma Gallery.
        /// </summary>
        public async void HandleChromaGalleryRequest(string skinsPath)
        {
            if (ChromaGallery == null) return;
            
            ViewModel.IsChromaGalleryVisible = true;
            await ChromaGallery.InitializeAsync(skinsPath);
        }

        /// <summary>
        /// Handles the selection of a chroma from the gallery.
        /// </summary>
        public void HandleChromaSelected(ChromaSkinModel skin)
        {
            ViewModel.IsChromaGalleryVisible = false;
            
            if (!string.IsNullOrEmpty(skin.ModelPath))
            {
                ProcessModelLoading(skin.ModelPath, skin.TexturePath, true);
            }
            else
            {
                CustomMessageBoxService.ShowWarning("Model Not Found", "Could not automatically find the .skn model for this skin folder.", Window.GetWindow(this));
            }
        }

        public void LoadInitialModel(string filePath)
        {
            ProcessModelLoading(filePath, null, true);
        }

        public void LoadSkeleton(string filePath)
        {
            if (_viewModel.SelectedModel == null)
            {
                CustomMessageBoxService.ShowWarning("No Model Selected", "Please select a model to associate the skeleton with.", Window.GetWindow(this));
                return;
            }
            using (var stream = File.OpenRead(filePath))
            {
                _viewModel.SelectedModel.Skeleton = new RigResource(stream);
            }
            LogService.LogDebug($"Loaded skeleton: {Path.GetFileName(filePath)} for model {_viewModel.SelectedModel.Name}");
        }

        public void ProcessModelLoading(string modelPath, string texturePath, bool isInitialLoad)
        {
            _currentMode = ViewerType.Skn;
            ViewModel.IsMapMode = false;

            SceneModel newModel = null;
            string extension = Path.GetExtension(modelPath).ToLowerInvariant();

            if (extension == ".sco" || extension == ".scb")
            {
                newModel = ScoLoadingService.LoadModel(modelPath);
            }
            else
            {
                if (string.IsNullOrEmpty(texturePath))
                {
                    newModel = SknLoadingService.LoadModel(modelPath);
                }
                else
                {
                    newModel = SknLoadingService.LoadModel(modelPath, texturePath);
                }
            }

            if (newModel != null)
            {
                if (extension == ".skn")
                {
                    string sklFilePath = Path.ChangeExtension(modelPath, ".skl");
                    if (File.Exists(sklFilePath))
                    {
                        using (var stream = File.OpenRead(sklFilePath))
                        {
                            newModel.Skeleton = new RigResource(stream);
                        }
                    }
                }

                if (isInitialLoad)
                {
                    Viewport?.HandleSceneSetupRequest();
                    ViewModel.ShowMainContent(); // MVVM State Update
                }

                // Initialize Transform
                newModel.PositionY = SceneElements.GroundLevel;

                Viewport?.AddModel(newModel);
                _viewModel.SelectedModelParts = newModel.Parts;

                _viewModel.LoadedModels.Add(newModel);
                _viewModel.SelectedModel = newModel;
                ModelsListBox.SelectedItem = newModel;

                Viewport?.ResetCamera();
                LoadAnimationButton.IsEnabled = true;
            }
        }

        private void LoadAnimationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedModel == null && _viewModel.LoadedModels.Count == 1)
            {
                ModelsListBox.SelectedIndex = 0;
            }

            if (_viewModel.SelectedModel == null)
            {
                CustomMessageBoxService.ShowWarning("No Model Selected", "Please select a model from the 'Models' tab first.", Window.GetWindow(this));
                return;
            }

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
            if (_viewModel.SelectedModel == null)
            {
                CustomMessageBoxService.ShowWarning("No Model Selected", "Please select a model from the 'Models' tab first.", Window.GetWindow(this));
                return;
            }

            if (_viewModel.SelectedModel.Skeleton == null)
            {
                CustomMessageBoxService.ShowWarning("Missing Skeleton", "Please load a skeleton (.skl) file first.", Window.GetWindow(this));
                return;
            }
            using (var stream = File.OpenRead(filePath))
            {
                var animationAsset = AnimationAsset.Load(stream);
                var animationName = Path.GetFileNameWithoutExtension(filePath);

                if (!_viewModel.AnimationModels.Any(a => a.Name == animationName))
                {
                    var animationData = new AnimationData { AnimationAsset = animationAsset, Name = animationName };
                    var animationModel = new AnimationModel(animationData);

                    _viewModel.SelectedModel.Animations.AddRange(new[] { animationData });
                    _viewModel.AnimationModels.AddRange(new[] { animationModel });
                }
            }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedModel == null)
            {
                CustomMessageBoxService.ShowWarning("No Model Selected", "Please select a model from the 'Models' tab first.", Window.GetWindow(this));
                return;
            }

            if (AnimationsListBox.SelectedItem is AnimationModel animationModel)
            {
                if (_currentlyPlayingAnimation != null && _currentlyPlayingAnimation != animationModel)
                {
                    _currentlyPlayingAnimation.IsPlaying = false;
                }

                _currentlyPlayingAnimation = animationModel;
                _currentlyPlayingAnimation.IsPlaying = true;

                Viewport?.SetAnimation(animationModel);
            }
        }

        public async Task LoadMapGeometry(string filePath, string materialsPath, string gameDataPath)
        {
            _currentMode = ViewerType.MapGeometry;
            ViewModel.IsMapMode = true;

            SceneModel newModel;
            if (!string.IsNullOrEmpty(materialsPath))
            {
                newModel = await MapGeometryLoadingService.LoadMapGeometry(filePath, materialsPath, gameDataPath);
            }
            else
            {
                newModel = await MapGeometryLoadingService.LoadMapGeometry(filePath, gameDataPath);
            }

            if (newModel != null)
            {
                Viewport?.HandleSceneSetupRequest();
                ViewModel.ShowMainContent(); // MVVM State Update

                Viewport?.AddModel(newModel);
                _viewModel.SelectedModelParts = newModel.Parts;

                foreach (var model in _viewModel.LoadedModels)
                {
                    model?.Dispose();
                }
                _viewModel.LoadedModels.Clear();
                _viewModel.LoadedModels.Add(newModel);

                Viewport?.ResetCamera();
                LoadModelButton.IsEnabled = false;
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (AnimationsListBox.SelectedItem is AnimationModel animationModel)
            {
                Viewport?.TogglePauseResume(animationModel);
            }
        }

        public void SetAnimationPlayingState(AnimationModel animationModel, bool isPlaying)
        {
            if (animationModel != null)
            {
                animationModel.IsPlaying = isPlaying;
            }
        }

        public void UpdateAnimationProgress(double currentTime)
        {
            if (_currentlyPlayingAnimation != null && _currentlyPlayingAnimation.IsPlaying)
            {
                _currentlyPlayingAnimation.CurrentTime = currentTime;
            }
        }

        private bool _isSliderDragging = false;

        private void AnimationSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _isSliderDragging = true;
        }

        private void AnimationSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _isSliderDragging = false;
        }

        private void AnimationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AnimationsListBox.SelectedItem is AnimationModel animationModel && _isSliderDragging)
            {
                Viewport?.SeekAnimation(TimeSpan.FromSeconds(e.NewValue));
            }
        }

        private void ResetPosition_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedModel == null) return;
            if (PositionXLock.IsChecked == false) _viewModel.SelectedModel.PositionX = 0;
            if (PositionYLock.IsChecked == false) _viewModel.SelectedModel.PositionY = SceneElements.GroundLevel;
            if (PositionZLock.IsChecked == false) _viewModel.SelectedModel.PositionZ = 0;
        }

        private void ResetRotation_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedModel == null) return;
            if (RotationXLock.IsChecked == false) _viewModel.SelectedModel.RotationX = 0;
            if (RotationYLock.IsChecked == false) _viewModel.SelectedModel.RotationY = 0;
            if (RotationZLock.IsChecked == false) _viewModel.SelectedModel.RotationZ = 0;
        }

        private void ResetScale_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedModel == null) return;
            if (ScaleLock.IsChecked == false) _viewModel.SelectedModel.Scale = 1;
        }

        public void ApplyAutoRotation(double angle)
        {
            if (_viewModel.SelectedModel != null)
            {
                _viewModel.SelectedModel.RotationY = (_viewModel.SelectedModel.RotationY + angle) % 360;
            }
        }

        // STUDIO HANDLERS
        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            Viewport?.InitiateSnapshot(4.0);
        }

        private void ResetStudio_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.ViewportViewModel != null)
            {
                _viewModel.ViewportViewModel.AmbientIntensity = 100;
                _viewModel.ViewportViewModel.LightRotation = 0;
                _viewModel.ViewportViewModel.LightHeight = 0;
                _viewModel.ViewportViewModel.FieldOfView = 45;
                _viewModel.ViewportViewModel.IsTransparentBg = false;
                _viewModel.ViewportViewModel.ShowSkybox = true;
            }
        }
    }
}
