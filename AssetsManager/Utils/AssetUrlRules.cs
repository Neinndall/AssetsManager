using System;
using System.Collections.Generic;
using System.IO;

namespace AssetsManager.Utils
{
    public static class AssetUrlRules
    {
        private static readonly HashSet<string> _excludedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".luabin", ".luabin64", ".preload", ".scb",
            ".sco", ".skn", ".skl", ".mapgeo", ".stringtable",
            ".anm", ".dat", ".bnk", ".wpk",
            ".cfg", ".cfgbin", ".subchunktoc"
        };

        public static string Adjust(string url)
        {
            // Primero, comprobar extensiones excluidas
            string extension = Path.GetExtension(url);
            if (!string.IsNullOrEmpty(extension) && _excludedExtensions.Contains(extension))
                return null;

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

            // Gestionar ficheros .bin: ignorar los complejos y transformar los demás.
            if (url.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                // Primero, ignorar los que tienen nombres complejos que agrupan skins.
                string fileName = Path.GetFileName(url);
                int separatorCount = (fileName.Length - fileName.Replace("_skins_", "", StringComparison.OrdinalIgnoreCase).Length) / "_skins_".Length;

                if (separatorCount > 1)
                {
                    return null; // Ignorar el .bin complejo
                }

                // Para todos los demás ficheros .bin, se les añade la extensión .json para su descarga.
                url += ".json";
            }
            return url;
        }
    }
}