using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>Typed reads of BIN properties shared by the viewer's material parsers.</summary>
    internal static class BinPropertyReader
    {
        /// <summary>A bool or bit-bool property's value, else <paramref name="fallback"/>.</summary>
        internal static bool ReadBool(BinTreeProperty property, bool fallback) =>
            property switch
            {
                BinTreeBool value => value.Value,
                BinTreeBitBool value => value.Value,
                _ => fallback
            };
    }
}
