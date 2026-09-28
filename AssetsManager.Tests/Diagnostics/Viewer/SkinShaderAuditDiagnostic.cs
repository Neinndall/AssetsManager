using System;
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

            var skinUsage = new ShaderInputUsage();
            var particleUsage = new ShaderInputUsage();
            foreach (string skin in skins)
            {
                MapCharacterAssetData asset = await loader.LoadAsync(skin, projectRoot, CancellationToken.None);
                if (asset == null)
                {
                    Console.WriteLine($"[SkinShader] {skin}: not loaded.");
                    continue;
                }

                string name = skin.Split('/').ElementAtOrDefault(1) ?? skin;
                var materials = new[] { asset.Materials?.DefaultMaterialDefinition }
                    .Concat(asset.Materials?.MaterialDefinitions?.Values ?? Enumerable.Empty<ModelMaterialDefinition>())
                    .Where(material => material?.Program != null)
                    .Distinct()
                    .ToArray();
                foreach (ModelMaterialDefinition material in materials)
                    skinUsage.Collect(material.Program, name, settings);

                VfxEmitterDefinition[] emitters = (asset.Vfx?.Systems?.Values ?? Enumerable.Empty<VfxSystemDefinition>())
                    .SelectMany(system => system.Emitters)
                    .ToArray();
                VfxEmitterDefinition[] custom = emitters.Where(emitter => emitter.CustomMaterial?.Program != null).ToArray();
                particleUsage.CollectParticles(emitters.Where(emitter => !emitter.Disabled), name, settings);

                Console.WriteLine(
                    $"[SkinShader] {skin}: programMaterials={materials.Length} vfxSystems={asset.Vfx?.Systems?.Count ?? 0} " +
                    $"emitters={emitters.Length} customMaterialEmitters={custom.Length}.");
            }

            skinUsage.Print("SkinShader");
            particleUsage.Print("SkinParticleShader");
        }
    }
}
