using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    internal static class VfxPersistentEffectSemantics
    {
        private const string PreviewPrefix = "persistent-condition:";

        internal static string PreviewKey(int index) => PreviewPrefix + index.ToString(CultureInfo.InvariantCulture);

        internal static IEnumerable<string> PreviewKeys(VfxOwnerSceneContext owner) =>
            Enumerable.Range(0, owner?.SubmeshConditions?.Count ?? 0).Select(PreviewKey);

        internal static bool TryPreviewIndex(string key, out int index)
        {
            index = -1;
            return key?.StartsWith(PreviewPrefix, StringComparison.Ordinal) == true &&
                int.TryParse(key.AsSpan(PreviewPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index);
        }

        internal static bool Active(VfxSubmeshCondition condition, int index, GameMaterialState state) =>
            state.HasBuff(PreviewKey(index)) || condition.Condition?.Evaluate(state) == true;

        internal static IEnumerable<VfxIdleEffectDefinition> AllEffects(VfxOwnerSceneContext owner) =>
            (owner?.SubmeshConditions ?? Array.Empty<VfxSubmeshCondition>()).SelectMany(condition => condition.Effects);
    }
}
