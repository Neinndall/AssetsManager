using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// Measures map-state switches on a real extraction: full reload of structures/VFX versus the
    /// reconciling plans, then checks that closing the scene releases every owner and temp overlay.
    /// </summary>
    internal static class MapStateSwitchBenchDiagnostic
    {
        private const string DefaultMap = "Maps/MapGeometry/Map11/Base_SRX";

        public static async Task Run(string root, string mapEntry = null)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                Console.WriteLine("Usage: map-state-switch-bench <extracted-map-root> [map-entry]");
                return;
            }

            string fullRoot = Path.GetFullPath(root);
            string requested = string.IsNullOrWhiteSpace(mapEntry) ? DefaultMap : mapEntry;
            MapSceneSource source = StudioProjectCatalog.ScanBrowser(fullRoot, CancellationToken.None, null, null)
                .MapSources
                .FirstOrDefault(candidate => string.Equals(candidate.Map.Value, requested, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                Console.WriteLine($"[Bench] Map '{requested}' was not discovered.");
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
                resolver,
                new MapGeometryDecoder(),
                new MapMaterialParser(hashResolver),
                new MapPlaceableParser(),
                new MapCharacterParser(),
                new MapParticleParser(),
                new MapParticleSystemParser(),
                new MapTextureLoadingService(resolver, log),
                hashResolver,
                log);
            var factory = new MapSceneRuntimeFactory(
                new MapCharacterLoadingService(resolver, new MapCharacterSkinParser(), new MapCharacterMeshDecoder(), null, null),
                resolver,
                hashResolver,
                null);

            MapSceneData scene = await sceneLoader.LoadBackdropAsync(source, CancellationToken.None);
            if (scene == null)
            {
                Console.WriteLine("[Bench] Backdrop load failed.");
                return;
            }

            MapVisibilityState opening = scene.OpeningVisibility;
            MapVisibilityDomainData primary = scene.Visibility.Definitions.Primary;
            var sequence = new List<(string Label, MapVisibilityState State)>
            {
                ("Mountain", opening.WithFlags(MapVisibilitySemantics.TransformationFlags(primary, 2))),
                ("Base", opening),
                ("Tunnel", opening.WithSecondaryFlags(4)),
                ("Mountain", opening.WithFlags(MapVisibilitySemantics.TransformationFlags(primary, 2))),
                ("Chemtech", opening.WithFlags(MapVisibilitySemantics.TransformationFlags(primary, 6)))
            };

            var watch = Stopwatch.StartNew();
            MapSceneRuntime runtime = await factory.CreateAsync(scene, CancellationToken.None);
            Console.WriteLine($"[Bench] opening load {watch.ElapsedMilliseconds} ms · skins={runtime.CharacterGroups.Count} vfx={runtime.Particles.Runtimes.Count}");
            string overlay = runtime.Particles.Resources?.SearchDirectory;

            try
            {
                foreach ((string label, MapVisibilityState state) in sequence)
                {
                    watch.Restart();
                    IReadOnlyList<MapCharacterRuntimeGroup> fullCharacters = await factory.LoadCharactersAsync(scene, state, CancellationToken.None);
                    MapParticleSceneRuntime fullParticles = await factory.LoadParticlesAsync(scene, state, CancellationToken.None);
                    long fullMs = watch.ElapsedMilliseconds;
                    int fullSkins = fullCharacters.Count;
                    int fullVfx = fullParticles.Runtimes.Count;
                    foreach (MapCharacterRuntimeGroup group in fullCharacters)
                        group.Dispose();
                    fullParticles.Dispose();

                    watch.Restart();
                    using MapCharacterPlan characters = await factory.PlanCharactersAsync(
                        scene, state, runtime.CharacterReuseCandidates(), runtime.CharacterGeneration, CancellationToken.None);
                    using MapParticlePlan particles = await factory.PlanParticlesAsync(
                        scene, state, runtime.Particles, runtime.ParticleGeneration, CancellationToken.None);
                    bool applied = runtime.TryApplyCharacterPlan(characters) && runtime.TryApplyParticlePlan(particles);
                    runtime.SetVisibility(state);
                    long planMs = watch.ElapsedMilliseconds;

                    Console.WriteLine(
                        $"[Bench] {label,-9} full={fullMs,6} ms ({fullSkins} skins, {fullVfx} vfx) · " +
                        $"reconcile={planMs,6} ms (skins {characters.ReusedCount} reused/{characters.LoadedCount} loaded, " +
                        $"vfx {particles.KeptCount} kept/{particles.CreatedCount} new, retained={runtime.RetainedCharacterGroupCount}) " +
                        $"match={(fullSkins == runtime.CharacterGroups.Count && fullVfx == runtime.Particles.Runtimes.Count)} applied={applied}");
                }
            }
            finally
            {
                MapCharacterRuntimeGroup[] owned = runtime.CharacterGroups.ToArray();
                runtime.Dispose();
                Console.WriteLine(
                    $"[Bench] closed scene · groups released={owned.All(group => group.IsDisposed)} · " +
                    $"overlay removed={(overlay == null ? "n/a" : (!Directory.Exists(overlay)).ToString())}");
            }
        }
    }
}
