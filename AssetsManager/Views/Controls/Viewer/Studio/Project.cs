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
        private void OpenChromaLibrary_Click(object sender, RoutedEventArgs e)
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
            OpenTkControl?.InvalidateVisual();
        }

        private void StudioOptions_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.ContextMenu != null)
            {
                button.ContextMenu.DataContext = _model;
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.IsOpen = true;
            }
        }

        private void ExitStudio_Click(object sender, RoutedEventArgs e)
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
            _model.RootPath = string.Empty;
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

        private void BrowseRoot_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Open 3D Studio project folder"
            };

            if (dialog.ShowDialog() == true)
                LoadExtractedContainer(dialog.FolderName);
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

            string fullRoot = Path.GetFullPath(rootFolder);
            ReleaseCurrentProject();
            _model.RootPath = fullRoot;
            // Folder selection is discovery-only. MAP geometry is loaded exclusively from the
            // unified 3D Studio browser through an explicit MapFile selection.
            _ = ScanRootDirectoryAsync(fullRoot);
        }

        private async Task<bool> ScanRootDirectoryAsync(string rootFolder)
        {
            if (!Directory.Exists(rootFolder)) return false;
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
                _model.DetectedSkins.Clear();
                _model.BrowserRoots.Clear();
                _model.CharacterBackdrops.Clear();

                // Project geometry is listed first and wins for the same logical MapPath. Installation
                // choices then fill the rest so a Character-only project can still use Map11/other maps.
                var backdropKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (MapSceneSource mapSource in catalog.MapSources)
                {
                    string key = InstallationMapCatalog.BackdropKey(mapSource);
                    if (string.IsNullOrWhiteSpace(key) || !backdropKeys.Add(key)) continue;
                    _model.CharacterBackdrops.Add(new CharacterBackdropOption(
                        InstallationMapCatalog.Label(mapSource, projectSource: true),
                        mapSource));
                }
                foreach (MapSceneSource mapSource in installationMaps)
                {
                    string key = InstallationMapCatalog.BackdropKey(mapSource);
                    if (string.IsNullOrWhiteSpace(key) || !backdropKeys.Add(key)) continue;
                    _model.CharacterBackdrops.Add(new CharacterBackdropOption(
                        InstallationMapCatalog.Label(mapSource, projectSource: false),
                        mapSource));
                }
                _model.NotifyCharacterCollectionsChanged();
                foreach (StudioSkinItem entry in catalog.Entries)
                {
                    // Never carry expansion state into a freshly discovered project tree.
                    entry.IsExpanded = false;
                    _model.DetectedSkins.Add(entry);
                }
                foreach (StudioBrowserFolder rootNode in catalog.Roots)
                    _model.BrowserRoots.Add(rootNode);

                _suppressMapVariantReload = true;
                try
                {
                    _model.SetMapVariants(catalog.MapVariants);
                }
                finally
                {
                    _suppressMapVariantReload = false;
                }

                _model.ProjectBrowserMessage = "No previewable assets were found in this folder.";
                StudioBrowserFolder charactersRoot = catalog.Roots.FirstOrDefault(root =>
                    string.Equals(root.Title, "Characters", StringComparison.Ordinal));
                int characterCount = charactersRoot?.Children.OfType<StudioBrowserFolder>().Count() ?? 0;
                _model.StatusText = catalog.MapSources.Count > 0
                    ? $"Found {catalog.MapSources.Count} map assets and {catalog.Entries.Count} BIN entries across {characterCount} characters. Select a map asset to load it."
                    : $"Found {catalog.Entries.Count} BIN entries across {characterCount} characters.";
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

        private void CloseProject_Click(object sender, RoutedEventArgs e)
        {
            ReleaseCurrentProject();
            OpenTkControl?.InvalidateVisual();
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
                TimelineOptionsPopup.IsOpen = false;
            }
        }
    }
}
