using System.Collections.Generic;
using System.Text.RegularExpressions;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>Reads shared by skin and map material resolution: pass switches, macros, blend factors and placeholder textures.</summary>
    internal static class MaterialSwitchSemantics
    {
        private static readonly Regex PlaceholderTexture = new(
            @"(?i)shared[\\/]materials[\\/](black|white|grey|gray|flat_normal|default|transparent|blank)|[\\/]blank\.tex$|alpha-mask\.tex$",
            RegexOptions.Compiled);

        internal static bool IsEnabled(IReadOnlyDictionary<string, bool> switches, string name) =>
            switches != null && switches.TryGetValue(name, out bool enabled) && enabled;

        internal static bool IsMacroEnabled(IReadOnlyDictionary<string, string> macros, string name) =>
            macros != null && macros.TryGetValue(name, out string value) && value == "1";

        /// <summary>Engine stand-ins (shared black/white/flat normals, blank and alpha-mask) that carry no authored look.</summary>
        internal static bool IsPlaceholder(string texturePath) =>
            !string.IsNullOrWhiteSpace(texturePath) && PlaceholderTexture.IsMatch(texturePath);

        internal static MapBlendFactor BlendFactorOf(uint? value, MapBlendFactor fallback) =>
            value switch
            {
                0 => MapBlendFactor.Zero,
                1 => MapBlendFactor.One,
                2 => MapBlendFactor.SourceColor,
                3 => MapBlendFactor.OneMinusSourceColor,
                4 => MapBlendFactor.DestinationColor,
                5 => MapBlendFactor.OneMinusDestinationColor,
                6 => MapBlendFactor.SourceAlpha,
                7 => MapBlendFactor.OneMinusSourceAlpha,
                _ => fallback
            };
    }
}
