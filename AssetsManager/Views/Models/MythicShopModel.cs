using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AssetsManager.Views.Models
{
    public class MythicShopResponse
    {
        [JsonPropertyName("data")]
        public List<MythicShopData> Data { get; set; }
    }

    public class MythicShopData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("productId")]
        public string ProductId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("catalogEntries")]
        public List<MythicShopCatalogEntry> CatalogEntries { get; set; }
    }

    public class MythicShopCatalogEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("productId")]
        public string ProductId { get; set; }

        [JsonPropertyName("purchaseUnits")]
        public List<MythicShopPurchaseUnit> PurchaseUnits { get; set; }

        [JsonPropertyName("endTime")]
        public DateTime EndTime { get; set; }
    }

    public class MythicShopPurchaseUnit
    {
        [JsonPropertyName("paymentOptions")]
        public List<MythicShopPaymentOption> PaymentOptions { get; set; }

        [JsonPropertyName("fulfillment")]
        public MythicShopFulfillment Fulfillment { get; set; }
    }

    public class MythicShopPaymentOption
    {
        [JsonPropertyName("payments")]
        public List<MythicShopPayment> Payments { get; set; }
    }

    public class MythicShopPayment
    {
        [JsonPropertyName("delta")]
        public int Delta { get; set; }

        [JsonPropertyName("currencyId")]
        public string CurrencyId { get; set; }
    }

    public class MythicShopFulfillment
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("itemTypeId")]
        public string ItemTypeId { get; set; }

        [JsonPropertyName("itemId")]
        public string ItemId { get; set; }
    }
}
