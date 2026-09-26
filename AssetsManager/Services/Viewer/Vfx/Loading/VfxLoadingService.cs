using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Loading
{
    /// <summary>
    /// Loads a model's complete effect catalog and prepares every referenced emitter resource.
    /// </summary>
    public sealed class VfxLoadingService : IDisposable
    {
        // LTK bounds every breadth-first linked BIN search to 32 files beyond the primary document.
        private const int MaximumLinkedBins = 32;
        private readonly VfxResourceResolver _resources = new();
        private readonly HashResolverService _hashResolverService;
        private readonly SemaphoreSlim _catalogGate = new(1, 1);
        private int _disposeState;

        public VfxLoadingService(HashResolverService hashResolverService = null)
        {
            _hashResolverService = hashResolverService;
        }

        /// <summary>
        /// Resolves a BIN object path hash through the same catalog used by the generic BIN tools.
        /// VFX Studio uses this only for semantic browser discovery (for example Character Spells).
        /// </summary>
        internal string ResolveBinEntryPath(uint pathHash)
            => _hashResolverService?.ResolveBinEntry(pathHash);

        /// <summary>
        /// Resolves a GAME WAD path hash through the shared hash catalog. Installation backdrop
        /// discovery uses the same catalog as Explorer instead of maintaining a second path database.
        /// </summary>
        internal string ResolveGamePath(ulong pathHash)
            => _hashResolverService?.ResolveHash(pathHash);

        public sealed class Bundle
        {
            public string PrimaryBinPath { get; internal set; }
            public Dictionary<uint, VfxSystemDefinition> Systems { get; private set; } = new();
            public Dictionary<uint, uint> ResourceMap { get; private set; } = new();
            public Dictionary<uint, string> SystemSources { get; private set; } = new();
            public Dictionary<uint, AnimationClipDefinition> EventSequences { get; private set; } = new();
            public List<AnimationClipDefinition> Clips { get; private set; } = new();
            public List<AnimationGraphDefinition> AnimationGraphs { get; private set; } = new();
            public List<VfxIdleEffectDefinition> IdleEffects { get; private set; } = new();
            public IReadOnlyList<VfxCharacterFormDefinition> CharacterForms { get; internal set; } =
                Array.Empty<VfxCharacterFormDefinition>();
            public VfxOwnerSceneContext OwnerSceneContext { get; set; }
            public List<string> LoadedBins { get; private set; } = new();
            public List<string> MissingDependencies { get; private set; } = new();
            public List<string> AmbiguousDependencies { get; private set; } = new();
            internal int? CharacterGearIndex { get; private set; }

            public Bundle()
            {
            }

            private Bundle(Bundle source, VfxCharacterFormDefinition form)
            {
                PrimaryBinPath = source.PrimaryBinPath;
                Systems = source.Systems;
                SystemSources = source.SystemSources;
                EventSequences = source.EventSequences;
                Clips = source.Clips;
                AnimationGraphs = source.AnimationGraphs;
                LoadedBins = source.LoadedBins;
                MissingDependencies = source.MissingDependencies;
                AmbiguousDependencies = source.AmbiguousDependencies;
                CharacterForms = source.CharacterForms;
                OwnerSceneContext = source.OwnerSceneContext;

                ResourceMap = new Dictionary<uint, uint>(source.ResourceMap);
                if (form.ResourceMap != null)
                {
                    foreach (var entry in form.ResourceMap)
                        ResourceMap[entry.Key] = entry.Value;
                }
                IdleEffects = form.EnableOverrideIdleEffects
                    ? (form.OverrideIdleEffects ?? Array.Empty<VfxIdleEffectDefinition>()).ToList()
                    : source.IdleEffects;
                CharacterGearIndex = form.GearIndex;
            }

            internal Bundle CreateCharacterPlaybackView(VfxCharacterFormDefinition form)
                => form == null ? this : new Bundle(this, form);
        }

        public Bundle Load(string skinBinPath, LogService log)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            _catalogGate.Wait();
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
                return LoadCore(skinBinPath, log, CancellationToken.None);
            }
            finally
            {
                _catalogGate.Release();
                TryDisposeResources();
            }
        }

        public async Task<Bundle> LoadAsync(string skinBinPath, LogService log, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
                return await Task.Run(() => LoadCore(skinBinPath, log, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _catalogGate.Release();
                TryDisposeResources();
            }
        }

        private Bundle LoadCore(string skinBinPath, LogService log, CancellationToken cancellationToken)
        {
            var bundle = new Bundle();
            if (string.IsNullOrEmpty(skinBinPath) || !File.Exists(skinBinPath)) return bundle;
            bundle.PrimaryBinPath = Path.GetFullPath(skinBinPath);

            try
            {
                string charFolder = Path.GetDirectoryName(Path.GetDirectoryName(skinBinPath));

                string wadRoot = ResolveWadRoot(skinBinPath);
                string searchFolder = charFolder;
                if (!string.IsNullOrEmpty(wadRoot)) searchFolder = wadRoot;

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var clipKeys = new HashSet<(uint Graph, uint Clip)>();
                var graphKeys = new HashSet<uint>();
                var queue = new Queue<string>();
                var characterFormDocuments = new List<VfxCharacterFormDocumentData>();

                void Enqueue(string p)
                {
                    if (File.Exists(p) && visited.Add(Path.GetFullPath(p)))
                        queue.Enqueue(p);
                }

                Enqueue(skinBinPath);
                int openedLinkedBins = 0;

                while (queue.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string currentBinPath = queue.Dequeue();
                    bool isPrimary = string.Equals(
                        Path.GetFullPath(currentBinPath),
                        bundle.PrimaryBinPath,
                        StringComparison.OrdinalIgnoreCase);
                    if (!isPrimary)
                    {
                        if (openedLinkedBins >= MaximumLinkedBins) break;
                        openedLinkedBins++;
                    }
                    try
                    {
                        if (!File.Exists(currentBinPath)) continue;
                        byte[] fileBytes = File.ReadAllBytes(currentBinPath);
                        VfxBinDocument document = VfxGraphParser.ParseDocument(
                            fileBytes,
                            ResolveGraphHashName,
                            ResolveGraphClassName,
                            _hashResolverService == null ? null : _hashResolverService.ResolveHash,
                            _hashResolverService == null ? null : _hashResolverService.ResolveBinEntry);
                        bundle.LoadedBins.Add(Path.GetFullPath(currentBinPath));
                        if (document.CharacterFormData != null)
                            characterFormDocuments.Add(document.CharacterFormData);

                        foreach (var kv in document.Systems)
                        {
                            if (bundle.Systems.TryAdd(kv.Key, kv.Value))
                            {
                                bundle.SystemSources[kv.Key] = Path.GetFullPath(currentBinPath);
                            }
                        }
                        // Skin/clip effect keys resolve through SkinCharacterDataProperties.mResourceResolver
                        // in the primary document. Linked systems keep their own document resolver scope.
                        if (string.Equals(
                                Path.GetFullPath(currentBinPath),
                                bundle.PrimaryBinPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var kv in document.SkinResourceMap)
                                bundle.ResourceMap.TryAdd(kv.Key, kv.Value);
                        }
                        foreach (AnimationClipDefinition sequence in document.EventSequences)
                        {
                            bundle.EventSequences.TryAdd(sequence.OwnerPathHash, sequence);
                            if (clipKeys.Add((sequence.GraphPathHash, sequence.OwnerPathHash)))
                                bundle.Clips.Add(sequence);
                        }
                        foreach (AnimationGraphDefinition graph in
                                 document.AnimationGraphs ?? Array.Empty<AnimationGraphDefinition>())
                        {
                            if (graphKeys.Add(graph.PathHash))
                                bundle.AnimationGraphs.Add(graph);
                        }
                        if (document.IdleEffects != null &&
                            string.Equals(Path.GetFullPath(currentBinPath), Path.GetFullPath(skinBinPath), StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (VfxIdleEffectDefinition idle in document.IdleEffects)
                                bundle.IdleEffects.Add(idle);
                        }
                        bundle.OwnerSceneContext ??= document.OwnerSceneContext;

                        foreach (var dep in document.Dependencies)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (string.IsNullOrEmpty(dep)) continue;
                            IReadOnlyList<string> resolvedDependencies =
                                _resources.ResolveLinkedBins(dep, wadRoot, searchFolder);
                            bool enqueued = resolvedDependencies.Count > 0;
                            if (resolvedDependencies.Count > 1)
                            {
                                bundle.AmbiguousDependencies.Add(dep);
                            }
                            foreach (string resolvedDependency in resolvedDependencies)
                                Enqueue(resolvedDependency);

                            if (!enqueued)
                            {
                                bundle.MissingDependencies.Add(dep);
                            }
                        }

                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        log?.LogError(ex, $"Error scanning bin dependency file: {currentBinPath}");
                    }
                }

                bundle.CharacterForms = VfxCharacterFormParser.Resolve(characterFormDocuments, ResolveBinEntryPath);
                log?.Log($"Loaded {bundle.Systems.Count} VFX systems.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.LogError(ex, $"Failed to load VFX files for model.");
            }

            return bundle;
        }

        public VfxPlaybackRuntime PreparePlayback(
            VfxSystemDefinition definition,
            string searchDirectory,
            Matrix4x4 transform,
            int seed,
            LogService log,
            VfxOwnerSceneContext ownerSceneContext = null)
            => PreparePlaybackCore(
                definition,
                searchDirectory,
                transform,
                seed,
                log,
                ownerSceneContext,
                applyDefinitionTransform: true);

        internal VfxPlaybackRuntime PreparePlaybackAtWorldTransform(
            VfxSystemDefinition definition,
            string searchDirectory,
            Matrix4x4 worldTransform,
            int seed,
            LogService log,
            VfxOwnerSceneContext ownerSceneContext = null)
            => PreparePlaybackCore(
                definition,
                searchDirectory,
                worldTransform,
                seed,
                log,
                ownerSceneContext,
                applyDefinitionTransform: false);

        private VfxPlaybackRuntime PreparePlaybackCore(
            VfxSystemDefinition definition,
            string searchDirectory,
            Matrix4x4 transform,
            int seed,
            LogService log,
            VfxOwnerSceneContext ownerSceneContext,
            bool applyDefinitionTransform)
        {
            ArgumentNullException.ThrowIfNull(definition);
            definition = ResolveMeshAvailability(definition, searchDirectory);
            var runtime = new VfxPlaybackRuntime(seed);
            Matrix4x4 resolvedTransform = applyDefinitionTransform
                ? definition.Transform.GetValueOrDefault(Matrix4x4.Identity) * transform
                : transform;
            runtime.SetSystem(definition, resolvedTransform);

            PrepareRuntimeResources(runtime, searchDirectory, log, ownerSceneContext, resetResolved: false);

            IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> emissionSurfaces =
                PrepareEmissionSurfaces(new[] { definition }, searchDirectory, log);
            if (emissionSurfaces.Count > 0)
                runtime.SetEmissionSurfaces(emissionSurfaces, replayCurrentTime: false);

            runtime.ApplyRenderOrder();

            return runtime;
        }

        private void PrepareRuntimeResources(
            VfxPlaybackRuntime runtime,
            string searchDirectory,
            LogService log,
            VfxOwnerSceneContext ownerSceneContext,
            bool resetResolved)
        {
            if (runtime?.Emitters is null) return;

            foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
            {
                if (resetResolved)
                    emitter.ResetResolvedResources();

                emitter.PendingTexture = _resources.ResolveTexture(emitter.Def.TexturePath, searchDirectory);
                emitter.PendingTextureMult = _resources.ResolveTexture(emitter.Def.TextureMultPath, searchDirectory);
                emitter.PendingDistortionTexture = _resources.ResolveTexture(
                    emitter.Def.Distortion?.NormalMapTexturePath,
                    searchDirectory);
                emitter.PendingErosionTexture = _resources.ResolveTexture(
                    emitter.Def.AlphaErosion?.TexturePath,
                    searchDirectory);
                emitter.PendingReflectionTexture = _resources.ResolveCubeMap(
                    emitter.Def.Reflection?.TexturePath,
                    searchDirectory);
                emitter.PendingPaletteTexture = _resources.ResolveTexture(
                    emitter.Def.PaletteDefinition?.PaletteTexturePath,
                    searchDirectory);
                emitter.PendingColorGradient = _resources.ResolveTexture(
                    emitter.Def.ParticleColorTexturePath,
                    searchDirectory);

                if (emitter.Def.PrimitiveKind == VfxPrimitiveKind.AttachedMesh)
                {
                    if (!string.IsNullOrWhiteSpace(ownerSceneContext?.MeshPath))
                    {
                        emitter.PendingMesh = _resources.ResolveAttachedMesh(
                            ownerSceneContext.MeshPath,
                            ownerSceneContext.SkeletonPath,
                            emitter.Def.SubmeshesToDraw,
                            emitter.Def.SubmeshesToDrawAlways,
                            ownerSceneContext.InitialHiddenSubmeshHashes,
                            searchDirectory,
                            ownerSceneContext.SkinScale);
                    }
                    continue;
                }

                if (!emitter.Def.IsMeshPrimitive || string.IsNullOrWhiteSpace(emitter.Def.MeshPath))
                    continue;

                VfxMeshData? mesh = _resources.ResolveMesh(
                    emitter.Def.MeshPath,
                    emitter.Def.SubmeshesToDraw,
                    emitter.Def.SubmeshesToDrawAlways,
                    searchDirectory);

                // Current LTK main poses a skinned VFX mesh independently for every live
                // particle. Resolve the skeleton even when no ANM is authored so bind-pose
                // skinning and boneToSpawnAt children share the same joint table.
                if (mesh.HasValue && emitter.Def.MeshIsSkinned &&
                    !string.IsNullOrWhiteSpace(emitter.Def.MeshSkeletonPath))
                {
                    VfxAnimatedMesh bindPose = _resources.ResolveMeshAnimation(
                        emitter.Def.MeshPath,
                        emitter.Def.MeshSkeletonPath,
                        null,
                        searchDirectory,
                        log);
                    if (bindPose is not null)
                    {
                        mesh = mesh.Value with
                        {
                            BoneIndices = bindPose.BoneIndices,
                            BoneWeights = bindPose.BoneWeights
                        };

                        emitter.MeshBaseAnimation = string.IsNullOrWhiteSpace(emitter.Def.MeshAnimationPath)
                            ? bindPose
                            : _resources.ResolveMeshAnimation(
                                emitter.Def.MeshPath,
                                emitter.Def.MeshSkeletonPath,
                                emitter.Def.MeshAnimationPath,
                                searchDirectory,
                                log) ?? bindPose;

                        IReadOnlyList<string> variants = emitter.Def.MeshAnimationVariants ?? Array.Empty<string>();
                        if (variants.Count > 0)
                        {
                            emitter.MeshAnimationVariants = new VfxAnimatedMesh[variants.Count];
                            for (int variant = 0; variant < variants.Count; variant++)
                            {
                                emitter.MeshAnimationVariants[variant] = _resources.ResolveMeshAnimation(
                                    emitter.Def.MeshPath,
                                    emitter.Def.MeshSkeletonPath,
                                    variants[variant],
                                    searchDirectory,
                                    log);
                            }
                        }
                        emitter.MeshAnimation = emitter.MeshBaseAnimation;
                    }
                }
                emitter.PendingMesh = mesh;
            }
        }

        internal static bool UsesSameResolvedAssets(
            VfxSystemDefinition before,
            VfxSystemDefinition after)
        {
            if (before is null || after is null || before.Emitters?.Count != after.Emitters?.Count)
                return false;

            for (int index = 0; index < (before.Emitters?.Count ?? 0); index++)
            {
                if (!UsesSameResolvedAssets(before.Emitters[index], after.Emitters[index]))
                    return false;
            }
            return true;
        }

        private static bool UsesSameResolvedAssets(VfxEmitterDefinition before, VfxEmitterDefinition after)
        {
            if (before is null || after is null) return before is null && after is null;
            return string.Equals(before.TexturePath, after.TexturePath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.TextureMultPath, after.TextureMultPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.ParticleColorTexturePath, after.ParticleColorTexturePath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.AlphaErosion?.TexturePath, after.AlphaErosion?.TexturePath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.Distortion?.NormalMapTexturePath, after.Distortion?.NormalMapTexturePath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.Reflection?.TexturePath, after.Reflection?.TexturePath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.PaletteDefinition?.PaletteTexturePath, after.PaletteDefinition?.PaletteTexturePath, StringComparison.OrdinalIgnoreCase) &&
                   before.PrimitiveKind == after.PrimitiveKind &&
                   before.IsMeshPrimitive == after.IsMeshPrimitive &&
                   before.MeshIsSkinned == after.MeshIsSkinned &&
                   string.Equals(before.MeshPath, after.MeshPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.MeshFallbackPath, after.MeshFallbackPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.MeshSkeletonPath, after.MeshSkeletonPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.MeshAnimationPath, after.MeshAnimationPath, StringComparison.OrdinalIgnoreCase) &&
                   SequenceEqual(before.MeshAnimationVariants, after.MeshAnimationVariants, StringComparer.OrdinalIgnoreCase) &&
                   SequenceEqual(before.SubmeshesToDraw, after.SubmeshesToDraw) &&
                   SequenceEqual(before.SubmeshesToDrawAlways, after.SubmeshesToDrawAlways) &&
                   SameEmissionSurface(before.EmissionSurface, after.EmissionSurface);
        }

        private static bool SameEmissionSurface(
            VfxEmissionSurfaceDefinition before,
            VfxEmissionSurfaceDefinition after)
        {
            if (before is null || after is null) return before is null && after is null;
            return before.Kind == after.Kind &&
                   string.Equals(before.MeshPath, after.MeshPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.SkeletonPath, after.SkeletonPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(before.AnimationPath, after.AnimationPath, StringComparison.OrdinalIgnoreCase) &&
                   SequenceEqual(before.Submeshes, after.Submeshes) &&
                   SequenceEqual(before.Joints, after.Joints) &&
                   before.Scale.Equals(after.Scale) &&
                   before.MaxJointWeights == after.MaxJointWeights;
        }

        private static bool SequenceEqual<T>(IReadOnlyList<T> before, IReadOnlyList<T> after)
            => SequenceEqual(before, after, EqualityComparer<T>.Default);

        private static bool SequenceEqual<T>(
            IReadOnlyList<T> before,
            IReadOnlyList<T> after,
            IEqualityComparer<T> comparer)
        {
            before ??= Array.Empty<T>();
            after ??= Array.Empty<T>();
            if (before.Count != after.Count) return false;
            for (int index = 0; index < before.Count; index++)
            {
                if (!comparer.Equals(before[index], after[index])) return false;
            }
            return true;
        }

        internal void RefreshPlaybackGraphResources(
            VfxPlaybackGraphRuntime graph,
            string searchDirectory,
            LogService log,
            VfxOwnerSceneContext ownerSceneContext)
        {
            ArgumentNullException.ThrowIfNull(graph);
            foreach (VfxPlaybackRuntime runtime in graph.ResourceRuntimes)
                PrepareRuntimeResources(runtime, searchDirectory, log, ownerSceneContext, resetResolved: true);

            IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> emissionSurfaces =
                PrepareEmissionSurfaces(graph.ResourceDefinitions, searchDirectory, log);
            graph.SetEmissionSurfaces(emissionSurfaces);
            graph.RefreshResolvedResourceBindings();
        }

        /// <summary>
        /// LTK only exposes a mesh model when the backend located its geometry. Keep the
        /// authored paths in the parsed model, but lower them to the assets available in this
        /// extraction before draw-kind selection and runtime creation.
        /// </summary>
        internal VfxSystemDefinition ResolveMeshAvailability(
            VfxSystemDefinition definition,
            string searchDirectory)
        {
            if (definition is null || string.IsNullOrWhiteSpace(searchDirectory) || definition.Emitters.Count == 0)
                return definition;

            VfxEmitterDefinition[] emitters = null;
            for (int index = 0; index < definition.Emitters.Count; index++)
            {
                VfxEmitterDefinition emitter = definition.Emitters[index];
                if (emitter.PrimitiveKind == VfxPrimitiveKind.AttachedMesh ||
                    string.IsNullOrWhiteSpace(emitter.MeshPath))
                {
                    continue;
                }

                bool meshAvailable;
                VfxEmitterDefinition resolved = emitter;
                if (emitter.MeshIsSkinned)
                {
                    meshAvailable = _resources.ResolvePath(
                        emitter.MeshPath,
                        searchDirectory,
                        VfxMeshFormatSemantics.SkinnedExtensions) is not null;
                    if (!meshAvailable)
                    {
                        string fallback = emitter.MeshFallbackPath;
                        bool fallbackAvailable = !string.IsNullOrWhiteSpace(fallback) &&
                            _resources.ResolvePath(fallback, searchDirectory, VfxMeshFormatSemantics.AuthoredSimpleExtensions) is not null;
                        resolved = emitter with
                        {
                            MeshPath = fallbackAvailable ? fallback : null,
                            MeshSkeletonPath = null,
                            MeshAnimationPath = null,
                            MeshIsSkinned = false
                        };
                    }
                }
                else
                {
                    meshAvailable = _resources.ResolvePath(
                        emitter.MeshPath,
                        searchDirectory,
                        VfxMeshFormatSemantics.AuthoredSimpleExtensions) is not null;
                    if (!meshAvailable)
                        resolved = emitter with { MeshPath = null };
                }

                if (ReferenceEquals(resolved, emitter)) continue;
                emitters ??= definition.Emitters.ToArray();
                emitters[index] = resolved;
            }

            return emitters is null ? definition : definition with { Emitters = emitters };
        }

        public VfxPlaybackGraphRuntime PreparePlaybackGraph(
            VfxSystemDefinition definition,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            Matrix4x4 transform,
            int seed,
            LogService log,
            VfxOwnerSceneContext ownerSceneContext = null)
        {
            var resolvedSystems = new Dictionary<uint, VfxSystemDefinition>();
            if (systems is not null)
            {
                foreach (KeyValuePair<uint, VfxSystemDefinition> pair in systems)
                    resolvedSystems[pair.Key] = ResolveMeshAvailability(pair.Value, searchDirectory);
            }

            VfxSystemDefinition resolvedDefinition = ResolveMeshAvailability(definition, searchDirectory);
            if (resolvedSystems.TryGetValue(definition.PathHash, out VfxSystemDefinition catalogDefinition))
                resolvedDefinition = catalogDefinition;
            else
                resolvedSystems[resolvedDefinition.PathHash] = resolvedDefinition;

            var graph = new VfxPlaybackGraphRuntime(
                resolvedDefinition,
                transform,
                seed,
                resolvedSystems,
                resourceMap,
                (childDefinition, childTransform, childSeed) => PreparePlaybackCore(
                    childDefinition,
                    searchDirectory,
                    childTransform,
                    childSeed,
                    log,
                    ownerSceneContext,
                    applyDefinitionTransform: false));
            IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> emissionSurfaces =
                PrepareEmissionSurfaces(resolvedSystems.Values, searchDirectory, log);
            if (emissionSurfaces.Count > 0)
                graph.SetEmissionSurfaces(emissionSurfaces);
            return graph;
        }

        private IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> PrepareEmissionSurfaces(
            IEnumerable<VfxSystemDefinition> definitions,
            string searchDirectory,
            LogService log)
        {
            var surfaces = new Dictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler>(ReferenceEqualityComparer.Instance);
            foreach (VfxSystemDefinition definition in definitions ?? Array.Empty<VfxSystemDefinition>())
            {
                foreach (VfxEmitterDefinition emitter in definition?.Emitters ?? Array.Empty<VfxEmitterDefinition>())
                {
                    VfxEmissionSurfaceDefinition surface = emitter?.EmissionSurface;
                    if (surface is null) continue;
                    IVfxEmissionSurfaceSampler sampler = PrepareEmissionSurface(surface, searchDirectory, log);
                    if (sampler is not null) surfaces.TryAdd(emitter, sampler);
                }
            }
            return surfaces;
        }

        private IVfxEmissionSurfaceSampler PrepareEmissionSurface(
            VfxEmissionSurfaceDefinition surface,
            string searchDirectory,
            LogService log)
        {
            if (surface.Kind == VfxEmissionSurfaceKind.Skeleton)
            {
                if (string.IsNullOrWhiteSpace(surface.SkeletonPath)) return null;
                VfxAnimatedMesh bind = _resources.ResolveSkeletonAnimation(
                    surface.SkeletonPath,
                    null,
                    searchDirectory,
                    log);
                if (bind is null) return null;
                VfxAnimatedMesh pose = string.IsNullOrWhiteSpace(surface.AnimationPath)
                    ? bind
                    : _resources.ResolveSkeletonAnimation(
                        surface.SkeletonPath,
                        surface.AnimationPath,
                        searchDirectory,
                        log) ?? bind;
                return new VfxSkeletonEmissionSurfaceSampler(pose, surface.Joints, surface.Scale);
            }

            if (string.IsNullOrWhiteSpace(surface.MeshPath)) return null;
            VfxMeshData? mesh = _resources.ResolveMesh(
                surface.MeshPath,
                surface.Submeshes,
                Array.Empty<uint>(),
                searchDirectory);
            if (!mesh.HasValue) return null;

            VfxAnimatedMesh meshPose = null;
            if (!string.IsNullOrWhiteSpace(surface.SkeletonPath))
            {
                VfxAnimatedMesh bind = _resources.ResolveMeshAnimation(
                    surface.MeshPath,
                    surface.SkeletonPath,
                    null,
                    searchDirectory,
                    log);
                if (bind is not null)
                {
                    meshPose = string.IsNullOrWhiteSpace(surface.AnimationPath)
                        ? bind
                        : _resources.ResolveMeshAnimation(
                            surface.MeshPath,
                            surface.SkeletonPath,
                            surface.AnimationPath,
                            searchDirectory,
                            log) ?? bind;
                }
            }
            return new VfxMeshEmissionSurfaceSampler(
                mesh.Value,
                meshPose,
                surface.Scale,
                surface.MaxJointWeights);
        }
        internal BitmapSource ResolveTexture(string authoredPath, string searchDirectory)
            => _resources.ResolveTexture(authoredPath, searchDirectory);

        internal string ResolveAssetPath(string authoredPath, string searchDirectory, string extension)
            => _resources.ResolvePath(authoredPath, searchDirectory, new[] { extension });

        internal VfxMeshData? ResolveMesh(
            string authoredPath,
            string searchDirectory)
            => _resources.ResolveMesh(authoredPath, searchDirectory);

        private string ResolveGraphHashName(uint hash)
        {
            if (_hashResolverService == null || hash == 0u) return null;

            // AnimationGraph Hash values use the BIN value-name table. Do not fall through
            // to entry/field/type catalogs because an equal hash there names a different domain.
            string resolved = _hashResolverService.ResolveBinHash(hash);
            return string.IsNullOrWhiteSpace(resolved) ||
                   resolved.Equals(hash.ToString("x8"), StringComparison.OrdinalIgnoreCase)
                ? null
                : resolved;
        }

        private string ResolveGraphClassName(uint hash)
        {
            if (_hashResolverService == null || hash == 0u) return null;

            // Clip class hashes belong to the BIN type-name table, independently from map keys.
            string resolved = _hashResolverService.ResolveBinType(hash);
            return string.IsNullOrWhiteSpace(resolved) ||
                   resolved.Equals(hash.ToString("x8"), StringComparison.OrdinalIgnoreCase)
                ? null
                : resolved;
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0) return;
            TryDisposeResources();
        }

        private void TryDisposeResources()
        {
            if (Volatile.Read(ref _disposeState) != 1 || !_catalogGate.Wait(0)) return;
            try
            {
                if (Interlocked.CompareExchange(ref _disposeState, 2, 1) == 1)
                    _resources.Dispose();
            }
            finally
            {
                // Pending loads must be able to acquire, reject disposal, and release safely.
                _catalogGate.Release();
            }
        }

        private static string ResolveWadRoot(string skinBinPath)
        {
            string dataMarker = $"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}";
            int idx = skinBinPath.IndexOf(dataMarker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return skinBinPath.Substring(0, idx);

            string assetsMarker = $"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}";
            idx = skinBinPath.IndexOf(assetsMarker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return skinBinPath.Substring(0, idx);

            return string.Empty;
        }

    }
}
