using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Runtime
{
    /// <summary>
    /// Scene-scoped resource overlay for MAP VFX. Referenced assets are materialized once from
    /// project/WAD into a temporary virtual-path tree so the audited VFX resolver can be reused unchanged.
    /// </summary>
    internal sealed class MapParticleResourceContext : IDisposable
    {
        private static readonly string[] TextureExtensions = { ".tex", ".dds" };
        private static readonly string[] MeshExtensions = { ".scb", ".skn", ".tmesh", ".gmesh" };
        private static readonly string[] SkeletonExtensions = { ".skl" };
        private static readonly string[] AnimationExtensions = { ".anm" };
        private const int MaximumConcurrentCopies = 4;

        private readonly MapAssetResolver _assetResolver;
        private readonly VfxLoadingService _loadingService;
        private readonly LogService _logService;
        private bool _disposed;

        private MapParticleResourceContext(
            MapAssetResolver assetResolver,
            HashResolverService hashResolver,
            LogService logService,
            string searchDirectory)
        {
            _assetResolver = assetResolver;
            _loadingService = new VfxLoadingService(hashResolver);
            _logService = logService;
            SearchDirectory = searchDirectory;
        }

        internal string SearchDirectory { get; }

        internal static async Task<MapParticleResourceContext> CreateAsync(
            MapParticleSystemCatalog catalog,
            string projectRoot,
            MapAssetResolver assetResolver,
            HashResolverService hashResolver,
            LogService logService,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(assetResolver);

            string tempRoot = Path.Combine(
                Path.GetTempPath(),
                "AssetsManager",
                "MapVfx",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var context = new MapParticleResourceContext(
                assetResolver,
                hashResolver,
                logService,
                tempRoot);
            try
            {
                await context.MaterializeAsync(catalog, projectRoot, cancellationToken);
                return context;
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }

        internal IReadOnlyList<MapParticleRuntime> CreateRuntimes(MapParticleSystemCatalog catalog)
        {
            ThrowIfDisposed();
            return MapParticleRuntime.CreateAll(
                catalog,
                (definition, worldTransform, seed) => _loadingService.PreparePlaybackAtWorldTransform(
                    definition,
                    SearchDirectory,
                    worldTransform,
                    seed,
                    _logService));
        }

        internal static IReadOnlyList<ResourceRequest> CollectRequests(MapParticleSystemCatalog catalog)
        {
            if (catalog?.Systems == null || catalog.Systems.Count == 0)
                return Array.Empty<ResourceRequest>();

            var requests = new Dictionary<string, ResourceRequest>(StringComparer.OrdinalIgnoreCase);
            foreach (VfxSystemDefinition system in catalog.Systems.Values)
            {
                if (system?.Emitters == null)
                    continue;
                foreach (VfxEmitterDefinition emitter in system.Emitters)
                {
                    if (emitter == null)
                        continue;
                    Add(requests, emitter.TexturePath, TextureExtensions);
                    Add(requests, emitter.TextureMultPath, TextureExtensions);
                    Add(requests, emitter.Distortion?.NormalMapTexturePath, TextureExtensions);
                    Add(requests, emitter.AlphaErosion?.TexturePath, TextureExtensions);
                    Add(requests, emitter.Reflection?.TexturePath, TextureExtensions);
                    Add(requests, emitter.PaletteDefinition?.PaletteTexturePath, TextureExtensions);
                    Add(requests, emitter.ParticleColorTexturePath, TextureExtensions);
                    Add(requests, emitter.MeshPath, MeshExtensions);
                    Add(requests, emitter.MeshFallbackPath, MeshExtensions);
                    Add(requests, emitter.MeshSkeletonPath, SkeletonExtensions);
                    Add(requests, emitter.MeshAnimationPath, AnimationExtensions);
                }
            }
            return requests.Values.ToArray();
        }

        internal static MapAssetReference ReferenceFor(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return null;

            string normalized = candidate.Replace('\\', '/').TrimStart('/');
            string fileName = Path.GetFileName(normalized);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            bool pathless = normalized.Equals(fileName, StringComparison.OrdinalIgnoreCase);
            if (pathless && stem.Length == 16 && ulong.TryParse(
                    stem,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out ulong hash))
            {
                return new MapAssetReference(null, hash);
            }

            return new MapAssetReference(normalized, 0);
        }

        private async Task MaterializeAsync(
            MapParticleSystemCatalog catalog,
            string projectRoot,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ResourceRequest> requests = CollectRequests(catalog);
            if (requests.Count == 0)
                return;

            var candidates = new List<Candidate>();
            foreach (ResourceRequest request in requests)
            {
                foreach (string candidatePath in CandidatePaths(request.AuthoredPath, request.Extensions))
                {
                    MapAssetReference reference = ReferenceFor(candidatePath);
                    if (reference?.IsEmpty == false)
                        candidates.Add(new Candidate(request.AuthoredPath, candidatePath, reference));
                }
            }

            MapAssetReference[] uniqueReferences = candidates
                .Select(candidate => candidate.Reference)
                .Distinct()
                .ToArray();
            IReadOnlyDictionary<MapAssetReference, MapResolvedAsset> resolved =
                await _assetResolver.ResolveReferencesAsync(uniqueReferences, projectRoot, cancellationToken);

            var chosen = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            foreach (Candidate candidate in candidates)
            {
                if (chosen.ContainsKey(candidate.AuthoredPath) || !resolved.ContainsKey(candidate.Reference))
                    continue;
                chosen[candidate.AuthoredPath] = candidate;
            }

            using var gate = new SemaphoreSlim(MaximumConcurrentCopies, MaximumConcurrentCopies);
            Candidate[] copyCandidates = chosen.Values
                .GroupBy(candidate => candidate.CandidatePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            Task[] copies = copyCandidates.Select(async candidate =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await gate.WaitAsync(cancellationToken);
                try
                {
                    MapResolvedAsset asset = resolved[candidate.Reference];
                    byte[] bytes = await _assetResolver.ReadBytesAsync(asset, cancellationToken);
                    if (bytes == null || bytes.Length == 0)
                        return;
                    string destination = SafeDestination(SearchDirectory, candidate.CandidatePath);
                    if (destination == null)
                        return;
                    string directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);
                    await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(copies);
        }

        private static IEnumerable<string> CandidatePaths(
            string authoredPath,
            IReadOnlyList<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(authoredPath))
                yield break;

            string normalized = authoredPath.Replace('\\', '/').TrimStart('/');
            string authoredExtension = Path.GetExtension(normalized);
            bool syntheticHashAsset = IsSyntheticHashAsset(normalized);
            if (!string.IsNullOrWhiteSpace(authoredExtension) && !syntheticHashAsset)
            {
                if ((extensions ?? Array.Empty<string>()).Contains(
                        authoredExtension,
                        StringComparer.OrdinalIgnoreCase))
                {
                    yield return normalized;
                }
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(authoredExtension))
                yield return normalized;

            foreach (string extension in extensions ?? Array.Empty<string>())
            {
                string candidate = Path.ChangeExtension(normalized, extension);
                if (!candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    yield return candidate;
            }
        }

        private static void Add(
            IDictionary<string, ResourceRequest> requests,
            string authoredPath,
            IReadOnlyList<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(authoredPath))
                return;

            if (requests.TryGetValue(authoredPath, out ResourceRequest existing))
            {
                string[] merged = (existing.Extensions ?? Array.Empty<string>())
                    .Concat(extensions ?? Array.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                requests[authoredPath] = existing with { Extensions = merged };
                return;
            }

            requests[authoredPath] = new ResourceRequest(
                authoredPath,
                (extensions ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private static bool IsSyntheticHashAsset(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path ?? string.Empty);
            if (stem.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                stem = stem[2..];
            if (stem.Length is not (8 or 16))
                return false;
            return ulong.TryParse(
                stem,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out _);
        }

        private static string SafeDestination(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath))
                return null;

            string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
                return null;

            string rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string destination = Path.GetFullPath(Path.Combine(rootFull, normalized));
            return destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                ? destination
                : null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MapParticleResourceContext));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _loadingService.Dispose();
            try
            {
                if (Directory.Exists(SearchDirectory))
                    Directory.Delete(SearchDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        internal sealed record ResourceRequest(string AuthoredPath, IReadOnlyList<string> Extensions);
        private sealed record Candidate(string AuthoredPath, string CandidatePath, MapAssetReference Reference);
    }
}
