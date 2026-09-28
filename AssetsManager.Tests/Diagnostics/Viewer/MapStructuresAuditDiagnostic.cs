using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// Lists every MAP structure skin a state stands with the material binding of each submesh, so
    /// red (missing material) or untextured structures can be traced to their authored data.
    /// </summary>
    internal static class MapStructuresAuditDiagnostic
    {
        private const string DefaultMap = "Maps/MapGeometry/Map11/Base_SRX";

        public static async Task Run(string[] args)
        {
            string root = args.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                Console.WriteLine("Usage: map-structures-audit <extracted-map-root> [mutator...] [--filter text]");
                return;
            }

            string filter = null;
            var mutators = new List<string>();
            for (int index = 1; index < args.Length; index++)
            {
                if (args[index] == "--filter" && index + 1 < args.Length)
                    filter = args[++index];
                else
                    mutators.Add(args[index]);
            }

            string fullRoot = Path.GetFullPath(root);
            MapSceneSource source = VfxFolderCatalog.ScanBrowser(fullRoot, CancellationToken.None, null, null)
                .MapSources
                .FirstOrDefault(candidate => string.Equals(candidate.Map.Value, DefaultMap, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                Console.WriteLine("[Structures] Base_SRX was not discovered.");
                return;
            }

            var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            var directories = new DirectoriesCreator();
            using var hashResolver = new HashResolverService(directories, log);
            await hashResolver.LoadHashesAsync();
            await hashResolver.LoadBinHashesAsync();

            var settings = AppSettings.GetDefaultSettings();
            string pbe = @"C:\Riot Games\League of Legends (PBE)";
            string live = @"C:\Riot Games\League of Legends";
            settings.LolPbeDirectory = Directory.Exists(pbe) ? pbe : null;
            settings.LolLiveDirectory = Directory.Exists(live) ? live : null;
            settings.PreferredClient = settings.LolPbeDirectory != null ? PreferredClient.PBE : PreferredClient.LIVE;

            var wadProvider = new WadContentProvider(log, new WadNodeLoaderService(hashResolver, log), directories, new SvgParser());
            var resolver = new MapAssetResolver(wadProvider, settings);
            var sceneLoader = new MapSceneLoadingService(
                resolver, new MapGeometryDecoder(), new MapMaterialParser(hashResolver), new MapPlaceableParser(),
                new MapCharacterParser(), new MapParticleParser(), new MapParticleSystemParser(),
                new MapTextureLoadingService(resolver, log), hashResolver, log);
            var factory = new MapSceneRuntimeFactory(
                new MapCharacterLoadingService(resolver, new MapCharacterSkinParser(), new MapCharacterMeshDecoder(), null, null),
                resolver, hashResolver, null);

            MapSceneData scene = await sceneLoader.LoadBackdropAsync(source, CancellationToken.None);
            MapVisibilityState state = new(scene.OpeningVisibility.Flags, scene.OpeningVisibility.SecondaryFlags, mutators);
            Console.WriteLine($"[Structures] state=({state})");

            IReadOnlyList<MapCharacterRuntimeGroup> groups = await factory.LoadCharactersAsync(scene, state, CancellationToken.None);
            try
            {
                foreach (MapCharacterRuntimeGroup group in groups.OrderBy(group => group.Skin, StringComparer.OrdinalIgnoreCase))
                {
                    if (filter != null && group.Skin?.Contains(filter, StringComparison.OrdinalIgnoreCase) != true)
                        continue;
                    MapCharacterAssetData asset = group.Asset;
                    IReadOnlyList<MapCharacterMeshRange> ranges = asset?.Mesh?.Ranges ?? Array.Empty<MapCharacterMeshRange>();
                    Console.WriteLine(
                        $"[Skin] {group.Skin} placements={group.Placements.Count} " +
                        $"controllers={string.Join(",", group.Placements.Select(p => p.VisibilityController?.ToString("x8") ?? "-").Distinct())} " +
                        $"textures={asset?.Textures?.Count ?? 0} [{string.Join(", ", asset?.Textures?.Keys.Take(6) ?? Array.Empty<string>())}] " +
                        $"graph={(asset?.AnimationGraph != null)}");
                    foreach (MapCharacterMeshRange range in ranges)
                    {
                        ModelMaterialDefinition material = asset.Materials?.ResolveMaterialDefinition(range.Name) ??
                                                           ModelMaterialDefinition.TextureOnly(null);
                        bool textureFound = material.BaseTextureName != null &&
                                            asset.Textures?.ContainsKey(material.BaseTextureName) == true;
                        string color = material.BindingKind == ModelMaterialBindingKind.Missing ? "RED(missing material)"
                            : !textureFound ? "GREY(no texture)"
                            : "textured";
                        Console.WriteLine(
                            $"   {range.Name,-28} {color,-22} kind={material.BindingKind} tex={material.BaseTextureName ?? "-"} " +
                            $"blend={material.RenderState.Blending} cutout={material.RenderState.Cutout} alphaCut={material.AlphaCutoff:0.###} " +
                            $"wrap={material.WrapU}/{material.WrapV} uv={UvRange(asset.Mesh, range)} " +
                            $"shader={material.ShaderPath ?? "-"}");
                    }
                }
            }
            finally
            {
                foreach (MapCharacterRuntimeGroup group in groups)
                    group.Dispose();
            }
        }

        private static string UvRange(MapCharacterMeshData mesh, MapCharacterMeshRange range)
        {
            if (mesh?.Uv == null || mesh.Indices == null)
                return "-";
            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            int outside = 0;
            int end = Math.Min(range.StartIndex + range.IndexCount, mesh.Indices.Length);
            for (int at = range.StartIndex; at < end; at++)
            {
                System.Numerics.Vector2 uv = mesh.Uv[mesh.Indices[at]];
                minU = Math.Min(minU, uv.X); maxU = Math.Max(maxU, uv.X);
                minV = Math.Min(minV, uv.Y); maxV = Math.Max(maxV, uv.Y);
                if (uv.X < -0.001f || uv.X > 1.001f || uv.Y < -0.001f || uv.Y > 1.001f)
                    outside++;
            }
            return $"[{minU:0.##}..{maxU:0.##}]x[{minV:0.##}..{maxV:0.##}] outside01={outside * 100.0 / Math.Max(1, end - range.StartIndex):0.#}%";
        }
    }
}
