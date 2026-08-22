using System;
using System.Collections.Generic;
using System.IO;
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
        private readonly VfxResourceResolver _resources = new();
        private readonly SemaphoreSlim _catalogGate = new(1, 1);
        private int _disposeState;

        public sealed class Bundle
        {
            public Dictionary<uint, VfxSystemDefinition> Systems { get; } = new();
            public Dictionary<uint, uint> ResourceMap { get; } = new();
            public Dictionary<uint, string> SystemSources { get; } = new();
            public Dictionary<uint, VfxEventSequenceDefinition> EventSequences { get; } = new();
            public VfxOwnerSceneContext OwnerSceneContext { get; set; }
            public List<string> LoadedBins { get; } = new();
            public List<string> MissingDependencies { get; } = new();
            public List<string> AmbiguousDependencies { get; } = new();
        }

        public Bundle Load(string skinBinPath, LogService log)
        {
            _catalogGate.Wait();
            try
            {
                return LoadCore(skinBinPath, log, CancellationToken.None);
            }
            finally
            {
                _catalogGate.Release();
            }
        }

        private Bundle LoadCore(string skinBinPath, LogService log, CancellationToken cancellationToken)
        {
            var bundle = new Bundle();
            if (string.IsNullOrEmpty(skinBinPath) || !File.Exists(skinBinPath)) return bundle;

            try
            {
                string charFolder = Path.GetDirectoryName(Path.GetDirectoryName(skinBinPath));

                string wadRoot = ResolveWadRoot(skinBinPath);
                string searchFolder = charFolder;
                if (!string.IsNullOrEmpty(wadRoot)) searchFolder = wadRoot;

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<string>();

                void Enqueue(string p)
                {
                    if (File.Exists(p) && visited.Add(Path.GetFullPath(p)))
                        queue.Enqueue(p);
                }

                Enqueue(skinBinPath);

                while (queue.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string currentBinPath = queue.Dequeue();
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
                        foreach (var kv in document.ResourceMap)
                            bundle.ResourceMap.TryAdd(kv.Key, kv.Value);
                        foreach (VfxEventSequenceDefinition sequence in document.EventSequences)
                            bundle.EventSequences.TryAdd(sequence.OwnerPathHash, sequence);
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

        public async Task<Bundle> LoadAsync(string skinBinPath, LogService log, CancellationToken cancellationToken = default)
        {
            await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    () => LoadCore(skinBinPath, log, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _catalogGate.Release();
            }
        }

        public VfxPlaybackRuntime PreparePlayback(
            VfxSystemDefinition definition,
            string searchDirectory,
            Matrix4x4 transform,
            int seed,
            LogService log,
            VfxOwnerSceneContext ownerSceneContext = null)
        {
            ArgumentNullException.ThrowIfNull(definition);
            var runtime = new VfxPlaybackRuntime(seed);
            runtime.SetSystem(definition, definition.Transform.GetValueOrDefault(Matrix4x4.Identity) * transform);
            var alphaSemantics = new Dictionary<BitmapSource, bool>(ReferenceEqualityComparer.Instance);

            foreach (var emitter in runtime.Emitters)
            {
                BitmapSource texture = _resources.ResolveTexture(emitter.Def.TexturePath, searchDirectory);
                if (texture != null)
                {
                    emitter.PendingTexture = texture;
                    if (!alphaSemantics.TryGetValue(texture, out bool isLegacyOpaqueRgbMask))
                    {
                        isLegacyOpaqueRgbMask = VfxTextureAlphaSemantics.IsLegacyOpaqueRgbMask(texture);
                        alphaSemantics[texture] = isLegacyOpaqueRgbMask;
                    }
                    emitter.DeriveAlphaFromRgb = VfxTextureAlphaSemantics.ShouldDeriveAlphaFromRgb(
                        isLegacyOpaqueRgbMask,
                        emitter.Def.BlendMode,
                        emitter.Def.PrimitiveKind);
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
                emitter.PendingReflectionTexture = _resources.ResolveTexture(
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
                            emitter.Def.AttachedSubmeshHashes,
                            searchDirectory,
                            ownerSceneContext.SkinScale);
                    }
                }
                else if (emitter.Def.IsMeshPrimitive)
                {
                    if (!string.IsNullOrWhiteSpace(emitter.Def.MeshPath))
                    {
                        emitter.PendingMesh = _resources.ResolveMesh(emitter.Def.MeshPath, searchDirectory);
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
            return new VfxPlaybackGraphRuntime(
                definition,
                transform,
                seed,
                systems,
                resourceMap,
                (childDefinition, childTransform, childSeed) => PreparePlayback(
                    childDefinition,
                    searchDirectory,
                    childTransform,
                    childSeed,
                    log,
                    ownerSceneContext));
        }

        internal BitmapSource ResolveTexture(string authoredPath, string searchDirectory)
            => _resources.ResolveTexture(authoredPath, searchDirectory);

        internal (float[] Positions, float[] Uvs, float[] Colors, uint[] Indices)? ResolveMesh(
            string authoredPath,
            string searchDirectory)
            => _resources.ResolveMesh(authoredPath, searchDirectory);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;

            try
            {
                _resources.Dispose();
            }
            finally
            {
                _catalogGate.Dispose();
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
