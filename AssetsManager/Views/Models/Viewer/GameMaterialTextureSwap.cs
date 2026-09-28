using System.Collections.Generic;
using System.Linq;

namespace AssetsManager.Views.Models.Viewer
{
    // Buff, Dead and Animation read the preview's game state; the resting preview has no buff, is alive and
    // plays no scripted animation. Inactive covers what it never does: cast, attack, move, stand in grass,
    // belong to the enemy team or carry buffs picked by type or attribute.
    internal enum GameMaterialBoolKind { Unsupported, Gear, Buff, Dead, Animation, Inactive, All, Any, Not }

    /// <param name="Name">The buff script a Buff condition checks.</param>
    /// <param name="Animations">The clip name hashes an Animation condition checks.</param>
    internal sealed record GameMaterialBoolCondition(
        GameMaterialBoolKind Kind, int GearIndex = 0,
        IReadOnlyList<GameMaterialBoolCondition> Children = null,
        string Name = null,
        IReadOnlyList<uint> Animations = null)
    {
        internal bool? Evaluate(GameMaterialState state) => Kind switch
        {
            GameMaterialBoolKind.Gear => state.Gear == GearIndex,
            GameMaterialBoolKind.Buff => state.HasBuff(Name),
            GameMaterialBoolKind.Dead => state.Dead,
            GameMaterialBoolKind.Animation => Animations?.Any(state.IsPlaying) == true,
            GameMaterialBoolKind.Inactive => false,
            GameMaterialBoolKind.Any => EvaluateAny(state),
            GameMaterialBoolKind.Not when Children?.Count == 1 => !Children[0].Evaluate(state),
            GameMaterialBoolKind.All => EvaluateAll(state),
            _ => null
        };

        /// <summary>The buff scripts this condition, or any it nests, checks.</summary>
        internal IEnumerable<string> Buffs() =>
            (Kind == GameMaterialBoolKind.Buff && !string.IsNullOrEmpty(Name) ? new[] { Name } : Enumerable.Empty<string>())
            .Concat((Children ?? System.Array.Empty<GameMaterialBoolCondition>()).SelectMany(child => child.Buffs()));

        private bool? EvaluateAny(GameMaterialState state)
        {
            bool unknown = false;
            foreach (var child in Children ?? System.Array.Empty<GameMaterialBoolCondition>())
            {
                bool? value = child.Evaluate(state);
                if (value == true) return true;
                unknown |= !value.HasValue;
            }
            return unknown ? null : false;
        }

        private bool? EvaluateAll(GameMaterialState state)
        {
            bool unknown = false;
            foreach (var child in Children ?? System.Array.Empty<GameMaterialBoolCondition>())
            {
                bool? value = child.Evaluate(state);
                if (value == false) return false;
                unknown |= !value.HasValue;
            }
            return unknown ? null : true;
        }
    }

    internal sealed record GameMaterialTextureSwapOption(string TexturePath, GameMaterialBoolCondition Condition);

    internal sealed record GameMaterialTextureSwap(string SamplerName, IReadOnlyList<GameMaterialTextureSwapOption> Options)
    {
        // Preserve authored ordering, including specific buff conditions before the generic gear option.
        internal string Resolve(GameMaterialState state) => Options.FirstOrDefault(
            option => option.Condition.Evaluate(state) == true)?.TexturePath;
    }
}
