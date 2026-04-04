using System;

namespace AssetsManager.Services.Explorer
{
    /// <summary>
    /// Defines how the assets should be exported to the disk.
    /// </summary>
    public enum WadExportMode
    {
        /// <summary>
        /// Preserves the original format (raw bytes from the WAD/Chunk).
        /// </summary>
        Original,

        /// <summary>
        /// Performs smart conversions (e.g., textures to PNG, audio to MP3/OGG).
        /// </summary>
        Smart
    }
}
