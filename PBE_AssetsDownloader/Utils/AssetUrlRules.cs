using System;
using System.IO;

namespace PBE_AssetsDownloader.Utils
{
    public static class AssetUrlRules
    {
        public static string Adjust(string url)
        {
            // Ignorar shaders del juego
            if (url.Contains("/shaders/"))
                return null;

            // Ignorar assets .png en /hud/
            if (url.Contains("/hud/") && url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return null;

            // Ignorar companions .png
            if (url.Contains("/loot/companions/") && url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return null;

            // Ignorar _le.dds
            if (url.Contains("_le.") && url.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                return null;

            // Ignorar summonericons .jpg, .tex o .png
            if (url.Contains("/summonericons/") &&
                (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 url.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                 url.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                return null;

            // Si la URL acaba en .tex y contiene /summoneremotes/ y _glow, se descarga como .png
            if (url.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) &&
                url.Contains("/summoneremotes/", StringComparison.OrdinalIgnoreCase) &&
                url.Contains("_glow.", StringComparison.OrdinalIgnoreCase))
            {
                url = Path.ChangeExtension(url, ".png");
            }
            // Si la URL acaba en .tex y contiene /summoneremotes/ sin _glow, se ignora
            else if (url.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) &&
                     url.Contains("/summoneremotes/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Cambiar .dds a .png si corresponde
            if (url.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
                (url.Contains("/loot/companions/") ||
                 url.Contains("2x_") ||
                 url.Contains("4x_") ||
                 url.Contains("/maps/") ||
                 url.Contains("/shared/") ||
                 url.Contains("tx_cm") ||
                 url.Contains("/particles/") ||
                 url.Contains("/clash/") ||
                 url.Contains("/skins/") ||
                 url.Contains("/uiautoatlas/") ||
                 url.Contains("/summonerbanners/") ||
                 url.Contains("/summoneremotes/") ||
                 url.Contains("/hud/") ||
                 url.Contains("/regalia/") ||
                 url.Contains("/levels/") ||
                 url.Contains("/spells/") ||
                 url.Contains("/ux/")))
            {
                url = Path.ChangeExtension(url, ".png");
            }

            // Cambiar summonericons .dds con accessories a .png
            if (url.Contains("/summonericons/") && url.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                if (url.Contains(".accessories_"))
                {
                    url = Path.ChangeExtension(url, ".png");
                }
                else
                {
                    return null;
                }
            }

            // Cambiar .tex a .png
            if (url.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                url = Path.ChangeExtension(url, ".png");
            }

            // Cambiar .atlas a .png
            if (url.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
            {
                url = Path.ChangeExtension(url, ".png");
            }

            // Ignorar bin largos de game/data
            if (url.Contains("/game/data/") && url.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                return null;

            return url;
        }
    }
}