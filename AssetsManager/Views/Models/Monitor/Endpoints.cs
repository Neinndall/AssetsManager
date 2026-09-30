using System;
using System.Collections.Generic;

namespace AssetsManager.Views.Models.Monitor
{
    public static class Endpoints
    {
        public static string LeagueEdgeUrlTemplate => "https://{region}-red.lol.sgp.pvp.net";
        public static string PlayerPlatformUrlTemplate => "https://{region}.pp.sgp.pvp.net";

        public static string GetRemoteBaseUrl(string endpointKey, string region, string playerPlatformUrl = null)
        {
            if (endpointKey != "progression")
                return LeagueEdgeUrlTemplate.Replace("{region}", region);

            const string playerPlatformSuffix = ".pp.sgp.pvp.net";
            if (!Uri.TryCreate(playerPlatformUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.EndsWith(playerPlatformSuffix, StringComparison.OrdinalIgnoreCase)
                || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return null;

            // Player Platform uses the cluster advertised by the selected client, not its League region.
            string playerPlatformRegion = uri.Host[..^playerPlatformSuffix.Length];
            return PlayerPlatformUrlTemplate.Replace("{region}", playerPlatformRegion);
        }

        public static Dictionary<string, string> GetLocalEndpoints() => new Dictionary<string, string>
        {
            { "entitlementsToken", "/entitlements/v1/token" },
            { "leagueSessionToken", "/lol-league-session/v1/league-session-token" },
            { "playerPlatformUrl", "/client-config/v2/config/lol.client_settings.player_platform_edge.url" }
        };

        public static Dictionary<string, string> GetRemoteEndpoints() => new Dictionary<string, string>
        {
            { "sales", "/storefront/v3/view/skins" },
            { "mythic_shop", "/catalog/v1/products/d1c2664a-5938-4c41-8d1b-61fd51052c22/stores" },
            { "progression", "/services/cap/progression/progression-api/v1/products/d1c2664a-5938-4c41-8d1b-61fd51052c22/groups/{events_id}" },
            { "rewards", "/services/rewards/public-api/v2/products/d1c2664a-5938-4c41-8d1b-61fd51052c22/groups?locale={locales}" }
        };
    }
}
