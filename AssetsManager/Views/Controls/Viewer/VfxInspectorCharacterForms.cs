using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

                foreach (VfxCharacterFormDefinition form in _activeBundle.CharacterForms)
                {
                    if (form.HasMaterialOverrides)
                        continue;
                    if (!string.IsNullOrWhiteSpace(form.MeshPath) &&
                        !SameCharacterAsset(form.MeshPath, _activeBundle.OwnerSceneContext?.MeshPath))
                        continue;
                    if (!string.IsNullOrWhiteSpace(form.SkeletonPath) &&
                        !SameCharacterAsset(form.SkeletonPath, _activeBundle.OwnerSceneContext?.SkeletonPath))
                        continue;
                    _model.CharacterForms.Add(new VfxCharacterFormOption(form, _championModel));
                }
                uint? selectedHash = _model.SelectedWorkspaceTab?.SelectedCharacterFormPathHash;
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

        private static bool SameCharacterAsset(string left, string right) =>
            !string.IsNullOrWhiteSpace(right) && string.Equals(
                left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private IReadOnlySet<uint> GetCharacterFormHiddenSubmeshes() =>
            VfxCharacterFormSemantics.HiddenSubmeshes(
                _activeBundle?.OwnerSceneContext?.InitialHiddenSubmeshHashes,
                ReferenceEquals(_championBundle, _activeBundle)
                    ? _model.SelectedCharacterForm?.Definition : null);

        private void ApplySelectedCharacterForm(bool clearManualOverrides, bool restoreTextures)
        {
            if (_championModel == null || !ReferenceEquals(_championBundle, _activeBundle))
                return;
            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
            if (tab?.Kind == VfxWorkspaceTabKind.Skin)
            {
                tab.SelectedCharacterFormPathHash = _model.SelectedCharacterForm?.Definition.PathHash;
                if (clearManualOverrides)
                    tab.CharacterSubmeshOverrides.Clear();
            }
            int gearIndex = _model.SelectedCharacterForm?.Definition.GearIndex ?? 0;
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
