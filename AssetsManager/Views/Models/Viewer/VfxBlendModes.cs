namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>Semantic rendering families used by authored League particle blend modes.</summary>
    public enum VfxBlendModeKind
    {
        Alpha,
        Multiply,
        Additive
    }

    /// <summary>Single source of truth for translating raw BIN blendMode values.</summary>
    public static class VfxBlendModes
    {
        public static bool IsKnown(int rawMode) => rawMode is >= 0 and <= 5;

        public static bool IsAdditive(int rawMode) => rawMode is 2 or 4;

        // Additive blending already uses the authored source alpha. Do not amplify
        // RGB outside the BIN material, or authored highlights become overexposed.
        public static float ResolveEmissiveStrength(int rawMode) => 1f;

        public static bool ShouldAlphaTest(int rawMode, int alphaReference)
            => !IsAdditive(rawMode) && alphaReference > 0;

        public static bool ShouldWriteDepth(int rawMode, int alphaReference)
            => !IsAdditive(rawMode) && alphaReference > 0;

        public static int ResolveColorRenderFlags(int rawFlags, bool hasParticleColorTexture)
            => hasParticleColorTexture ? rawFlags | 1 : rawFlags;

        /// <summary>
        /// Riot's miscRenderFlags bit 0 requests inverted mesh faces for normal/multiply
        /// materials. The renderer uses this to choose a safe double-sided fallback because
        /// VFX mesh buffers do not carry normals.
        /// </summary>
        public static bool ShouldFlipFaces(int miscRenderFlags, int rawMode, bool disableBackfaceCull)
            => (miscRenderFlags & 1) != 0 && !disableBackfaceCull && rawMode is 1 or 3;

        public static VfxBlendModeKind Resolve(int rawMode)
            => rawMode switch
            {
                2 or 4 => VfxBlendModeKind.Additive,
                3 => VfxBlendModeKind.Multiply,
                _ => VfxBlendModeKind.Alpha
            };

        public static string Describe(int rawMode)
        {
            if (!IsKnown(rawMode))
                return $"Unknown ({rawMode}, safe alpha fallback)";

            return Resolve(rawMode) switch
            {
                VfxBlendModeKind.Multiply => $"Multiply ({rawMode})",
                VfxBlendModeKind.Additive => $"Additive ({rawMode})",
                _ => $"Alpha Blend ({rawMode})"
            };
        }
    }
}
