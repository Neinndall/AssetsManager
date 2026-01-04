using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using AssetsManager.Views.Models.Models3D;
using AssetsManager.Services.Models;
using AssetsManager.Services.Core;
using Material.Icons;
using System.Threading.Tasks;
using AssetsManager.Utils;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Helpers;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Linq;

namespace AssetsManager.Views.Controls.Models
{
    public class ModelTransformData
    {
        public Vector3D Position { get; set; } = new Vector3D(0, 0, 0);
        public Vector3D Rotation { get; set; } = new Vector3D(0, 0, 0);
        public double Scale { get; set; } = 1.0;
    }

    public partial class ModelPanelControl : UserControl
    {
        private enum ModelType { Skn, MapGeometry }
        private ModelType _currentMode;

        public SknModelLoadingService SknModelLoadingService { get; set; }
        public MapGeometryLoadingService MapGeometryLoadingService { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }

        public event EventHandler<AnimationModel> AnimationReadyForDisplay;
        public event EventHandler<(AnimationModel, TimeSpan)> AnimationSeekRequested;
        public event Action<SceneModel> ModelRemovedFromViewport;
        public event EventHandler<AnimationModel> AnimationStopRequested;
        public event EventHandler SceneClearRequested;
        public event EventHandler MapGeometryLoadRequested;

        // Studio Events
        public event Action<double> FieldOfViewChanged;
        public event Action<double, double> LightDirectionChanged;
        public event Action<double> AmbientIntensityChanged;
        public event Action<bool> BackgroundTransparencyChanged;
        public event Action SnapshotRequested;
        public event Action StudioResetRequested;

        public event Action<SceneModel> ModelReadyForViewport;
        public event Action<SceneModel> ActiveModelChanged;
        public event Action SceneSetupRequested;
        public event Action CameraResetRequested;
        public event Action<Visibility> EmptyStateVisibilityChanged;
        public event Action<Visibility> MainContentVisibilityChanged;

        public ObservableCollection<AnimationModel> AnimationModels => _animationModels;

        private readonly ObservableCollection<SceneModel> _loadedModels = new();
        private readonly ObservableCollection<AnimationModel> _animationModels = new();
        private readonly Dictionary<SceneModel, ModelTransformData> _transformData = new();
        private SceneModel _selectedModel;
        private AnimationModel _currentlyPlayingAnimation;

        public ModelPanelControl()
        {
            InitializeComponent();
            ModelsListBox.ItemsSource = _loadedModels;
            AnimationsListBox.ItemsSource = _animationModels;

            Unloaded += (s, e) => Cleanup();
        }

        public void Cleanup()
        {
            ResetScene();
        }

        public void ResetScene()
        {
            // 1. Limpiar animaciones (ahora se limpian con el Dispose de SceneModel)

            // 2. CRÍTICO: Liberar recursos de TODOS los modelos
            foreach (var model in _loadedModels)
            {
                model?.Dispose();  // Libera texturas, geometrías y materiales
            }
            _loadedModels.Clear();
            _transformData.Clear();

            // 3. Limpiar referencias
            _currentlyPlayingAnimation = null;

            // 4. Limpiar UI
            MeshesItemsControl.ItemsSource = null;
            _animationModels.Clear();
            ModelsListBox.SelectedItem = null;
            AnimationControlsPanel.Visibility = Visibility.Collapsed;
            AnimationControlsPanel.DataContext = null;

            LoadModelButton.IsEnabled = true;
            LoadChromaModelButton.IsEnabled = true;

            if (_currentMode == ModelType.MapGeometry)
            {
                LoadModelIcon.Kind = MaterialIconKind.Map;
                LoadModelButton.ToolTip = "Load MapGeometry";
                LoadAnimationButton.Visibility = Visibility.Collapsed;
                LoadChromaModelButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                LoadModelIcon.Kind = MaterialIconKind.CubeOutline;
                LoadModelButton.ToolTip = "Load Model";
                LoadAnimationButton.Visibility = Visibility.Visible;
                LoadChromaModelButton.Visibility = Visibility.Visible;
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
                _transformData.Remove(modelToDelete);
                ModelRemovedFromViewport?.Invoke(modelToDelete);

                if (_loadedModels.Count == 0)
                {
                    ResetScene();
                    SceneClearRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    ModelsListBox.SelectedIndex = 0;
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
                    Title = "Select the texture folder for the chroma"
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
            if (_selectedModel == null)
            {
                CustomMessageBoxService.ShowWarning("No Model Selected", "Please select a model to associate the skeleton with.", Window.GetWindow(this));
                return;
            }
            using (var stream = File.OpenRead(filePath))
            {
                _selectedModel.Skeleton = new RigResource(stream);
            }
            LogService.LogDebug($"Loaded skeleton: {Path.GetFileName(filePath)} for model {_selectedModel.Name}");
        }

        public void ProcessModelLoading(string modelPath, string texturePath, bool isInitialLoad)
        {
            _currentMode = ModelType.Skn;
            LoadModelIcon.Kind = MaterialIconKind.CubeOutline;
            LoadModelButton.ToolTip = "Load Model";

            SceneModel newModel;
            if (string.IsNullOrEmpty(texturePath))
            {
                newModel = SknModelLoadingService.LoadModel(modelPath);
            }
            else
            {
                newModel = SknModelLoadingService.LoadModel(modelPath, texturePath);
            }

            if (newModel != null)
            {
                string sklFilePath = Path.ChangeExtension(modelPath, ".skl");
                if (File.Exists(sklFilePath))
                {
                    using (var stream = File.OpenRead(sklFilePath))
                    {
                        newModel.Skeleton = new RigResource(stream);
                    }
                }

                if (isInitialLoad)
                {
                    SceneSetupRequested?.Invoke();
                    EmptyStateVisibilityChanged?.Invoke(Visibility.Collapsed);
                    MainContentVisibilityChanged?.Invoke(Visibility.Visible);
                }

                var transformData = new ModelTransformData { Position = new Vector3D(0, SceneElements.GroundLevel, 0) };
                _transformData.Add(newModel, transformData);
                UpdateTransform(newModel, transformData);

                ModelReadyForViewport?.Invoke(newModel);
                MeshesItemsControl.ItemsSource = newModel.Parts;

                _loadedModels.Add(newModel);
                ModelsListBox.SelectedItem = newModel;

                CameraResetRequested?.Invoke();

                LoadAnimationButton.IsEnabled = true;
            }
        }

        private void LoadAnimationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel == null && _loadedModels.Count == 1)
            {
                ModelsListBox.SelectedIndex = 0;
            }

            if (_selectedModel == null)
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
            if (_selectedModel == null)
            {
                CustomMessageBoxService.ShowWarning("No Model Selected", "Please select a model from the 'Models' tab first.", Window.GetWindow(this));
                return;
            }

            if (_selectedModel.Skeleton == null)
            {
                CustomMessageBoxService.ShowWarning("Missing Skeleton", "Please load a skeleton (.skl) file first.", Window.GetWindow(this));
                return;
            }
            using (var stream = File.OpenRead(filePath))
            {
                var animationAsset = AnimationAsset.Load(stream);
                var animationName = Path.GetFileNameWithoutExtension(filePath);

                if (!_animationModels.Any(a => a.Name == animationName))
                {
                    var animationData = new AnimationData { AnimationAsset = animationAsset, Name = animationName };
                    _selectedModel.Animations.Add(animationData);
                    _animationModels.Add(new AnimationModel(animationData));
                }
            }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedModel == null)
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

                AnimationReadyForDisplay?.Invoke(this, animationModel);
            }
        }

        public async Task LoadMapGeometry(string filePath, string materialsPath, string gameDataPath)
        {
            _currentMode = ModelType.MapGeometry;
            LoadModelIcon.Kind = MaterialIconKind.Map;
            LoadModelButton.ToolTip = "Load MapGeometry";

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
                SceneSetupRequested?.Invoke();
                EmptyStateVisibilityChanged?.Invoke(Visibility.Collapsed);
                MainContentVisibilityChanged?.Invoke(Visibility.Visible);

                ModelReadyForViewport?.Invoke(newModel);
                MeshesItemsControl.ItemsSource = newModel.Parts;

                foreach (var model in _loadedModels)
                {
                    model?.Dispose();
                }
                _loadedModels.Clear();
                _loadedModels.Add(newModel);

                CameraResetRequested?.Invoke();

                LoadModelButton.IsEnabled = false;
                LoadAnimationButton.Visibility = Visibility.Collapsed;
                LoadChromaModelButton.Visibility = Visibility.Collapsed;
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (AnimationsListBox.SelectedItem is AnimationModel animationModel)
            {
                AnimationStopRequested?.Invoke(this, animationModel);
            }
        }

        private void AnimationsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AnimationsListBox.SelectedItem is AnimationModel selectedAnimation)
            {
                AnimationControlsPanel.DataContext = selectedAnimation;
                AnimationControlsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                AnimationControlsPanel.DataContext = null;
                AnimationControlsPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void ModelsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Hide animation controls when model selection changes
            AnimationControlsPanel.Visibility = Visibility.Collapsed;
            AnimationControlsPanel.DataContext = null;

            if (e.AddedItems.Count > 0 && e.AddedItems[0] is SceneModel selectedModel)
            {
                _selectedModel = selectedModel;
                ActiveModelChanged?.Invoke(selectedModel);
                MeshesItemsControl.ItemsSource = selectedModel.Parts;

                _animationModels.Clear();
                if (selectedModel.Animations != null)
                {
                    foreach (var animData in selectedModel.Animations)
                    {
                        _animationModels.Add(new AnimationModel(animData));
                    }
                }

                if (_transformData.TryGetValue(selectedModel, out var transformData))
                {
                    // Unsubscribe from events to prevent feedback loop
                    PositionXSlider.ValueChanged -= TransformSlider_ValueChanged;
                    PositionYSlider.ValueChanged -= TransformSlider_ValueChanged;
                    PositionZSlider.ValueChanged -= TransformSlider_ValueChanged;
                    RotationXSlider.ValueChanged -= TransformSlider_ValueChanged;
                    RotationYSlider.ValueChanged -= TransformSlider_ValueChanged;
                    RotationZSlider.ValueChanged -= TransformSlider_ValueChanged;
                    ScaleSlider.ValueChanged -= TransformSlider_ValueChanged;

                    PositionXSlider.Value = transformData.Position.X;
                    PositionYSlider.Value = transformData.Position.Y;
                    PositionZSlider.Value = transformData.Position.Z;
                    RotationXSlider.Value = transformData.Rotation.X;
                    RotationYSlider.Value = transformData.Rotation.Y;
                    RotationZSlider.Value = transformData.Rotation.Z;
                    ScaleSlider.Value = transformData.Scale;

                    // Re-subscribe to events
                    PositionXSlider.ValueChanged += TransformSlider_ValueChanged;
                    PositionYSlider.ValueChanged += TransformSlider_ValueChanged;
                    PositionZSlider.ValueChanged += TransformSlider_ValueChanged;
                    RotationXSlider.ValueChanged += TransformSlider_ValueChanged;
                    RotationYSlider.ValueChanged += TransformSlider_ValueChanged;
                    RotationZSlider.ValueChanged += TransformSlider_ValueChanged;
                    ScaleSlider.ValueChanged += TransformSlider_ValueChanged;
                }

                // Reset locks
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
            if (_selectedModel == null || !_transformData.TryGetValue(_selectedModel, out var transformData))
            {
                return;
            }

            transformData.Position = new Vector3D(PositionXSlider.Value, PositionYSlider.Value, PositionZSlider.Value);
            transformData.Rotation = new Vector3D(RotationXSlider.Value, RotationYSlider.Value, RotationZSlider.Value);
            transformData.Scale = ScaleSlider.Value;

            UpdateTransform(_selectedModel, transformData);
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

        private void UpdateTransform(SceneModel model, ModelTransformData data)
        {
            var transformGroup = new Transform3DGroup();

            // Scale
            transformGroup.Children.Add(new ScaleTransform3D(data.Scale, data.Scale, data.Scale));

            // Rotation
            var rotationX = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), data.Rotation.X));
            var rotationY = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), data.Rotation.Y));
            var rotationZ = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), data.Rotation.Z));
            transformGroup.Children.Add(rotationX);
            transformGroup.Children.Add(rotationY);
            transformGroup.Children.Add(rotationZ);

            // Position
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
                AnimationSeekRequested?.Invoke(this, (animationModel, TimeSpan.FromSeconds(e.NewValue)));
            }
        }

        private void ResetPosition_Click(object sender, RoutedEventArgs e)
        {
            if (PositionXLock.IsChecked == false)
            {
                PositionXSlider.Value = 0;
            }
            if (PositionYLock.IsChecked == false)
            {
                PositionYSlider.Value = SceneElements.GroundLevel;
            }
            if (PositionZLock.IsChecked == false)
            {
                PositionZSlider.Value = 0;
            }
        }

        private void ResetRotation_Click(object sender, RoutedEventArgs e)
        {
            if (RotationXLock.IsChecked == false)
            {
                RotationXSlider.Value = 0;
            }
            if (RotationYLock.IsChecked == false)
            {
                RotationYSlider.Value = 0;
            }
            if (RotationZLock.IsChecked == false)
            {
                RotationZSlider.Value = 0;
            }
        }

        private void ResetScale_Click(object sender, RoutedEventArgs e)
        {
            if (ScaleLock.IsChecked == false)
            {
                ScaleSlider.Value = 1;
            }
        }

        public void ApplyAutoRotation(double angle)
        {
            if (_selectedModel != null && _transformData.TryGetValue(_selectedModel, out var transformData))
            {
                var newRotationY = transformData.Rotation.Y + angle;
                // Normalize the angle to be within -360 to 360
                newRotationY %= 360;
                transformData.Rotation = new Vector3D(transformData.Rotation.X, newRotationY, transformData.Rotation.Z);

                // Update the slider, which will trigger the TransformSlider_ValueChanged event
                // and update the visual transform.
                RotationYSlider.Value = newRotationY;
            }
        }

        // STUDIO HANDLERS

        private void TransparentBgCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox)
            {
                BackgroundTransparencyChanged?.Invoke(checkBox.IsChecked ?? false);
            }
        }

        private void FovSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            FieldOfViewChanged?.Invoke(e.NewValue);
        }

        private void LightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LightRotationSlider != null && LightHeightSlider != null && AmbientIntensitySlider != null)
            {
                LightDirectionChanged?.Invoke(LightRotationSlider.Value, LightHeightSlider.Value);
                AmbientIntensityChanged?.Invoke(AmbientIntensitySlider.Value);
            }
        }

        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            SnapshotRequested?.Invoke();
        }

        private void ResetStudio_Click(object sender, RoutedEventArgs e)
        {
            // Reset Sliders to Neutral
            FovSlider.Value = 45;
            LightRotationSlider.Value = 0;
            LightHeightSlider.Value = 0;
            AmbientIntensitySlider.Value = 100; // Return to 100% (Normal mode)
            TransparentBgCheck.IsChecked = false;

            // Trigger Reset Event
            StudioResetRequested?.Invoke();
        }
    }
}
