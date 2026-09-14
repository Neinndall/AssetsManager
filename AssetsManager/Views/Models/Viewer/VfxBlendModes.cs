namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>Semantic rendering families used by authored League particle blend modes.</summary>
    public enum VfxBlendModeKind
    {
        Alpha,
        Multiply,
        Additive,
        Opaque
    }

    public enum VfxBlendFactor
    {
        Zero,
        One,
        SourceAlpha,
        OneMinusSourceAlpha,
        DestinationColor,
        OneMinusSourceColor,
        DestinationAlpha,
        OneMinusDestinationAlpha
    }

    public enum VfxBlendEquationKind
    {
        Add,
        Min,
        Max
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
            // 0: ADD -> One, One, Add (Pure additive)
            new(0, "Add", VfxBlendModeKind.Additive, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendEquationKind.Add, VfxBlendEquationKind.Add, AllowsAlphaTest: true, AllowsDepthWrite: false, NeutralizeTransparentRgb: false),

            // 1: ALPHA -> SrcAlpha, OneMinusSrcAlpha, Add
            new(1, "Alpha Blend", VfxBlendModeKind.Alpha, VfxBlendFactor.SourceAlpha, VfxBlendFactor.OneMinusSourceAlpha, VfxBlendFactor.SourceAlpha, VfxBlendFactor.OneMinusSourceAlpha, VfxBlendEquationKind.Add, VfxBlendEquationKind.Add, AllowsAlphaTest: true, AllowsDepthWrite: false, NeutralizeTransparentRgb: false),

            // 2: SUBTRACT -> Zero, OneMinusSrcColor (Color), Zero, OneMinusSrcAlpha (Alpha) (Dest darken)
            new(2, "Subtract", VfxBlendModeKind.Multiply, VfxBlendFactor.Zero, VfxBlendFactor.OneMinusSourceColor, VfxBlendFactor.Zero, VfxBlendFactor.OneMinusSourceAlpha, VfxBlendEquationKind.Add, VfxBlendEquationKind.Add, AllowsAlphaTest: true, AllowsDepthWrite: false, NeutralizeTransparentRgb: true),

            // 3: NONE -> Opaque (depthWrite: true)
            new(3, "None (Opaque)", VfxBlendModeKind.Opaque, VfxBlendFactor.One, VfxBlendFactor.Zero, VfxBlendFactor.One, VfxBlendFactor.Zero, VfxBlendEquationKind.Add, VfxBlendEquationKind.Add, AllowsAlphaTest: true, AllowsDepthWrite: true, NeutralizeTransparentRgb: false),

            // 4: ALPHA_ADD -> SrcAlpha, One, Add (Alpha-modulated additive)
            new(4, "Alpha Add", VfxBlendModeKind.Additive, VfxBlendFactor.SourceAlpha, VfxBlendFactor.One, VfxBlendFactor.SourceAlpha, VfxBlendFactor.One, VfxBlendEquationKind.Add, VfxBlendEquationKind.Add, AllowsAlphaTest: true, AllowsDepthWrite: false, NeutralizeTransparentRgb: false),

            // 5: PREMULTIPLIED_ALPHA -> One, OneMinusSrcAlpha, Add
            new(5, "Premultiplied Alpha", VfxBlendModeKind.Alpha, VfxBlendFactor.One, VfxBlendFactor.OneMinusSourceAlpha, VfxBlendFactor.One, VfxBlendFactor.OneMinusSourceAlpha, VfxBlendEquationKind.Add, VfxBlendEquationKind.Add, AllowsAlphaTest: true, AllowsDepthWrite: false, NeutralizeTransparentRgb: false),

            // 6: MIN -> One, One, Min
            new(6, "Min", VfxBlendModeKind.Additive, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendEquationKind.Min, VfxBlendEquationKind.Min, AllowsAlphaTest: true, AllowsDepthWrite: false, NeutralizeTransparentRgb: false),

            // 7: MAX -> One, One, Max
            new(7, "Max", VfxBlendModeKind.Additive, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendEquationKind.Max, VfxBlendEquationKind.Max, AllowsAlphaTest: true, AllowsDepthWrite: false, NeutralizeTransparentRgb: false),

            // 8: TARGET_ALPHA -> OneMinusDstAlpha, DstAlpha
            new(8, "Target Alpha", VfxBlendModeKind.Alpha, VfxBlendFactor.OneMinusDestinationAlpha, VfxBlendFactor.DestinationAlpha, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendEquationKind.Add, VfxBlendEquationKind.Add, AllowsAlphaTest: true, AllowsDepthWrite: false, NeutralizeTransparentRgb: false)
        };

        private static readonly VfxBlendModeDescriptor SafeAlphaFallback = new(
            -1,
            "Safe Alpha Fallback",
            VfxBlendModeKind.Alpha,
            VfxBlendFactor.SourceAlpha,
            VfxBlendFactor.OneMinusSourceAlpha,
            VfxBlendFactor.One,
            VfxBlendFactor.OneMinusSourceAlpha,
            VfxBlendEquationKind.Add,
            VfxBlendEquationKind.Add,
            AllowsAlphaTest: true,
            AllowsDepthWrite: false,
            NeutralizeTransparentRgb: false);

        public static bool IsKnown(int rawMode) => rawMode >= 0 && rawMode < AuthoredModes.Length;

        public static VfxBlendModeDescriptor GetDescriptor(int rawMode)
            => IsKnown(rawMode) ? AuthoredModes[rawMode] : SafeAlphaFallback;

        public static VfxBlendModeDescriptor GetDrawDescriptor(int rawMode, bool distortion)
            => GetDescriptor(distortion ? 1 : rawMode);

        public static bool ShouldTestDepth(int miscRenderFlags) => (miscRenderFlags & 1) == 0;

        public static bool ShouldSortBackToFront(int rawMode) => rawMode is 1 or 2 or 5 or 8;

        public static bool IsAdditive(int rawMode) => GetDescriptor(rawMode).Kind == VfxBlendModeKind.Additive;

        public static bool IsMultiply(int rawMode) => GetDescriptor(rawMode).Kind == VfxBlendModeKind.Multiply;

        public static float ResolveEmissiveStrength(int rawMode) => 1f;

        public static bool ShouldAlphaTest(int rawMode, int alphaReference)
            => GetDescriptor(rawMode).AllowsAlphaTest && alphaReference > 0;

        public static bool ShouldWriteDepth(int rawMode, int alphaReference)
            => GetDescriptor(rawMode).AllowsDepthWrite;

        public static int ResolveColorRenderFlags(int rawFlags, bool hasParticleColorTexture)
            => hasParticleColorTexture ? rawFlags | 1 : rawFlags;

        public static VfxBlendModeKind Resolve(int rawMode)
            => GetDescriptor(rawMode).Kind;

        public static string Describe(int rawMode)
        {
            if (!IsKnown(rawMode))
                return $"Unknown ({rawMode}, safe alpha fallback)";

            return $"{GetDescriptor(rawMode).Name} ({rawMode})";
        }
    }
}
