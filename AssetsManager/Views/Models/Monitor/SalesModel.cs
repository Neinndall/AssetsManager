using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Monitor
{
    public class SalesCatalog
    {
        [JsonPropertyName("catalog")]
        public List<CatalogItem> Catalog { get; set; }
    }

    public class CatalogItem
    {
        [JsonPropertyName("inventoryType")]
        public string InventoryType { get; set; }

        [JsonPropertyName("rp")]
        public int? Rp { get; set; } // This is the original price RP

        public string FormattedOriginalPrice => Rp.HasValue ? $"Original Price: {Rp.Value} RP" : "Original Price: N/A";

        [JsonPropertyName("ip")]
        public int? Ip { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("sale")]
        public SaleInfo Sale { get; set; }
    }

    public class SaleInfo
    {
        [JsonPropertyName("rp")]
        public int Rp { get; set; }

        [JsonPropertyName("percentOff")]
        public int PercentOff { get; set; }

        [JsonPropertyName("endDate")]
        public DateTime EndDateValue { get; set; }

        public string FormattedEndDate => FormatUtils.FormatTimeRemaining(EndDateValue);
        public string FormattedSaleDetails => $"Sale: {Rp} RP ({PercentOff}% dto)";
    }
}
