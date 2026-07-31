using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Discovers the BIN graph associated with a model and exposes authored VFX
    /// systems without fabricating fallback emitters.
    /// </summary>
    public sealed class VfxDataService
    {
        private readonly LogService _logService;

        public VfxDataService(LogService logService, HashResolverService hashResolver = null)
        {
            _logService = logService;
            _ = hashResolver;
        }

        public Task<List<VfxSystemModel>> LoadVfxSystemsForModelAsync(
            string modelFilePath,
            string projectRootFolder = null)
            => Task.Run(() => LoadVfxSystemsForModel(modelFilePath, projectRootFolder));

        public List<VfxSystemModel> LoadVfxSystemsForModel(
            string modelFilePath,
            string projectRootFolder = null)
        {
            if (string.IsNullOrWhiteSpace(modelFilePath)) return new List<VfxSystemModel>();

            string searchRoot = ResolveSearchRoot(modelFilePath, projectRootFolder);
            string skinTag = ExtractSkinTag(modelFilePath);
            var candidates = DiscoverBinCandidates(modelFilePath, searchRoot, skinTag);
            var systems = new Dictionary<uint, VfxSystemDefinition>();
            var resourceMap = new Dictionary<uint, uint>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(candidates);

            _logService?.LogDebug(
                $"[VFX] Graph scan found {queue.Count} initial BIN candidate(s) for '{skinTag}'.");

            while (queue.Count > 0 && visited.Count < 512)
            {
                string binPath = queue.Dequeue();
                if (!File.Exists(binPath)) continue;
                binPath = Path.GetFullPath(binPath);
                if (!visited.Add(binPath)) continue;

                try
                {
                    VfxBinDocument document = VfxGraphParser.ParseDocument(File.ReadAllBytes(binPath));
                    foreach (var pair in document.Systems)
                    {
                        systems.TryAdd(pair.Key, pair.Value);
                    }
                    foreach (var pair in document.ResourceMap)
                    {
                        resourceMap.TryAdd(pair.Key, pair.Value);
                    }

                    foreach (string dependency in document.Dependencies)
                    {
                        string resolved = ResolveDependency(dependency, searchRoot, Path.GetDirectoryName(binPath));
                        if (resolved != null && !visited.Contains(resolved)) queue.Enqueue(resolved);
                    }
                }
                catch (Exception ex)
                {
                    _logService?.LogError(ex, $"[VFX] Failed to parse graph BIN: {binPath}");
                }
            }

            var models = CreateModels(systems, resourceMap, searchRoot);
            _logService?.LogSuccess(
                $"[VFX] Loaded {models.Count} authored systems with {models.Sum(model => model.Emitters.Count)} emitters from {visited.Count} BIN file(s).");
            return models;
        }

        public List<VfxSystemModel> LoadVfxSystemsFromBin(string binFilePath)
        {
            if (string.IsNullOrWhiteSpace(binFilePath) || !File.Exists(binFilePath))
                return new List<VfxSystemModel>();

            try
            {
                VfxBinDocument document = VfxGraphParser.ParseDocument(File.ReadAllBytes(binFilePath));
                return CreateModels(
                    new Dictionary<uint, VfxSystemDefinition>(document.Systems),
                    new Dictionary<uint, uint>(document.ResourceMap),
                    Path.GetDirectoryName(binFilePath));
            }
            catch (Exception ex)
            {
                _logService?.LogError(ex, $"[VFX] Failed to parse graph BIN: {binFilePath}");
                return new List<VfxSystemModel>();
            }
        }

        private static List<VfxSystemModel> CreateModels(
            Dictionary<uint, VfxSystemDefinition> systems,
            Dictionary<uint, uint> resourceMap,
            string searchDirectory)
        {
            return systems.Values
                .OrderBy(system => system.Name, StringComparer.OrdinalIgnoreCase)
                .Select(system =>
                {
                    string displayName = system.Name;
                    if ((string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        && !string.IsNullOrWhiteSpace(system.ParticlePath))
                    {
                        displayName = Path.GetFileName(system.ParticlePath);
                    }
                    return new VfxSystemModel
                    {
                        Name = string.IsNullOrWhiteSpace(displayName) ? $"Vfx_{system.PathHash:X8}" : displayName,
                        ParticlePath = system.ParticlePath ?? string.Empty,
                        Definition = system,
                        SystemCatalog = systems,
                        ResourceMap = resourceMap,
                        SearchDirectory = searchDirectory ?? string.Empty,
                        Emitters = system.Emitters.Select(ToPanelEmitter).ToList(),
                        TotalDuration = ComputeDuration(system, systems, resourceMap, new HashSet<uint>(), 0),
                        FrameRateText = DescribeFrameRate(system)
                    };
                })
                .ToList();
        }

        private static double ComputeDuration(
            VfxSystemDefinition system,
            IReadOnlyDictionary<uint, VfxSystemDefinition> catalog,
            IReadOnlyDictionary<uint, uint> resourceMap,
            HashSet<uint> path,
            int depth)
        {
            if (depth >= 8 || !path.Add(system.PathHash)) return double.PositiveInfinity;
            double systemEnd = 0;

            foreach (VfxEmitterDefinition emitter in system.Emitters.Where(item => !item.Disabled))
            {
                if (emitter.EmitterLifetime is null)
                {
                    path.Remove(system.PathHash);
                    return double.PositiveInfinity;
                }

                double particleLifetime = Math.Max(
                    emitter.ParticleLifetime.Constant,
                    emitter.ParticleLifetime.Values?.DefaultIfEmpty(0f).Max() ?? 0f);
                double emitterEnd = emitter.TimeBeforeFirstEmission + emitter.EmitterLifetime.Value +
                    Math.Max(0, particleLifetime) + Math.Max(emitter.ParticleLinger, emitter.EmitterLinger);

                if (emitter.ChildParticleSet is { Children.Count: > 0 } children)
                {
                    foreach (VfxChildSystemReference child in children.Children)
                    {
                        uint childHash = child.SystemHash;
                        if (childHash == 0 && child.EffectKey != 0)
                            resourceMap.TryGetValue(child.EffectKey, out childHash);
                        if (!catalog.TryGetValue(childHash, out VfxSystemDefinition childSystem)) continue;

                        double childDuration = ComputeDuration(childSystem, catalog, resourceMap, path, depth + 1);
                        if (double.IsInfinity(childDuration))
                        {
                            path.Remove(system.PathHash);
                            return double.PositiveInfinity;
                        }
                        emitterEnd += childDuration;
                    }
                }

                systemEnd = Math.Max(systemEnd, emitterEnd);
            }

            path.Remove(system.PathHash);
            return Math.Max(0, systemEnd);
        }

        private static string DescribeFrameRate(VfxSystemDefinition system)
        {
            float[] rates = system.Emitters
                .Where(emitter => emitter.NumFrames > 1)
                .Select(emitter => emitter.BirthFrameRate?.Constant ?? emitter.FrameRate ?? 0f)
                .Where(rate => rate > 0f)
                .Select(rate => MathF.Round(rate, 2))
                .Distinct()
                .ToArray();
            return rates.Length switch
            {
                0 => "Realtime",
                1 => $"{rates[0]:0.##} FPS",
                _ => "Variable FPS"
            };
        }

        private static VfxEmitterModel ToPanelEmitter(VfxEmitterDefinition emitter)
        {
            return new VfxEmitterModel
            {
                Name = emitter.Name ?? "Emitter",
                TexturePath = emitter.TexturePath ?? string.Empty,
                MeshPath = emitter.MeshPath ?? string.Empty,
                Lifetime = Math.Max(0.05f, emitter.ParticleLifetime.Constant),
                Duration = emitter.EmitterLifetime ?? 0f,
                Delay = emitter.TimeBeforeFirstEmission,
                EmissionRate = Math.Max(0f, emitter.Rate.Constant),
                InitialVelocity = emitter.BirthVelocity?.Constant ?? default,
                Acceleration = emitter.Acceleration?.Constant ?? default,
                InitialScale = emitter.BirthScale.Constant,
                StartColor = emitter.BirthColor.Constant,
                BlendMode = emitter.BlendMode,
                NumFrames = (ushort)Math.Clamp(emitter.NumFrames, 1, ushort.MaxValue),
                IsLooping = emitter.EmitterLifetime is null
            };
        }

        private static HashSet<string> DiscoverBinCandidates(
            string modelFilePath,
            string searchRoot,
            string skinTag)
        {
            var bins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (modelFilePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && File.Exists(modelFilePath))
                bins.Add(Path.GetFullPath(modelFilePath));

            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = Path.GetDirectoryName(modelFilePath);
            for (int level = 0; level < 6 && !string.IsNullOrWhiteSpace(current); level++)
            {
                if (Directory.Exists(current)) directories.Add(current);
                current = Path.GetDirectoryName(current);
            }
            if (Directory.Exists(searchRoot)) directories.Add(searchRoot);

            foreach (string directory in directories)
            {
                try
                {
                    foreach (string bin in Directory.EnumerateFiles(directory, "*.bin", SearchOption.AllDirectories))
                    {
                        if (IsBinRelevantToSkin(bin, skinTag)) bins.Add(Path.GetFullPath(bin));
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
            return bins;
        }

        private static string ResolveDependency(string dependency, string searchRoot, string currentDirectory)
        {
            if (string.IsNullOrWhiteSpace(dependency)) return null;
            string relative = dependency.Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            foreach (string root in new[] { searchRoot, currentDirectory })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string candidate = Path.Combine(root, relative);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);

                if (relative.StartsWith($"data{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith($"assets{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = Path.Combine(root, relative[(relative.IndexOf(Path.DirectorySeparatorChar) + 1)..]);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            return null;
        }

        private static string ResolveSearchRoot(string modelFilePath, string projectRootFolder)
        {
            if (!string.IsNullOrWhiteSpace(projectRootFolder) && Directory.Exists(projectRootFolder))
                return Path.GetFullPath(projectRootFolder);

            string directory = Path.GetDirectoryName(modelFilePath);
            while (!string.IsNullOrWhiteSpace(directory))
            {
                if (Directory.Exists(Path.Combine(directory, "data")) ||
                    Directory.Exists(Path.Combine(directory, "assets")))
                    return directory;
                directory = Path.GetDirectoryName(directory);
            }
            return Path.GetDirectoryName(modelFilePath) ?? string.Empty;
        }

        private static string ExtractSkinTag(string path)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                path ?? string.Empty,
                @"skin(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Value.ToLowerInvariant() : "skin0";
        }

        private static bool IsBinRelevantToSkin(string binPath, string skinTag)
        {
            string name = Path.GetFileNameWithoutExtension(binPath);
            var matches = System.Text.RegularExpressions.Regex.Matches(
                name,
                @"skin(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return matches.Count == 0 || matches.Cast<System.Text.RegularExpressions.Match>()
                .Any(match => string.Equals(match.Value, skinTag, StringComparison.OrdinalIgnoreCase));
        }
    }
}
