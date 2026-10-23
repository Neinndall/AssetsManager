using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Semantics;
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

        /// <summary>Offers buffs read by materials and submesh conditions, on as the focused actor left them.</summary>
        private void RebuildCharacterGameStates()
        {
            var buffs = CharacterFormSemantics.GameStateBuffs(_championModel?.Parts, _activeBundle?.OwnerSceneContext)
                .Concat(VfxPersistentEffectSemantics.PreviewKeys(_activeBundle?.OwnerSceneContext)).ToArray();
            var knownBuffs = new HashSet<string>(buffs, StringComparer.OrdinalIgnoreCase);
            // Preview already expands a script-name buff to its spell keys. Offer that state once.
            string CanonicalBuff(string key) => GameMaterialState.TrySpellBuffHash(key, out uint hash) &&
                _activeBundle?.SpellPreviews.TryGetValue(hash, out var spell) == true &&
                !string.IsNullOrWhiteSpace(spell.ScriptName) && knownBuffs.Contains(spell.ScriptName)
                    ? spell.ScriptName : key;
            _model.SetCharacterGameStates(buffs, FocusedActor?.EnabledGameStates, OnCharacterGameStateChanged,
                CharacterGameStateLabel, key => VfxPersistentEffectSemantics.TryPreviewIndex(key, out _),
                CharacterGameStateDescription, CanonicalBuff);
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
            {
                ConfigureAnimationClipCues(_activeAnimationClip);
                ApplyAnimationClipCues(_model.CurrentTime);
            }
            else
                ApplyOwnerSubmeshVisibility(GetCharacterFormHiddenSubmeshes());
        }

        private IEnumerable<string> EnabledCharacterGameStates() =>
            _model.CharacterGameStates.Concat(_model.CharacterPersistentConditions)
                .Where(option => option.IsEnabled).Select(option => option.Name);

        private string CharacterGameStateLabel(string key)
        {
            if (VfxPersistentEffectSemantics.TryPreviewIndex(key, out int index))
            {
                var names = PersistentEffectNames(index).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                string name = names.FirstOrDefault();
                if (!string.IsNullOrEmpty(name))
                {
                    name = name.Replace('\\', '/').Split('/').Last();
                    name = System.Text.RegularExpressions.Regex.Replace(name, @"^.*?_Skin\d+_", "",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Replace('_', ' ');
                }
                else
                {
                    var conditions = _activeBundle?.OwnerSceneContext?.SubmeshConditions;
                    name = conditions != null && index < conditions.Count &&
                        (conditions[index].Show.Count > 0 || conditions[index].Hide.Count > 0)
                        ? "Mesh visibility" : "Condition";
                }
                return $"{index + 1:00} · {name}" + (names.Length > 1 ? $" (+{names.Length - 1})" : "");
            }
            if (!GameMaterialState.TrySpellBuffHash(key, out uint hash)) return key;
            if (_activeBundle?.SpellPreviews.TryGetValue(hash, out var spell) == true &&
                !string.IsNullOrWhiteSpace(spell.ScriptName)) return spell.ScriptName;

            string path = VfxLoadingService?.ResolveBinEntryPath(hash);
            return !string.IsNullOrWhiteSpace(path) && !path.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? path.Split('/').Last() : $"{hash:x8}";
        }

        private string PersistentConditionLabel(GameMaterialBoolCondition condition) => condition.Kind switch
        {
            GameMaterialBoolKind.Buff => CharacterGameStateLabel(condition.Name ?? "Unknown buff"),
            GameMaterialBoolKind.Gear => $"Gear {condition.GearIndex}",
            GameMaterialBoolKind.Dead => "Dead",
            GameMaterialBoolKind.Inactive => "Gameplay condition",
            GameMaterialBoolKind.Animation => "Animation: " + string.Join(", ",
                (condition.Animations ?? Array.Empty<uint>()).Select(hash =>
                    _model.DetectedAnimations.FirstOrDefault(clip => GameMaterialState.AnimationHash(clip.Name) == hash ||
                        clip.Clip?.OwnerPathHash == hash)?.Name ?? $"{hash:x8}")),
            GameMaterialBoolKind.Not when condition.Children?.Count == 1 => $"Not ({PersistentConditionLabel(condition.Children[0])})",
            GameMaterialBoolKind.All when condition.Children?.Count == 0 => "Always",
            GameMaterialBoolKind.All => string.Join(" + ", (condition.Children ?? Array.Empty<GameMaterialBoolCondition>()).Select(PersistentConditionLabel)),
            GameMaterialBoolKind.Any => string.Join(" / ", (condition.Children ?? Array.Empty<GameMaterialBoolCondition>()).Select(PersistentConditionLabel)),
            GameMaterialBoolKind.Compare => "Value comparison",
            _ => "Manual condition"
        };

        private IEnumerable<string> PersistentEffectNames(int index)
        {
            var conditions = _activeBundle?.OwnerSceneContext?.SubmeshConditions;
            if (conditions == null || index < 0 || index >= conditions.Count) yield break;
            foreach (var effect in conditions[index].Effects)
                yield return _activeBundle.ResourceMap.TryGetValue(effect.EffectKey, out uint target) &&
                    _activeBundle.Systems.TryGetValue(target, out var system) ? system.Name :
                    !string.IsNullOrEmpty(effect.EffectName) ? effect.EffectName : $"Effect {effect.EffectKey:x8}";
        }

        private string CharacterGameStateDescription(string key)
        {
            if (!VfxPersistentEffectSemantics.TryPreviewIndex(key, out int index)) return CharacterGameStateLabel(key);
            var condition = _activeBundle.OwnerSceneContext.SubmeshConditions[index];
            var names = PersistentEffectNames(index).ToArray();
            return $"Manual preview of condition {index + 1}. Unchecked uses the automatic state.\n" +
                $"Condition: {PersistentConditionLabel(condition.Condition)}\n" +
                $"Effects: {(names.Length == 0 ? "None" : string.Join(", ", names))}\n" +
                $"Bones: {string.Join(", ", condition.Effects.Select(effect => string.IsNullOrEmpty(effect.BoneName) ? "Root" : effect.BoneName).Distinct())}\n" +
                $"Submeshes: show {condition.Show.Count}, hide {condition.Hide.Count}";
        }

        private void UpdateChampionGameState()
        {
            if (_championModel == null)
                return;
            _championModel.GameState = GameMaterialState.Preview(EnabledCharacterGameStates(), _playingAnimationClip,
                _activeBundle?.SpellPreviews);
            UpdatePersistentEffectState();
            var state = (_championModel.GameState ?? GameMaterialState.Resting) with
                { Gear = _model.SelectedCharacterForm?.Definition?.GearIndex ?? -1 };
            foreach (var option in _model.CharacterPersistentConditions)
                if (VfxPersistentEffectSemantics.TryPreviewIndex(option.Name, out int index) &&
                    _activeBundle?.OwnerSceneContext?.SubmeshConditions is { } conditions && index < conditions.Count)
                    option.IsAutomaticallyActive = conditions[index].Condition.Evaluate(state) == true;
            // A paused or bind-pose preview draws on demand; the new state needs its own frame.
            StudioViewportView.OpenTkControl?.InvalidateVisual();
        }

        private void UpdatePersistentEffectState() => _vfxRenderer?.SetOwnerGameState(
            (_championModel?.GameState ?? GameMaterialState.Resting) with
            {
                Gear = ReferenceEquals(_championBundle, _activeBundle)
                    ? _model.SelectedCharacterForm?.Definition?.GearIndex ?? -1 : -1
            });

        private void ConfigureBindPoseEffects()
        {
            var bundle = GetCharacterPlaybackBundle();
            if (bundle == null || (bundle.IdleEffects.Count == 0 &&
                VfxPersistentEffectSemantics.AllEffects(bundle.OwnerSceneContext).Any() == false))
            {
                _vfxRenderer?.SetSystem(null);
                return;
            }
            EnsureVfxRenderSession();
            _vfxRenderer.SetAnimationSession(null, bundle.IdleEffects, bundle.Systems, bundle.ResourceMap,
                ResolvePreviewSearchDirectory(), 1337, 3d, bundle.OwnerSceneContext);
            _vfxRenderer.SetBoneTransformSampler(null);
            ApplyChampionBindPose();
            UpdatePersistentEffectState();
            ApplyOwnerSubmeshVisibility(GetCharacterFormHiddenSubmeshes());
            ResetPreviewLoopRange(3d);
        }
    }
}
