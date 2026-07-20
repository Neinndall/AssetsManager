using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Loads the full VFX/animation graph for a model from an extracted project folder.
    /// Replicates the dependency-walk strategy: it follows each bin's declared dependencies
    /// (relative to the WAD root), queues every skin bin and the champion root bin, and extracts
    /// VfxSystemDefinition objects, animation clips and the effectKey -> objectHash resource map.
    /// </summary>
    public sealed class VfxProjectLoader
    {
        public sealed class Bundle
        {
            public Dictionary<uint, VfxSystemDefinition> Systems { get; } = new();
            public Dictionary<string, VfxAnimationClip> Clips { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<uint, uint> ResourceMap { get; } = new();
        }

        public Bundle Load(string skinBinPath, ILogSink log)
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

                // Only load VFX data for the selected skin: the skin's own bin plus the fused
                // "multi_skins_*" bins that include this skin (e.g. skin0 -> lulu_multi_skins_skin0_*).
                // Scanning every skin/bin would load every other skin's VFX into the base model.
                string animationsDir = Path.Combine(charFolder, "animations");
                if (Directory.Exists(animationsDir))
                {
                    foreach (var extra in Directory.GetFiles(animationsDir, "*.bin", SearchOption.TopDirectoryOnly))
                        if (Path.GetFileName(extra).IndexOf(skinName, StringComparison.OrdinalIgnoreCase) >= 0)
                            Enqueue(extra);
                }

                string skinsDir = Path.Combine(charFolder, "skins");
                if (Directory.Exists(skinsDir))
                {
                    foreach (var extra in Directory.GetFiles(skinsDir, "*.bin", SearchOption.TopDirectoryOnly))
                        if (Path.GetFileName(extra).IndexOf(skinName, StringComparison.OrdinalIgnoreCase) >= 0)
                            Enqueue(extra);
                }
                string champRootBin = Path.Combine(charFolder, charName + ".bin");
                Enqueue(champRootBin);

                int guard = 0;
                while (queue.Count > 0 && guard++ < 256)
                {
                    string currentBinPath = queue.Dequeue();
                    try
                    {
                        if (!File.Exists(currentBinPath)) continue;
                        byte[] fileBytes = File.ReadAllBytes(currentBinPath);

                        foreach (var kv in VfxSystemResolver.ExtractAll(fileBytes))
                            bundle.Systems.TryAdd(kv.Key, kv.Value);
                        foreach (var kv in VfxSystemResolver.ExtractAnimationClips(fileBytes))
                            MergeClip(bundle.Clips, kv.Key, kv.Value);
                        foreach (var kv in VfxSystemResolver.ExtractResourceMap(fileBytes))
                            bundle.ResourceMap.TryAdd(kv.Key, kv.Value);

                        string currentDir = Path.GetDirectoryName(currentBinPath);
                        foreach (var dep in VfxSystemResolver.ExtractDependencies(fileBytes))
                        {
                            if (string.IsNullOrEmpty(dep)) continue;
                            string relativeDepPath = dep
                                .Replace("DATA/", "", StringComparison.OrdinalIgnoreCase)
                                .Replace("data/", "", StringComparison.OrdinalIgnoreCase)
                                .Replace('/', Path.DirectorySeparatorChar);

                            bool enqueued = false;
                            foreach (var root in new[] { wadRoot, searchFolder, currentDir })
                            {
                                if (string.IsNullOrEmpty(root)) continue;
                                string fullDepPath = Path.Combine(root, relativeDepPath);
                                if (File.Exists(fullDepPath)) { Enqueue(fullDepPath); enqueued = true; break; }
                            }

                            if (!enqueued && !string.IsNullOrEmpty(wadRoot) && Directory.Exists(wadRoot))
                            {
                                try
                                {
                                    string fileName = Path.GetFileName(relativeDepPath);
                                    var matches = Directory.GetFiles(wadRoot, fileName, SearchOption.AllDirectories);
                                    if (matches.Length > 0) Enqueue(matches[0]);
                                }
                                catch { }
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
    }

    /// <summary>Minimal logging contract to avoid a hard dependency on LogService in the loader.</summary>
    public interface ILogSink
    {
        void Log(string message);
        void LogError(Exception ex, string message);
    }
}
