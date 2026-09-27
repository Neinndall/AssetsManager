using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private bool _isUpdatingCharacterForms;
        private VfxLoadingService.Bundle _characterPlaybackSource;
        private VfxCharacterFormDefinition _characterPlaybackForm;
        private VfxLoadingService.Bundle _characterPlaybackBundle;

        private VfxLoadingService.Bundle GetCharacterPlaybackBundle()
        {
            VfxCharacterFormDefinition form = ReferenceEquals(_championBundle, _activeBundle)
                ? _model.SelectedCharacterForm?.Definition : null;
            if (!ReferenceEquals(_characterPlaybackSource, _activeBundle) ||
                !ReferenceEquals(_characterPlaybackForm, form))
            {
                _characterPlaybackSource = _activeBundle;
                _characterPlaybackForm = form;
                _characterPlaybackBundle = _activeBundle?.CreateCharacterPlaybackView(form);
            }
            return _characterPlaybackBundle;
        }

        private void ClearCharacterFormState()
        {
            _isUpdatingCharacterForms = true;
            try
            {
                _model.SelectedCharacterForm = null;
                _model.CharacterForms.Clear();
                _characterPlaybackSource = null;
                _characterPlaybackForm = null;
                _characterPlaybackBundle = null;
                _model.NotifyCharacterCollectionsChanged();
            }
            finally
            {
                _isUpdatingCharacterForms = false;
            }
        }

        private void RebuildCharacterFormOptions()
        {
            _isUpdatingCharacterForms = true;
            try
            {
                _model.SelectedCharacterForm = null;
                _model.CharacterForms.Clear();
                if (_championModel == null || !ReferenceEquals(_championBundle, _activeBundle))
                {
                    _characterPlaybackSource = null;
                    _characterPlaybackForm = null;
                    _characterPlaybackBundle = null;
                    return;
                }

                IReadOnlyList<VfxCharacterFormDefinition> compatibleForms = VfxCharacterFormSemantics.CompatibleForms(
                    _activeBundle.CharacterForms,
                    _activeBundle.OwnerSceneContext);

                if (compatibleForms.Count > 0)
                {
                    var baseDef = VfxCharacterFormDefinition.CreateBase("Base");
                    _model.CharacterForms.Add(new VfxCharacterFormOption(baseDef, _championModel));
                    foreach (var form in compatibleForms)
                    {
                        _model.CharacterForms.Add(new VfxCharacterFormOption(form, _championModel));
                    }
                }
                uint? selectedHash = FocusedActor?.SelectedCharacterFormPathHash;
                _model.SelectedCharacterForm = _model.CharacterForms.FirstOrDefault(option =>
                    option.Definition.PathHash == selectedHash) ?? _model.CharacterForms.FirstOrDefault();
            }
            finally
            {
                _isUpdatingCharacterForms = false;
                _model.NotifyCharacterCollectionsChanged();
            }
            ApplySelectedCharacterForm(clearManualOverrides: false, restoreTextures: false);
        }

        private IReadOnlySet<uint> GetCharacterFormHiddenSubmeshes() =>
            VfxCharacterFormSemantics.HiddenSubmeshes(
                _activeBundle?.OwnerSceneContext?.InitialHiddenSubmeshHashes,
                ReferenceEquals(_championBundle, _activeBundle)
                    ? _model.SelectedCharacterForm?.Definition : null,
                _championModel?.Parts);

        private void ApplySelectedCharacterForm(bool clearManualOverrides, bool restoreTextures)
        {
            if (_championModel == null || !ReferenceEquals(_championBundle, _activeBundle))
                return;
            VfxSceneActor actor = FocusedActor;
            if (actor != null)
            {
                actor.SelectedCharacterFormPathHash = _model.SelectedCharacterForm?.Definition.PathHash;
                if (clearManualOverrides)
                    actor.SubmeshOverrides.Clear();
            }
            // A model-swap form (or returning to Base from one) reinstalls the owner mesh; the
            // reinstall rebuilds the form options and re-enters here with the matching SKN loaded.
            if (TrySwapCharacterFormModel())
                return;
            int gearIndex = _model.SelectedCharacterForm?.Definition.EquippedGearIndex ?? -1;
            bool gearChanged = _championModel.Parts.Any(part => part.EquippedGearIndex != gearIndex);
            foreach (var part in _championModel.Parts) part.EquippedGearIndex = gearIndex;
            if (restoreTextures || gearChanged)
                VfxCharacterFormSemantics.RestoreAuthoredTextures(_championModel.Parts);

            var clip = _activeAnimationClip;
            if (clip != null)
            {
                ConfigureAnimationClipCues(clip);
                ApplyAnimationClipCues(_model.CurrentTime);
            }
            else
            {
                ApplyOwnerSubmeshVisibility(GetCharacterFormHiddenSubmeshes());
            }
            AnimationClipCatalogItem selected = RebuildCharacterFormAnimationCatalog();
            if (clearManualOverrides)
            {
                _animationClipCancellation?.Cancel();
                if (_model.SelectedSpell != null)
                    _ = PlaySelectedSpellAsync(_model.SelectedSpell);
                else if (selected is { IsBindPose: false })
                    _ = RefreshCharacterFormAnimationAsync(selected);
            }
            OpenTkControl?.InvalidateVisual();
        }

        private bool TrySwapCharacterFormModel()
        {
            VfxCharacterFormDefinition form = _model.SelectedCharacterForm?.Definition;
            string authoredMesh = form is { IsModelSwap: true }
                ? form.MeshPath
                : _championBundle?.OwnerSceneContext?.MeshPath;
            string sknPath = ResolveSknPath(authoredMesh, _championSearchDir);
            if (string.IsNullOrEmpty(sknPath) || !File.Exists(sknPath) || SknLoadingService == null ||
                string.Equals(Path.GetFullPath(sknPath), Path.GetFullPath(_championSknPath ?? sknPath),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            _ = LoadCharacterFormModelAsync(form, sknPath);
            return true;
        }

        private async Task LoadCharacterFormModelAsync(VfxCharacterFormDefinition form, string sknPath)
        {
            int generation = ++_championLoadGeneration;
            var bundle = _championBundle;
            string searchDir = _championSearchDir;
            bool swapped = form is { IsModelSwap: true };
            string skinBinPath = swapped
                ? SknMaterialTextureResolver.TryResolveBinPath(sknPath) ?? bundle?.PrimaryBinPath
                : bundle?.PrimaryBinPath;
            try
            {
                SceneModel loaded = await SknLoadingService.LoadModelWithSkinBin(sknPath, skinBinPath, searchDir);
                if (generation != _championLoadGeneration || !ReferenceEquals(bundle, _activeBundle) || _isCleanedUp)
                {
                    loaded?.Dispose();
                    return;
                }
                if (loaded == null)
                {
                    _model.LogMessages.Add($"[CHAMPION MESH] Form model could not be loaded: {Path.GetFileName(sknPath)}");
                    return;
                }

                InstallChampionModel(loaded, bundle, sknPath, searchDir, startPreview: false,
                    authoredSkeletonPath: swapped ? form.SkeletonPath : null);

                _animationClipCancellation?.Cancel();
                if (_model.SelectedSpell != null)
                    _ = PlaySelectedSpellAsync(_model.SelectedSpell);
                else if (_model.SelectedAnimation is { IsBindPose: false } animation)
                    _ = PlaySelectedAnimationAsync(animation);
                OpenTkControl?.InvalidateVisual();
            }
            catch (Exception ex)
            {
                LogService?.LogDebug($"Form model not loaded: {ex.Message}");
            }
        }

        private AnimationClipCatalogItem RebuildCharacterFormAnimationCatalog()
        {
            AnimationClipCatalogItem selected = _model.SelectedAnimation;
            if (_clipCatalog == null || _model.DetectedAnimations.Count == 0)
                return selected;
            var rebuilt = BuildAnimationCatalog(_model.AnimationParameter);
            AnimationClipCatalogItem bindPose = AnimationClipCatalogItem.CreateBindPoseItem();
            AnimationClipCatalogItem replacement = selected?.IsBindPose == true ? bindPose :
                rebuilt.FirstOrDefault(item => item.Clip?.GraphPathHash == selected?.Clip?.GraphPathHash &&
                    item.Clip?.OwnerPathHash == selected?.Clip?.OwnerPathHash);

            _isUpdatingCharacterForms = true;
            try
            {
                _model.DetectedAnimations.Clear();
                _model.DetectedAnimations.Add(bindPose);
                foreach (AnimationClipCatalogItem item in rebuilt)
                    _model.DetectedAnimations.Add(item);
                _model.SelectedAnimation = replacement;
                ConfigureAnimationParameterOptions(replacement);
            }
            finally
            {
                _isUpdatingCharacterForms = false;
            }
            return replacement;
        }

        private async Task RefreshCharacterFormAnimationAsync(AnimationClipCatalogItem replacement)
        {
            var bundle = _activeBundle;
            var form = _model.SelectedCharacterForm;
            double time = _model.CurrentTime;
            bool playing = _model.IsPlaying;
            AnimationClipCatalogItem previousPlayback = _activeAnimationClip;
            await PlaySelectedAnimationAsync(replacement);
            if (_isCleanedUp || !ReferenceEquals(bundle, _activeBundle) ||
                !ReferenceEquals(form, _model.SelectedCharacterForm) ||
                !ReferenceEquals(replacement, _model.SelectedAnimation) ||
                ReferenceEquals(previousPlayback, _activeAnimationClip) ||
                !SameAnimationClip(_activeAnimationClip, replacement) ||
                _championModel?.CurrentAnimation == null)
                return;

            double resumeAt = _model.TotalDuration > 0d ? Math.Min(time, _model.TotalDuration) : 0d;
            _model.CurrentTime = resumeAt;
            if (_championModel != null)
                _championModel.AnimationTime = resumeAt;
            ApplyAnimationClipCues(resumeAt);
            _vfxRenderer?.Seek(resumeAt);
            if (!playing)
                _vfxRenderer?.Pause();
            _model.IsPlaying = playing;
            UpdatePlayheadPosition();
        }
    }
}
