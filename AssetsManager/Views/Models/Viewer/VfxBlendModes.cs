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

        public static VfxBlendModeKind Resolve(int rawMode)
            => rawMode switch
            {
                0 or 1 or 4 or 5 => VfxBlendModeKind.Additive,
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
