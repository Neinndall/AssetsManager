using AssetsManager.Services.Viewer.Loading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Views.Models.Viewer;
using Microsoft.Win32;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private sealed record ProjectFolderCatalog(
            StudioProjectCatalog.BrowserCatalog Catalog,
            StudioBrowserFolder BrowserRoot,
            IReadOnlyList<MapSceneSource> InstallationMaps);

        private readonly Dictionary<string, ProjectFolderCatalog> _projectFolders = new(StringComparer.OrdinalIgnoreCase);

        internal void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: StudioBrowserFolder { ProjectFolderPath: not null } folder })
                RemoveProjectFolder(folder.ProjectFolderPath);
        }

        private void RemoveProjectFolder(string path)
        {
            if (!_projectFolders.Remove(path, out ProjectFolderCatalog folder)) return;
            _scanCancellation?.Cancel();
            _model.BrowserRoots.Remove(folder.BrowserRoot);
            foreach (StudioSkinItem skin in folder.Catalog.Entries) _model.DetectedSkins.Remove(skin);
            _model.SetProjectFolders(_projectFolders.Keys);
            RebuildProjectBackdrops();
            RefreshProjectBrowserSearch();
            _model.StatusText = "Folder removed from project. Open scenes remain available.";
        }

        internal void OpenChromaLibrary_Click(object sender, RoutedEventArgs e)
            => OpenChromaLibrary();

        public void OpenChromaLibrary()
        {
            if (_model.IsChromaLibraryVisible)
                _model.IsChromaLibraryVisible = false;
            else if (!string.IsNullOrEmpty(StudioChromaLibrary.ViewModel.CurrentSourcePath))
                _model.IsChromaLibraryVisible = true;
            else
                ChooseChromaFolder();
        }

        private async void ChooseChromaFolder()
        {
            if (_isCleanedUp || ChromaLoadingService == null) return;
            var dialog = new OpenFolderDialog
            {
                Title = "Select the skins folder for Chroma Library",
                InitialDirectory = StudioChromaLibrary.ViewModel.CurrentSourcePath ?? _model.RootPath
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            _model.IsChromaLibraryVisible = true;
            await StudioChromaLibrary.InitializeAsync(dialog.FolderName);
        }

        private void LoadStudioChromas(IReadOnlyList<ChromaSkinModel> selected, bool addToScene)
        {
            if (_isCleanedUp || SknLoadingService == null || VfxLoadingService == null || selected.Count == 0) return;
            var skins = selected.Select(AssetsManager.Services.Viewer.Loading.ChromaLoadingService.CreateStudioSkin).ToList();
            if (skins.Any(skin => skin == null))
            {
                CustomMessageBoxService?.ShowWarning("Chroma Unavailable",
                    "A selected model or texture folder is no longer available. Choose the skins folder again.",
                    Window.GetWindow(this));
                return;
            }
            StudioWorkspaceTab current = _model.SelectedWorkspaceTab;
            int existing = current?.Kind == StudioWorkspaceTabKind.Skin ? current.Actors.Count : 0;
            int additions = skins.Count(skin => current?.Kind != StudioWorkspaceTabKind.Skin ||
                !current.Actors.Any(actor => actor.HasSkin(skin)));
            if (addToScene && existing + additions > MaxSceneActors)
            {
                CustomMessageBoxService?.ShowWarning("Scene Limit",
                    $"A scene holds up to {MaxSceneActors} characters. Select fewer chromas or open them in tabs.",
                    Window.GetWindow(this));
                return;
            }
            if (string.IsNullOrEmpty(_model.RootPath))
            {
                _model.RootPath = skins[0].ResourceRoot;
                _ = ScanRootDirectoryAsync(_model.RootPath);
            }
            foreach (StudioSkinItem skin in skins)
            {
                if (addToScene) AddSkinToScene(skin);
                else OpenSkin(skin, ownTab: true);
            }
            _model.IsChromaLibraryVisible = false;
            StudioViewportView.OpenTkControl?.InvalidateVisual();
        }

        public void LoadChromas(IReadOnlyList<ChromaSkinModel> selected, bool addToScene)
            => LoadStudioChromas(selected, addToScene);

        internal void StudioOptions_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.ContextMenu != null)
            {
                button.ContextMenu.DataContext = _model;
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.IsOpen = true;
            }
        }

        internal void ExitStudio_Click(object sender, RoutedEventArgs e)
        {
            if (_isExitPending) return;

            ReleaseCurrentProject();
            if (_gl != null && _isGlStarted && _isActive && IsVisible)
            {
                // Finish GPU teardown on the render callback while the OpenGL context is current.
                _isExitPending = true;
                return;
            }

            ExitRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ReleaseCurrentProject()
        {
            _model.InspectorVisible = false;
            _model.IsChromaLibraryVisible = false;
            StudioChromaLibrary.Reset();
            _pendingSnapshot = null;
            _scanCancellation?.Cancel();
            _scanCancellation = null;
            _model.IsProjectLoading = false;
            _model.ProjectBrowserMessage = "Open a project folder to browse assets.";
            _pendingWorkspaceRestoreTab = null;
            _startNextPreviewPaused = false;
            _startNextPreviewTime = 0;
            ClearWorkspaceTabs();
            CancelMapLoadAndClearScene();
            _mapClipCancellation?.Dispose();
            _mapClipCancellation = null;
            ClearLoadedSkinState();
            _standaloneRunMemory.Clear();
            _model.SelectedSkin = null;
            _model.DetectedSkins.Clear();
            _model.BrowserRoots.Clear();
            _projectFolders.Clear();
            _model.SetProjectFolders(Array.Empty<string>());
            _suppressMapVariantReload = true;
            try
            {
                _model.SetMapVariants(Array.Empty<MapVariantData>());
            }
            finally
            {
                _suppressMapVariantReload = false;
            }
            _model.LogMessages.Clear();
            _isApplyingCharacterViewportState = true;
            try
            {
                _model.SelectedCharacterBackdrop = null;
                _model.CharacterBackdropEnabled = false;
                _model.CharacterBackdrops.Clear();
            }
            finally
            {
                _isApplyingCharacterViewportState = false;
            }
            _model.NotifyCharacterCollectionsChanged();
            _model.SearchQuery = string.Empty;
            _model.EmitterFilterText = string.Empty;
            _model.CurrentTime = 0;
            _model.TotalDuration = 5.0;
            _model.ActiveLoopStart = 0;
            _model.ActiveLoopDuration = 0;
            _model.LiveParticleCount = 0;
            _model.HasChampionMesh = false;
            _model.HasAnySolo = false;
            _model.IsAllMuted = false;
            _model.StatusText = "Ready";
            ResetCameraToConfiguredPreset();
        }

        internal async void BrowseRoot_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleanedUp || _model.IsProjectLoading) return;
            bool adding = _model.HasProject;
            var owner = Window.GetWindow(this);
            var dialog = new OpenFolderDialog
            {
                Title = adding ? "Add folder to 3D Studio project" : "Open 3D Studio project folder",
                InitialDirectory = _model.RootPath ?? string.Empty
            };
            bool? accepted = dialog.ShowDialog(owner);
            owner?.Activate();
            if (accepted != true) return;
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.FolderName));
            if (!adding) LoadExtractedContainer(path);
            else if (_projectFolders.ContainsKey(path)) _model.StatusText = "This folder is already in the project.";
            else await ScanRootDirectoryAsync(path);
        }

        private void RootPathTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_model.RootPath))
                LoadExtractedContainer(_model.RootPath);
        }

        public void LoadExtractedContainer(string rootFolder)
        {
            if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder) || _isCleanedUp)
                return;

            string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));
            ReleaseCurrentProject();
            _model.RootPath = fullRoot;
            if (AppSettings != null)
            {
                AppSettings.StudioRecentProjects = StudioHomeModel.RememberProject(
                    AppSettings.StudioRecentProjects, fullRoot).ToList();
                PersistRecentProjects();
            }
            // Folder selection is discovery-only. MAP geometry is loaded exclusively from the
            // unified 3D Studio browser through an explicit MapFile selection.
            _ = ScanRootDirectoryAsync(fullRoot);
        }

        private async void PersistRecentProjects()
        {
            try { await AppSettings.SaveAsync(); }
            catch (Exception ex) { LogService?.LogError(ex, "Failed to save recent Studio projects."); }
        }

        private async Task<bool> ScanRootDirectoryAsync(string rootFolder)
        {
            rootFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));
            if (!Directory.Exists(rootFolder) || _projectFolders.ContainsKey(rootFolder)) return false;
            _scanCancellation?.Cancel();
            _scanCancellation = new System.Threading.CancellationTokenSource();
            var operation = _scanCancellation;
            _model.IsProjectLoading = true;
            _model.ProjectBrowserMessage = "Discovering project assets...";
            _model.StatusText = "Loading project...";
            Func<uint, string> resolveBinEntry = VfxLoadingService == null
                ? null
                : VfxLoadingService.ResolveBinEntryPath;
            Func<ulong, string> resolveGamePath = VfxLoadingService == null
                ? null
                : VfxLoadingService.ResolveGamePath;
            try
            {
                StudioProjectCatalog.BrowserCatalog catalog = await System.Threading.Tasks.Task.Run(
                    () => StudioProjectCatalog.ScanBrowser(
                        rootFolder,
                        operation.Token,
                        resolveBinEntry,
                        LogService),
                    operation.Token);
                if (operation.IsCancellationRequested || _isCleanedUp || !ReferenceEquals(_scanCancellation, operation)) return false;
                _model.ProjectBrowserMessage = "Preparing scene options...";
                IReadOnlyList<MapSceneSource> installationMaps = await System.Threading.Tasks.Task.Run(
                    () => InstallationMapCatalog.Discover(
                        AppSettings,
                        rootFolder,
                        resolveGamePath,
                        operation.Token,
                        LogService),
                    operation.Token);
                if (operation.IsCancellationRequested || _isCleanedUp || !ReferenceEquals(_scanCancellation, operation)) return false;
                var projectRoot = new StudioBrowserFolder(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(rootFolder)) is { Length: > 0 } name ? name : rootFolder,
                    StudioBrowserFolderKind.Root)
                {
                    ProjectFolderPath = rootFolder,
                    IsExpanded = true
                };
                foreach (StudioBrowserFolder semanticRoot in catalog.Roots) projectRoot.Children.Add(semanticRoot);
                _projectFolders.Add(rootFolder, new(catalog, projectRoot, installationMaps));
                foreach (StudioSkinItem entry in catalog.Entries) _model.DetectedSkins.Add(entry);
                _model.BrowserRoots.Add(projectRoot);
                _model.SetProjectFolders(_projectFolders.Keys);
                RebuildProjectBackdrops();
                if (!_model.HasMapPreview)
                {
                    _suppressMapVariantReload = true;
                    try { _model.SetMapVariants(_projectFolders.Values.SelectMany(folder => folder.Catalog.MapVariants)); }
                    finally { _suppressMapVariantReload = false; }
                }
                RefreshProjectBrowserSearch();
                _model.ProjectBrowserMessage = "No previewable assets were found in the project folders.";
                _model.StatusText = $"Added {projectRoot.Title}: {catalog.Entries.Count} BIN entries and {catalog.MapSources.Count} map assets.";
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to scan Studio project folder.");
                if (ReferenceEquals(_scanCancellation, operation))
                {
                    _model.ProjectBrowserMessage = "Could not load this folder. Select the project folder again.";
                    _model.StatusText = "Unable to load project.";
                }
                return false;
            }
            finally
            {
                if (ReferenceEquals(_scanCancellation, operation))
                {
                    _scanCancellation = null;
                    _model.IsProjectLoading = false;
                }
                operation.Dispose();
            }
        }

        private void RebuildProjectBackdrops()
        {
            var choices = new List<CharacterBackdropOption>();
            var projectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectFolderCatalog folder in _projectFolders.Values)
                foreach (MapSceneSource source in folder.Catalog.MapSources)
                {
                    string key = InstallationMapCatalog.BackdropKey(source);
                    projectKeys.Add(key);
                    choices.Add(new CharacterBackdropOption(
                        $"{folder.BrowserRoot.Title} - {InstallationMapCatalog.Label(source, projectSource: true)}", source));
                }
            foreach (MapSceneSource source in _projectFolders.Values.SelectMany(folder => folder.InstallationMaps))
            {
                string key = InstallationMapCatalog.BackdropKey(source);
                if (string.IsNullOrWhiteSpace(key) || !projectKeys.Add(key)) continue;
                choices.Add(new CharacterBackdropOption(InstallationMapCatalog.Label(source, projectSource: false), source));
            }
            CharacterBackdropOption selected = _model.SelectedCharacterBackdrop;
            // Running tabs retain their resource source even when its folder leaves the browser.
            if (selected != null && !choices.Any(choice => choice.Source == selected.Source)) choices.Insert(0, selected);
            for (int index = 0; index < choices.Count; index++)
                choices[index] = _model.CharacterBackdrops.FirstOrDefault(option => option.Source == choices[index].Source) ?? choices[index];
            foreach (CharacterBackdropOption obsolete in _model.CharacterBackdrops.Where(option => !choices.Contains(option)).ToArray())
                _model.CharacterBackdrops.Remove(obsolete);
            for (int index = 0; index < choices.Count; index++)
            {
                int current = _model.CharacterBackdrops.IndexOf(choices[index]);
                if (current < 0) _model.CharacterBackdrops.Insert(index, choices[index]);
                else if (current != index) _model.CharacterBackdrops.Move(current, index);
            }
            _model.NotifyCharacterCollectionsChanged();
        }

        private static string SelectedMapFileFor(MapPath map, string rootFolder)
        {
            if (map == null || string.IsNullOrWhiteSpace(rootFolder))
                return null;

            string geometry = Path.Combine(rootFolder, map.GeometryVirtualPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(geometry))
                return Path.GetFullPath(geometry);

            string materials = Path.Combine(rootFolder, map.MaterialsVirtualPath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(materials) ? Path.GetFullPath(materials) : null;
        }

        private static string MapSourceDisplayName(MapSceneSource source)
        {
            if (!string.IsNullOrWhiteSpace(source?.SelectedMapFilePath))
                return Path.GetFileName(source.SelectedMapFilePath);
            if (!string.IsNullOrWhiteSpace(source?.Map?.Value))
                return source.Map.Value.Replace('\\', '/').Split('/').LastOrDefault() ?? source.Map.Value;
            return "MAP";
        }

        private GridLength _expandedTimelineHeight = new(170);

        internal void CloseProject_Click(object sender, RoutedEventArgs e)
        {
            ReleaseCurrentProject();
            StudioViewportView.OpenTkControl?.InvalidateVisual();
        }

        private void ApplyTimelineVisibility()
        {
            if (TimelineDeckRow == null || TimelineSplitterRow == null) return;
            if (_model.TimelineVisible)
            {
                TimelineDeckRow.MinHeight = 170;
                TimelineDeckRow.Height = _expandedTimelineHeight;
                TimelineSplitterRow.Height = new GridLength(6);
            }
            else
            {
                _expandedTimelineHeight = TimelineDeckRow.Height;
                TimelineDeckRow.MinHeight = 0;
                TimelineDeckRow.Height = new GridLength(0);
                TimelineSplitterRow.Height = new GridLength(0);
                StudioTimelineView.TimelineOptionsPopup.IsOpen = false;
            }
        }
    }
}
