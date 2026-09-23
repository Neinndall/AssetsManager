using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class MapFlowAuditDiagnostic
    {
        private const string DefaultMap = "Maps/MapGeometry/Map11/Base_SRX";

        public static async Task Run(string root, string mapEntry = null)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                Console.WriteLine("Usage: map-flow-audit <extracted-map-root> [map-entry]");
                return;
            }

            string fullRoot = Path.GetFullPath(root);
            string requested = string.IsNullOrWhiteSpace(mapEntry) ? DefaultMap : mapEntry;
            VfxFolderCatalog.BrowserCatalog catalog = VfxFolderCatalog.ScanBrowser(
                fullRoot,
                CancellationToken.None,
                resolveBinEntry: null,
                log: null);
            Console.WriteLine(
                $"[MapFlow] catalog maps={catalog.MapSources.Count}, vfxEntries={catalog.Entries.Count}, variants={catalog.MapVariants.Count}.");

            MapSceneSource source = catalog.MapSources.FirstOrDefault(candidate =>
                string.Equals(candidate.Map.Value, requested, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                Console.WriteLine($"[MapFlow] Map '{requested}' was not discovered.");
                return;
            }

            var resolver = new MapAssetResolver(null, null);
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

            MapSceneRuntime runtime = null;
            try
            {
                MapSceneData scene = await sceneLoader.LoadBackdropAsync(source, CancellationToken.None);
                if (scene == null)
                {
                    Console.WriteLine("[MapFlow] Backdrop load failed.");
                    return;
                }

                int placeableCount = scene.Placeables.Sum(chunk => chunk.Items.Count);
                var stoodCharacters = MapCharacterSemantics.StoodForFlags(
                    scene.Characters,
                    scene.OpeningVisibilityFlags);
                int uniqueStoodSkins = stoodCharacters
                    .Where(character => !string.IsNullOrWhiteSpace(character?.Skin))
                    .Select(character => character.Skin)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var playedParticles = MapParticleSemantics.PlayedForFlags(
                    scene.Particles,
                    scene.OpeningVisibilityFlags);
                Console.WriteLine(
                    $"[MapFlow] backdrop meshes={scene.Geometry.Meshes.Count}, submeshes={scene.Geometry.Submeshes.Count}, " +
                    $"materials={scene.Materials.Count}, chunks={scene.Placeables.Count}, placeables={placeableCount}, " +
                    $"characters={scene.Characters.Count}, stoodCharacters={stoodCharacters.Count}, stoodSkins={uniqueStoodSkins}, " +
                    $"particles={scene.Particles.Count}, playedParticles={playedParticles.Count}, " +
                    $"particleSystems={scene.ParticleSystems.Groups.Count}.");

                var characterLoader = new MapCharacterLoadingService(
                    resolver,
                    new MapCharacterSkinParser(),
                    new MapCharacterMeshDecoder(),
                    null,
                    null);
                var runtimeFactory = new MapSceneRuntimeFactory(characterLoader, resolver, null, null);
                runtime = await runtimeFactory.CreateAsync(scene, CancellationToken.None);

                int loadedPlacements = runtime.CharacterGroups.Sum(group => group.Placements.Count);
                int graphGroups = runtime.CharacterGroups.Count(group => group.Asset?.AnimationGraph != null);
                int graphClips = runtime.CharacterGroups.Sum(group => group.Asset?.AnimationGraph?.Clips?.Count ?? 0);
                int skinSystems = runtime.CharacterGroups.Sum(group => group.Asset?.Vfx?.Systems?.Count ?? 0);
                int idleEffects = runtime.CharacterGroups.Sum(group => group.Asset?.Vfx?.IdleEffects?.Count ?? 0);
                Console.WriteLine(
                    $"[MapFlow] runtime characterGroups={runtime.CharacterGroups.Count}, placements={loadedPlacements}, " +
                    $"graphs={graphGroups}, clips={graphClips}, skinVfxSystems={skinSystems}, idleEffects={idleEffects}, " +
                    $"mapVfxRuntimes={runtime.Particles.Runtimes.Count}.");

                string[] loadedSkins = runtime.CharacterGroups
                    .Select(group => group.Asset?.Skin?.Skin)
                    .Where(skin => !string.IsNullOrWhiteSpace(skin))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string[] missingSkins = stoodCharacters
                    .Select(character => character?.Skin)
                    .Where(skin => !string.IsNullOrWhiteSpace(skin))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Except(loadedSkins, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(skin => skin, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                Console.WriteLine(
                    $"[MapFlow] skinResolution loaded={loadedSkins.Length}/{uniqueStoodSkins}, missing={missingSkins.Length}. " +
                    $"particleResolution runtimes={runtime.Particles.Runtimes.Count}/{playedParticles.Count}.");

                foreach (MapParticleSystemGroupData empty in scene.ParticleSystems.Groups
                             .Where(group => group.System?.Emitters is not { Count: > 0 }))
                {
                    scene.MaterialsDocument.Objects.TryGetValue(empty.SystemHash, out BinTreeObject authored);
                    int complex = CountEmitterItems(authored, LeagueToolkit.Hashing.Fnv1a.HashLower("complexEmitterDefinitionData"));
                    int simple = CountEmitterItems(authored, LeagueToolkit.Hashing.Fnv1a.HashLower("simpleEmitterDefinitionData"));
                    Console.WriteLine(
                        $"[MapFlow] EMPTY MAP VFX system=0x{empty.SystemHash:x8} placements={empty.Particles?.Count ?? 0} " +
                        $"authoredEmitters={complex + simple} (complex={complex}, simple={simple}).");
                }

                var resolvedParticleSystems = scene.ParticleSystems.Groups
                    .Select(group => group.SystemHash)
                    .ToHashSet();
                foreach (var unresolved in playedParticles
                             .Where(particle => !resolvedParticleSystems.Contains(particle.SystemHash))
                             .GroupBy(particle => particle.SystemHash)
                             .OrderBy(group => group.Key))
                {
                    bool objectExists = scene.MaterialsDocument.Objects.TryGetValue(unresolved.Key, out BinTreeObject systemObject);
                    Console.WriteLine(
                        $"[MapFlow] UNRESOLVED MAP VFX system=0x{unresolved.Key:x8} placements={unresolved.Count()} " +
                        $"object={(objectExists ? $"0x{systemObject.ClassHash:x8}" : "missing")}.");
                }

                foreach (string skin in missingSkins)
                {
                    string stage;
                    try
                    {
                        MapCharacterAssetData direct = await characterLoader.LoadAsync(
                            skin,
                            fullRoot,
                            CancellationToken.None);
                        stage = direct == null
                            ? await DiagnoseSkinAsync(resolver, skin, fullRoot)
                            : "loaded-directly-after-runtime";
                    }
                    catch (Exception ex)
                    {
                        stage = $"load-exception:{ex.GetType().Name}:{ex.Message}";
                    }
                    Console.WriteLine($"[MapFlow] MISSING SKIN {skin} stage={stage}");
                }

                int clipTotal = 0;
                int clipPrepared = 0;
                int clipUnavailable = 0;
                int clipsWithAuthoredParticles = 0;
                int clipsWithResolvedVfx = 0;
                int authoredParticleEvents = 0;
                int composedParticleEvents = 0;
                int resolvedParticleEvents = 0;
                int unresolvedParticleEvents = 0;
                int unresolvedZeroKey = 0;
                int unresolvedUnmappedKey = 0;
                int unresolvedNullMapping = 0;
                int unresolvedMissingMappedSystem = 0;
                var missingMappedTargets = new System.Collections.Generic.HashSet<uint>();
                foreach (MapCharacterRuntimeGroup group in runtime.CharacterGroups)
                {
                    MapCharacterVfxCatalog groupVfx = group.Asset?.Vfx ?? MapCharacterVfxCatalog.Empty;
                    VfxParticleEventDefinition[] groupEvents = (group.Asset?.AnimationGraph?.Clips ?? Array.Empty<AnimationClipDefinition>())
                        .SelectMany(clip => clip?.ParticleEvents ?? Array.Empty<VfxParticleEventDefinition>())
                        .ToArray();
                    if (groupEvents.Length > 0)
                    {
                        uint[] eventKeys = groupEvents
                            .Select(evt => evt.EffectKey)
                            .Where(key => key != 0)
                            .Distinct()
                            .ToArray();
                        int mappedKeys = eventKeys.Count(key => groupVfx.ResourceMap.ContainsKey(key));
                        int loadedMappedKeys = eventKeys.Count(key =>
                            groupVfx.ResourceMap.TryGetValue(key, out uint systemHash) &&
                            systemHash != 0 &&
                            groupVfx.Systems.ContainsKey(systemHash));
                        Console.WriteLine(
                            $"[MapFlow] SKIN VFX MAP skin={group.Asset?.Skin?.Skin} events={groupEvents.Length} " +
                            $"keys={eventKeys.Length}, resourceMap={groupVfx.ResourceMap.Count}, systems={groupVfx.Systems.Count}, " +
                            $"mappedKeys={mappedKeys}, loadedMappedKeys={loadedMappedKeys}.");

                        if (mappedKeys > loadedMappedKeys)
                        {
                            foreach (uint key in eventKeys.Where(groupVfx.ResourceMap.ContainsKey))
                            {
                                uint target = groupVfx.ResourceMap[key];
                                if (target == 0 || groupVfx.Systems.ContainsKey(target))
                                    continue;
                                missingMappedTargets.Add(target);
                                Console.WriteLine(
                                    $"[MapFlow] MAPPED SYSTEM MISSING skin={group.Asset?.Skin?.Skin} key=0x{key:x8} " +
                                    $"target=0x{target:x8} catalogSystem=absent.");
                            }
                        }
                    }

                    foreach (AnimationClipDefinition clip in group.Asset?.AnimationGraph?.Clips ?? Array.Empty<AnimationClipDefinition>())
                    {
                        clipTotal++;
                        bool prepared = await group.Animation.PrepareClipAsync(
                            group.Asset,
                            clip,
                            null,
                            CancellationToken.None);
                        if (!prepared)
                        {
                            clipUnavailable++;
                            Console.WriteLine(
                                $"[MapFlow] UNAVAILABLE CLIP skin={group.Asset?.Skin?.Skin} " +
                                $"clip={ClipName(clip)} animation={clip.AnimationFilePath ?? "<none>"}.");
                            continue;
                        }

                        clipPrepared++;
                        var playlist = group.Animation.PreparedClipPlaylist(clip);
                        int authored = playlist.Sum(step => step?.ParticleEventCount ?? 0);
                        authoredParticleEvents += authored;
                        if (authored > 0)
                            clipsWithAuthoredParticles++;

                        MapCharacterVfxCatalog vfx = group.Asset?.Vfx ?? MapCharacterVfxCatalog.Empty;
                        VfxAbilityComposition composition = VfxAbilityCompositionBuilder.BuildTimedPlaylist(
                            clip,
                            playlist,
                            group.Animation.PreparedClipStepDurations(clip),
                            group.Animation.PreparedClipFrameSeconds(clip),
                            vfx.Systems,
                            vfx.ResourceMap);
                        composedParticleEvents += composition.Events.Count;
                        resolvedParticleEvents += composition.ResolvedCount;
                        unresolvedParticleEvents += composition.UnresolvedCount;
                        foreach (VfxCompositionEvent unresolved in composition.Events.Where(item => item.System == null))
                        {
                            uint key = unresolved.Event.EffectKey;
                            if (key == 0)
                            {
                                unresolvedZeroKey++;
                            }
                            else if (!vfx.ResourceMap.TryGetValue(key, out uint mapped))
                            {
                                unresolvedUnmappedKey++;
                            }
                            else if (mapped == 0)
                            {
                                unresolvedNullMapping++;
                            }
                            else
                            {
                                unresolvedMissingMappedSystem++;
                            }
                        }
                        if (composition.ResolvedCount > 0)
                            clipsWithResolvedVfx++;

                    }
                }

                foreach ((uint target, string[] sources) in FindObjectSources(fullRoot, missingMappedTargets))
                {
                    Console.WriteLine(
                        $"[MapFlow] MAPPED SYSTEM SOURCE target=0x{target:x8} " +
                        $"files={(sources.Length == 0 ? "<none>" : string.Join(" | ", sources))}.");
                }

                MapBrowserNode browser = MapBrowserSemantics.Build(runtime);
                MapBrowserNode[] browserNodes = DescendantsAndSelf(browser).ToArray();
                int browserSkins = browserNodes.Count(node => node.Kind == MapBrowserNodeKind.CharacterSkin);
                int browserClips = browserNodes.Count(node => node.Kind == MapBrowserNodeKind.Clip);
                int browserParticleSystems = browserNodes.Count(node => node.Kind == MapBrowserNodeKind.ParticleSystem);
                Console.WriteLine(
                    $"[MapFlow] clips total={clipTotal}, prepared={clipPrepared}, unavailable={clipUnavailable}, " +
                    $"withParticleEvents={clipsWithAuthoredParticles}, withResolvedVfx={clipsWithResolvedVfx}, " +
                    $"authoredParticleEvents={authoredParticleEvents}, composed={composedParticleEvents}, " +
                    $"resolved={resolvedParticleEvents}, unresolved={unresolvedParticleEvents}.");
                Console.WriteLine(
                    $"[MapFlow] unresolved clip VFX zeroKey={unresolvedZeroKey}, unmappedKey={unresolvedUnmappedKey}, " +
                    $"nullMapping={unresolvedNullMapping}, missingMappedSystem={unresolvedMissingMappedSystem}.");
                Console.WriteLine(
                    $"[MapFlow] browser skins={browserSkins}, clips={browserClips}, particleSystems={browserParticleSystems}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MapFlow] FAIL {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex);
            }
            finally
            {
                runtime?.Dispose();
            }
        }

        private static async Task<string> DiagnoseSkinAsync(
            MapAssetResolver resolver,
            string skinPath,
            string projectRoot)
        {
            string skinFile = MapCharacterSemantics.SkinFile(skinPath);
            MapResolvedAsset skinAsset = await resolver.ResolveReferenceAsync(
                new MapAssetReference(skinFile, 0),
                projectRoot,
                CancellationToken.None);
            if (skinAsset == null)
                return "skin-bin-unresolved";

            BinTree tree;
            await using (Stream stream = await resolver.OpenReadAsync(skinAsset, CancellationToken.None))
            {
                if (stream == null)
                    return "skin-bin-unreadable";
                tree = new BinTree(stream);
            }

            MapCharacterSkinData skin = new MapCharacterSkinParser().Parse(tree, skinPath);
            if (skin == null)
                return "skin-object-missing";
            if (skin.Mesh?.IsEmpty != false)
                return "mesh-reference-missing";
            if (skin.Skeleton?.IsEmpty != false)
                return "skeleton-reference-missing";

            MapResolvedAsset meshAsset = await resolver.ResolveReferenceAsync(
                skin.Mesh,
                projectRoot,
                CancellationToken.None);
            if (meshAsset == null)
                return $"mesh-unresolved:{Describe(skin.Mesh)}";

            MapResolvedAsset skeletonAsset = await resolver.ResolveReferenceAsync(
                skin.Skeleton,
                projectRoot,
                CancellationToken.None);
            if (skeletonAsset == null)
                return $"skeleton-unresolved:{Describe(skin.Skeleton)}";

            try
            {
                await using (Stream meshStream = await resolver.OpenReadAsync(meshAsset, CancellationToken.None))
                {
                    if (meshStream == null)
                        return "mesh-unreadable";
                    _ = new MapCharacterMeshDecoder().Decode(meshStream);
                }
            }
            catch (Exception ex)
            {
                return $"mesh-decode:{ex.GetType().Name}:{ex.Message}";
            }

            try
            {
                await using Stream skeletonStream = await resolver.OpenReadAsync(skeletonAsset, CancellationToken.None);
                if (skeletonStream == null)
                    return "skeleton-unreadable";
                var skeleton = new RigResource(skeletonStream);
                if (skeleton.Joints.Count == 0 || skeleton.Influences.Count == 0)
                    return $"skeleton-empty:joints={skeleton.Joints.Count},influences={skeleton.Influences.Count}";
            }
            catch (Exception ex)
            {
                return $"skeleton-decode:{ex.GetType().Name}:{ex.Message}";
            }

            return "post-skin-assets";
        }

        private static System.Collections.Generic.IEnumerable<(uint Target, string[] Sources)> FindObjectSources(
            string root,
            System.Collections.Generic.IReadOnlyCollection<uint> targets)
        {
            if (targets == null || targets.Count == 0)
                yield break;

            var remaining = targets.ToHashSet();
            var found = targets.ToDictionary(
                target => target,
                _ => new System.Collections.Generic.List<string>());
            foreach (string file in Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories))
            {
                if (remaining.Count == 0)
                    break;
                try
                {
                    using Stream stream = File.OpenRead(file);
                    var tree = new BinTree(stream);
                    foreach (uint target in remaining.ToArray())
                    {
                        if (!tree.Objects.ContainsKey(target))
                            continue;
                        found[target].Add(Path.GetRelativePath(root, file));
                        remaining.Remove(target);
                    }
                }
                catch
                {
                    // Audit-only scan: malformed/non-BIN payloads are irrelevant here.
                }
            }

            foreach (uint target in targets.OrderBy(value => value))
                yield return (target, found[target].ToArray());
        }

        private static System.Collections.Generic.IEnumerable<MapBrowserNode> DescendantsAndSelf(MapBrowserNode node)
        {
            if (node == null)
                yield break;
            yield return node;
            foreach (MapBrowserNode child in node.Children)
            foreach (MapBrowserNode descendant in DescendantsAndSelf(child))
                yield return descendant;
        }

        private static string ClipName(AnimationClipDefinition clip) =>
            !string.IsNullOrWhiteSpace(clip?.ClipName)
                ? clip.ClipName
                : $"0x{clip?.OwnerPathHash ?? 0:x8}";

        private static int CountEmitterItems(BinTreeObject system, uint field)
        {
            if (system?.Properties == null ||
                !system.Properties.TryGetValue(field, out BinTreeProperty property) ||
                property is not LeagueToolkit.Core.Meta.Properties.BinTreeContainer container)
            {
                return 0;
            }

            return container.Elements?.Count ?? 0;
        }

        private static string Describe(MapAssetReference reference) =>
            !string.IsNullOrWhiteSpace(reference?.VirtualPath)
                ? reference.VirtualPath
                : $"0x{reference?.PathHash ?? 0:x16}";
    }
}
