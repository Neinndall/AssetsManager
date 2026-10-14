using System;
using System.Collections.Generic;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>
    /// Utility methods for resolving texture and material bindings per submesh,
    /// adhering to the 4-level engine priority order and fallback rules.
    /// </summary>
    public static class SubmeshBindingUtils
    {
        /// <summary>
        /// Selects the effective diffuse texture key for a submesh according to material slot definitions
        /// and blending modes, mirroring the native engine's mapOf contract.
        /// </summary>
        /// <param name="materialBaseTextureKey">The base texture named by the material, or null if none.</param>
        /// <param name="fallbackSubmeshTextureKey">The direct texture assigned to the submesh or skin.</param>
        /// <param name="isMaterialOpaque">True if the material has opaque blending (depth write on, blending off).</param>
        /// <param name="isMaterialMissing">True if the material link could not be resolved.</param>
        /// <returns>The resolved texture key, or null if untextured/errored.</returns>
        public static string SelectActiveTexture(
            string materialBaseTextureKey,
            string fallbackSubmeshTextureKey,
            bool isMaterialOpaque,
            bool isMaterialMissing)
        {
            if (isMaterialMissing)
                return null;

            if (!string.IsNullOrWhiteSpace(materialBaseTextureKey))
                return materialBaseTextureKey;

            // When no material base sampler is authored, opaque materials draw the submesh's direct texture.
            // Translucent or additive passes do not inherit the base texture as diffuse.
            return isMaterialOpaque ? fallbackSubmeshTextureKey : null;
        }

        /// <summary>
        /// Resolves the direct texture candidate for a submesh given submesh overrides and skin default texture.
        /// Priority: Specific Submesh Override > Skin Default Texture.
        /// </summary>
        public static string ResolveDirectTexture(
            string submeshName,
            IReadOnlyDictionary<string, string> submeshOverrides,
            string skinDefaultTexture)
        {
            if (!string.IsNullOrWhiteSpace(submeshName) && submeshOverrides != null &&
                submeshOverrides.TryGetValue(submeshName, out string overrideTexture) &&
                !string.IsNullOrWhiteSpace(overrideTexture))
            {
                return overrideTexture;
            }

            return skinDefaultTexture;
        }
    }
}
