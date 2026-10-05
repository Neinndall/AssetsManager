using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private void BindBrowserSkin()
        {
            if (_browserSkin != null)
            {
                _browserSkin.IsExpanded = false;
                foreach (var section in _browserSkin.Sections)
                    section.Items = new ListCollectionView(Array.Empty<object>());
            }
            _browserSkin = _model.SelectedSkin;
            if (_browserSkin == null) return;
            StudioBrowserSection systems = _browserSkin.Sections.FirstOrDefault(section => section.Kind == StudioBrowserSectionKind.Systems);
            StudioBrowserSection clips = _browserSkin.Sections.FirstOrDefault(section => section.Kind == StudioBrowserSectionKind.Clips);
            StudioBrowserSection spells = _browserSkin.Sections.FirstOrDefault(section => section.Kind == StudioBrowserSectionKind.Spells);
            if (systems != null) systems.Items = CollectionViewSource.GetDefaultView(_model.Systems);
            if (clips != null) clips.Items = CollectionViewSource.GetDefaultView(_model.DetectedAnimations);
            if (spells != null) spells.Items = CollectionViewSource.GetDefaultView(_browserSkin.SpellItems);
            _model.IsRawSystemsMode = true;
            LoadBinFile(_browserSkin.BinPath);
        }

        private void BrowserItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem { DataContext: StudioSkinItem skin })
                TrySelectSceneSkin(skin);
        }

        /// <summary>
        /// Browser clicks and expansion never open a Skin: that is an explicit Open in New Tab / Add to
        /// Current Scene action. They only reach Characters already composed into the active scene.
        /// </summary>
        private bool TrySelectSceneSkin(StudioSkinItem skin)
        {
            if (skin == null) return false;
            if (ReferenceEquals(_model.SelectedSkin, skin)) return true;

            StudioWorkspaceTab tab = _model.SelectedWorkspaceTab;
            if (tab?.Kind == StudioWorkspaceTabKind.Skin && tab.Actors.Any(actor => actor.HasSkin(skin)))
            {
                _model.SelectedSkin = skin;
                return true;
            }

            _model.StatusText = $"{skin.Title} · right-click to open it in a new tab or add it to the current scene.";
            return false;
        }

        private void StudioBrowser_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue != null && !_isSceneFocusHandover)
            {
                _startNextPreviewPaused = false;
                _startNextPreviewTime = 0d;
            }
            if (e.NewValue is not MapBrowserNode)
            {
                _model.SelectedMapNode = null;
                ClearMapCharacterClipPreview();
            }

            switch (e.NewValue)
            {
                case MapBrowserNode mapNode:
                    _model.SelectedMapNode = mapNode;
                    HandleMapBrowserSelection(mapNode);
                    break;
                case StudioSkinItem skin:
                    // Selecting (or collapsing) the Skin node never replaces the running System/Clip/Spell;
                    // only a Character not yet focused in the active scene takes the focus.
                    TrySelectSceneSkin(skin);
                    break;
                case StudioBrowserSection section:
                    if (!TrySelectSceneSkin(section.Owner)) break;
                    _model.IsAnimationMode = section.Kind != StudioBrowserSectionKind.Systems;
                    break;
                case VfxSystemDiagnosticItem system:
                    _pendingSpell = null;
                    _model.SelectedSpell = null;
                    _model.SelectedAnimation = null;
                    _model.IsRawSystemsMode = true;
                    _model.SelectedSystem = system;
                    break;
                case AnimationClipCatalogItem animation:
                    _pendingSpell = null;
                    _model.SelectedSpell = null;
                    _model.SelectedSystem = null;
                    _model.IsAnimationMode = true;
                    _model.SelectedAnimation = animation;
                    break;
                case StudioSpellBrowserItem spell:
                    if (!TrySelectSceneSkin(spell.Owner)) break;
                    _pendingSpell = spell;
                    _model.SelectedSystem = null;
                    _model.SelectedAnimation = null;
                    _model.IsAnimationMode = true;
                    _model.SelectedSpell = spell;
                    break;
            }
        }

        private void ClearLoadedSkinState()
        {
            _bindPoseReturnAnimation = null;
            _binCancellation?.Cancel();
            _animationClipCancellation?.Cancel();
            _animationClipCancellation?.Dispose();
            _animationClipCancellation = null;
            _model.IsPlaying = false;
            _vfxRenderer?.Pause();
            if (_inspectedSystem != null)
                RememberStandaloneRun(_inspectedSystem);
            _pendingSystem = null;
            _inspectedSystem = null;
            _pendingSpell = null;
            _activeSpellPlan = null;
            _activeBundle = null;
            _championLoadGeneration++;
            ClearAnimationClipCues();
            _model.SelectedAnimation = null;
            _model.SelectedSpell = null;
            _model.SetAnimationParameterOptions(Array.Empty<float>(), null);
            _model.DetectedAnimations.Clear();
            _vfxRenderer?.SetSystem(null);

            if (_championModel != null)
            {
                SceneModel championModel = _championModel;
                _championModel = null;
                RefreshCharacterInteractionTarget();
                _championMeshRenderer?.QueueRelease(championModel);
                RunReleaseStep("Champion SceneModel", championModel.Dispose);
            }

            _championBundle = null;
            _championSknPath = null;
            _championAwaitingFirstPose = false;
            ClearCharacterFormState();
            _championAuthoredScale = 1d;
            _characterAuthoredHiddenSubmeshes.Clear();
            InvalidateChampionBindPose();
            _model.HasChampionMesh = false;
            _model.HasCharacterSkeleton = false;
            RebuildCharacterSubmeshOptions();
            RebuildCharacterGameStates();
            ClearCharacterArmatureOverlay();
            RunReleaseStep("Champion animation cache", () => _championAnimationService?.ClearCache());

            VfxClipCatalog clipCatalog = _clipCatalog;
            _clipCatalog = null;
            RunReleaseStep(nameof(VfxClipCatalog), () => clipCatalog?.Dispose());

            _model.SelectedSystem = null;
            _model.Systems.Clear();
            _model.SelectedEmitter = null;
            _model.Emitters.Clear();
            _model.Textures.Clear();
            _model.HasAnySolo = false;
            _model.IsAllMuted = false;
            _model.CurrentTime = 0;
            _model.TotalDuration = 5.0;
            _model.ActiveLoopStart = 0;
            _model.ActiveLoopDuration = 0;
        }

        private async void LoadBinFile(string binFilePath)
        {
            if (!File.Exists(binFilePath)) return;

            StudioSceneActorRuntime adoption = TakePendingActorAdoption(binFilePath);
            ClearLoadedSkinState();
            _binCancellation = new System.Threading.CancellationTokenSource();
            var operation = _binCancellation;
            StudioWorkspaceTab restoreTab = _pendingWorkspaceRestoreTab;
            bool servicesAdopted = false;

            try
            {
                _model.LogMessages.Add($"[BIN] Loading BIN definitions from: {Path.GetFileName(binFilePath)}");

                // A promoted scene actor already carries this Skin's bundle, model and decoded clips.
                var bundle = adoption?.Bundle ??
                    await VfxLoadingService.LoadAsync(binFilePath, LogService, operation.Token);
                if (operation.IsCancellationRequested || _isCleanedUp) return;
                _activeBundle = bundle;
                if (adoption != null)
                {
                    AdoptFocusedServices(adoption);
                    servicesAdopted = true;
                }
                _championAnimationService ??= new AnimationService(LogService);

                // AnimationGraph metadata is independent from the preview mesh. Populate the
                // picker immediately; ANM payloads are decoded only when a clip/spell needs one.
                BindAnimationCatalog(_model.RootPath);

                foreach (var (hash, sysDef) in _activeBundle.Systems)
                {
                    string name = sysDef.Name ?? $"VFX_0x{hash:X8}";

                    if (!HasPlayableEmitters(sysDef))
                    {
                        continue;
                    }

                    VfxEmitterDefinition[] playableEmitters = sysDef.Emitters
                        .Where(emitter => !emitter.Disabled)
                        .ToArray();
                    var item = new VfxSystemDiagnosticItem
                    {
                        Name = name,
                        PathHash = hash,
                        Definition = sysDef,
                        EmitterCount = playableEmitters.Length,
                        TextureCount = playableEmitters.Count(e =>
                            !string.IsNullOrWhiteSpace(e.TexturePath) ||
                            !string.IsNullOrWhiteSpace(e.TextureMultPath) ||
                            !string.IsNullOrWhiteSpace(e.ParticleColorTexturePath) ||
                            !string.IsNullOrWhiteSpace(e.PaletteDefinition?.PaletteTexturePath)),
                        MeshCount = playableEmitters.Count(e => e.IsMeshPrimitive)
                    };
                    _model.Systems.Add(item);
                }

                _model.LogMessages.Add($"[BIN SUCCESS] Extracted {_model.Systems.Count} VFX systems.");
                _model.StatusText = $"Loaded {_model.Systems.Count} systems from {Path.GetFileName(binFilePath)}.";

                if (adoption != null)
                {
                    CharacterFormDefinition adoptedForm = adoption.Form;
                    // Keep the installed form identity so rebuilding the picker reuses this model.
                    InstallChampionModel(
                        adoption.Model, bundle, adoption.SknPath, adoption.SearchDirectory, startPreview: false,
                        formPathHash: adoptedForm is { ReloadsModel: true } ? adoptedForm.PathHash : 0u,
                        formSkinScale: adoptedForm is { ReloadsModel: true } ? adoptedForm.SkinScale : null);
                    adoption = null;
                    bool restored = restoreTab != null && ReferenceEquals(_pendingWorkspaceRestoreTab, restoreTab) &&
                        RestoreWorkspaceSelection(restoreTab, binFilePath);
                    _pendingWorkspaceRestoreTab = null;
                    if (!restored)
                        TrySelectOpeningSkinAnimation();
                    TryPlayPendingSpell();
                    return;
                }

                if (restoreTab != null && ReferenceEquals(_pendingWorkspaceRestoreTab, restoreTab))
                {
                    _pendingWorkspaceRestoreTab = null;
                    RestoreWorkspaceSelection(restoreTab, binFilePath);
                }

                // Selecting a skin loads its owner model and browser data only. A VFX system
                // starts exclusively from an explicit System selection in the browser.
                TryLoadChampionModelAsync(_model.RootPath);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to load VFX BIN.");
                _model.StatusText = "Unable to load this BIN.";
                _model.LogMessages.Add($"[ERROR] Failed to load BIN: {ex.Message}");
            }
            finally
            {
                if (adoption != null)
                {
                    // Only the model is still ours once the pose/session services were adopted.
                    if (servicesAdopted)
                    {
                        _championMeshRenderer?.QueueRelease(adoption.Model);
                        adoption.Model.CurrentAnimation = null;
                        RunReleaseStep("Scene actor model", adoption.Model.Dispose);
                    }
                    else
                    {
                        ReleaseSceneActorRuntime(adoption);
                    }
                }
                if (ReferenceEquals(_binCancellation, operation)) _binCancellation = null;
                operation.Dispose();
            }
        }

        internal static bool HasPlayableEmitters(VfxSystemDefinition definition)
            => definition?.Emitters.Any(emitter => !emitter.Disabled) == true;

        private void SearchQuery_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = _model.SearchQuery?.Trim() ?? "";

            var animView = CollectionViewSource.GetDefaultView(_model.DetectedAnimations);
            if (animView != null)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    animView.Filter = null;
                }
                else
                {
                    animView.Filter = obj =>
                    {
                        if (obj is AnimationClipCatalogItem item)
                        {
                            return item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || (!string.IsNullOrEmpty(item.VfxSummary) && item.VfxSummary.Contains(query, StringComparison.OrdinalIgnoreCase));
                        }
                        return false;
                    };
                }
            }

            var view = CollectionViewSource.GetDefaultView(_model.Systems);
            if (view != null)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    view.Filter = null;
                }
                else
                {
                    view.Filter = obj =>
                    {
                        if (obj is VfxSystemDiagnosticItem item)
                        {
                            return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                        }
                        return false;
                    };
                }
            }
        }
    }
}
