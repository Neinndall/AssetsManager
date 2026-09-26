using System.Collections.Generic;
using System.Linq;

namespace AssetsManager.Views.Models.Viewer
{
    internal enum GameMaterialBoolKind { Unsupported, Gear, Buff, All, Not }

    internal sealed record GameMaterialBoolCondition(
        GameMaterialBoolKind Kind, int GearIndex = 0,
        IReadOnlyList<GameMaterialBoolCondition> Children = null)
    {
        internal bool? Evaluate(int gearIndex) => Kind switch
        {
            GameMaterialBoolKind.Gear => gearIndex == GearIndex,
            // The model preview has no active gameplay buffs.
            GameMaterialBoolKind.Buff => false,
            GameMaterialBoolKind.Not when Children?.Count == 1 => !Children[0].Evaluate(gearIndex),
            GameMaterialBoolKind.All => EvaluateAll(gearIndex),
            _ => null
        };

        private bool? EvaluateAll(int gearIndex)
        {
            bool unknown = false;
            foreach (var child in Children ?? System.Array.Empty<GameMaterialBoolCondition>())
            {
                bool? value = child.Evaluate(gearIndex);
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
        internal string Resolve(int gearIndex) => Options.FirstOrDefault(
            option => option.Condition.Evaluate(gearIndex) == true)?.TexturePath;
    }
}
