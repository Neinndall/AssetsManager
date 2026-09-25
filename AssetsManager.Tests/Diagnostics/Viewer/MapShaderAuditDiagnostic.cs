using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Shaders;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class MapShaderAuditDiagnostic
    {
        private const string DefaultMap = "Maps/MapGeometry/Map11/Base_SRX";

        public static async Task Run(string root, string mapEntry = null)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                Console.WriteLine("Usage: map-shader-audit <extracted-map-root> [map-entry]");
                return;
            }

            string install = FindInstalledShaderCacheRoot();
            if (install == null)
            {
                Console.WriteLine("[MapShader] No League install with ShaderCache.dx11.wad.client was found.");
                return;
            }

            var settings = AppSettings.GetDefaultSettings();
            settings.PreferredClient = PreferredClient.PBE;
            settings.LolPbeDirectory = install;
            settings.LolLiveDirectory = null;

            var diagnosticLog = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            var wadProvider = new WadContentProvider(
                diagnosticLog,
                new WadNodeLoaderService(null, diagnosticLog),
                new DirectoriesCreator(),
                new SvgParser());
            var resolver = new MapAssetResolver(wadProvider, settings);

            string fullRoot = Path.GetFullPath(root);
            MapResolvedAsset shaderDefinitions = await resolver.ResolveVirtualAsync(
                "data/shaders/shaders.bin",
                fullRoot,
                CancellationToken.None);
            Console.WriteLine(
                shaderDefinitions == null
                    ? "[MapShader] shaders.bin unresolved."
                    : $"[MapShader] shaders.bin origin={shaderDefinitions.Origin} wad={shaderDefinitions.WadPath ?? "-"} physical={shaderDefinitions.PhysicalPath ?? "-"} hash=0x{shaderDefinitions.WadPathHash:x16}.");
            if (shaderDefinitions != null)
            {
                await using Stream shaderStream = await resolver.OpenReadAsync(shaderDefinitions, CancellationToken.None);
                if (shaderStream == null)
                {
                    Console.WriteLine("[MapShader] shaders.bin resolved but could not be opened.");
                }
                else
                {
                    try
                    {
                        var shaderTree = new LeagueToolkit.Core.Meta.BinTree(shaderStream);
                        Console.WriteLine($"[MapShader] shaders.bin objects={shaderTree.Objects.Count}.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MapShader] shaders.bin parse failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            string requested = string.IsNullOrWhiteSpace(mapEntry) ? DefaultMap : mapEntry;
            VfxFolderCatalog.BrowserCatalog catalog = VfxFolderCatalog.ScanBrowser(
                fullRoot,
                CancellationToken.None,
                resolveBinEntry: null,
                log: null);
            MapSceneSource source = catalog.MapSources.FirstOrDefault(candidate =>
                string.Equals(candidate.Map.Value, requested, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                Console.WriteLine($"[MapShader] Map '{requested}' was not discovered.");
                return;
            }

            var sceneLoader = new MapSceneLoadingService(
                resolver,
                new MapGeometryDecoder(),
                new MapMaterialParser(),
                new MapPlaceableParser(),
                new MapCharacterParser(),
                new MapParticleParser(),
                new MapParticleSystemParser(),
                new MapTextureLoadingService(resolver, null),
                null,
                null);
            MapSceneData scene = await sceneLoader.LoadBackdropAsync(source, CancellationToken.None);
            if (scene == null)
            {
                Console.WriteLine("[MapShader] Backdrop load failed.");
                return;
            }

            int materialPrograms = 0;
            int passes = 0;
            int bytecodeReady = 0;
            int translatedReady = 0;
            int fullyReadyMaterials = 0;
            int textureBindings = 0;
            int engineBlocks = 0;
            var permutations = new HashSet<string>(StringComparer.Ordinal);
            var dimensions = new Dictionary<GameShaderTranslator.TextureDimension, int>();
            var failures = new Dictionary<string, int>(StringComparer.Ordinal);
            var patches = new Dictionary<GameShaderTranslator.AppliedPatch, int>();

            foreach (MapMaterialDefinition material in scene.Materials)
            {
                GameMaterialProgram program = material?.Program;
                if (program?.Passes is not { Count: > 0 })
                    continue;

                materialPrograms++;
                bool materialReady = false;
                foreach (GameMaterialPass pass in program.Passes)
                {
                    passes++;
                    GameShaderProgramResolver.ShaderBytecodeRead read =
                        GameShaderProgramResolver.Read(pass, program.Kind, settings);
                    if (!read.Ready)
                    {
                        AddFailure(failures, "bytecode: " + read.Failure);
                        continue;
                    }

                    bytecodeReady++;
                    permutations.Add(pass.ShaderPath + "|" + string.Join(";", read.Program.Defines.Select(define => define.Name + "=" + define.Value)));
                    GameShaderTranslator.TranslationRead translated = GameShaderTranslator.Translate(
                        read.Program.Vertex,
                        read.Program.VertexReflection,
                        read.Program.Pixel,
                        read.Program.PixelReflection);
                    if (!translated.Ready)
                    {
                        AddFailure(failures, "translate: " + translated.Failure);
                        continue;
                    }

                    translatedReady++;
                    materialReady = true;
                    GameShaderTranslator.TranslatedProgram ready = translated.Program;
                    foreach (GameShaderTranslator.TextureBinding texture in ready.Vertex.Sidecar.Textures.Concat(ready.Pixel.Sidecar.Textures))
                    {
                        textureBindings++;
                        dimensions[texture.Dimension] = dimensions.GetValueOrDefault(texture.Dimension) + 1;
                    }
                    engineBlocks += ready.Vertex.Sidecar.Blocks.Count(block => block.Name != "$Globals") +
                                    ready.Pixel.Sidecar.Blocks.Count(block => block.Name != "$Globals");
                    foreach (GameShaderTranslator.AppliedPatch patch in ready.Vertex.Applied.Concat(ready.Pixel.Applied))
                        patches[patch] = patches.GetValueOrDefault(patch) + 1;
                }

                if (materialReady)
                    fullyReadyMaterials++;
            }

            Console.WriteLine(
                $"[MapShader] map={source.Map.Value} install={install} materials={scene.Materials.Count} " +
                $"programMaterials={materialPrograms} readyMaterials={fullyReadyMaterials} passes={passes} " +
                $"bytecode={bytecodeReady}/{passes} translated={translatedReady}/{passes} uniquePermutations={permutations.Count} " +
                $"textureBindings={textureBindings} engineBlocks={engineBlocks}.");
            if (dimensions.Count > 0)
                Console.WriteLine("[MapShader] dimensions=" + string.Join(", ", dimensions.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")));
            if (patches.Count > 0)
            {
                Console.WriteLine("[MapShader] patches=" + string.Join(", ", patches.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")));
            }
            foreach ((string failure, int count) in failures.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(20))
                Console.WriteLine($"[MapShader] FAIL x{count}: {failure}");
        }

        private static void AddFailure(IDictionary<string, int> failures, string failure)
        {
            string key = string.IsNullOrWhiteSpace(failure) ? "unknown" : failure;
            failures[key] = failures.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        private static string FindInstalledShaderCacheRoot() =>
            new[]
                {
                    @"C:\Riot Games\League of Legends (PBE)",
                    @"C:\Riot Games\League of Legends"
                }
                .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client")));
    }
}

