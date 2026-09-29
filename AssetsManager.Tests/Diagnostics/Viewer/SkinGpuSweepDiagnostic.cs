using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `skin-gpu-sweep <skin-path>... | --all [--max-skins N] [--snapshots DIR] [--csv FILE]`: draws every
    /// submesh of each skin with its game program and with the default program over its base texture (the
    /// shaders-off look), and flags the submeshes whose game output is discarded, non-finite, far darker or
    /// brighter than that reference, fails to bind, or misses a texture.
    /// </summary>
    internal static class SkinGpuSweepDiagnostic
    {
        private sealed record Row(
            string Skin,
            string Submesh,
            string Shader,
            bool Hidden,
            SkinSubmeshRenderer.Result Game,
            SkinSubmeshRenderer.Result Reference,
            IReadOnlyList<string> Flags);

        public static void Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: skin-gpu-sweep <Characters/Name/Skins/SkinN>... | --all [--max-skins N] [--snapshots DIR] [--csv FILE]");
                return;
            }

            string install = InstalledSkins.FindInstall();
            if (install == null)
            {
                Console.WriteLine("[SkinGpu] No League install with ShaderCache.dx11.wad.client was found.");
                return;
            }

            string snapshots = Option(args, "--snapshots");
            string csv = Option(args, "--csv");
            AppSettings settings = InstalledSkins.Settings(install);
            var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            MapCharacterLoadingService loader = InstalledSkins.CreateLoader(settings, log);
            string projectRoot = Path.Combine(Path.GetTempPath(), "am-skin-gpu-sweep");
            Directory.CreateDirectory(projectRoot);
            string[] skins = InstalledSkins.FromArguments(install, args);
            Console.WriteLine($"[SkinGpu] skins={skins.Length}.");

            // The GL context belongs to this thread, so skins load synchronously instead of across awaits.
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new SkinSubmeshRenderer(gl, settings, SceneElements.LoadGenericSkyCube(settings, log));

            var rows = new List<Row>();
            int loaded = 0;
            string champion = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (string skin in skins)
            {
                string owner = skin.Split('/')[1];
                if (!string.Equals(owner, champion, StringComparison.Ordinal))
                {
                    renderer.Reset();
                    champion = owner;
                }

                MapCharacterAssetData asset;
                try
                {
                    asset = loader.LoadAsync(skin, projectRoot, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SkinGpu] {skin}: load failed {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                if (asset?.Mesh?.Ranges == null)
                {
                    Console.WriteLine($"[SkinGpu] {skin}: not loaded.");
                    continue;
                }

                loaded++;
                var hidden = new HashSet<string>(asset.Materials?.InitialHiddenSubmeshes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (MapCharacterMeshRange range in asset.Mesh.Ranges.Where(range => range.IndexCount > 0))
                {
                    ModelMaterialDefinition material = asset.Materials?.ResolveMaterialDefinition(range.Name);
                    if (material?.Program == null)
                        continue;

                    Row row = Check(renderer, asset, range, material, skin, hidden.Contains(range.Name), snapshots != null);
                    // Pixels are only kept long enough to snapshot a flagged row; a full sweep holds tens of thousands of rows.
                    rows.Add(row with { Game = row.Game with { Pixels = null }, Reference = row.Reference with { Pixels = null } });
                    if (row.Flags.Count > 0)
                    {
                        Console.WriteLine(
                            $"[SkinGpu] {string.Join(",", row.Flags)} {skin} {range.Name}{(row.Hidden ? " (hidden)" : "")} shader={row.Shader} " +
                            $"covered={row.Game.Covered}/{row.Reference.Covered} lum={row.Game.Luminance:0.###}/{row.Reference.Luminance:0.###} " +
                            $"max={row.Game.MaxComponent:0.##} nonFinite={row.Game.NonFinite}" +
                            (row.Game.MissingTextures.Count > 0 ? $" missing={string.Join(";", row.Game.MissingTextures)}" : ""));
                        if (snapshots != null)
                        {
                            string stem = Path.Combine(snapshots, $"{skin.Replace('/', '_')}_{range.Name}");
                            if (row.Game.Pixels != null) renderer.SavePng(row.Game.Pixels, stem + "_game.png");
                            if (row.Reference.Pixels != null) renderer.SavePng(row.Reference.Pixels, stem + "_off.png");
                        }
                    }
                }
            }

            Console.WriteLine($"[SkinGpu] loaded={loaded}/{skins.Length} submeshes={rows.Count} elapsed={clock.Elapsed:hh\\:mm\\:ss}.");
            foreach (IGrouping<string, Row> flag in rows.SelectMany(row => row.Flags.Select(flag => (flag, row)))
                         .GroupBy(pair => pair.flag, pair => pair.row)
                         .OrderByDescending(group => group.Count()))
            {
                Console.WriteLine($"[SkinGpu] {flag.Key} x{flag.Count()} (visible {flag.Count(row => !row.Hidden)})");
                foreach (IGrouping<string, Row> shader in flag.GroupBy(row => row.Shader).OrderByDescending(group => group.Count()).Take(12))
                    Console.WriteLine($"[SkinGpu]   {shader.Key} x{shader.Count()} e.g. {string.Join(", ", shader.Take(3).Select(row => $"{row.Skin.Split('/')[1]}/{row.Skin.Split('/')[3]}:{row.Submesh}"))}");
            }
            if (csv != null)
                WriteCsv(csv, rows);
        }

        private static Row Check(
            SkinSubmeshRenderer renderer,
            MapCharacterAssetData asset,
            MapCharacterMeshRange range,
            ModelMaterialDefinition material,
            string skin,
            bool hidden,
            bool keepPixels)
        {
            SkinSubmeshRenderer.Result game = renderer.Render(asset, range, material, keepPixels);
            GameMaterialProgram fallback = GameShaderProgramResolver.CreateDefaultSkinnedProgram(material.BaseTextureName);
            SkinSubmeshRenderer.Result reference = renderer.Render(
                asset, range, ModelMaterialDefinition.TextureOnly(material.BaseTextureName, fallback), keepPixels);

            var flags = new List<string>();
            if (!game.Bound)
                flags.Add("BIND");
            else
            {
                // A dynamic TintColor with zero alpha is how a skin hides an alternate form at rest.
                bool tintHidden = material.DynamicParameters.Any(parameter =>
                    parameter.Name == "TintColor" && parameter.Evaluate(0) is Vector4 { W: 0f });
                if (game.NonFinite > 0)
                    flags.Add("NONFINITE");
                // Dynamic parameters (a dissolve, a zero tint) hide alternate forms and combat effects at rest.
                if (game.Covered == 0 && reference.Covered > 0)
                    flags.Add(tintHidden ? "TINT_HIDDEN" : material.DynamicParameters.Count > 0 ? "DYNAMIC_HIDDEN" : "DISCARDED");
                else if (reference.Covered > 50 && game.Covered < reference.Covered / 4)
                    flags.Add("PARTIAL");
                // A blended pass whose alpha stays near zero draws nothing, whatever colour it writes.
                if (game.Covered > 0 && game.Mean.W < 0.05f && material.Program.Passes.FirstOrDefault()?.State.BlendEnabled == true)
                    flags.Add("TRANSPARENT");
                else if (game.Covered > 0 && reference.Luminance > 0.05f && game.Luminance < reference.Luminance * 0.2f)
                    flags.Add("DARK");
                // Without a base texture the shaders-off look draws nothing, so there is no brightness to compare.
                if (reference.Covered == 0 && game.Covered > 0)
                    flags.Add("SHADER_ONLY");
                else if (game.Luminance > Math.Max(reference.Luminance, 0.05f) * 5f || game.MaxComponent > 16f)
                    flags.Add("BRIGHT");
                if (game.MissingTextures.Count > 0)
                    flags.Add("MISSING_TEXTURE");
            }
            return new Row(skin, range.Name, material.Program.Passes.FirstOrDefault()?.ShaderPath ?? "?", hidden, game, reference, flags);
        }

        private static void WriteCsv(string path, IEnumerable<Row> rows)
        {
            var culture = CultureInfo.InvariantCulture;
            var lines = new List<string> { "skin,submesh,shader,hidden,flags,covered,refCovered,lum,refLum,alpha,max,nonFinite,missing" };
            lines.AddRange(rows.Select(row => string.Join(",",
                row.Skin, row.Submesh, row.Shader, row.Hidden, string.Join("|", row.Flags),
                row.Game.Covered, row.Reference.Covered,
                row.Game.Luminance.ToString("0.####", culture), row.Reference.Luminance.ToString("0.####", culture),
                row.Game.Mean.W.ToString("0.###", culture),
                row.Game.MaxComponent.ToString("0.##", culture), row.Game.NonFinite, string.Join("|", row.Game.MissingTextures))));
            File.WriteAllLines(path, lines);
            Console.WriteLine($"[SkinGpu] csv={path}");
        }

        private static string Option(string[] args, string name)
        {
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }
    }
}
