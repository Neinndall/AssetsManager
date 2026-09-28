using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `skin-shader-audit <skin-path>...`: engine inputs read by the game shaders of champion skins and
    /// their custom particle materials, resolved from the installed client.
    /// </summary>
    internal static class SkinShaderAuditDiagnostic
    {
        public static async Task Run(string[] skins)
        {
            if (skins.Length == 0)
            {
                Console.WriteLine("Usage: skin-shader-audit <Characters/Name/Skins/SkinN>...");
                return;
            }

            string install = new[]
                {
                    @"C:\Riot Games\League of Legends (PBE)",
                    @"C:\Riot Games\League of Legends"
                }
                .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client")));
            if (install == null)
            {
                Console.WriteLine("[SkinShader] No League install with ShaderCache.dx11.wad.client was found.");
                return;
            }

            var settings = AppSettings.GetDefaultSettings();
            settings.PreferredClient = PreferredClient.PBE;
            settings.LolPbeDirectory = install;
            settings.LolLiveDirectory = null;

            var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            var wadProvider = new WadContentProvider(
                log,
                new WadNodeLoaderService(null, log),
                new DirectoriesCreator(),
                new SvgParser());
            var resolver = new MapAssetResolver(wadProvider, settings);
            var loader = new MapCharacterLoadingService(
                resolver,
                new MapCharacterSkinParser(),
                new MapCharacterMeshDecoder(),
                null,
                null);

            // An empty project root: every asset resolves from the install WADs.
            string projectRoot = Path.Combine(Path.GetTempPath(), "am-skin-shader-audit");
            Directory.CreateDirectory(projectRoot);

            if (skins.Contains("--all", StringComparer.OrdinalIgnoreCase))
                skins = AllSkins(install, skins).ToArray();
            Console.WriteLine($"[SkinShader] skins={skins.Length}.");

            var skinUsage = new ShaderInputUsage();
            var particleUsage = new ShaderInputUsage();
            int loaded = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (string skin in skins)
            {
                MapCharacterAssetData asset;
                try
                {
                    asset = await loader.LoadAsync(skin, projectRoot, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SkinShader] {skin}: load failed {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                if (asset == null)
                {
                    Console.WriteLine($"[SkinShader] {skin}: not loaded.");
                    continue;
                }

                loaded++;
                string[] segments = skin.Split('/');
                string name = segments.Length >= 4 ? $"{segments[1]}/{segments[3]}" : skin;
                var materials = new[] { asset.Materials?.DefaultMaterialDefinition }
                    .Concat(asset.Materials?.MaterialDefinitions?.Values ?? Enumerable.Empty<ModelMaterialDefinition>())
                    .Where(material => material?.Program != null)
                    .Distinct()
                    .ToArray();
                foreach (ModelMaterialDefinition material in materials)
                    skinUsage.Collect(material.Program, name, settings);
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AM_SHADER_PARAMS")))
                {
                    var submeshes = (asset.Materials?.MaterialDefinitions ?? new Dictionary<string, ModelMaterialDefinition>())
                        .Select(pair => (Submesh: pair.Key, Material: pair.Value))
                        .Prepend(("<default>", asset.Materials?.DefaultMaterialDefinition));
                    foreach ((string submesh, ModelMaterialDefinition material) in submeshes)
                        Console.WriteLine(
                            $"[Submesh] {name} {submesh}: shader={material?.Program?.Passes.FirstOrDefault()?.ShaderPath ?? "-"} " +
                            $"state={material?.Program?.Passes.FirstOrDefault()?.State} " +
                            $"textures={string.Join(", ", material?.Program?.Passes.FirstOrDefault()?.Textures?.Select(texture => $"{texture.Name}=0x{texture.Texture?.PathHash:x16}") ?? Array.Empty<string>())}");
                    Console.WriteLine($"[Submesh] {name} hidden={string.Join(", ", asset.Materials?.InitialHiddenSubmeshes ?? Array.Empty<string>())}");
                    foreach ((string submesh, ModelMaterialDefinition material) in submeshes)
                        foreach (GameMaterialDynamicParameter dynamic in material?.DynamicParameters ?? Array.Empty<GameMaterialDynamicParameter>())
                            Console.WriteLine($"[Submesh] {name} {submesh} dynamic {dynamic}");
                    foreach (MapCharacterMeshRange range in asset.Mesh?.Ranges ?? Array.Empty<MapCharacterMeshRange>())
                    {
                        // Farthest bind-pose vertex from the model origin, which dissolve radii are measured against.
                        float farthest = Enumerable.Range(range.StartIndex, range.IndexCount)
                            .Select(at => asset.Mesh.Positions[asset.Mesh.Indices[at]].Length())
                            .DefaultIfEmpty(0f)
                            .Max();
                        Console.WriteLine($"[Submesh] {name} mesh {range.Name} indices={range.IndexCount} farthest={farthest:0}");
                    }
                }

                VfxEmitterDefinition[] emitters = (asset.Vfx?.Systems?.Values ?? Enumerable.Empty<VfxSystemDefinition>())
                    .SelectMany(system => system.Emitters)
                    .ToArray();
                VfxEmitterDefinition[] custom = emitters.Where(emitter => emitter.CustomMaterial?.Program != null).ToArray();
                particleUsage.CollectParticles(emitters.Where(emitter => !emitter.Disabled), name, settings);

                Console.WriteLine(
                    $"[SkinShader] {skin}: programMaterials={materials.Length} vfxSystems={asset.Vfx?.Systems?.Count ?? 0} " +
                    $"emitters={emitters.Length} customMaterialEmitters={custom.Length}.");
            }

            Console.WriteLine($"[SkinShader] loaded={loaded}/{skins.Length} elapsed={clock.Elapsed:hh\\:mm\\:ss}.");
            skinUsage.Print("SkinShader");
            particleUsage.Print("SkinParticleShader");
        }

        /// <summary>
        /// Every champion WAD of the install (localized WADs excluded) with its skin BINs from the game hash
        /// list, capped by `--max-skins N` per champion (all by default).
        /// </summary>
        private static IEnumerable<string> AllSkins(string install, string[] args)
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
            var byChampion = champions.ToDictionary(name => name.ToLowerInvariant(), _ => new SortedSet<int>());
            var pattern = new System.Text.RegularExpressions.Regex(@"^data/characters/([a-z0-9_]+)/skins/skin(\d+)\.bin$");
            foreach (string line in File.ReadLines(hashes))
            {
                int space = line.IndexOf(' ');
                if (space < 0)
                    continue;
                var match = pattern.Match(line[(space + 1)..]);
                if (match.Success && byChampion.TryGetValue(match.Groups[1].Value, out SortedSet<int> ids))
                    ids.Add(int.Parse(match.Groups[2].Value));
            }

            foreach (string champion in champions)
                foreach (int id in byChampion[champion.ToLowerInvariant()].Take(maxSkins))
                    yield return $"Characters/{champion}/Skins/Skin{id}";
        }
    }
}
