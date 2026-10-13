using System;
using System.IO;
using System.Text.RegularExpressions;

namespace AssetsManager.Services.Monitor;

public static class AssetCatalogRouting
{
    public const string CdnHost = "d392eissrffsyf.cloudfront.net";

    public static string GetInventoryType(string categoryId) => categoryId switch
    {
        "1" or "2" or "3" or "4" or "10" => "BUNDLES",
        "5" => "EVENT_PASS", "6" => "CURRENCY", "7" => "HEXTECH_CRAFTING",
        "8" => "REGALIA_BANNER", "9" => "SKIN_AUGMENT", _ => null
    };

    public static string NormalizeCdnUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.Host.Equals(CdnHost, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/storeImages/", StringComparison.Ordinal)) return null;
        return uri.AbsoluteUri;
    }

    public static long? GetImageNumber(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        string file = Path.GetFileName(uri.AbsolutePath);
        var match = Regex.Match(file, @"^(?:regalia_banner_)?(\d+)(?:_[^.]+)?\.[^.]+$", RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups[1].Value, out long id) ? id : null;
    }

    public static string GetArticleCategory(string inventoryType, long itemId, string subInventoryType = null)
    {
        if (inventoryType == "BUNDLES")
        {
            string id = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (id.StartsWith("333", StringComparison.Ordinal)) return null;
            if (id.StartsWith("799", StringComparison.Ordinal)) return "1";
            if (id.StartsWith("999", StringComparison.Ordinal)) return "2";
            if (id.StartsWith("899", StringComparison.Ordinal)) return "3";
            if (id.StartsWith("699", StringComparison.Ordinal)) return "4";
            if (id.StartsWith("910", StringComparison.Ordinal)) return "10";
            return subInventoryType switch
            {
                "CHROMA_BUNDLE" => "1",
                "BORDER_SET_BUNDLE" => "2",
                "SKIN_BUNDLE" => "3",
                "HEXTECH_BUNDLE" => "4",
                "SKIN_VARIANT_BUNDLE" => "10",
                _ => null
            };
        }
        return inventoryType switch
        {
            "EVENT_PASS" when !itemId.ToString().StartsWith("333", StringComparison.Ordinal) => "5",
            "CURRENCY" => "6",
            "HEXTECH_CRAFTING" => "7",
            "REGALIA_BANNER" => "8",
            "SKIN_AUGMENT" => "9",
            _ => null
        };
    }

    public static string GetImageCategory(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        string path = uri.AbsolutePath;
        if (path.StartsWith("/storeImages/bundles/", StringComparison.Ordinal))
            return GetImageNumber(url) is long id ? GetArticleCategory("BUNDLES", id) : null;
        if (path.StartsWith("/storeImages/eventpass/", StringComparison.Ordinal)) return "5";
        if (path.StartsWith("/storeImages/storeCards/Currency/", StringComparison.Ordinal)) return "6";
        if (path.StartsWith("/storeImages/hextechCrafting/", StringComparison.Ordinal)) return "7";
        if (path.StartsWith("/storeImages/regaliaBanner/", StringComparison.Ordinal)) return "8";
        if (path.StartsWith("/storeImages/skinaugment/", StringComparison.Ordinal)) return "9";
        return null;
    }

}
