namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>Semantic rendering families used by authored League particle blend modes.</summary>
    public enum VfxBlendModeKind
    {
        Alpha,
        Multiply,
        Additive
    }

    public enum VfxBlendFactor
    {
        Zero,
        One,
        SourceAlpha,
        OneMinusSourceAlpha,
        DestinationColor
    }

    public enum VfxBlendEquationKind
    {
        Add
    }

    /// <summary>Complete backend-neutral render contract for one authored blendMode value.</summary>
    public sealed record VfxBlendModeDescriptor(
        int RawMode,
        string Name,
        VfxBlendModeKind Kind,
        VfxBlendFactor SourceRgb,
        VfxBlendFactor DestinationRgb,
        VfxBlendFactor SourceAlpha,
        VfxBlendFactor DestinationAlpha,
        VfxBlendEquationKind RgbEquation,
        VfxBlendEquationKind AlphaEquation,
        bool AllowsAlphaTest,
        bool AllowsDepthWrite,
        bool NeutralizeTransparentRgb);

    /// <summary>Single source of truth for translating raw BIN blendMode values.</summary>
    public static class VfxBlendModes
    {
        private static readonly VfxBlendModeDescriptor[] AuthoredModes =
        {
            Alpha(0, "Alpha Blend"),
            Alpha(1, "Alpha Blend"),
            Additive(2),
            new(
                3,
                "Multiply",
                VfxBlendModeKind.Multiply,
                VfxBlendFactor.DestinationColor,
                VfxBlendFactor.Zero,
                VfxBlendFactor.One,
                VfxBlendFactor.OneMinusSourceAlpha,
                VfxBlendEquationKind.Add,
                VfxBlendEquationKind.Add,
                AllowsAlphaTest: true,
                AllowsDepthWrite: true,
                NeutralizeTransparentRgb: true),
            Additive(4),
            Alpha(5, "Alpha Blend")
        };

        private static readonly VfxBlendModeDescriptor SafeAlphaFallback = Alpha(-1, "Safe Alpha Fallback");

        public static bool IsKnown(int rawMode) => rawMode >= 0 && rawMode < AuthoredModes.Length;

        public static VfxBlendModeDescriptor GetDescriptor(int rawMode)
            => IsKnown(rawMode) ? AuthoredModes[rawMode] : SafeAlphaFallback;

        public static bool IsAdditive(int rawMode) => GetDescriptor(rawMode).Kind == VfxBlendModeKind.Additive;

        public static bool IsMultiply(int rawMode) => GetDescriptor(rawMode).Kind == VfxBlendModeKind.Multiply;

        // Additive blending already uses the authored source alpha. Do not amplify
        // RGB outside the BIN material, or authored highlights become overexposed.
        public static float ResolveEmissiveStrength(int rawMode) => 1f;

        public static bool ShouldAlphaTest(int rawMode, int alphaReference)
            => GetDescriptor(rawMode).AllowsAlphaTest && alphaReference > 0;

        public static bool ShouldWriteDepth(int rawMode, int alphaReference)
            => GetDescriptor(rawMode).AllowsDepthWrite && alphaReference > 0;

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
            => GetDescriptor(rawMode).Kind;

        public static string Describe(int rawMode)
        {
            if (!IsKnown(rawMode))
                return $"Unknown ({rawMode}, safe alpha fallback)";

            return $"{GetDescriptor(rawMode).Name} ({rawMode})";
        }

        private static VfxBlendModeDescriptor Alpha(int rawMode, string name) => new(
            rawMode,
            name,
            VfxBlendModeKind.Alpha,
            VfxBlendFactor.SourceAlpha,
            VfxBlendFactor.OneMinusSourceAlpha,
            VfxBlendFactor.One,
            VfxBlendFactor.OneMinusSourceAlpha,
            VfxBlendEquationKind.Add,
            VfxBlendEquationKind.Add,
            AllowsAlphaTest: true,
            AllowsDepthWrite: true,
            NeutralizeTransparentRgb: false);

        private static VfxBlendModeDescriptor Additive(int rawMode) => new(
            rawMode,
            "Additive",
            VfxBlendModeKind.Additive,
            VfxBlendFactor.SourceAlpha,
            VfxBlendFactor.One,
            VfxBlendFactor.One,
            VfxBlendFactor.One,
            VfxBlendEquationKind.Add,
            VfxBlendEquationKind.Add,
            AllowsAlphaTest: false,
            AllowsDepthWrite: false,
            NeutralizeTransparentRgb: false);
    }
}
