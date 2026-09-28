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
using AssetsManager.Services.Viewer.Runtime;
using LeagueToolkit.Core.Meta.Properties;
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

        public static async Task Run(string root, string mapEntry = null,
            Action<GameShaderTranslator.TranslatedProgram> verifyProgram = null, bool verifyResources = false)
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

            if (verifyResources)
            {
                IReadOnlyDictionary<string, MapTextureImage> textures =
                    await sceneLoader.LoadFullProgramTexturesAsync(scene);
                int requestedTextures = 0;
                int loadedTextures = 0;
                foreach (MapMaterialDefinition material in scene.Materials)
                {
                    if (material?.Program?.Passes == null)
                        continue;
                    for (int pass = 0; pass < material.Program.Passes.Count; pass++)
                    {
                        foreach (GameMaterialTexture texture in material.Program.Passes[pass].Textures ?? Array.Empty<GameMaterialTexture>())
                        {
                            if (texture?.Texture?.IsEmpty != false)
                                continue;
                            requestedTextures++;
                            string key = MapTextureLoadingService.ProgramTextureKey(material.Name, pass, texture.Name);
                            if (textures.ContainsKey(key))
                                loadedTextures++;
                            else
                                Console.WriteLine($"[MapShader] Missing texture {key}: {texture.Texture.VirtualPath ?? $"0x{texture.Texture.PathHash:x16}"}.");
                        }
                    }
                }
                IReadOnlyDictionary<string, MapTextureImage> lights =
                    await sceneLoader.LoadFullLightmapsAsync(scene);
                Console.WriteLine($"[MapShader] decodedProgramTextures={loadedTextures}/{requestedTextures} lightmaps={lights.Count}/{scene.Geometry.Lightmaps.Count}.");
            }

            BinTreeStruct bake = MapPostEffectsParser.FindComponent(scene.MaterialsDocument, source.Map, 0x6a4a3409);
            string lightGrid = (bake?.Properties.GetValueOrDefault(0x7561b09eu) as BinTreeString)?.Value;
            Console.WriteLine($"[MapShader] bakedLightMeshes={scene.Geometry.Meshes.Count(mesh => mesh.BakedLight?.IsEmpty == false)} stationaryLightMeshes={scene.Geometry.Meshes.Count(mesh => mesh.StationaryLight?.IsEmpty == false)} lightGrid={lightGrid ?? "-"} loadedGrid={(scene.LightGrid == null ? "-" : $"{scene.LightGrid.Width}x{scene.LightGrid.Height} scale={scene.LightGrid.Scale} fullBright={scene.LightGrid.FullBright}")}.");

            MapTerrainData terrain = scene.Terrain;
            Console.WriteLine(terrain == null
                ? "[MapShader] terrain=-"
                : $"[MapShader] terrain bounds={terrain.BoundsMin}..{terrain.BoundsMax} xform={terrain.TerrainTransform} " +
                  $"grassTint={terrain.GrassTint?.VirtualPath ?? $"0x{terrain.GrassTint?.PathHash:x16}"} " +
                  $"alternates={string.Join(", ", terrain.GrassTintAlternates.Select(alternate => $"0x{alternate.Flag:x2}:{alternate.Texture.VirtualPath ?? $"0x{alternate.Texture.PathHash:x16}"}"))}.");
            if (terrain != null)
            {
                IReadOnlyDictionary<string, MapTextureImage> programTextures = await sceneLoader.LoadPreviewProgramTexturesAsync(scene);
                Console.WriteLine("[MapShader] terrainTextures=" + string.Join(", ", terrain.TextureRequests.Select(request =>
                    $"{request.Key}:{(programTextures.TryGetValue(request.Key, out MapTextureImage image) ? $"{image.BaseLevel.PixelWidth}px" : "missing")}")));
            }

            // Inspect the retained shader definitions without uploading resources or creating a GL context.
            MapParticleSystemCatalog runtimeCatalog = MapSceneRuntimeFactory.ParseParticleSystems(
                scene, scene.OpeningVisibilityFlags);
            VfxEmitterDefinition[] initialEmitters = scene.ParticleSystems.Systems.Values.SelectMany(system => system.Emitters).ToArray();
            VfxEmitterDefinition[] runtimeEmitters = runtimeCatalog.Systems.Values.SelectMany(system => system.Emitters).ToArray();
            Console.WriteLine($"[MapShader] customMaterialEmitters initial={initialEmitters.Count(emitter => emitter.CustomMaterial != null)} runtime={runtimeEmitters.Count(emitter => emitter.CustomMaterial != null)} programs initial={initialEmitters.Count(emitter => emitter.CustomMaterial?.Program != null)} runtime={runtimeEmitters.Count(emitter => emitter.CustomMaterial?.Program != null)}.");
            Console.WriteLine("[MapShader] Coverage below measures bytecode and GLSL translation; it does not execute OpenGL draws.");

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
            var usage = new ShaderInputUsage();

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
                    string dumpFilter = Environment.GetEnvironmentVariable("AM_SHADER_DUMP");
                    string dumpDir = Environment.GetEnvironmentVariable("AM_SHADER_DUMP_DIR");
                    if (!string.IsNullOrWhiteSpace(dumpFilter) && !string.IsNullOrWhiteSpace(dumpDir) &&
                        material.Name?.Contains(dumpFilter, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Directory.CreateDirectory(dumpDir);
                        string stem = Path.Combine(dumpDir, material.Name.Split('/').Last());
                        File.WriteAllText(stem + ".vs.glsl", ready.Vertex.Glsl);
                        File.WriteAllText(stem + ".ps.glsl", ready.Pixel.Glsl);
                        File.WriteAllText(stem + ".sidecar.txt",
                            "VS attributes: " + string.Join(", ", ready.Vertex.Sidecar.Attributes.Select(a => a.ToString())) + Environment.NewLine +
                            "VS blocks: " + string.Join(" | ", ready.Vertex.Sidecar.Blocks.Select(b => b.Name + "{" + string.Join(",", b.Members.Select(m => m.ToString())) + "}")) + Environment.NewLine +
                            "PS blocks: " + string.Join(" | ", ready.Pixel.Sidecar.Blocks.Select(b => b.Name + "{" + string.Join(",", b.Members.Select(m => m.ToString())) + "}")) + Environment.NewLine +
                            "PS textures: " + string.Join(", ", ready.Pixel.Sidecar.Textures.Select(t => t.Name)) + Environment.NewLine +
                            "VS textures: " + string.Join(", ", ready.Vertex.Sidecar.Textures.Select(t => t.Name)) + Environment.NewLine +
                            "Defines: " + string.Join(";", read.Program.Defines.Select(d => d.Name + "=" + d.Value)));
                        Console.WriteLine($"[MapShader] dumped {stem}");
                    }
                    verifyProgram?.Invoke(ready);
                    foreach (GameShaderTranslator.TextureBinding texture in ready.Vertex.Sidecar.Textures.Concat(ready.Pixel.Sidecar.Textures))
                    {
                        textureBindings++;
                        dimensions[texture.Dimension] = dimensions.GetValueOrDefault(texture.Dimension) + 1;
                    }
                    usage.Track(ready, material.Name?.Split('/').LastOrDefault() ?? "?");
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
            usage.Print("MapShader");

            // Map particles run the stock particle programs (or their custom material) through the same engine blocks.
            var particleUsage = new ShaderInputUsage();
            particleUsage.CollectParticles(runtimeEmitters.Where(emitter => !emitter.Disabled), "map", settings);
            particleUsage.Print("MapParticleShader");
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

