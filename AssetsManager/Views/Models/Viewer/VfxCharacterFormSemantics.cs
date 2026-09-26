using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Resolvers;

namespace AssetsManager.Views.Models.Viewer
{
    internal static class VfxCharacterFormSemantics
    {
        internal static IReadOnlySet<uint> HiddenSubmeshes(
            IEnumerable<uint> initiallyHidden, VfxCharacterFormDefinition form)
        {
            var hidden = new HashSet<uint>(initiallyHidden ?? Array.Empty<uint>());
            if (form != null)
            {
                hidden.ExceptWith(form.ShowSubmeshHashes);
                hidden.UnionWith(form.HideSubmeshHashes);
            }
            return hidden;
        }

        internal static void RestoreAuthoredTextures(IEnumerable<ModelPart> parts)
        {
            foreach (ModelPart part in parts)
            {
                var material = part.MaterialDefinition;
                if (material == null) continue;
                string path = material.ResolveTextureSwap(material.BaseSamplerName, part.EquippedGearIndex);
                if (path != null)
                    part.SelectedTextureName = SknMaterialTextureResolver.MatchTextureKey(
                        path, part.AllTextures?.Keys.ToArray());
                else if (!string.IsNullOrEmpty(material.BaseTextureName))
                    part.SelectedTextureName = material.BaseTextureName;
            }
        }
    }
}
