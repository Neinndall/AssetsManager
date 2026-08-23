using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor
{
    public static class MythicShopParser
    {
        private const string MythicShopfrontId = "MYTHIC_SHOP";
        private const string MythicEssencePaymentName = "lol_mythic_essence";
        private const string MythicEssenceCurrencyId = "8ce03930-7079-5e3f-a49b-d4721b00dbb3";
        private static readonly string[] CategoryOrder = { "FEATURED", "BIWEEKLY", "WEEKLY", "DAILY" };

        public static List<MythicShopCategory> Parse(MythicShopResponse response)
        {
            var categories = CategoryOrder.ToDictionary(
                category => category,
                category => new MythicShopCategory { CategoryName = category },
                StringComparer.OrdinalIgnoreCase);

            foreach (var section in response?.Data ?? Enumerable.Empty<MythicShopData>())
            {
                var shoppefront = section?.DisplayMetadata?.Shoppefront;
                if (!string.Equals(shoppefront?.Id, MythicShopfrontId, StringComparison.OrdinalIgnoreCase))
                    continue;

                string categoryKey = shoppefront.Categories?.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate) && categories.ContainsKey(candidate));
                if (string.IsNullOrWhiteSpace(categoryKey) || !categories.TryGetValue(categoryKey, out var category))
                    continue;

                foreach (var entry in section.CatalogEntries ?? Enumerable.Empty<MythicShopCatalogEntry>())
                {
                    if (!TryGetMythicPurchase(entry, out var purchaseUnit, out var payment))
                        continue;

                    category.Items.Add(new MythicShopModel
                    {
                        Name = purchaseUnit.Fulfillment.Name,
                        Price = payment.FinalDelta ?? payment.Delta,
                        EndTime = FormatUtils.FormatTimeRemaining(entry.EndTime)
                    });
                }
            }

            return CategoryOrder
                .Select(category => categories[category])
                .Where(category => category.Items.Count > 0)
                .ToList();
        }

        private static bool TryGetMythicPurchase(
            MythicShopCatalogEntry entry,
            out MythicShopPurchaseUnit purchaseUnit,
            out MythicShopPayment payment)
        {
            purchaseUnit = null;
            payment = null;

            foreach (var unit in entry?.PurchaseUnits ?? Enumerable.Empty<MythicShopPurchaseUnit>())
            {
                if (string.IsNullOrWhiteSpace(unit?.Fulfillment?.Name))
                    continue;

                foreach (var option in unit.PaymentOptions ?? Enumerable.Empty<MythicShopPaymentOption>())
                {
                    payment = option?.Payments?.FirstOrDefault(IsMythicEssencePayment);
                    if (payment == null)
                        continue;

                    purchaseUnit = unit;
                    return true;
                }
            }

            return false;
        }

        private static bool IsMythicEssencePayment(MythicShopPayment payment) =>
            string.Equals(payment?.Name, MythicEssencePaymentName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(payment?.CurrencyId, MythicEssenceCurrencyId, StringComparison.OrdinalIgnoreCase);
    }
}
