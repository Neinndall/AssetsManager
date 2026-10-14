using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    /// <summary>
    /// The game state the champion's dynamic materials read: the buffs the Show menu turns on and the clips
    /// the preview plays. Aatrox Skin33 lights its sword with AatroxInCombat or while Recall plays; Skin40
    /// shows its R form with AatroxR. Each scene actor keeps its own buffs, which background actors apply
    /// with their own clip (<see cref="Services.Viewer.Runtime.StudioSceneActorRuntime.SetGameStates"/>).
    /// </summary>
    public partial class StudioControl
    {
        private AnimationClipCatalogItem _playingAnimationClip;

        /// <summary>Offers every buff the champion's materials read, on as the focused actor left them.</summary>
        private void RebuildCharacterGameStates()
        {
            IEnumerable<string> buffs = (_championModel?.Parts ?? Enumerable.Empty<ModelPart>())
                .Select(part => part.MaterialDefinition)
                .Where(material => material != null)
                .Distinct()
                .SelectMany(material => material.DynamicParameters.SelectMany(parameter => parameter.Buffs ?? Array.Empty<string>())
                    .Concat(material.TextureSwaps.SelectMany(swap => swap.Options.SelectMany(option => option.Condition.Buffs()))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(buff => buff, StringComparer.OrdinalIgnoreCase);
            _model.SetCharacterGameStates(buffs, FocusedActor?.EnabledGameStates, OnCharacterGameStateChanged,
                CharacterGameStateLabel);
            UpdateChampionGameState();
        }

        /// <summary>The clip and the parallel clips it plays, which animation conditions test.</summary>
        private void SetPlayingAnimation(AnimationClipCatalogItem clip)
        {
            _playingAnimationClip = clip;
            UpdateChampionGameState();
        }

        /// <summary>A buff toggled in the Inspector: persistent submesh conditions may read it too.</summary>
        private void OnCharacterGameStateChanged()
        {
            if (FocusedActor is { } actor)
            {
                actor.EnabledGameStates.Clear();
                actor.EnabledGameStates.UnionWith(EnabledCharacterGameStates());
            }
            UpdateChampionGameState();
            if (_activeAnimationClip != null)
                ConfigureAnimationClipCues(_activeAnimationClip);
            else
                ApplyOwnerSubmeshVisibility(GetCharacterFormHiddenSubmeshes());
        }

        private IEnumerable<string> EnabledCharacterGameStates() =>
            _model.CharacterGameStates.Where(option => option.IsEnabled).Select(option => option.Name);

        private string CharacterGameStateLabel(string key)
        {
            if (!GameMaterialState.TrySpellBuffHash(key, out uint hash)) return key;
            if (_activeBundle?.SpellPreviews.TryGetValue(hash, out var spell) == true &&
                !string.IsNullOrWhiteSpace(spell.ScriptName)) return spell.ScriptName;

            string path = VfxLoadingService?.ResolveBinEntryPath(hash);
            return !string.IsNullOrWhiteSpace(path) && !path.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? path.Split('/').Last() : $"{hash:x8}";
        }

        private void UpdateChampionGameState()
        {
            if (_championModel == null)
                return;
            _championModel.GameState = GameMaterialState.Preview(EnabledCharacterGameStates(), _playingAnimationClip,
                _activeBundle?.SpellPreviews);
            // A paused or bind-pose preview draws on demand; the new state needs its own frame.
            StudioViewportView.OpenTkControl?.InvalidateVisual();
        }
    }
}
