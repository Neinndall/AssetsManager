using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Services.Viewer.Loading
{
    /// <summary>
    /// Resolves and decodes one map character skin once, including its dependency BINs,
    /// shared SKN/SKL, authored material state and full-resolution textures.
    /// </summary>
    internal sealed class MapCharacterLoadingService
    {
        private const string ShadersPath = "data/shaders/shaders.bin";
        // LTK bounds breadth-first linked BIN walks to 32 files beyond the primary skin document.
        private const int MaximumLinkedBins = 32;
        private const int MaxConcurrentTextureLoads = 4;

        private readonly MapAssetResolver _assetResolver;
        private readonly BinDocumentClosureLoader _binClosureLoader;
        private readonly MapCharacterSkinParser _skinParser;
        private readonly MapCharacterMeshDecoder _meshDecoder;
        private readonly HashResolverService _hashResolver;
        private readonly LogService _logService;

        public MapCharacterLoadingService(
            MapAssetResolver assetResolver,
            MapCharacterSkinParser skinParser,
            MapCharacterMeshDecoder meshDecoder,
            HashResolverService hashResolver,
            LogService logService)
        {
            _assetResolver = assetResolver;
            _binClosureLoader = new BinDocumentClosureLoader(assetResolver);
            _skinParser = skinParser;
            _meshDecoder = meshDecoder;
            _hashResolver = hashResolver;
            _logService = logService;
        }

        public async Task<MapCharacterAssetData> LoadAsync(
            string skinPath,
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(skinPath))
                return null;

            string skinFile = MapCharacterSemantics.SkinFile(skinPath);
            MapResolvedAsset skinAsset = await _assetResolver.ResolveReferenceAsync(
                new MapAssetReference(skinFile, 0),
                projectRoot,
                cancellationToken);
            if (skinAsset == null)
                return null;

            List<BinTree> documents = await _binClosureLoader.LoadAsync(
                skinAsset,
                projectRoot,
                MaximumLinkedBins,
                cancellationToken,
                (asset, ex) => _logService?.LogWarning(
                    $"MAP character BIN unavailable '{asset?.VirtualPath}': {ex.Message}"));
            if (documents.Count == 0)
                return null;

            BinTree primary = documents[0];
            MapCharacterSkinData skin = _skinParser.Parse(primary, skinPath);
            if (skin == null || skin.Mesh?.IsEmpty != false || skin.Skeleton?.IsEmpty != false)
                return null;

            Task<MapResolvedAsset> meshResolve = _assetResolver.ResolveReferenceAsync(
                skin.Mesh,
                projectRoot,
                cancellationToken);
            Task<MapResolvedAsset> skeletonResolve = _assetResolver.ResolveReferenceAsync(
                skin.Skeleton,
                projectRoot,
                cancellationToken);
            await Task.WhenAll(meshResolve, skeletonResolve);
            MapResolvedAsset meshAsset = await meshResolve;
            MapResolvedAsset skeletonAsset = await skeletonResolve;
            if (meshAsset == null || skeletonAsset == null)
                return null;

            MapCharacterMeshData mesh;
            RigResource skeleton;
            try
            {
                await using (Stream stream = await _assetResolver.OpenReadAsync(meshAsset, cancellationToken))
                {
                    if (stream == null) return null;
                    mesh = _meshDecoder.Decode(stream);
                }

                await using (Stream stream = await _assetResolver.OpenReadAsync(skeletonAsset, cancellationToken))
                {
                    if (stream == null) return null;
                    skeleton = new RigResource(stream);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService?.LogError(ex, $"Failed to decode MAP character skin '{skinPath}'.");
                return null;
            }

            if (skeleton.Joints.Count == 0 || skeleton.Influences.Count == 0)
                return null;

            BinTree shaders = await LoadSingleDocumentAsync(
                new MapAssetReference(ShadersPath, 0),
                projectRoot,
                cancellationToken);
            IEnumerable<BinTree> shaderTrees = shaders == null
                ? documents
                : documents.Append(shaders);
            string targetSknPath = skin.Mesh.VirtualPath;
            Func<ulong, string> wadChunkPathResolver = _hashResolver == null
                ? null
                : _hashResolver.ResolveHash;
            Func<uint, string> binEntryResolver = _hashResolver == null
                ? null
                : _hashResolver.ResolveBinEntry;
            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(
                documents,
                shaderTrees,
                wadChunkPathResolver,
                binEntryResolver,
                targetSknPath);

            IReadOnlyDictionary<string, BitmapSource> textures = await LoadTexturesAsync(
                metadata.ReferencedTexturePaths,
                projectRoot,
                cancellationToken);
            SknMaterialTextureResolution materials = SknMaterialTextureResolver.Resolve(
                metadata,
                textures.Keys,
                includeSpecializedEffects: false);
            Func<uint, string> graphHashNameResolver = _hashResolver == null
                ? hash => hash.ToString("x8")
                : _hashResolver.ResolveBinHashGeneral;
            Func<uint, string> graphClassNameResolver = _hashResolver == null
                ? hash => hash.ToString("x8")
                : _hashResolver.ResolveBinType;
            AnimationGraphDefinition graph = AnimationGraphReader.Read(
                documents,
                skin.AnimationGraphHash,
                graphHashNameResolver,
                graphClassNameResolver);
            MapCharacterVfxCatalog vfx = BuildVfxCatalog(
                documents,
                graphHashNameResolver,
                graphClassNameResolver,
                wadChunkPathResolver,
                binEntryResolver);

            return new MapCharacterAssetData(
                skin with
                {
                    Scale = materials.SkinScale,
                    HiddenSubmeshes = materials.InitialHiddenSubmeshes
                },
                mesh,
                skeleton,
                materials,
                textures,
                graph,
                vfx);
        }

        internal static MapCharacterVfxCatalog BuildVfxCatalog(
            IReadOnlyList<BinTree> documents,
            Func<uint, string> graphHashNameResolver,
            Func<uint, string> graphClassNameResolver,
            Func<ulong, string> wadChunkPathResolver,
            Func<uint, string> binEntryResolver)
        {
            if (documents == null || documents.Count == 0)
                return MapCharacterVfxCatalog.Empty;

            var systems = new Dictionary<uint, VfxSystemDefinition>();
            var resourceMap = new Dictionary<uint, uint>();
            var idleEffects = new List<VfxIdleEffectDefinition>();
            VfxOwnerSceneContext ownerSceneContext = null;

            for (int index = 0; index < documents.Count; index++)
            {
                BinTree tree = documents[index];
                if (tree == null) continue;

                VfxBinDocument parsed = VfxGraphParser.ParseDocument(
                    tree,
                    graphHashNameResolver,
                    graphClassNameResolver,
                    wadChunkPathResolver,
                    binEntryResolver);
                foreach ((uint hash, VfxSystemDefinition system) in parsed.Systems)
                    systems.TryAdd(hash, system);

                // Clip effect keys are scoped to the owner skin's resource resolver. Linked VFX
                // definitions retain their own document-local ResourceMap in VfxSystemDefinition.
                if (index == 0)
                {
                    foreach ((uint key, uint systemHash) in parsed.SkinResourceMap)
                        resourceMap.TryAdd(key, systemHash);
                    foreach (VfxIdleEffectDefinition idle in parsed.IdleEffects ?? Array.Empty<VfxIdleEffectDefinition>())
                        idleEffects.Add(idle);
                }

                ownerSceneContext ??= parsed.OwnerSceneContext;
            }

            return new MapCharacterVfxCatalog(
                systems,
                resourceMap,
                idleEffects,
                ownerSceneContext);
        }

        private async Task<BinTree> LoadSingleDocumentAsync(
            MapAssetReference reference,
            string projectRoot,
            CancellationToken cancellationToken)
        {
            MapResolvedAsset asset = await _assetResolver.ResolveReferenceAsync(
                reference,
                projectRoot,
                cancellationToken);
            return asset == null ? null : await ReadDocumentAsync(asset, cancellationToken);
        }

        private async Task<BinTree> ReadDocumentAsync(
            MapResolvedAsset asset,
            CancellationToken cancellationToken)
        {
            try
            {
                await using Stream stream = await _assetResolver.OpenReadAsync(asset, cancellationToken);
                return stream == null ? null : new BinTree(stream);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService?.LogWarning($"MAP character BIN unavailable '{asset?.VirtualPath}': {ex.Message}");
                return null;
            }
        }

        private async Task<IReadOnlyDictionary<string, BitmapSource>> LoadTexturesAsync(
            IEnumerable<string> textureKeys,
            string projectRoot,
            CancellationToken cancellationToken)
        {
            string[] keys = (textureKeys ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (keys.Length == 0)
                return new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

            var keyedReferences = keys
                .Select(key => (Key: key, Reference: ReferenceFromAuthoredTexture(key)))
                .Where(item => item.Reference?.IsEmpty == false)
                .ToArray();
            IReadOnlyDictionary<MapAssetReference, MapResolvedAsset> resolved =
                await _assetResolver.ResolveReferencesAsync(
                    keyedReferences.Select(item => item.Reference),
                    projectRoot,
                    cancellationToken);

            var loaded = new ConcurrentDictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            using var gate = new SemaphoreSlim(MaxConcurrentTextureLoads, MaxConcurrentTextureLoads);
            Task[] tasks = keyedReferences
                .GroupBy(item => item.Reference)
                .Where(group => resolved.ContainsKey(group.Key))
                .Select(async group =>
                {
                    MapAssetReference reference = group.Key;
                    MapResolvedAsset asset = resolved[reference];
                    string[] aliases = group.Select(item => item.Key).ToArray();

                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        await using Stream stream = await _assetResolver.OpenReadAsync(asset, cancellationToken);
                        if (stream == null)
                            return;
                        string extension = MapTextureLoadingService.DetectTextureExtension(
                            stream,
                            reference.VirtualPath ?? asset.VirtualPath);
                        BitmapSource bitmap = await Task.Run(
                            () => TextureUtils.LoadViewerTexture(stream, extension),
                            cancellationToken);
                        if (bitmap == null)
                            return;

                        foreach (string alias in aliases)
                            loaded[alias] = bitmap;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logService?.LogDebug(
                            $"MAP structure texture unavailable '{aliases[0]}': {ex.Message}");
                    }
                    finally
                    {
                        gate.Release();
                    }
                })
                .ToArray();

            await Task.WhenAll(tasks);
            cancellationToken.ThrowIfCancellationRequested();
            return new Dictionary<string, BitmapSource>(loaded, StringComparer.OrdinalIgnoreCase);
        }

        internal static MapAssetReference ReferenceFromAuthoredTexture(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string trimmed = value.Trim();
            if (trimmed.Length == 16 && ulong.TryParse(
                    trimmed,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out ulong hash))
            {
                return new MapAssetReference(null, hash);
            }

            return new MapAssetReference(trimmed, 0);
        }

    }
}
