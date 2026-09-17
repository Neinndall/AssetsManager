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
        private static readonly string[] SkinnedMeshExtensions = { ".skn" };
        private static readonly string[] SimpleMeshExtensions = { ".scb", ".tmesh", ".gmesh" };
        private readonly VfxResourceResolver _resources = new();
        private readonly SemaphoreSlim _catalogGate = new(1, 1);
        private int _disposeState;

        public sealed class Bundle
        {
            public string PrimaryBinPath { get; internal set; }
            public Dictionary<uint, VfxSystemDefinition> Systems { get; } = new();
            public Dictionary<uint, uint> ResourceMap { get; } = new();
            public Dictionary<uint, string> SystemSources { get; } = new();
            public Dictionary<uint, AnimationClipDefinition> EventSequences { get; } = new();
            public List<AnimationClipDefinition> Clips { get; } = new();
            public List<VfxIdleEffectDefinition> IdleEffects { get; } = new();
            public VfxOwnerSceneContext OwnerSceneContext { get; set; }
            public List<string> LoadedBins { get; } = new();
            public List<string> MissingDependencies { get; } = new();
            public List<string> AmbiguousDependencies { get; } = new();
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
                var queue = new Queue<string>();

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
                        VfxBinDocument document = VfxGraphParser.ParseDocument(fileBytes);
                        bundle.LoadedBins.Add(Path.GetFullPath(currentBinPath));

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

            foreach (var emitter in runtime.Emitters)
            {
                BitmapSource texture = _resources.ResolveTexture(emitter.Def.TexturePath, searchDirectory);
                if (texture != null)
                {
                    emitter.PendingTexture = texture;
                    if (emitter.Def.UseTextureAspect)
                    {
                        float cellWidth = texture.PixelWidth / Math.Max(1f, emitter.Def.TexDiv.X);
                        float cellHeight = texture.PixelHeight / Math.Max(1f, emitter.Def.TexDiv.Y);
                        if (cellHeight > 0f)
                            emitter.SpriteAspect = Math.Clamp(cellWidth / cellHeight, 0.05f, 20f);
                    }
                }
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

                BitmapSource gradient = _resources.ResolveTexture(
                    emitter.Def.ParticleColorTexturePath,
                    searchDirectory);
                if (gradient != null) emitter.PendingColorGradient = gradient;

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
                }
                else if (emitter.Def.IsMeshPrimitive)
                {
                    if (!string.IsNullOrWhiteSpace(emitter.Def.MeshPath))
                    {
                        emitter.PendingMesh = _resources.ResolveMesh(
                            emitter.Def.MeshPath,
                            emitter.Def.SubmeshesToDraw,
                            emitter.Def.SubmeshesToDrawAlways,
                            searchDirectory);
                        if (emitter.PendingMesh != null && !string.IsNullOrWhiteSpace(emitter.Def.MeshAnimationPath))
                        {
                            emitter.MeshAnimation = _resources.ResolveMeshAnimation(
                                emitter.Def.MeshPath,
                                emitter.Def.MeshSkeletonPath,
                                emitter.Def.MeshAnimationPath,
                                searchDirectory,
                                log);
                        }
                    }
                }
            }

            runtime.ApplyRenderOrder();

            return runtime;
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
                        SkinnedMeshExtensions) is not null;
                    if (!meshAvailable)
                    {
                        string fallback = emitter.MeshFallbackPath;
                        bool fallbackAvailable = !string.IsNullOrWhiteSpace(fallback) &&
                            _resources.ResolvePath(fallback, searchDirectory, SimpleMeshExtensions) is not null;
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
                        SimpleMeshExtensions) is not null;
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

            return new VfxPlaybackGraphRuntime(
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
        }

        internal BitmapSource ResolveTexture(string authoredPath, string searchDirectory)
            => _resources.ResolveTexture(authoredPath, searchDirectory);

        internal string ResolveAssetPath(string authoredPath, string searchDirectory, string extension)
            => _resources.ResolvePath(authoredPath, searchDirectory, new[] { extension });

        internal VfxMeshData? ResolveMesh(
            string authoredPath,
            string searchDirectory)
            => _resources.ResolveMesh(authoredPath, searchDirectory);

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
