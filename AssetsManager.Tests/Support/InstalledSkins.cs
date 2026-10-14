using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Tests.Support
{
    /// <summary>Champion skins of the installed client, loaded the way the viewer loads them.</summary>
    internal static class InstalledSkins
    {
        /// <returns>The first League install (PBE, then live) with a DX11 shader cache, or null.</returns>
        internal static string FindInstall() =>
            new[]
                {
                    @"C:\Riot Games\League of Legends (PBE)",
                    @"C:\Riot Games\League of Legends"
                }
                .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client")));

        internal static AppSettings Settings(string install)
        {
            var settings = AppSettings.GetDefaultSettings();
            settings.PreferredClient = PreferredClient.PBE;
            settings.LolPbeDirectory = install;
            settings.LolLiveDirectory = null;
            return settings;
        }

        internal static MapCharacterLoadingService CreateLoader(AppSettings settings, LogService log)
        {
            var wadProvider = new WadContentProvider(
                log,
                new WadNodeLoaderService(null, log),
                new DirectoriesCreator(),
                new SvgParser());
            return new MapCharacterLoadingService(
                new MapAssetResolver(wadProvider, settings),
                new MapCharacterSkinParser(),
                new MapCharacterMeshDecoder(),
                null,
                null);
        }

        /// <summary>
        /// The skins named on the command line, or with `--all` every champion WAD of the install (localized
        /// WADs excluded) with its skin BINs from the game hash list, capped by `--max-skins N` per champion.
        /// </summary>
        internal static string[] FromArguments(string install, string[] args) =>
            args.Contains("--all", StringComparer.OrdinalIgnoreCase)
                ? All(install, args).ToArray()
                : args.Where(arg => arg.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)).ToArray();

        private static IEnumerable<string> All(string install, string[] args)
        {
            int at = Array.IndexOf(args, "--max-skins");
            int maxSkins = at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int parsed) ? parsed : int.MaxValue;
            string hashes = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetsManager", "hashes", "hashes.game.txt");

            string[] champions = Directory.GetFiles(Path.Combine(install, @"Game\DATA\FINAL\Champions"), "*.wad.client")
                .Select(path => Path.GetFileName(path)[..^".wad.client".Length])
                .Where(name => !name.Contains('.') && !name.StartsWith("TFT", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // The hash list also names skins the installed client does not ship; each id keeps its BIN's chunk hash.
            var byChampion = champions.ToDictionary(name => name.ToLowerInvariant(), _ => new SortedDictionary<int, ulong>());
            var pattern = new System.Text.RegularExpressions.Regex(@"^data/characters/([a-z0-9_]+)/skins/skin(\d+)\.bin$");
            foreach (string line in File.ReadLines(hashes))
            {
                int space = line.IndexOf(' ');
                if (space < 0)
                    continue;
                var match = pattern.Match(line[(space + 1)..]);
                if (match.Success && byChampion.TryGetValue(match.Groups[1].Value, out SortedDictionary<int, ulong> ids) &&
                    ulong.TryParse(line.AsSpan(0, space), System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                    ids[int.Parse(match.Groups[2].Value)] = hash;
            }

            foreach (string champion in champions)
            {
                using var wad = new LeagueToolkit.Core.Wad.WadFile(Path.Combine(install, @"Game\DATA\FINAL\Champions", champion + ".wad.client"));
                int[] shipped = byChampion[champion.ToLowerInvariant()]
                    .Where(pair => wad.Chunks.ContainsKey(pair.Value))
                    .Select(pair => pair.Key)
                    .Take(maxSkins)
                    .ToArray();
                foreach (int id in shipped)
                    yield return $"Characters/{champion}/Skins/Skin{id}";
            }
        }
    }
}
