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
            public Dictionary<string, VfxAnimationClip> Clips { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<uint, uint> ResourceMap { get; } = new();
        }

        public Bundle Load(string skinBinPath, LogService log)
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

                string charName = Path.GetFileName(charFolder);
                string skinName = Path.GetFileNameWithoutExtension(skinBinPath);
                string animBinPath = Path.Combine(charFolder, "animations", skinName + ".bin");
                Enqueue(animBinPath);

                // The selected skin BIN is the source of truth. Its dependency table identifies
                // the champion, animation and shared multi-skin BINs without confusing skin1 with skin11.
                // Explicit fallbacks keep older extractions that omitted dependency metadata usable.
                string champRootBin = Path.Combine(charFolder, charName + ".bin");
                Enqueue(champRootBin);

                int targetSkinIndex = ExtractSkinIndex(skinName);

                foreach (string multiBin in Directory.GetFiles(charFolder, "*multi_skins*.bin", SearchOption.TopDirectoryOnly))
                {
                    string binName = Path.GetFileName(multiBin);
                    if (targetSkinIndex < 0 || binName.Contains($"skin{targetSkinIndex}", StringComparison.OrdinalIgnoreCase) || binName.Contains($"skin0{targetSkinIndex}", StringComparison.OrdinalIgnoreCase))
                    {
                        Enqueue(multiBin);
                    }
                }

                int guard = 0;
                while (queue.Count > 0 && guard++ < 256)
                {
                    string currentBinPath = queue.Dequeue();
                    try
                    {
                        if (!File.Exists(currentBinPath)) continue;
                        byte[] fileBytes = File.ReadAllBytes(currentBinPath);
                        VfxBinDocument document = VfxGraphParser.ParseDocument(fileBytes);

                        bool isTargetSkinBin = currentBinPath.Equals(skinBinPath, StringComparison.OrdinalIgnoreCase);

                        foreach (var kv in document.Systems)
                        {
                            // Systems directly in the skin BIN or dependencies are filtered to exclude systems belonging to OTHER skins.
                            if (IsSystemForSkin(kv.Value.Name, targetSkinIndex))
                            {
                                bundle.Systems.TryAdd(kv.Key, kv.Value);
                            }
                        }
                        foreach (var kv in document.AnimationClips)
                            MergeClip(bundle.Clips, kv.Key, kv.Value);
                        foreach (var kv in document.ResourceMap)
                            bundle.ResourceMap.TryAdd(kv.Key, kv.Value);

                        string currentDir = Path.GetDirectoryName(currentBinPath);
                        foreach (var dep in document.Dependencies)
                        {
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
                                string resolvedDependency = _resources.ResolveBin(dep, currentDir ?? searchFolder);
                                if (resolvedDependency != null) Enqueue(resolvedDependency);
                                else log?.Log($"VFX BIN dependency missing: {dep}.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.LogError(ex, $"Error scanning bin dependency file: {currentBinPath}");
                    }
                }

                log?.Log($"Loaded {bundle.Systems.Count} VFX systems and {bundle.Clips.Count} animation clips.");
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
                return await Task.Run(() => Load(skinBinPath, log), cancellationToken).ConfigureAwait(false);
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
            runtime.SetSystem(definition, transform);

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

                BitmapSource gradient = _resources.ResolveTexture(
                    emitter.Def.ParticleColorTexturePath,
                    searchDirectory);
                if (gradient != null) ApplyColorGradient(emitter, gradient);

                if (emitter.Def.IsMeshPrimitive)
                {
                    emitter.PendingMesh = _resources.ResolveMesh(emitter.Def.MeshPath, searchDirectory);
                    if (emitter.PendingMesh == null)
                        log?.Log($"VFX mesh missing: {emitter.Def.MeshPath} ({definition.Name}/{emitter.Def.Name}).");
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

        /// <summary>
        /// Merges clip entries across bins. The same anm is often declared in several bins
        /// (skins, animations, fused); only one carries the mEventDataMap that links the
        /// animation to its VFX. Accumulate particle/sound events so the link is never lost.
        /// </summary>
        private static void MergeClip(Dictionary<string, VfxAnimationClip> map, string key, VfxAnimationClip clip)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = clip;
                return;
            }

            foreach (var pe in clip.ParticleEvents)
                if (existing.ParticleEvents.All(e => e.EffectHash != pe.EffectHash || e.EffectName != pe.EffectName))
                    existing.ParticleEvents.Add(pe);
            foreach (var se in clip.SoundEvents)
                if (existing.SoundEvents.All(e => e.SoundHash != se.SoundHash || e.SoundName != se.SoundName))
                    existing.SoundEvents.Add(se);

            if (string.IsNullOrEmpty(existing.AnimationName) && !string.IsNullOrEmpty(clip.AnimationName))
                existing.AnimationName = clip.AnimationName;
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

        private static int ExtractSkinIndex(string skinName)
        {
            if (string.IsNullOrEmpty(skinName)) return -1;
            var match = System.Text.RegularExpressions.Regex.Match(skinName, @"skin(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out int idx) ? idx : -1;
        }

        private static bool IsSystemForSkin(string systemName, int targetSkinIndex)
        {
            if (string.IsNullOrEmpty(systemName) || targetSkinIndex < 0) return true;

            var match = System.Text.RegularExpressions.Regex.Match(
                systemName, @"(?:^|_|/|\\)skin0*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success && int.TryParse(match.Groups[1].Value, out int sysSkinIndex))
            {
                return sysSkinIndex == targetSkinIndex;
            }

            return true;
        }
    }
}
