using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    /// <summary>
    /// The game state the champion's dynamic materials read: the buffs the Show menu turns on and the clips
    /// the preview plays. Aatrox Skin33 lights its sword with AatroxInCombat or while Recall plays; Skin40
    /// shows its R form with AatroxR.
    /// </summary>
    public partial class VfxInspectorControl
    {
        private readonly HashSet<uint> _playingAnimationHashes = new();

        /// <summary>Offers every buff the champion's materials read, all off.</summary>
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
            _model.SetCharacterGameStates(buffs, UpdateChampionGameState);
            UpdateChampionGameState();
        }

        /// <summary>The clip and the parallel clips it plays, which animation conditions test.</summary>
        private void SetPlayingAnimation(AnimationClipCatalogItem clip)
        {
            _playingAnimationHashes.Clear();
            if (clip != null && !clip.IsBindPose)
            {
                _playingAnimationHashes.Add(GameMaterialState.AnimationHash(clip.Name));
                foreach (uint child in clip.Clip?.ChildClipHashes ?? Array.Empty<uint>())
                    _playingAnimationHashes.Add(child);
            }
            UpdateChampionGameState();
        }

        private void UpdateChampionGameState()
        {
            if (_championModel == null)
                return;
            string[] buffs = _model.CharacterGameStates.Where(option => option.IsEnabled).Select(option => option.Name).ToArray();
            _championModel.GameState = buffs.Length == 0 && _playingAnimationHashes.Count == 0
                ? null
                : GameMaterialState.From(0, buffs, _playingAnimationHashes);
            // A paused or bind-pose preview draws on demand; the new state needs its own frame.
            OpenTkControl?.InvalidateVisual();
        }
    }
}
