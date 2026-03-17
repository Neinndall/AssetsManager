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
    public class ViewerTransformData
    {
        public Vector3D Position { get; set; } = new Vector3D(0, 0, 0);
        public Vector3D Rotation { get; set; } = new Vector3D(0, 0, 0);
        public double Scale { get; set; } = 1.0;
    }

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

        // Peer Controls (Direct communication v3.2.2.0)
        public ViewerViewportControl Viewport { get; set; }
        public ChromaSelectionControl ChromaGallery { get; set; }

        public event Action<Visibility> EmptyStateVisibilityChanged;
        public event Action<Visibility> MainContentVisibilityChanged;

        public ObservableRangeCollection<AnimationModel> AnimationModels => _viewModel.AnimationModels;

        private readonly Dictionary<SceneModel, ViewerTransformData> _transformData = new();
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

            ModelsListBox.ItemsSource = _viewModel.LoadedModels;
            AnimationsListBox.ItemsSource = _viewModel.AnimationModels;

            Unloaded += (s, e) => Cleanup();
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
            _transformData.Clear();

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
                _transformData.Remove(modelToDelete);
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
        /// Handles the selection of a chroma from the gallery (v3.2.2.0).
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

                var transformData = new ViewerTransformData { Position = new Vector3D(0, SceneElements.GroundLevel, 0) };
                _transformData.Add(newModel, transformData);
                UpdateTransform(newModel, transformData);

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

        private void ModelsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0)
            {
                _viewModel.SelectedModel = null;
                _viewModel.SelectedModelParts = null;
                _viewModel.AnimationModels.Clear();
                return;
            }

            if (e.AddedItems[0] is SceneModel selectedModel)
            {
                _viewModel.SelectedModel = selectedModel;
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

                if (_transformData.TryGetValue(selectedModel, out var transformData))
                {
                    PositionXSlider.Value = transformData.Position.X;
                    PositionYSlider.Value = transformData.Position.Y;
                    PositionZSlider.Value = transformData.Position.Z;
                    RotationXSlider.Value = transformData.Rotation.X;
                    RotationYSlider.Value = transformData.Rotation.Y;
                    RotationZSlider.Value = transformData.Rotation.Z;
                    ScaleSlider.Value = transformData.Scale;
                }

                PositionXLock.IsChecked = false;
                PositionYLock.IsChecked = false;
                PositionZLock.IsChecked = false;
                RotationXLock.IsChecked = false;
                RotationYLock.IsChecked = false;
                RotationZLock.IsChecked = false;
                ScaleLock.IsChecked = false;
            }
        }

        private void TransformSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null || _viewModel.SelectedModel == null || !_transformData.TryGetValue(_viewModel.SelectedModel, out var transformData))
            {
                return;
            }

            transformData.Position = new Vector3D(PositionXSlider.Value, PositionYSlider.Value, PositionZSlider.Value);
            transformData.Rotation = new Vector3D(RotationXSlider.Value, RotationYSlider.Value, RotationZSlider.Value);
            transformData.Scale = ScaleSlider.Value;

            UpdateTransform(_viewModel.SelectedModel, transformData);
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

        private void UpdateTransform(SceneModel model, ViewerTransformData data)
        {
            var transformGroup = new Transform3DGroup();
            transformGroup.Children.Add(new ScaleTransform3D(data.Scale, data.Scale, data.Scale));

            var rotationX = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), data.Rotation.X));
            var rotationY = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), data.Rotation.Y));
            var rotationZ = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), data.Rotation.Z));
            transformGroup.Children.Add(rotationX);
            transformGroup.Children.Add(rotationY);
            transformGroup.Children.Add(rotationZ);

            transformGroup.Children.Add(new TranslateTransform3D(data.Position.X, data.Position.Y, data.Position.Z));
            model.RootVisual.Transform = transformGroup;
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
            if (PositionXLock.IsChecked == false) PositionXSlider.Value = 0;
            if (PositionYLock.IsChecked == false) PositionYSlider.Value = SceneElements.GroundLevel;
            if (PositionZLock.IsChecked == false) PositionZSlider.Value = 0;
        }

        private void ResetRotation_Click(object sender, RoutedEventArgs e)
        {
            if (RotationXLock.IsChecked == false) RotationXSlider.Value = 0;
            if (RotationYLock.IsChecked == false) RotationYSlider.Value = 0;
            if (RotationZLock.IsChecked == false) RotationZSlider.Value = 0;
        }

        private void ResetScale_Click(object sender, RoutedEventArgs e)
        {
            if (ScaleLock.IsChecked == false) ScaleSlider.Value = 1;
        }

        public void ApplyAutoRotation(double angle)
        {
            if (_viewModel.SelectedModel != null && _transformData.TryGetValue(_viewModel.SelectedModel, out var transformData))
            {
                var newRotationY = (transformData.Rotation.Y + angle) % 360;
                transformData.Rotation = new Vector3D(transformData.Rotation.X, newRotationY, transformData.Rotation.Z);
                RotationYSlider.Value = newRotationY;
            }
        }

        // STUDIO HANDLERS
        private void EnvironmentCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (TransparentBgCheck == null || ShowSkyboxCheck == null) return;

            if (sender == TransparentBgCheck && TransparentBgCheck.IsChecked == true)
            {
                ShowSkyboxCheck.IsChecked = false;
            }
            else if (sender == ShowSkyboxCheck && ShowSkyboxCheck.IsChecked == true)
            {
                TransparentBgCheck.IsChecked = false;
            }

            UpdateEnvironment();
        }

        public void UpdateEnvironment()
        {
            if (Viewport == null || TransparentBgCheck == null || ShowSkyboxCheck == null) return;

            bool isTransparent = TransparentBgCheck.IsChecked ?? false;
            bool showSkybox = ShowSkyboxCheck.IsChecked ?? true;

            Viewport.SetGroundVisibility(!isTransparent); 
            Viewport.SetSkyboxVisibility(showSkybox);
        }

        private void FovSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            Viewport?.SetFieldOfView(e.NewValue);
        }

        private void LightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LightRotationSlider != null && LightHeightSlider != null && AmbientIntensitySlider != null)
            {
                Viewport?.SetLightDirection(LightRotationSlider.Value, LightHeightSlider.Value);
                Viewport?.SetAmbientIntensity(AmbientIntensitySlider.Value);
            }
        }

        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            Viewport?.InitiateSnapshot(4.0);
        }

        private void ResetStudio_Click(object sender, RoutedEventArgs e)
        {
            FovSlider.Value = 45;
            LightRotationSlider.Value = 0;
            LightHeightSlider.Value = 0;
            AmbientIntensitySlider.Value = 100;
            TransparentBgCheck.IsChecked = false;
            ShowSkyboxCheck.IsChecked = true;

            Viewport?.ResetStudioLighting();
            UpdateEnvironment();
        }
    }
}
