using System;
using System.Collections.Generic;

namespace AssetsManager.Views.Models.Library
{
    /// <summary>
    /// Represents a single asset indexed from any WAD file in the game.
    /// </summary>
    public class LibraryAsset
    {
        public string Path { get; set; }
        public ulong PathHash { get; set; }
        public ulong Checksum { get; set; }
        public string WadSource { get; set; }
        public string Extension { get; set; }
        public long Size { get; set; }

        // UI Helpers
        public string FileName => System.IO.Path.GetFileName(Path);
        
        /// <summary>
        /// Logic-based category (e.g., "Champions", "UI", "Audio")
        /// </summary>
        public string Category { get; set; }
    }

    /// <summary>
    /// The persistent database of all indexed assets.
    /// </summary>
    public class LibraryIndex
    {
        public DateTime LastScan { get; set; }
        public string GameVersion { get; set; }
        
        /// <summary>
        /// Flat list of all assets discovered across all WADs.
        /// </summary>
        public List<LibraryAsset> Assets { get; set; } = new List<LibraryAsset>();
    }
}
