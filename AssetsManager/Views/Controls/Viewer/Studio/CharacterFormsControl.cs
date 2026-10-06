using AssetsManager.Services.Viewer.Semantics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private bool _isUpdatingCharacterForms;
        private int _characterFormLoadGeneration;
        private VfxLoadingService.Bundle _characterPlaybackSource;
        private CharacterFormDefinition _characterPlaybackForm;
        private VfxLoadingService.Bundle _characterPlaybackBundle;

        private VfxLoadingService.Bundle GetCharacterPlaybackBundle()
        {
            CharacterFormDefinition form = ReferenceEquals(_championBundle, _activeBundle)
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

                foreach (var option in CharacterFormSemantics.FormOptions(
                    _activeBundle.CharacterForms,
                    _activeBundle.OwnerSceneContext,
                    _championModel))
                    _model.CharacterForms.Add(option);
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
            CharacterFormSemantics.HiddenSubmeshes(
                _activeBundle?.OwnerSceneContext?.InitialHiddenSubmeshHashes,
                ReferenceEquals(_championBundle, _activeBundle)
                    ? _model.SelectedCharacterForm?.Definition : null,
                _championModel?.Parts,
                _activeBundle?.OwnerSceneContext?.SubmeshConditions,
                _championModel?.GameState);

        private void ApplySelectedCharacterForm(bool clearManualOverrides, bool restoreTextures)
        {
            if (_championModel == null || !ReferenceEquals(_championBundle, _activeBundle))
                return;
            StudioSceneActor actor = FocusedActor;
            if (actor != null)
            {
                actor.SelectedCharacterFormPathHash = _model.SelectedCharacterForm?.Definition.PathHash;
                if (clearManualOverrides)
                    actor.SubmeshOverrides.Clear();
            }
            // A form that reloads the model (or returning from one) reinstalls the owner mesh; the
            // reinstall rebuilds the form options and re-enters here with that model installed.
            if (TryReloadCharacterFormModel())
                return;
            int gearIndex = _model.SelectedCharacterForm?.Definition.GearIndex ?? -1;
            bool gearChanged = _championModel.Parts.Any(part => part.EquippedGearIndex != gearIndex);
            foreach (var part in _championModel.Parts) part.EquippedGearIndex = gearIndex;
            if (restoreTextures || gearChanged)
                CharacterFormSemantics.RestoreAuthoredTextures(_championModel.Parts);

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
            StudioViewportView.OpenTkControl?.InvalidateVisual();
        }

        private bool TryReloadCharacterFormModel()
        {
            // Every selection supersedes a pending form load, including a return to the installed form.
            int formGeneration = ++_characterFormLoadGeneration;
            CharacterFormDefinition form = _model.SelectedCharacterForm?.Definition;
            bool reloads = form is { ReloadsModel: true };
            uint formPathHash = reloads ? form.PathHash : 0u;
            string authoredMesh = reloads && !string.IsNullOrWhiteSpace(form.MeshPath)
                ? form.MeshPath
                : _championBundle?.OwnerSceneContext?.MeshPath;
            string sknPath = ResolveSknPath(authoredMesh, _championSearchDir);
            if (string.IsNullOrEmpty(sknPath) || !File.Exists(sknPath) || SknLoadingService == null ||
                (formPathHash == _championFormPathHash &&
                 string.Equals(Path.GetFullPath(sknPath), Path.GetFullPath(_championSknPath ?? sknPath),
                     StringComparison.OrdinalIgnoreCase)))
                return false;

            _ = LoadCharacterFormModelAsync(form, formPathHash, sknPath, formGeneration);
            return true;
        }

        private async Task LoadCharacterFormModelAsync(
            CharacterFormDefinition form, uint formPathHash, string sknPath, int formGeneration)
        {
            int generation = ++_championLoadGeneration;
            var bundle = _championBundle;
            string searchDir = _championSearchDir;
            try
            {
                // The GearData lives in the Skin BIN: its skinMeshProperties supplies the form's materials.
                SceneModel loaded = await SknLoadingService.LoadModelWithSkinBin(
                    sknPath, bundle?.PrimaryBinPath, searchDir, gearUpgradePathHash: formPathHash);
                if (generation != _championLoadGeneration || formGeneration != _characterFormLoadGeneration ||
                    !ReferenceEquals(bundle, _activeBundle) || _isCleanedUp)
                {
                    loaded?.Dispose();
                    return;
                }
                if (loaded == null)
                {
                    _model.LogMessages.Add($"[CHAMPION MESH] Form model could not be loaded: {Path.GetFileName(sknPath)}");
                    return;
                }

                bool reloaded = formPathHash != 0;
                InstallChampionModel(loaded, bundle, sknPath, searchDir, startPreview: false,
                    authoredSkeletonPath: reloaded ? form.SkeletonPath : null,
                    formPathHash: formPathHash,
                    formSkinScale: reloaded ? form.SkinScale : null);

                _animationClipCancellation?.Cancel();
                if (_model.SelectedSpell != null)
                    _ = PlaySelectedSpellAsync(_model.SelectedSpell);
                else if (_model.SelectedAnimation is { IsBindPose: false } animation)
                    _ = PlaySelectedAnimationAsync(animation);
                StudioViewportView.OpenTkControl?.InvalidateVisual();
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
