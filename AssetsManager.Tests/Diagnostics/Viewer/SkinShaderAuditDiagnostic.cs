using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Tests.Support;
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

            string install = InstalledSkins.FindInstall();
            if (install == null)
            {
                Console.WriteLine("[SkinShader] No League install with ShaderCache.dx11.wad.client was found.");
                return;
            }

            AppSettings settings = InstalledSkins.Settings(install);
            var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            var loader = InstalledSkins.CreateLoader(settings, log);

            // An empty project root: every asset resolves from the install WADs.
            string projectRoot = Path.Combine(Path.GetTempPath(), "am-skin-shader-audit");
            Directory.CreateDirectory(projectRoot);

            skins = InstalledSkins.FromArguments(install, skins);
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
                            Console.WriteLine($"[Submesh] {name} {submesh} dynamic {dynamic.Name}={dynamic.Evaluate(0)?.ToString() ?? "unresolved"}");
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
    }
}
