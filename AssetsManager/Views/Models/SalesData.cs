using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AssetsManager.Views.Models
{
    public class SalesCatalog
    {
        [JsonPropertyName("player")]
        public PlayerInfo Player { get; set; }

        [JsonPropertyName("catalog")]
        public List<CatalogItem> Catalog { get; set; }
    }

    public class PlayerInfo
    {
        [JsonPropertyName("accountId")]
        public long AccountId { get; set; }

        [JsonPropertyName("rp")]
        public int Rp { get; set; }

        [JsonPropertyName("ip")]
        public int Ip { get; set; }

        [JsonPropertyName("summonerLevel")]
        public int SummonerLevel { get; set; }
    }

    public class CatalogItem
    {
        [JsonPropertyName("itemId")]
        public int ItemId { get; set; }

        [JsonPropertyName("inventoryType")]
        public string InventoryType { get; set; }

        [JsonPropertyName("iconUrl")]
        public string IconUrl { get; set; }

        [JsonPropertyName("ownedQuantity")]
        public int OwnedQuantity { get; set; }

        [JsonPropertyName("maxQuantity")]
        public int MaxQuantity { get; set; }

        [JsonPropertyName("rp")]
        public int? Rp { get; set; }

        [JsonPropertyName("ip")]
        public int? Ip { get; set; }

        [JsonPropertyName("releaseDate")]
        public long ReleaseDate { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; }

        [JsonPropertyName("purchaseLimitReached")]
        public bool PurchaseLimitReached { get; set; }

        [JsonPropertyName("owned")]
        public bool? Owned { get; set; }

        [JsonPropertyName("sale")]
        public SaleInfo Sale { get; set; }

        public string OriginalPriceText
        {
            get
            {
                if (Rp.HasValue)
                {
                    return $"Original Price: {Rp.Value} RP";
                }
                return string.Empty;
            }
        }

        public string SalePriceText
        {
            get
            {
                if (Sale != null)
                {
                    return $"Sale: {Sale.Rp} RP ({Sale.PercentOff}% off)";
                }
                return string.Empty;
            }
        }
    }

    public class SaleInfo
    {
        [JsonPropertyName("rp")]
        public int Rp { get; set; }

        [JsonPropertyName("percentOff")]
        public int PercentOff { get; set; }

        [JsonPropertyName("endDate")]
        public string EndDate { get; set; }

        public string FormattedEndDate
        {
            get
            {
                return $"Ends: {EndDate}";
            }
        }
    }
}
