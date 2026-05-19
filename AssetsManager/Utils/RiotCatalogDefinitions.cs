using System;
using System.Collections.Generic;

namespace AssetsManager.Utils
{
    /// <summary>
    /// Centralized technical definitions for Riot's data catalogs and asset paths.
    /// This ensures consistency across different services (Monitor/Explorer).
    /// </summary>
    public static class RiotCatalogDefinitions
    {
        // --- JSON Catalog Paths ---
        public const string SkinsJsonPath = "plugins/rcp-be-lol-game-data/global/default/v1/skins.json";
        public const string EmotesJsonPath = "plugins/rcp-be-lol-game-data/global/default/v1/summoner-emotes.json";
        public const string WardsJsonPath = "plugins/rcp-be-lol-game-data/global/default/v1/ward-skins.json";
        public const string IconsJsonPath = "plugins/rcp-be-lol-game-data/global/default/v1/summoner-icons.json";

        // --- Virtual Folder Paths (for Explorer/WAD resolution) ---
        public const string ProfileIconsVirtualPath = "v1/profile-icons/";
        public const string EmotesVirtualPath = "assets/loadouts/summoneremotes/";

        // --- Catalog Metadata Structure ---
        public class CatalogInfo
        {
            public string Path { get; set; }
            public string NameKey { get; set; }
            public string PathKey { get; set; }
        }

        // Static definitions for each catalog
        public static readonly CatalogInfo SkinCatalog = new CatalogInfo { Path = SkinsJsonPath, NameKey = "name", PathKey = "splashPath" };
        public static readonly CatalogInfo EmoteCatalog = new CatalogInfo { Path = EmotesJsonPath, NameKey = "name", PathKey = "inventoryIcon" };
        public static readonly CatalogInfo WardCatalog = new CatalogInfo { Path = WardsJsonPath, NameKey = "name", PathKey = "wardImagePath" };
        public static readonly CatalogInfo IconCatalog = new CatalogInfo { Path = IconsJsonPath, NameKey = "title", PathKey = "imagePath" };

        /// <summary>
        /// Returns all available catalogs for batch processing.
        /// </summary>
        public static IEnumerable<CatalogInfo> GetAllCatalogs()
        {
            yield return SkinCatalog;
            yield return EmoteCatalog;
            yield return WardCatalog;
            yield return IconCatalog;
        }
    }
}
