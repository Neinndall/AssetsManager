using System.Collections.Generic;

namespace AssetsManager.Views.Models.Explorer
{
    public class SummonerIconMetadata
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
        public List<DescriptionEntry> Descriptions { get; set; }
    }

    public class DescriptionEntry
    {
        public string Region { get; set; }
        public string Description { get; set; }
    }
}
