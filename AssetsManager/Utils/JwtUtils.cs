using AssetsManager.Views.Models;
using System;
using System.Text;
using System.Text.Json;

namespace AssetsManager.Utils
{
    public static class JwtUtils
    {
        public static TokenInfo ParsePayload(string token)
        {
            var payload = token.Split('.')[1];
            var padding = 4 - payload.Length % 4;
            if (padding < 4)
            {
                payload += new string('=', padding);
            }
            var bytes = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'));
            var jsonPayload = Encoding.UTF8.GetString(bytes);

            using var jsonDoc = JsonDocument.Parse(jsonPayload);
            var root = jsonDoc.RootElement;
            var info = new TokenInfo();

            // Expiration & Issued At
            if (root.TryGetProperty("exp", out var expElement) && expElement.TryGetInt64(out var expValue))
                info.Expiration = DateTimeOffset.FromUnixTimeSeconds(expValue).UtcDateTime;
            if (root.TryGetProperty("iat", out var iatElement) && iatElement.TryGetInt64(out var iatValue))
                info.IssuedAt = DateTimeOffset.FromUnixTimeSeconds(iatValue).UtcDateTime;

            // PUUID
            if (root.TryGetProperty("sub", out var subElement))
                info.Puuid = subElement.GetString();

            // Platform
            if (root.TryGetProperty("plt", out var pltElement) && pltElement.TryGetProperty("id", out var idElement))
                info.Platform = idElement.GetString();
            else if (root.TryGetProperty("lol.pvpnet.platform", out var pvpPltElement))
                info.Platform = pvpPltElement.GetString();

            // Region (with multiple fallbacks)
            if (root.TryGetProperty("lol.pvpnet.region", out var pvpRegElement))
                info.Region = pvpRegElement.GetString();
            else if (root.TryGetProperty("dat", out var datRegElement) && datRegElement.TryGetProperty("r", out var rElement))
                info.Region = rElement.GetString();
            else if (root.TryGetProperty("reg", out var regElement))
                info.Region = regElement.GetString();

            // Summoner ID (with fallback)
            if (root.TryGetProperty("lol.pvpnet.summoner.id", out var pvpIdElement) && pvpIdElement.TryGetInt64(out var pvpId))
                info.SummonerId = pvpId;
            else if (root.TryGetProperty("dat", out var datIdElement) && datIdElement.TryGetProperty("u", out var uElement) && uElement.TryGetInt64(out var uId))
                info.SummonerId = uId;

            return info;
        }
    }
}
