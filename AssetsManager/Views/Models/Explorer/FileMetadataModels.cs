using System.Collections.Generic;

namespace AssetsManager.Views.Models.Explorer
{
    public class NarrativeMetadata
    {
        public string Title { get; set; }
        public string Description { get; set; }
    }

    public class SummonerIconJsonEntry
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public int YearReleased { get; set; }
        public bool IsLegacy { get; set; }
        public string ImagePath { get; set; }
        public List<DescriptionEntry> Descriptions { get; set; }
    }

    public class EmoteJsonEntry
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string InventoryIcon { get; set; }
    }

    public class WardJsonEntry
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<WardRegionalDescription> RegionalDescriptions { get; set; }
    }

    public class WardRegionalDescription
    {
        public string Region { get; set; }
        public string Description { get; set; }
    }

    public class LootJsonEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Image { get; set; }
        public string Rarity { get; set; }
        public string Type { get; set; }
    }

    public class DescriptionEntry
    {
        public string Region { get; set; }
        public string Description { get; set; }
    }
}
