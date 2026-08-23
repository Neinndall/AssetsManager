using System.Text.Json;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Models.Monitor;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor
{
    public class MythicShopParserTests
    {
        [Fact]
        public void Parse_IgnoresJadeFeaturedSectionsAndUsesMythicShopMetadata()
        {
            const string json = """
            {
              "data": [
                {
                  "name": "JADE_SHOP_FEATURED_TEST",
                  "displayMetadata": {
                    "shoppefront": { "id": "JADE_SHOP", "categories": ["FEATURED"] }
                  },
                  "catalogEntries": [
                    {
                      "endTime": "2030-09-23T06:59:00Z",
                      "purchaseUnits": [
                        {
                          "paymentOptions": [{ "payments": [{ "name": "RP", "delta": 1550 }] }],
                          "fulfillment": { "name": "Classic Pass: Act I" }
                        }
                      ]
                    }
                  ]
                },
                {
                  "name": "MYTHIC_SHOPPE_FEATURED_25_13_EMOTE_V1",
                  "displayMetadata": {
                    "shoppefront": { "id": "MYTHIC_SHOP", "categories": ["FEATURED"] }
                  },
                  "catalogEntries": [
                    {
                      "endTime": "2026-08-26T18:00:00Z",
                      "purchaseUnits": [
                        {
                          "paymentOptions": [{ "payments": [{ "name": "lol_mythic_essence", "delta": 250, "finalDelta": 225 }] }],
                          "fulfillment": { "name": "Together as 1" }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

            var response = JsonSerializer.Deserialize<MythicShopResponse>(json);
            var categories = MythicShopParser.Parse(response);

            var featured = Assert.Single(categories);
            var item = Assert.Single(featured.Items);
            Assert.Equal("FEATURED", featured.CategoryName);
            Assert.Equal("Together as 1", item.Name);
            Assert.Equal(225, item.Price);
        }

        [Fact]
        public void Parse_SelectsMythicEssenceUnitWhenItIsNotFirst()
        {
            var response = new MythicShopResponse
            {
                Data = new()
                {
                    new MythicShopData
                    {
                        DisplayMetadata = new MythicShopDisplayMetadata
                        {
                            Shoppefront = new MythicShoppefrontMetadata
                            {
                                Id = "MYTHIC_SHOP",
                                Categories = new() { "UNKNOWN", "WEEKLY" }
                            }
                        },
                        CatalogEntries = new()
                        {
                            new MythicShopCatalogEntry
                            {
                                PurchaseUnits = new()
                                {
                                    new MythicShopPurchaseUnit
                                    {
                                        Fulfillment = new MythicShopFulfillment { Name = "Wrong RP item" },
                                        PaymentOptions = new()
                                        {
                                            new MythicShopPaymentOption
                                            {
                                                Payments = new() { new MythicShopPayment { Name = "RP", Delta = 1350 } }
                                            }
                                        }
                                    },
                                    new MythicShopPurchaseUnit
                                    {
                                        Fulfillment = new MythicShopFulfillment { Name = "Weekly Mythic Item" },
                                        PaymentOptions = new()
                                        {
                                            new MythicShopPaymentOption
                                            {
                                                Payments = new()
                                                {
                                                    new MythicShopPayment
                                                    {
                                                        CurrencyId = "8ce03930-7079-5e3f-a49b-d4721b00dbb3",
                                                        Delta = 35,
                                                        FinalDelta = 30
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            var item = Assert.Single(Assert.Single(MythicShopParser.Parse(response)).Items);

            Assert.Equal("Weekly Mythic Item", item.Name);
            Assert.Equal(30, item.Price);
        }
    }
}
