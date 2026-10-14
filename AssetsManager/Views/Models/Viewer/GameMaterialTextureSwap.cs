using System.Collections.Generic;
using System.Linq;

namespace AssetsManager.Views.Models.Viewer
{
    // Buff, Dead and Animation read the preview's game state; the resting preview has no buff, is alive and
    // plays no scripted animation. Inactive covers what it never does: cast, attack, move, stand in grass,
    // belong to the enemy team or carry buffs picked by type or attribute.
    // Compare tests two float drivers with a FloatComparisonMaterialDriver operator.
    internal enum GameMaterialBoolKind { Unsupported, Gear, Buff, Dead, Animation, Inactive, All, Any, Not, Compare }

    /// <param name="Name">The buff script a Buff condition checks.</param>
    /// <param name="Animations">The clip name hashes an Animation condition checks.</param>
    internal sealed record GameMaterialBoolCondition(
        GameMaterialBoolKind Kind, int GearIndex = 0,
        IReadOnlyList<GameMaterialBoolCondition> Children = null,
        string Name = null,
        IReadOnlyList<uint> Animations = null,
        System.Func<GameMaterialState, System.Numerics.Vector4?> Left = null,
        System.Func<GameMaterialState, System.Numerics.Vector4?> Right = null,
        uint Operator = 0)
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
            GameMaterialBoolKind.Compare => EvaluateCompare(state),
            _ => null
        };

        /// <summary>The buff scripts this condition, or any it nests, checks.</summary>
        internal IEnumerable<string> Buffs() =>
            (Kind == GameMaterialBoolKind.Buff && !string.IsNullOrEmpty(Name) ? new[] { Name } : Enumerable.Empty<string>())
            .Concat((Children ?? System.Array.Empty<GameMaterialBoolCondition>()).SelectMany(child => child.Buffs()));

        // mOperator, read from the game's data: 0 equal (Ezreal's 1..5 passive stacks, buffs against 1), 1 greater
        // (speed over 350), 2 at least (Irelia's 4 and Jax's 8 stacks), 3 less (health under 30%), 4 at most, 5 not equal.
        private bool? EvaluateCompare(GameMaterialState state)
        {
            if (Left?.Invoke(state) is not System.Numerics.Vector4 left || Right?.Invoke(state) is not System.Numerics.Vector4 right)
                return null;
            float a = left.X, b = right.X;
            const float Tolerance = 1e-4f;
            return Operator switch
            {
                0 => System.MathF.Abs(a - b) <= Tolerance,
                1 => a > b,
                2 => a >= b - Tolerance,
                3 => a < b,
                4 => a <= b + Tolerance,
                5 => System.MathF.Abs(a - b) > Tolerance,
                _ => null
            };
        }

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
