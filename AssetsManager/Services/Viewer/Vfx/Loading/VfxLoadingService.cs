using AssetsManager.Services.Viewer.Parsing;
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
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Loading
{
    /// <summary>
    /// Loads a model's complete effect catalog and prepares every referenced emitter resource.
    /// </summary>
    public sealed class VfxLoadingService : IDisposable
    {
        private readonly VfxResourceResolver _resources = new();
        private const string ShaderDefinitionsPath = "data/shaders/shaders.bin";
        private static readonly uint SpellObjectClass = Fnv1a.HashLower("SpellObject");
        private readonly HashResolverService _hashResolverService;
        private readonly MapAssetResolver _assetResolver;
        private readonly SemaphoreSlim _catalogGate = new(1, 1);
        private (string Root, BinTree Tree) _shaderDefinitions;
        private int _disposeState;

        public VfxLoadingService(
            HashResolverService hashResolverService = null,
            WadContentProvider wadContentProvider = null,
            AppSettings appSettings = null)
        {
            _hashResolverService = hashResolverService;
            _assetResolver = wadContentProvider != null && appSettings != null
                ? new MapAssetResolver(wadContentProvider, appSettings)
                : null;
        }

        /// <summary>
        /// Resolves a BIN object path hash through the same catalog used by the generic BIN tools.
        /// 3D Studio uses this only for semantic browser discovery (for example Character Spells).
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
            internal Dictionary<uint, VfxSpellPreview> SpellPreviews { get; private set; } = new();
            public Dictionary<uint, AnimationClipDefinition> EventSequences { get; private set; } = new();
            public List<AnimationClipDefinition> Clips { get; private set; } = new();
            public List<AnimationGraphDefinition> AnimationGraphs { get; private set; } = new();
            public List<VfxIdleEffectDefinition> IdleEffects { get; private set; } = new();
            public IReadOnlyList<CharacterFormDefinition> CharacterForms { get; internal set; } =
                Array.Empty<CharacterFormDefinition>();
            public VfxOwnerSceneContext OwnerSceneContext { get; set; }
            public List<string> LoadedBins { get; private set; } = new();
            public List<string> MissingDependencies { get; private set; } = new();
            public List<string> AmbiguousDependencies { get; private set; } = new();
            internal int? CharacterGearIndex { get; private set; }

            public Bundle()
            {
            }

            private Bundle(Bundle source, CharacterFormDefinition form)
            {
                PrimaryBinPath = source.PrimaryBinPath;
                Systems = source.Systems;
                SpellPreviews = source.SpellPreviews;
                EventSequences = source.EventSequences;
                Clips = source.Clips;
                AnimationGraphs = source.AnimationGraphs;
                LoadedBins = source.LoadedBins;
                MissingDependencies = source.MissingDependencies;
                AmbiguousDependencies = source.AmbiguousDependencies;
                CharacterForms = source.CharacterForms;
                // A reloaded form renders at its GearData skinScale, so bone anchors must use it too.
                OwnerSceneContext = form.ReloadsModel && form.SkinScale is > 0f && source.OwnerSceneContext != null
                    ? source.OwnerSceneContext with { SkinScale = form.SkinScale.Value }
                    : source.OwnerSceneContext;

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

            internal Bundle CreateCharacterPlaybackView(CharacterFormDefinition form)
                => form == null ? this : new Bundle(this, form);
        }

        /// <summary>
        /// The global shader definitions (data/shaders/shaders.bin) from the project or the installed game, read
        /// once per project root; null without an installation to read them from.
        /// </summary>
        private BinTree LoadShaderDefinitions(string projectRoot, LogService log, CancellationToken cancellationToken)
        {
            if (_assetResolver == null) return null;
            if (_shaderDefinitions.Tree != null &&
                string.Equals(_shaderDefinitions.Root, projectRoot, StringComparison.OrdinalIgnoreCase))
                return _shaderDefinitions.Tree;
            try
            {
                MapResolvedAsset asset = _assetResolver.ResolveVirtualAsync(ShaderDefinitionsPath, projectRoot, cancellationToken)
                    .GetAwaiter().GetResult();
                if (asset == null) return null;
                using Stream stream = _assetResolver.OpenReadAsync(asset, cancellationToken).GetAwaiter().GetResult();
                if (stream == null) return null;
                BinTree tree = new BinTree(stream);
                _shaderDefinitions = (projectRoot, tree);
                return tree;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.LogDebug($"Could not read the shader definitions for VFX materials: {ex.Message}");
                return null;
            }
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
                var characterFormDocuments = new List<CharacterFormDocumentData>();
                var loadedTrees = new List<BinTree>();
                // VFX custom materials link CustomShaderDefs the global shader BIN declares; without it a
                // material keeps its textures but no game program, and draws with the stock particle shader.
                BinTree[] shaderTrees = LoadShaderDefinitions(searchFolder, log, cancellationToken) is { } shaders
                    ? new[] { shaders }
                    : null;

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
                        if (openedLinkedBins >= BinDocumentClosureLoader.MaximumLinkedBins) break;
                        openedLinkedBins++;
                    }
                    try
                    {
                        if (!File.Exists(currentBinPath)) continue;
                        BinTree tree = VfxGraphParser.ParseTree(File.ReadAllBytes(currentBinPath));
                        loadedTrees.Add(tree);
                        foreach (BinTreeObject spell in tree.Objects.Values.Where(item =>
                                     item.ClassHash == SpellObjectClass))
                            bundle.SpellPreviews.TryAdd(spell.PathHash, VfxSpellPreviewReader.Read(spell));
                        VfxBinDocument document = VfxGraphParser.ParseDocument(
                            tree,
                            ResolveGraphHashName,
                            ResolveGraphClassName,
                            _hashResolverService == null ? null : _hashResolverService.ResolveHash,
                            _hashResolverService == null ? null : _hashResolverService.ResolveBinEntry,
                            shaderTrees);
                        bundle.LoadedBins.Add(Path.GetFullPath(currentBinPath));
                        if (document.CharacterFormData != null)
                            characterFormDocuments.Add(document.CharacterFormData);

                        foreach (var kv in document.Systems)
                        {
                            bundle.Systems.TryAdd(kv.Key, kv.Value);
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

                // A system's custom material may be declared by any BIN it links rather than its own.
                foreach (uint systemHash in bundle.Systems.Keys.ToArray())
                {
                    bundle.Systems[systemHash] = VfxGraphParser.ResolveLinkedCustomMaterials(
                        bundle.Systems[systemHash],
                        loadedTrees,
                        _hashResolverService == null ? null : _hashResolverService.ResolveHash,
                        _hashResolverService == null ? null : _hashResolverService.ResolveBinEntry,
                        shaderTrees);
                }

                bundle.CharacterForms = CharacterFormParser.Resolve(
                    characterFormDocuments, ResolveBinEntryPath, bundle.OwnerSceneContext);
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
                ownerSceneContext);

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
                ownerSceneContext);

        private VfxPlaybackRuntime PreparePlaybackCore(
            VfxSystemDefinition definition,
            string searchDirectory,
            Matrix4x4 transform,
            int seed,
            LogService log,
            VfxOwnerSceneContext ownerSceneContext)
        {
            ArgumentNullException.ThrowIfNull(definition);
            definition = ResolveMeshAvailability(definition, searchDirectory);
            var runtime = new VfxPlaybackRuntime(seed);
            runtime.SetSystem(definition, transform);

            PrepareRuntimeResources(runtime, searchDirectory, log, ownerSceneContext);

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
            VfxOwnerSceneContext ownerSceneContext)
        {
            if (runtime?.Emitters is null) return;

            foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
            {
                if (emitter.Def.CustomMaterial?.Program?.Passes != null)
                    foreach (var pass in emitter.Def.CustomMaterial.Program.Passes)
                        foreach (var texture in pass.Textures)
                        {
                            string path = texture.Texture?.VirtualPath;
                            if (string.IsNullOrWhiteSpace(path) && texture.Texture?.PathHash > 0)
                                path = texture.Texture.PathHash.ToString("x16");
                            if (!string.IsNullOrWhiteSpace(path))
                                emitter.PendingProgramTextures[path] = _resources.ResolveMaterialTexture(path, searchDirectory);
                        }
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
                    ownerSceneContext));
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
                    IVfxEmissionSurfaceSampler sampler = surface is null ? null : PrepareEmissionSurface(surface, searchDirectory, log);
                    IVfxEmissionSurfaceSampler staticSampler = null;
                    if (emitter?.EmissionMesh is { } emissionMesh &&
                        _resources.ResolveMesh(emissionMesh.MeshPath, searchDirectory) is { } staticMesh)
                        staticSampler = new VfxStaticEmissionMeshSampler(staticMesh, emissionMesh.Scale);
                    if (staticSampler is not null) surfaces.TryAdd(emitter, new VfxEmitterEmissionSampler(staticSampler, sampler));
                    else if (sampler is not null) surfaces.TryAdd(emitter, sampler);
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
