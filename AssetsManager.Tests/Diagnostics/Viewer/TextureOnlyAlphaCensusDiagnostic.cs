using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `texture-only-alpha-census <out.csv> [--champions] [--maps] [--max-skins N] [--shard i/n] [skin-path...]`: every submesh drawn
    /// from a texture alone (the game's LIT_UBER, which discards alpha 0 and blends the rest) with the share of its
    /// surface at alpha 0, below one half, below 0.9 and opaque. Each triangle is sampled at its centroid and corners.
    /// `--maps` loads every character whose skin BIN ships in a map or Common WAD.
    /// </summary>
    internal static class TextureOnlyAlphaCensusDiagnostic
    {
        public static async Task Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: texture-only-alpha-census <out.csv> [--champions] [--maps] [--max-skins N] [skin-path...]");
                return;
            }

            string install = InstalledSkins.FindInstall();
            AppSettings settings = InstalledSkins.Settings(install);
            var loader = InstalledSkins.CreateLoader(settings, new LogService(new Serilog.LoggerConfiguration().CreateLogger()));
            string projectRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "am-texture-only-alpha")).FullName;

            var skins = new List<(string Skin, string Source)>();
            skins.AddRange(args.Where(arg => arg.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)).Select(skin => (skin, "argument")));
            if (args.Contains("--champions"))
                skins.AddRange(InstalledSkins.FromArguments(install, new[] { "--all" }.Concat(args).ToArray()).Select(skin => (skin, "champion")));
            if (args.Contains("--maps"))
                skins.AddRange(MapCharacterSkins(install).Select(skin => (skin, "map")));
            // --shard i/n keeps every n-th skin from i, so several processes split one census.
            int shardAt = Array.IndexOf(args, "--shard");
            if (shardAt >= 0 && shardAt + 1 < args.Length && args[shardAt + 1].Split('/') is [var index, var count])
            {
                int shard = int.Parse(index, CultureInfo.InvariantCulture), shards = int.Parse(count, CultureInfo.InvariantCulture);
                skins = skins.Where((_, at) => at % shards == shard).ToList();
            }
            Console.WriteLine($"[TextureOnlyAlpha] skins={skins.Count}");

            // Rows are appended as each skin is measured, so a stopped census keeps what it read.
            using var csv = new StreamWriter(args[0], append: false) { AutoFlush = true };
            csv.WriteLine("source,skin,submesh,hidden,triangles,zero,low,mid,opaque,texture");
            int loaded = 0, failed = 0, textureOnly = 0;
            foreach ((string skin, string source) in skins)
            {
                MapCharacterAssetData asset;
                try
                {
                    asset = await loader.LoadAsync(skin, projectRoot, CancellationToken.None);
                }
                catch
                {
                    failed++;
                    continue;
                }
                if (asset?.Mesh == null || asset.Materials == null)
                {
                    failed++;
                    continue;
                }

                loaded++;
                var hidden = asset.Materials.InitialHiddenSubmeshes?.ToHashSet(StringComparer.OrdinalIgnoreCase) ??
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (MapCharacterMeshRange range in asset.Mesh.Ranges)
                {
                    ModelMaterialDefinition material = asset.Materials.ResolveMaterialDefinition(range.Name);
                    if (material?.BindingKind != ModelMaterialBindingKind.TextureOnly ||
                        string.IsNullOrWhiteSpace(material.BaseTextureName) ||
                        asset.Textures == null ||
                        !asset.Textures.TryGetValue(material.BaseTextureName, out BitmapSource texture))
                        continue;

                    textureOnly++;
                    int[] bands = Sample(asset.Mesh, range, texture);
                    csv.WriteLine(string.Join(",",
                        source, skin, Quote(range.Name), hidden.Contains(range.Name), range.IndexCount / 3,
                        bands[0], bands[1], bands[2], bands[3], material.BaseTextureName));
                }
            }

            Console.WriteLine($"[TextureOnlyAlpha] loaded={loaded} failed={failed} textureOnlySubmeshes={textureOnly} csv={args[0]}");
        }

        /// <returns>Samples at alpha 0, below 128, below 230 and from 230 up.</returns>
        private static int[] Sample(MapCharacterMeshData mesh, MapCharacterMeshRange range, BitmapSource texture)
        {
            BitmapSource bgra = texture.Format == PixelFormats.Bgra32
                ? texture
                : new FormatConvertedBitmap(texture, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            byte[] pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);

            var bands = new int[4];
            void Add(Vector2 uv)
            {
                int x = (int)Math.Floor((uv.X - Math.Floor(uv.X)) * width) % width;
                int y = (int)Math.Floor((uv.Y - Math.Floor(uv.Y)) * height) % height;
                byte alpha = pixels[(y * width + x) * 4 + 3];
                bands[alpha == 0 ? 0 : alpha < 128 ? 1 : alpha < 230 ? 2 : 3]++;
            }

            for (int at = range.StartIndex; at + 2 < range.StartIndex + range.IndexCount; at += 3)
            {
                Vector2 a = mesh.Uv[mesh.Indices[at]], b = mesh.Uv[mesh.Indices[at + 1]], c = mesh.Uv[mesh.Indices[at + 2]];
                Vector2 centre = (a + b + c) / 3f;
                Add(centre);
                // Corners pulled a little inward so a triangle's own texels are read rather than its neighbour's.
                Add(Vector2.Lerp(a, centre, 0.1f));
                Add(Vector2.Lerp(b, centre, 0.1f));
                Add(Vector2.Lerp(c, centre, 0.1f));
            }
            return bands;
        }

        /// <summary>Every character skin whose BIN ships in a map or Common WAD, from the game hash list.</summary>
        private static IEnumerable<string> MapCharacterSkins(string install)
        {
            string hashes = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetsManager", "hashes", "hashes.game.txt");
            var pattern = new System.Text.RegularExpressions.Regex(@"^data/characters/([a-z0-9_]+)/skins/skin(\d+)\.bin$");
            var byHash = new Dictionary<ulong, (string Name, int Id)>();
            foreach (string line in File.ReadLines(hashes))
            {
                int space = line.IndexOf(' ');
                if (space < 0) continue;
                var match = pattern.Match(line[(space + 1)..]);
                if (match.Success && ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    byHash[hash] = (match.Groups[1].Value, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
            }

            string final = Path.Combine(install, @"Game\DATA\FINAL");
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => path.Contains(@"\Maps\") || Path.GetFileName(path).StartsWith("Common", StringComparison.OrdinalIgnoreCase))
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys)
                {
                    if (byHash.TryGetValue(chunk, out var skin))
                        found.Add($"Characters/{skin.Name}/Skins/Skin{skin.Id}");
                }
            }
            return found;
        }

        private static string Quote(string value) => value.Contains(',') ? $"\"{value}\"" : value;
    }
}
