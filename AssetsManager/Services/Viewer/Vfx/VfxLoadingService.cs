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
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Loads a model's complete effect catalog and prepares every referenced emitter resource.
    /// </summary>
    public sealed class VfxLoadingService
    {
        private readonly VfxResourceResolver _resources = new();
        private readonly SemaphoreSlim _catalogGate = new(1, 1);

        public sealed class Bundle
        {
            public Dictionary<uint, VfxSystemDefinition> Systems { get; } = new();
            public Dictionary<uint, uint> ResourceMap { get; } = new();
            public Dictionary<uint, string> SystemSources { get; } = new();
            public List<string> LoadedBins { get; } = new();
            public List<string> MissingDependencies { get; } = new();
            public List<string> AmbiguousDependencies { get; } = new();
        }

        public Bundle Load(string skinBinPath, LogService log)
            => Load(skinBinPath, log, CancellationToken.None);

        private Bundle Load(string skinBinPath, LogService log, CancellationToken cancellationToken)
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

                string charName = Path.GetFileName(charFolder);
                string skinName = Path.GetFileNameWithoutExtension(skinBinPath);
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

                        string currentDir = Path.GetDirectoryName(currentBinPath);
                        foreach (var dep in document.Dependencies)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (string.IsNullOrEmpty(dep)) continue;
                            string normalizedDep = dep.Replace('/', Path.DirectorySeparatorChar);
                            string relativeDepPath = dep
                                .Replace("DATA/", "", StringComparison.OrdinalIgnoreCase)
                                .Replace("data/", "", StringComparison.OrdinalIgnoreCase)
                                .Replace('/', Path.DirectorySeparatorChar);

                            bool enqueued = false;
                            foreach (var root in new[] { wadRoot, searchFolder, currentDir })
                            {
                                if (string.IsNullOrEmpty(root)) continue;

                                string p1 = Path.Combine(root, normalizedDep);
                                string p2 = Path.Combine(root, "data", relativeDepPath);
                                string p3 = Path.Combine(root, relativeDepPath);

                                if (File.Exists(p1)) { Enqueue(p1); enqueued = true; break; }
                                if (File.Exists(p2)) { Enqueue(p2); enqueued = true; break; }
                                if (File.Exists(p3)) { Enqueue(p3); enqueued = true; break; }
                            }

                            if (!enqueued)
                            {
                                IReadOnlyList<string> resolvedDependencies =
                                    _resources.ResolveBins(dep, currentDir ?? searchFolder);
                                if (resolvedDependencies.Count > 1)
                                {
                                    bundle.AmbiguousDependencies.Add(dep);
                                    log?.Log($"VFX BIN dependency matched {resolvedDependencies.Count} extracted files: {dep}.");
                                }
                                foreach (string resolvedDependency in resolvedDependencies)
                                {
                                    Enqueue(resolvedDependency);
                                    enqueued = true;
                                }
                            }

                            if (!enqueued)
                            {
                                bundle.MissingDependencies.Add(dep);
                                log?.Log($"VFX BIN dependency missing: {dep}.");
                            }
                        }

                        if (currentBinPath.Equals(skinBinPath, StringComparison.OrdinalIgnoreCase) &&
                            document.Dependencies.Count == 0)
                        {
                            Enqueue(Path.Combine(charFolder, "animations", skinName + ".bin"));
                            Enqueue(Path.Combine(charFolder, charName + ".bin"));
                            foreach (string multiBin in FindLegacyMultiSkinBins(charFolder, skinName))
                                Enqueue(multiBin);
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
                    () => Load(skinBinPath, log, cancellationToken),
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
            LogService log)
        {
            ArgumentNullException.ThrowIfNull(definition);
            var runtime = new VfxPlaybackRuntime(seed);
            runtime.SetSystem(definition, definition.Transform.GetValueOrDefault(Matrix4x4.Identity) * transform);

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
                else if (!string.IsNullOrWhiteSpace(emitter.Def.TexturePath))
                {
                    log?.Log($"VFX resource missing: {emitter.Def.TexturePath} ({definition.Name}/{emitter.Def.Name}).");
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

                BitmapSource gradient = _resources.ResolveTexture(
                    emitter.Def.ParticleColorTexturePath,
                    searchDirectory);
                if (gradient != null) ApplyColorGradient(emitter, gradient);

                if (emitter.Def.IsMeshPrimitive)
                {
                    if (!string.IsNullOrWhiteSpace(emitter.Def.MeshPath))
                    {
                        emitter.PendingMesh = _resources.ResolveMesh(emitter.Def.MeshPath, searchDirectory);
                        if (emitter.PendingMesh == null)
                            log?.Log($"VFX mesh missing: {emitter.Def.MeshPath} ({definition.Name}/{emitter.Def.Name}).");
                        else if (!string.IsNullOrWhiteSpace(emitter.Def.MeshAnimationPath))
                        {
                            emitter.MeshAnimation = _resources.ResolveMeshAnimation(
                                emitter.Def.MeshPath,
                                emitter.Def.MeshSkeletonPath,
                                emitter.Def.MeshAnimationPath,
                                searchDirectory,
                                log);
                            if (emitter.MeshAnimation == null)
                            {
                                log?.Log(
                                    $"VFX mesh animation missing: {emitter.Def.MeshAnimationPath} " +
                                    $"({definition.Name}/{emitter.Def.Name}).");
                            }
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
            LogService log)
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
                    log));
        }

        public void ClearCaches() => _resources.ClearCaches();

        private static void ApplyColorGradient(VfxPlaybackRuntime.EmitterState emitter, BitmapSource bitmap)
        {
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = bitmap;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                bitmap = converted;
            }

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            for (int offset = 0; offset < pixels.Length; offset += 4)
                (pixels[offset], pixels[offset + 2]) = (pixels[offset + 2], pixels[offset]);

            emitter.ColorGradient = pixels;
            emitter.ColorGradientW = width;
            emitter.ColorGradientH = height;
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

        private static IEnumerable<string> FindLegacyMultiSkinBins(string charFolder, string skinName)
        {
            var target = System.Text.RegularExpressions.Regex.Match(
                skinName ?? string.Empty,
                @"^skin0*(\d+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!target.Success) return Array.Empty<string>();
            string token = "skin" + int.Parse(target.Groups[1].Value);
            return Directory.GetFiles(charFolder, "*multi_skins*.bin", SearchOption.TopDirectoryOnly)
                .Where(path => System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetFileNameWithoutExtension(path),
                    $@"(?:^|_)skin0*{System.Text.RegularExpressions.Regex.Escape(token[4..])}(?:_|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }
    }
}
