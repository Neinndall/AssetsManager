using System;
using System.Collections.Generic;

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
                if (!string.IsNullOrEmpty(part.MaterialDefinition?.BaseTextureName))
                    part.SelectedTextureName = part.MaterialDefinition.BaseTextureName;
            }
        }
    }
}
