using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Collections.Specialized;
using AssetsManager.Utils.Framework;
using AssetsManager.Utils;
using LeagueToolkit.Core.Animation;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Core;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Formatting;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Explorer;
using Material.Icons;
using System.Threading.Tasks;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Controls.Shared;
using System.Linq;
using System.Threading;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class ViewerPanelControl : UserControl
    {
        private readonly ViewerPanelModel _viewModel;
        public ViewerPanelModel ViewModel => _viewModel;

        public SknLoadingService SknLoadingService { get; set; }
        public MapGeometryLoadingService MapGeometryLoadingService { get; set; }
        public ChromaLoadingService ChromaLoadingService { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public TaskCancellationManager TaskCancellationManager { get; set; }
        public ViewerProjectExplorerControl ProjectExplorer { get; set; }

        // Peer Controls (Direct communication)
        public ViewerWindowModel WindowViewModel { get; set; }
        public ViewerViewportControl Viewport { get; set; }
        public ChromaSelectionControl ChromaGallery { get; set; }

        public ObservableRangeCollection<AnimationModel> AnimationModels => _viewModel.AnimationModels;

        private AnimationModel _currentlyPlayingAnimation;
        private CancellationTokenSource _modelLoadingCts;
        private bool _isCleanedUp;
        private bool _isSynchronizingModelSelection;
        private SceneModel _modelSelectionAnchor;

        public ViewerPanelControl()
        {
            _viewModel = new ViewerPanelModel();
            DataContext = _viewModel;

            InitializeComponent();

            _viewModel.LoadedModels.CollectionChanged += OnLoadedModelsCollectionChanged;
            _viewModel.AnimationModels.CollectionChanged += OnAnimationModelsCollectionChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            UpdateHeroStats();
            UpdateHeroSubtitle();
            UpdateInspectorInfo();

            Unloaded += OnPanelUnloaded;
            Loaded += (s, e) => _isCleanedUp = false;
        }

        private void OnLoadedModelsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (SceneModel model in e.NewItems)
                {
                    model.IsMeshSyncEnabled = _viewModel.IsMeshSyncEnabled;
                    model.IsTextureSyncEnabled = _viewModel.IsTextureSyncEnabled;
                    model.MeshVisibilityChanged += HandleMeshVisibilityChanged;
                    model.MeshTextureChanged += HandleMeshTextureChanged;
                }
            }
            if (e.OldItems != null)
            {
                foreach (SceneModel model in e.OldItems)
                {
                    model.MeshVisibilityChanged -= HandleMeshVisibilityChanged;
                    model.MeshTextureChanged -= HandleMeshTextureChanged;
                }
            }
            UpdateHeroStats();
        }

        private void OnAnimationModelsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateHeroStats();
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewerPanelModel.SelectedModel))
            {
                HandleSelectedModelChanged();
            }
            else if (e.PropertyName == nameof(ViewerPanelModel.IsAnimationSyncEnabled))
            {
                if (_viewModel.IsAnimationSyncEnabled)
                {
                    SyncLoadingForAllModels();
                }
            }
            else if (e.PropertyName == nameof(ViewerPanelModel.IsMeshSyncEnabled))
            {
                foreach (var model in _viewModel.LoadedModels)
                {
                    model.IsMeshSyncEnabled = _viewModel.IsMeshSyncEnabled;
                }
            }
            else if (e.PropertyName == nameof(ViewerPanelModel.IsTextureSyncEnabled))
            {
                foreach (var model in _viewModel.LoadedModels)
                {
                    model.IsTextureSyncEnabled = _viewModel.IsTextureSyncEnabled;
                }
            }
            else if (e.PropertyName == nameof(ViewerPanelModel.SelectedModelParts))
            {
                UpdateHeroStats();
            }
            else if (e.PropertyName == nameof(ViewerPanelModel.IsChromaGalleryVisible))
            {
                if (!_viewModel.IsChromaGalleryVisible)
                {
                    ChromaGallery?.ViewModel?.Reset();
                }
            }
        }

        private void OnPanelUnloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void HandleMeshVisibilityChanged(ModelPart sourcePart)
        {
            if (!_viewModel.IsMeshSyncEnabled) return;

            foreach (var model in _viewModel.LoadedModels)
            {
                // Find all parts with the same name (case-insensitive)
                var targetParts = model.Parts.Where(p => string.Equals(p.Name, sourcePart.Name, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var targetPart in targetParts)
                {
                    if (targetPart != sourcePart)
                    {
                        try
                        {
                            // Temporal deactivation of sync to avoid infinite recursion
                            model.IsMeshSyncEnabled = false;
                            targetPart.IsVisible = sourcePart.IsVisible;
                        }
                        catch (Exception ex)
                        {
                            LogService.LogError(ex, $"Failed to sync part '{targetPart.Name}' in model '{model.Name}'");
                        }
                        finally
                        {
                            model.IsMeshSyncEnabled = true;
                        }
                    }
                }
            }
        }

        private void HandleMeshTextureChanged(ModelPart sourcePart)
        {
            if (!_viewModel.IsTextureSyncEnabled) return;

            foreach (var model in _viewModel.LoadedModels)
            {
                // Find all parts with the same name (case-insensitive)
                var targetParts = model.Parts.Where(p => string.Equals(p.Name, sourcePart.Name, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var targetPart in targetParts)
                {
                    if (targetPart != sourcePart)
                    {
                        try
                        {
                            if (sourcePart.SelectedTextureName != null)
                            {
                                string sourceTexNormal = PathUtils.TruncateAtDot(sourcePart.SelectedTextureName);
                                string exactMatch = targetPart.AvailableTextureNames.FirstOrDefault(t =>
                                    string.Equals(PathUtils.TruncateAtDot(t), sourceTexNormal, StringComparison.OrdinalIgnoreCase));

                                if (exactMatch != null)
                                {
                                    if (targetPart.SelectedTextureName != exactMatch)
                                    {
                                        targetPart.SelectedTextureName = exactMatch;
                                    }
                                }
                                else
                                {
                                    // Fallback to index-based matching (for chromas which have differently named textures in the same order)
                                    int sourceIndex = sourcePart.AvailableTextureNames.IndexOf(sourcePart.SelectedTextureName);
                                    if (sourceIndex >= 0 && sourceIndex < targetPart.AvailableTextureNames.Count)
                                    {
                                        if (targetPart.SelectedTextureName != targetPart.AvailableTextureNames[sourceIndex])
                                        {
                                            targetPart.SelectedTextureName = targetPart.AvailableTextureNames[sourceIndex];
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.LogError(ex, $"Failed to sync texture for part '{targetPart.Name}' in model '{model.Name}'");
                        }
                    }
                }
            }
        }

        private void SyncLoadingForAllModels()
        {
            if (_viewModel.AnimationModels.Count == 0) return;

            foreach (var model in _viewModel.LoadedModels)
            {
                foreach (var animModel in _viewModel.AnimationModels)
                {
                    if (!model.Animations.Any(a => a.Name == animModel.Name))
                    {
                        model.Animations.Add(animModel.AnimationData);
                    }
                }
            }
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

        private void ModelsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSynchronizingModelSelection) return;

            SceneModel active = e.AddedItems.OfType<SceneModel>().LastOrDefault();
            if (active != null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                _modelSelectionAnchor = active;
            SynchronizeModelSelection(active);
        }

        public void SelectModelFromViewport(SceneModel model, ModifierKeys modifiers)
        {
            _isSynchronizingModelSelection = true;
            try
            {
                ApplyViewportSelection(model, modifiers);
                SynchronizeModelSelection(model);
            }
            finally
            {
                _isSynchronizingModelSelection = false;
            }
        }

        public void AutoArrangeSelectedModels()
        {
            ViewerInteractionService.ArrangeModels(GetSelectedModels());
        }

        private void ApplyViewportSelection(SceneModel model, ModifierKeys modifiers)
        {
            bool control = modifiers.HasFlag(ModifierKeys.Control);
            bool shift = modifiers.HasFlag(ModifierKeys.Shift);

            if (model == null)
            {
                if (!control && !shift)
                {
                    ModelsListBox.SelectedItems.Clear();
                    _modelSelectionAnchor = null;
                }
                return;
            }

            if (shift)
            {
                SelectModelRange(_modelSelectionAnchor ?? _viewModel.SelectedModel, model, control);
            }
            else if (control)
            {
                if (ModelsListBox.SelectedItems.Contains(model))
                    ModelsListBox.SelectedItems.Remove(model);
                else
                    ModelsListBox.SelectedItems.Add(model);
                _modelSelectionAnchor = model;
            }
            else
            {
                ModelsListBox.SelectedItems.Clear();
                ModelsListBox.SelectedItems.Add(model);
                _modelSelectionAnchor = model;
            }

            ModelsListBox.ScrollIntoView(model);
        }

        private void SelectModelRange(SceneModel anchor, SceneModel target, bool additive)
        {
            int anchorIndex = _viewModel.LoadedModels.IndexOf(anchor);
            int targetIndex = _viewModel.LoadedModels.IndexOf(target);
            if (targetIndex < 0) return;
            if (!additive) ModelsListBox.SelectedItems.Clear();
            if (anchorIndex < 0)
            {
                anchorIndex = targetIndex;
                _modelSelectionAnchor = target;
            }

            int start = Math.Min(anchorIndex, targetIndex);
            int end = Math.Max(anchorIndex, targetIndex);
            for (int index = start; index <= end; index++)
            {
                SceneModel model = _viewModel.LoadedModels[index];
                if (!ModelsListBox.SelectedItems.Contains(model))
                    ModelsListBox.SelectedItems.Add(model);
            }
        }

        private List<SceneModel> GetSelectedModels()
        {
            return _viewModel.LoadedModels
                .Where(ModelsListBox.SelectedItems.Contains)
                .ToList();
        }

        private void SynchronizeModelSelection(SceneModel preferredActive)
        {
            List<SceneModel> selected = GetSelectedModels();
            _viewModel.SelectedModel = preferredActive != null && selected.Contains(preferredActive)
                ? preferredActive
                : selected.LastOrDefault();
            Viewport?.SetSelectedModels(selected, _viewModel.SelectedModel);
        }

        public void Cleanup()
        {
            if (_isCleanedUp) return;
            _isCleanedUp = true;

            try
            {
                // Symmetric unsubscription: detach every handler we registered in the
                // constructor so the panel can be garbage-collected without leaking
                // references to the ViewModel singleton.
                _viewModel.LoadedModels.CollectionChanged -= OnLoadedModelsCollectionChanged;
                _viewModel.AnimationModels.CollectionChanged -= OnAnimationModelsCollectionChanged;
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

                // Also detach MeshVisibilityChanged and MeshTextureChanged from any model still in the list,
                // in case Cleanup is called before ResetScene.
                foreach (var model in _viewModel.LoadedModels)
                {
                    model.MeshVisibilityChanged -= HandleMeshVisibilityChanged;
                    model.MeshTextureChanged -= HandleMeshTextureChanged;
                }

                _modelLoadingCts?.Cancel();
                _modelLoadingCts?.Dispose();
                _modelLoadingCts = null;

                ResetScene();
            }
            catch (Exception ex)
            {
                LogService.LogDebug($"Notice during ViewerPanelControl.Cleanup: {ex.Message}");
            }
        }

        private void SafeDisposeModel(SceneModel model)
        {
            if (model == null) return;
            model.MeshVisibilityChanged -= HandleMeshVisibilityChanged;
            model.MeshTextureChanged -= HandleMeshTextureChanged;

            // Dispose animations that are not shared with any OTHER loaded model
            foreach (var anim in model.Animations)
            {
                bool isShared = _viewModel.LoadedModels.Any(m => m != model && m.Animations.Any(a => a.AnimationAsset == anim.AnimationAsset));
                if (!isShared)
                {
                    anim.Dispose();
                }
            }
            model.Dispose();
        }

        public void ResetScene()
        {
            // Dispose all loaded animations
            foreach (var animModel in _viewModel.AnimationModels)
            {
                animModel.Dispose();
            }
            _viewModel.AnimationModels.Clear();

            // 1. CRÍTICO: Liberar recursos de TODOS los modelos
            foreach (var model in _viewModel.LoadedModels)
            {
                if (model != null)
                {
                    SafeDisposeModel(model);
                }
            }
            _viewModel.LoadedModels.Clear();

            _currentlyPlayingAnimation = null;

            _viewModel.SelectedModelParts = null; // MVVM Cleanup
            _viewModel.SelectedModel = null;

            // Reset search states for clean re-entry
            _viewModel.ModelsSearchText = string.Empty;
            _viewModel.AnimationsSearchText = string.Empty;
            if (ModelsSearchBox != null) ModelsSearchBox.Text = string.Empty;
            if (AnimationsSearchBox != null) AnimationsSearchBox.Text = string.Empty;

            UpdateHeroStats();
        }

        private void DeleteModelButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SceneModel modelToDelete)
            {
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

        private void DeleteAnimationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is AnimationModel animationToDelete)
            {
                // 1. Remove from all model animation collections
                Viewport?.RemoveAnimation(animationToDelete);

                // 2. Remove from global UI collection
                _viewModel.AnimationModels.Remove(animationToDelete);

                if (_currentlyPlayingAnimation == animationToDelete)
                {
                    _currentlyPlayingAnimation = null;
                }

                if (_viewModel.SelectedAnimation == animationToDelete)
                {
                    _viewModel.SelectedAnimation = null;
                }

                // 3. Dispose the asset!
                animationToDelete.Dispose();
            }
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedModel == null) return;

            // DataContext of the toggle is the SelectedAnimation (inherited from the panel Border).
            if ((sender as FrameworkElement)?.DataContext is not AnimationModel animationModel) return;

            if (_currentlyPlayingAnimation != null && _currentlyPlayingAnimation != animationModel)
            {
                _currentlyPlayingAnimation.IsPlaying = false;
            }

            _currentlyPlayingAnimation = animationModel;

            if (animationModel.IsPlaying)
            {
                // Was playing -> Pause
                animationModel.IsPlaying = false;
                Viewport?.TogglePauseResume(animationModel);
            }
            else
            {
                // Was paused/stopped -> Play
                animationModel.IsPlaying = true;
                Viewport?.SetAnimation(animationModel);
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not AnimationModel animationModel) return;

            // Toggle pause/resume at current time (NO reset to 0).
            // The Viewport's TogglePauseResume updates IsAnimationPaused and notifies
            // the panel via SetAnimationPlayingState, which keeps the binding in sync.
            Viewport?.TogglePauseResume(animationModel);
        }

        private void CloseAnimationPlayer_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not AnimationModel animationModel) return;

            // Stop playback if currently playing
            if (animationModel.IsPlaying)
            {
                animationModel.IsPlaying = false;
                Viewport?.TogglePauseResume(animationModel);
            }

            if (_currentlyPlayingAnimation == animationModel)
            {
                _currentlyPlayingAnimation = null;
            }

            // Clear the selection to hide the player (preserves the animation in the list)
            _viewModel.SelectedAnimation = null;
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
        public async void HandleChromaSelected(ChromaSkinModel skin)
        {
            if (!string.IsNullOrEmpty(skin.ModelPath))
            {
                // Cargamos primero el modelo en segundo plano
                await ProcessModelLoading(skin.ModelPath, skin.TexturePath, true);

                // Una vez cargado y con el viewport listo, ocultamos la galería
                ViewModel.IsChromaGalleryVisible = false;
            }
            else
            {
                ViewModel.IsChromaGalleryVisible = false;
                CustomMessageBoxService.ShowWarning("Model Not Found", "Could not automatically find the .skn model for this skin folder.", Window.GetWindow(this));
            }
        }

        /// <summary>
        /// Handles the selection of multiple chromas from the gallery.
        /// </summary>
        public async void HandleMultipleChromasSelected(List<ChromaSkinModel> skins)
        {
            var skinsWithModels = skins.Where(s => !string.IsNullOrEmpty(s.ModelPath)).ToList();

            if (skinsWithModels.Count > 0)
            {
                // Mantenemos la galería abierta durante la carga para mostrar el estado
                foreach (var skin in skinsWithModels)
                {
                    await ProcessModelLoading(skin.ModelPath, skin.TexturePath, true);
                }

                // Cerramos solo cuando todo está cargado
                ViewModel.IsChromaGalleryVisible = false;
            }
            else
            {
                ViewModel.IsChromaGalleryVisible = false;
                CustomMessageBoxService.ShowWarning("Models Not Found", "Could not automatically find the .skn models for the selected skins.", Window.GetWindow(this));
            }
        }

        public async Task LoadInitialModel(string filePath)
        {
            await ProcessModelLoading(filePath, null, true);
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

        public async Task ProcessModelLoading(string modelPath, string texturePath, bool isInitialLoad)
        {
            ViewModel.IsMapMode = false;

            // Start a new cancellable operation. If another load is already in flight
            // (rapid clicks, double-load) it will be cancelled and its result dropped.
            _modelLoadingCts?.Cancel();
            _modelLoadingCts?.Dispose();
            _modelLoadingCts = new CancellationTokenSource();
            var cancellationToken = _modelLoadingCts.Token;

            SceneModel newModel = null;

            try
            {
                if (string.IsNullOrEmpty(texturePath))
                {
                    newModel = await SknLoadingService.LoadModel(modelPath, cancellationToken);
                }
                else
                {
                    newModel = await SknLoadingService.LoadModel(modelPath, texturePath, cancellationToken);
                }
            }
            catch (System.OperationCanceledException)
            {
                LogService.LogDebug("Model loading cancelled before completion.");
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                SafeDisposeModel(newModel);
                return;
            }

            if (newModel != null)
            {
                if (isInitialLoad)
                {
                    if (_viewModel.LoadedModels.Count == 0)
                        Viewport?.ApplyStudioParameters();

                    Viewport?.SetupScene(false);
                    ViewModel.ShowMainContent(); // MVVM State Update
                }

                // Initialize Transform
                newModel.PositionY = SceneElements.GroundLevel;
                newModel.SourceType = string.IsNullOrEmpty(texturePath) ? "Model" : "Chroma";

                Viewport?.AddModel(newModel);
                _viewModel.SelectedModelParts = newModel.Parts;

                // Sync current animations (v3.2.3.1)
                if (_viewModel.IsAnimationSyncEnabled && _viewModel.AnimationModels.Count > 0)
                {
                    foreach (var animModel in _viewModel.AnimationModels)
                    {
                        if (!newModel.Animations.Any(a => a.Name == animModel.Name))
                        {
                            newModel.Animations.Add(animModel.AnimationData);
                        }
                    }
                }

                _viewModel.LoadedModels.Add(newModel);
                _viewModel.SelectedModel = newModel;
                ModelsListBox.SelectedItem = newModel;

                Viewport?.SnapCamera();
            }
        }

        private bool EnsureAnimationTarget()
        {
            if (_viewModel.SelectedModel == null && _viewModel.LoadedModels.Count == 1)
                ModelsListBox.SelectedIndex = 0;

            if (_viewModel.SelectedModel == null)
            {
                CustomMessageBoxService.ShowWarning("No Model Selected", "Please select a model from the 'Models' tab first.", Window.GetWindow(this));
                return false;
            }

            if (_viewModel.SelectedModel.Skeleton == null)
            {
                CustomMessageBoxService.ShowWarning("Missing Skeleton", "Please load a skeleton (.skl) file first.", Window.GetWindow(this));
                return false;
            }

            return true;
        }

        private void LoadAnimation(string filePath)
        {
            var animationName = Path.GetFileNameWithoutExtension(filePath);
            if (_viewModel.AnimationModels.Any(animation => animation.Name == animationName))
                return;

            using (var stream = File.OpenRead(filePath))
            {
                var animationAsset = AnimationAsset.Load(stream);
                var animationData = new AnimationData { AnimationAsset = animationAsset, Name = animationName };
                var animationModel = new AnimationModel(animationData);

                if (_viewModel.IsAnimationSyncEnabled)
                {
                    foreach (var model in _viewModel.LoadedModels)
                    {
                        if (!model.Animations.Any(animation => animation.Name == animationName))
                        {
                            model.Animations.Add(animationData);
                        }
                    }
                }
                else
                {
                    _viewModel.SelectedModel.Animations.Add(animationData);
                }

                _viewModel.AnimationModels.Add(animationModel);
            }
        }

        public async Task LoadMapGeometry(string filePath, string materialsPath, string gameDataPath)
        {
            ViewModel.IsMapMode = true;

            _modelLoadingCts?.Cancel();
            _modelLoadingCts?.Dispose();
            _modelLoadingCts = new CancellationTokenSource();
            var cancellationToken = _modelLoadingCts.Token;

            SceneModel newModel;
            try
            {
                if (!string.IsNullOrEmpty(materialsPath) && File.Exists(materialsPath))
                {
                    newModel = await MapGeometryLoadingService.LoadMapGeometry(
                        filePath,
                        materialsPath,
                        gameDataPath,
                        cancellationToken);
                }
                else
                {
                    newModel = await MapGeometryLoadingService.LoadMapGeometry(
                        filePath,
                        gameDataPath,
                        cancellationToken);
                }
            }
            catch (System.OperationCanceledException)
            {
                LogService.LogDebug("Map geometry loading cancelled before completion.");
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                SafeDisposeModel(newModel);
                return;
            }

            if (newModel != null)
            {
                Viewport?.SetupScene(true);
                ViewModel.ShowMainContent(); // MVVM State Update

                Viewport?.AddModel(newModel);
                _viewModel.SelectedModelParts = newModel.Parts;

                foreach (var model in _viewModel.LoadedModels)
                {
                    SafeDisposeModel(model);
                }
                _viewModel.LoadedModels.Clear();
                _viewModel.LoadedModels.Add(newModel);
                _viewModel.SelectedModel = newModel;
                ModelsListBox.SelectedItem = newModel;

                Viewport?.SnapCamera();
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
            // Use the slider's DataContext (inherited from the panel Border = SelectedAnimation)
            // so seek works correctly even when the animation is unselected in the list.
            if ((sender as FrameworkElement)?.DataContext is AnimationModel && _isSliderDragging)
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

        // DIALOG METHODS (moved from ViewerWindow for passive orchestrator pattern)
        public async Task OpenSknModel()
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "3D Model Files (*.skn;*.skl;*.sco;*.scb)|*.skn;*.skl;*.sco;*.scb|All Files (*.*)|*.*",
                Title = "Select a model file"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                var extension = Path.GetExtension(openFileDialog.FileName).ToLower();
                if (extension == ".skl") LoadSkeleton(openFileDialog.FileName);
                else await LoadInitialModel(openFileDialog.FileName);
            }
        }

        public void OpenChromaFolder()
        {
            var folderBrowserDialog = new OpenFolderDialog
            {
                Title = "Select the skins folder"
            };

            if (folderBrowserDialog.ShowDialog() == true)
            {
                string skinsPath = folderBrowserDialog.FolderName;
                ProjectExplorer?.LoadProjectFolder(skinsPath);

                if (ProjectExplorer != null && WindowViewModel != null)
                    WindowViewModel.IsProjectExplorerVisible = true;

                HandleChromaGalleryRequest(skinsPath);
            }
        }

        private static string FindProjectRoot(string mapGeoPath)
        {
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(mapGeoPath)); directory != null; directory = directory.Parent)
            {
                if (directory.Name.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase) ||
                    (Directory.Exists(Path.Combine(directory.FullName, "assets")) &&
                     Directory.Exists(Path.Combine(directory.FullName, "data"))))
                    return directory.FullName;
            }

            return Path.GetDirectoryName(mapGeoPath);
        }

        public async Task OpenMapGeometry()
        {
            var openMapGeoDialog = new OpenFileDialog
            {
                Filter = "MapGeometry Files (*.mapgeo)|*.mapgeo|All Files (*.*)|*.*",
                Title = "Select a mapgeo file"
            };

            if (openMapGeoDialog.ShowDialog() == true)
            {
                string mapGeoPath = openMapGeoDialog.FileName;
                string materialsBinPath = Path.ChangeExtension(mapGeoPath, ".materials.bin");

                if (WindowViewModel != null)
                {
                    WindowViewModel.LoadingTitle = ViewerWindowModel.MapGeoLoadingTitle;
                    WindowViewModel.LoadingDescription = ViewerWindowModel.MapGeoLoadingDescription;
                    WindowViewModel.IsLoadingVisible = true;
                }

                string gameDataPath = ProjectExplorer?.CurrentRootFolder;

                if (string.IsNullOrEmpty(gameDataPath))
                {
                    gameDataPath = FindProjectRoot(mapGeoPath);
                    ProjectExplorer?.LoadProjectFolder(gameDataPath);

                    if (ProjectExplorer != null && WindowViewModel != null)
                        WindowViewModel.IsProjectExplorerVisible = true;

                    gameDataPath = ProjectExplorer?.CurrentRootFolder ?? gameDataPath;
                }

                await LoadMapGeometry(mapGeoPath, materialsBinPath, gameDataPath);

                if (WindowViewModel != null)
                    WindowViewModel.IsLoadingVisible = false;
            }
        }

        // STUDIO HANDLERS
        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            Viewport?.InitiateHighDefinitionSnapshot();
        }

        private void ResetStudio_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ViewportViewModel?.ResetStudioSettings();
        }

        // ===== Control Deck navigation handlers =====

        private void Close3DModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _modelLoadingCts?.Cancel();
                _modelLoadingCts?.Dispose();
                _modelLoadingCts = null;

                _viewModel.IsChromaGalleryVisible = false;
                Viewport?.ResetScene();
                ResetScene();

                _viewModel.IsMapMode = false;
                WindowViewModel?.IsProjectExplorerVisible = false;
                ProjectExplorer?.ClearImagePreview();
                _viewModel.ShowSelectionScreen();
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to close the 3D model workspace");
            }
        }

        private void ExpandAllToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle)
            {
                _viewModel.SetAllSectionsExpanded(toggle.IsChecked == true);
            }
        }

        private void ModelsSearchBox_SearchTextChanged(object sender, RoutedEventArgs e)
        {
            if (sender is SearchBoxControl searchBox)
            {
                _viewModel.ModelsSearchText = searchBox.Text;
            }
        }

        private void AnimationsSearchBox_SearchTextChanged(object sender, RoutedEventArgs e)
        {
            if (sender is SearchBoxControl searchBox)
            {
                _viewModel.AnimationsSearchText = searchBox.Text;
            }
        }

        private void ResetAllTransforms_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedModel == null) return;
            if (PositionXLock.IsChecked == false) _viewModel.SelectedModel.PositionX = 0;
            if (PositionYLock.IsChecked == false) _viewModel.SelectedModel.PositionY = SceneElements.GroundLevel;
            if (PositionZLock.IsChecked == false) _viewModel.SelectedModel.PositionZ = 0;
            if (RotationXLock.IsChecked == false) _viewModel.SelectedModel.RotationX = 0;
            if (RotationYLock.IsChecked == false) _viewModel.SelectedModel.RotationY = 0;
            if (RotationZLock.IsChecked == false) _viewModel.SelectedModel.RotationZ = 0;
            if (ScaleLock.IsChecked == false) _viewModel.SelectedModel.Scale = 1;
        }

        private void TabScene_Checked(object sender, RoutedEventArgs e) => UpdateHeroSubtitle();
        private void TabEnvironment_Checked(object sender, RoutedEventArgs e) => UpdateHeroSubtitle();
        private void TabMeshes_Checked(object sender, RoutedEventArgs e) => UpdateInspectorInfo();
        private void TabTransform_Checked(object sender, RoutedEventArgs e) => UpdateInspectorInfo();
        private void TabAnimations_Checked(object sender, RoutedEventArgs e) => UpdateInspectorInfo();
        private void TabSfx_Checked(object sender, RoutedEventArgs e) => UpdateInspectorInfo();

        // ===== Hero & Inspector update helpers =====

        private void UpdateHeroStats()
        {
            if (HeroModelsCountText != null)
                HeroModelsCountText.Text = _viewModel.LoadedModels.Count.ToString();
            if (HeroAnimsCountText != null)
                HeroAnimsCountText.Text = _viewModel.AnimationModels.Count.ToString();
            if (HeroMeshesCountText != null)
                HeroMeshesCountText.Text = _viewModel.MeshPartCount.ToString();
            if (ModelsCounterText != null)
                ModelsCounterText.Text = _viewModel.LoadedModels.Count.ToString();
        }

        private void UpdateHeroSubtitle()
        {
            if (HeroSubtitleText == null) return;
            HeroSubtitleText.Text = TabEnvironment != null && TabEnvironment.IsChecked == true
                ? "Studio Environment"
                : "Scene Editor";
        }

        private void UpdateInspectorInfo()
        {
            if (InspectorCounterText == null) return;

            if (TabMeshes != null && TabMeshes.IsChecked == true)
            {
                InspectorCounterText.Text = "M";
                if (InspectorSubtitleText != null) InspectorSubtitleText.Text = "Textures & meshes";
            }
            else if (TabTransform != null && TabTransform.IsChecked == true)
            {
                InspectorCounterText.Text = "T";
                if (InspectorSubtitleText != null) InspectorSubtitleText.Text = "Transform & rotation";
            }
            else if (TabAnimations != null && TabAnimations.IsChecked == true)
            {
                InspectorCounterText.Text = "A";
                if (InspectorSubtitleText != null) InspectorSubtitleText.Text = "Animations playback";
            }
        }

        public void LoadAnimationDirectly(string filePath)
            => LoadAnimationsDirectly(new[] { filePath });

        public void LoadAnimationsDirectly(IEnumerable<string> filePaths)
        {
            if (!EnsureAnimationTarget()) return;

            int failureCount = 0;
            foreach (string filePath in filePaths
                         .Where(path => string.Equals(Path.GetExtension(path), ".anm", StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    LoadAnimation(filePath);
                }
                catch (Exception ex)
                {
                    failureCount++;
                    LogService.LogError(ex, $"Failed to load animation: {filePath}");
                }
            }

            if (failureCount > 0)
                CustomMessageBoxService.ShowError("Load Error", $"Failed to load {failureCount} animation file(s). See the log for details.", Window.GetWindow(this));
        }
    }
}
