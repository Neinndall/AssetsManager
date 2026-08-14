namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Authored defaults from League's VfxEmitterDefinitionData schema. BIN omits
    /// fields whose value equals these defaults, so parsing must not invent preview values.
    /// </summary>
    public static class VfxAuthoredDefaults
    {
        public const int BlendMode = 0;
        public const byte AlphaReference = 5;
        public const byte ColorLookUpTypeX = 1;
        public const byte ColorLookUpTypeY = 0;
        public const byte MeshRenderFlags = 1;
        public const byte Importance = 1;
    }
}
