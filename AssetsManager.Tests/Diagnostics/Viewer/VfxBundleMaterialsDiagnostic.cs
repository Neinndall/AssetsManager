using System;
using System.Linq;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using AssetsManager.Services.Viewer.Vfx.Loading;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-bundle-materials <skin.bin> [--no-install]`: loads a skin BIN through VfxLoadingService as VFX Studio does and counts
    /// the emitters whose custom material resolved, and how many of those carry a game program to draw with.
    /// </summary>
    internal static class VfxBundleMaterialsDiagnostic
    {
        public static void Run(string[] args)
        {
            var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            // Built as the app's DI builds it: with the installation to read the global shader BIN from.
            var settings = InstalledSkins.Settings(InstalledSkins.FindInstall());
            var wadProvider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new DirectoriesCreator(), new SvgParser());
            using var service = args.Contains("--no-install") ? new VfxLoadingService() : new VfxLoadingService(null, wadProvider, settings);
            VfxLoadingService.Bundle bundle = service.Load(args[0], log);
            var custom = bundle.Systems.Values.SelectMany(system => system.Emitters.Select(emitter => (system, emitter)))
                .Where(pair => pair.emitter.HasResolvedCustomMaterial).ToArray();
            int withProgram = custom.Count(pair => pair.emitter.CustomMaterial.Program != null);
            Console.WriteLine($"[BundleMaterials] systems={bundle.Systems.Count} customMaterialEmitters={custom.Length} withProgram={withProgram}");
            foreach (var (system, emitter) in custom.Take(8))
                Console.WriteLine($"[BundleMaterials]   {system.Name}/{emitter.Name} shader={emitter.CustomMaterial.ShaderPath} program={(emitter.CustomMaterial.Program != null)}");
        }
    }
}
