using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Runtime
{
    /// <summary>
    /// Owns the runtime state of one loaded MAP scene. Structures share one continuous scene clock,
    /// while placed particle systems keep their independent frame-driven simulation runtime.
    /// </summary>
    internal sealed class MapSceneRuntime : IDisposable
    {
        /// <summary>How long a structure skin that left the map state stays loaded for a quick return.</summary>
        internal static readonly TimeSpan RetainedGroupLifetime = TimeSpan.FromSeconds(30);

        private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (MapCharacterRuntimeGroup Group, TimeSpan ExpiresAt)> _retainedGroups =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<MapTextureImage, Action> _backdropTextureHolds =
            new(ReferenceEqualityComparer.Instance);
        private Func<MapTextureImage, Action> _holdBackdropTexture;
        private bool _disposed;
        private float _sceneTimeSeconds;
        private float _characterTimeSeconds;
        private bool _showStructures = true;
        private bool _showParticles;
        private MapVisibilityState _visibility;

        internal MapSceneRuntime(
            MapSceneData scene,
            IReadOnlyList<MapCharacterRuntimeGroup> characterGroups,
            MapParticleSceneRuntime particles)
        {
            Scene = scene ?? throw new ArgumentNullException(nameof(scene));
            BackdropTextures = scene.Textures ?? new Dictionary<string, MapTextureImage>();
            BackdropProgramTextures = scene.ProgramTextures ?? new Dictionary<string, MapTextureImage>();
            BackdropLightmaps = scene.Lightmaps ?? new Dictionary<string, MapTextureImage>(StringComparer.OrdinalIgnoreCase);
            CharacterGroups = characterGroups ?? Array.Empty<MapCharacterRuntimeGroup>();
            Particles = particles ?? new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>());
            _visibility = scene.OpeningVisibility;
        }

        internal MapSceneData Scene { get; }
        internal IReadOnlyDictionary<string, MapTextureImage> BackdropTextures { get; private set; }
        internal IReadOnlyDictionary<string, MapTextureImage> BackdropProgramTextures { get; private set; }
        internal IReadOnlyDictionary<string, MapTextureImage> BackdropLightmaps { get; private set; }
        internal IReadOnlyList<MapCharacterRuntimeGroup> CharacterGroups { get; private set; }
        internal MapParticleSceneRuntime Particles { get; private set; }
        internal IReadOnlySet<string> Hidden => _hidden;
        internal float SceneTimeSeconds => _sceneTimeSeconds;
        internal float CharacterTimeSeconds => _characterTimeSeconds;
        internal MapVisibilityState Visibility => _visibility;

        /// <summary>Changes whenever the active or retained structure groups change; plans must match it.</summary>
        internal long CharacterGeneration { get; private set; }

        /// <summary>Changes whenever the placed VFX set changes; plans must match it.</summary>
        internal long ParticleGeneration { get; private set; }

        internal int RetainedCharacterGroupCount => _retainedGroups.Count;

        /// <summary>Monotonic clock for retention; replaceable by tests.</summary>
        internal Func<TimeSpan> Clock { get; set; } = static () => Stopwatch.GetElapsedTime(0);
        internal int VisibilityFlags => _visibility.Flags;
        internal bool ShowStructures
        {
            get => _showStructures;
            set
            {
                if (_showStructures == value) return;
                _showStructures = value;
                _characterTimeSeconds = 0f;
            }
        }

        internal bool ShowParticles
        {
            get => _showParticles;
            set
            {
                if (_showParticles == value) return;
                _showParticles = value;
                Particles.Restart();
            }
        }

        internal void Update(Matrix4x4 viewProjection, float deltaSeconds)
        {
            ThrowIfDisposed();

            if (float.IsFinite(deltaSeconds) && deltaSeconds > 0f)
            {
                _sceneTimeSeconds += deltaSeconds;
                if (ShowStructures)
                    _characterTimeSeconds += deltaSeconds;
            }

            if (ShowParticles)
                Particles.Update(viewProjection, deltaSeconds, _hidden);

            SweepRetainedGroups();
        }

        internal void SetVisibilityFlags(int flags) => SetVisibility(_visibility.WithFlags(flags));

        internal void SetVisibility(MapVisibilityState visibility)
        {
            ThrowIfDisposed();
            _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        }

        internal void SetBackdropTextureRetainer(Func<MapTextureImage, Action> holdTexture)
        {
            ThrowIfDisposed();
            ReleaseBackdropTextureHolds();
            _holdBackdropTexture = holdTexture;
            RefreshBackdropTextureHolds();
        }

        internal void SetBackdropTextures(IReadOnlyDictionary<string, MapTextureImage> textures)
        {
            ThrowIfDisposed();
            if (textures != null)
            {
                BackdropTextures = textures;
                RefreshBackdropTextureHolds();
            }
        }

        internal void MergeBackdropTextures(IReadOnlyDictionary<string, MapTextureImage> textures)
        {
            ThrowIfDisposed();
            if (textures == null || textures.Count == 0)
                return;

            var merged = new Dictionary<string, MapTextureImage>(BackdropTextures, StringComparer.Ordinal);
            foreach ((string key, MapTextureImage image) in textures)
                if (!string.IsNullOrWhiteSpace(key) && image != null)
                    merged[key] = image;
            BackdropTextures = merged;
            RefreshBackdropTextureHolds();
        }

        internal void SetBackdropProgramTextures(IReadOnlyDictionary<string, MapTextureImage> textures)
        {
            ThrowIfDisposed();
            if (textures != null)
            {
                BackdropProgramTextures = textures;
                RefreshBackdropTextureHolds();
            }
        }

        internal void MergeBackdropProgramTextures(IReadOnlyDictionary<string, MapTextureImage> textures)
        {
            ThrowIfDisposed();
            if (textures == null || textures.Count == 0)
                return;

            var merged = new Dictionary<string, MapTextureImage>(BackdropProgramTextures, StringComparer.Ordinal);
            foreach ((string key, MapTextureImage image) in textures)
                if (!string.IsNullOrWhiteSpace(key) && image != null)
                    merged[key] = image;
            BackdropProgramTextures = merged;
            RefreshBackdropTextureHolds();
        }

        internal void SetBackdropLightmaps(IReadOnlyDictionary<string, MapTextureImage> lightmaps)
        {
            ThrowIfDisposed();
            if (lightmaps != null)
            {
                BackdropLightmaps = lightmaps;
                RefreshBackdropTextureHolds();
            }
        }

        internal void MergeBackdropLightmaps(IReadOnlyDictionary<string, MapTextureImage> lightmaps)
        {
            ThrowIfDisposed();
            if (lightmaps == null || lightmaps.Count == 0)
                return;

            var merged = new Dictionary<string, MapTextureImage>(BackdropLightmaps, StringComparer.OrdinalIgnoreCase);
            foreach ((string key, MapTextureImage image) in lightmaps)
                if (!string.IsNullOrWhiteSpace(key) && image != null)
                    merged[key] = image;
            BackdropLightmaps = merged;
            RefreshBackdropTextureHolds();
        }

        internal void SetCharacterGroups(IReadOnlyList<MapCharacterRuntimeGroup> characterGroups)
        {
            ThrowIfDisposed();
            IReadOnlyList<MapCharacterRuntimeGroup> replacement =
                characterGroups ?? Array.Empty<MapCharacterRuntimeGroup>();
            if (ReferenceEquals(CharacterGroups, replacement))
                return;

            var kept = new HashSet<MapCharacterRuntimeGroup>(replacement, ReferenceEqualityComparer.Instance);
            foreach (MapCharacterRuntimeGroup group in CharacterGroups)
            {
                if (group != null && !kept.Contains(group))
                    group.Dispose();
            }
            CharacterGroups = replacement;
            CharacterGeneration++;
        }

        /// <summary>
        /// Skins a structure plan may reuse without loading: the active groups first, then the
        /// groups retained from recent map states.
        /// </summary>
        internal IReadOnlyDictionary<string, MapCharacterRuntimeGroup> CharacterReuseCandidates()
        {
            ThrowIfDisposed();
            var candidates = new Dictionary<string, MapCharacterRuntimeGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (MapCharacterRuntimeGroup group in CharacterGroups)
            {
                if (group is { IsDisposed: false } && !string.IsNullOrWhiteSpace(group.Skin))
                    candidates.TryAdd(group.Skin, group);
            }
            foreach (KeyValuePair<string, (MapCharacterRuntimeGroup Group, TimeSpan ExpiresAt)> retained in _retainedGroups)
            {
                if (!retained.Value.Group.IsDisposed)
                    candidates.TryAdd(retained.Key, retained.Value.Group);
            }
            return candidates;
        }

        /// <summary>
        /// Adopts a structure plan built on the current generation. Kept groups stay untouched,
        /// groups with another placement set are rebound without reloading, and groups that leave
        /// the state are retained for <see cref="RetainedGroupLifetime"/> before being released.
        /// </summary>
        internal bool TryApplyCharacterPlan(MapCharacterPlan plan)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(plan);
            if (plan.Generation != CharacterGeneration)
                return false;

            var active = new HashSet<MapCharacterRuntimeGroup>(
                CharacterGroups.Where(group => group != null),
                ReferenceEqualityComparer.Instance);
            foreach (MapCharacterPlan.Entry entry in plan.Entries)
            {
                MapCharacterRuntimeGroup reused = entry.Reused;
                if (reused == null)
                    continue;
                if (reused.IsDisposed || (!active.Contains(reused) && !IsRetained(entry.Skin, reused)))
                    return false;
            }

            var next = new List<MapCharacterRuntimeGroup>(plan.Entries.Count);
            var consumed = new HashSet<MapCharacterRuntimeGroup>(ReferenceEqualityComparer.Instance);
            foreach (MapCharacterPlan.Entry entry in plan.Entries)
            {
                if (entry.Loaded != null)
                {
                    next.Add(entry.Loaded);
                    continue;
                }

                MapCharacterRuntimeGroup reused = entry.Reused;
                consumed.Add(reused);
                if (IsRetained(entry.Skin, reused))
                    _retainedGroups.Remove(entry.Skin);
                if (SamePlacements(reused.Placements, entry.Placements))
                {
                    next.Add(reused);
                }
                else
                {
                    next.Add(reused.Rebind(entry.Placements));
                    // The detached group no longer owns anything; disposing it only marks it unusable.
                    reused.Dispose();
                }
            }

            TimeSpan expiresAt = Clock() + RetainedGroupLifetime;
            foreach (MapCharacterRuntimeGroup group in CharacterGroups)
            {
                if (group == null || consumed.Contains(group))
                    continue;
                string skin = group.Skin;
                if (string.IsNullOrWhiteSpace(skin) || group.IsDisposed)
                {
                    group.Dispose();
                    continue;
                }
                if (_retainedGroups.TryGetValue(skin, out var previous) && !ReferenceEquals(previous.Group, group))
                    previous.Group.Dispose();
                _retainedGroups[skin] = (group, expiresAt);
            }

            CharacterGroups = next;
            CharacterGeneration++;
            plan.MarkAdopted();
            SweepRetainedGroups();
            return true;
        }

        /// <summary>Adopts a placed-VFX plan built on the current generation, keeping continuing simulations.</summary>
        internal bool TryApplyParticlePlan(MapParticlePlan plan)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(plan);
            if (plan.Generation != ParticleGeneration)
                return false;

            Particles.Reconcile(plan.Catalog, plan.Runtimes, plan.CreatedResources);
            ParticleGeneration++;
            plan.MarkAdopted();
            return true;
        }

        /// <summary>Releases retained structure groups whose lifetime ended.</summary>
        internal void SweepRetainedGroups()
        {
            if (_disposed || _retainedGroups.Count == 0)
                return;

            TimeSpan now = Clock();
            string[] expired = _retainedGroups
                .Where(pair => pair.Value.ExpiresAt <= now || pair.Value.Group.IsDisposed)
                .Select(pair => pair.Key)
                .ToArray();
            if (expired.Length == 0)
                return;

            foreach (string skin in expired)
            {
                _retainedGroups[skin].Group.Dispose();
                _retainedGroups.Remove(skin);
            }
            CharacterGeneration++;
        }

        private bool IsRetained(string skin, MapCharacterRuntimeGroup group) =>
            !string.IsNullOrWhiteSpace(skin) &&
            _retainedGroups.TryGetValue(skin, out var held) &&
            ReferenceEquals(held.Group, group);

        private static bool SamePlacements(
            IReadOnlyList<MapCharacterData> current,
            IReadOnlyList<MapCharacterData> wanted)
        {
            if (current == null || wanted == null || current.Count != wanted.Count)
                return false;
            for (int index = 0; index < current.Count; index++)
            {
                if (!ReferenceEquals(current[index], wanted[index]))
                    return false;
            }
            return true;
        }

        internal void SetParticles(MapParticleSceneRuntime particles)
        {
            ThrowIfDisposed();
            MapParticleSceneRuntime replacement =
                particles ?? new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>());
            if (ReferenceEquals(Particles, replacement))
                return;

            Particles?.Dispose();
            Particles = replacement;
            ParticleGeneration++;
        }

        internal void SetHidden(string id, bool hidden)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (hidden) _hidden.Add(id);
            else _hidden.Remove(id);
        }

        internal bool IsHidden(MapCharacterData character) =>
            character != null && MapOutlineSemantics.IsHidden(_hidden, character.ChunkHash, character.KeyHash);

        private void RefreshBackdropTextureHolds()
        {
            if (_holdBackdropTexture == null)
                return;

            var desired = new HashSet<MapTextureImage>(ReferenceEqualityComparer.Instance);
            AddImages(desired, BackdropTextures);
            AddImages(desired, BackdropProgramTextures);
            AddImages(desired, BackdropLightmaps);

            var stale = new List<MapTextureImage>();
            foreach (MapTextureImage image in _backdropTextureHolds.Keys)
                if (!desired.Contains(image))
                    stale.Add(image);
            foreach (MapTextureImage image in stale)
            {
                _backdropTextureHolds[image]();
                _backdropTextureHolds.Remove(image);
            }

            foreach (MapTextureImage image in desired)
                if (!_backdropTextureHolds.ContainsKey(image))
                    _backdropTextureHolds[image] = _holdBackdropTexture(image) ?? NoopRelease;
        }

        private static void AddImages(
            HashSet<MapTextureImage> destination,
            IReadOnlyDictionary<string, MapTextureImage> textures)
        {
            if (textures == null)
                return;
            foreach (MapTextureImage image in textures.Values)
                if (image != null)
                    destination.Add(image);
        }

        private static void NoopRelease()
        {
        }

        private void ReleaseBackdropTextureHolds()
        {
            foreach (Action release in _backdropTextureHolds.Values)
                release?.Invoke();
            _backdropTextureHolds.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MapSceneRuntime));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ReleaseBackdropTextureHolds();
            _holdBackdropTexture = null;
            foreach (MapCharacterRuntimeGroup group in CharacterGroups)
                group?.Dispose();
            foreach ((MapCharacterRuntimeGroup group, _) in _retainedGroups.Values)
                group.Dispose();
            _retainedGroups.Clear();
            Particles.Dispose();
            _hidden.Clear();
        }
    }
}
