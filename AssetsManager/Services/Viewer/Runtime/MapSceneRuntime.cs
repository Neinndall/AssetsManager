using System;
using System.Collections.Generic;
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
        private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;
        private float _sceneTimeSeconds;
        private float _characterTimeSeconds;
        private bool _showStructures = true;
        private bool _showParticles = true;

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
        }

        internal void SetBackdropTextures(IReadOnlyDictionary<string, MapTextureImage> textures)
        {
            ThrowIfDisposed();
            if (textures != null)
                BackdropTextures = textures;
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
        }

        internal void SetBackdropProgramTextures(IReadOnlyDictionary<string, MapTextureImage> textures)
        {
            ThrowIfDisposed();
            if (textures != null)
                BackdropProgramTextures = textures;
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
        }

        internal void SetBackdropLightmaps(IReadOnlyDictionary<string, MapTextureImage> lightmaps)
        {
            ThrowIfDisposed();
            if (lightmaps != null)
                BackdropLightmaps = lightmaps;
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
        }

        internal void SetCharacterGroups(IReadOnlyList<MapCharacterRuntimeGroup> characterGroups)
        {
            ThrowIfDisposed();
            IReadOnlyList<MapCharacterRuntimeGroup> replacement =
                characterGroups ?? Array.Empty<MapCharacterRuntimeGroup>();
            if (ReferenceEquals(CharacterGroups, replacement))
                return;

            foreach (MapCharacterRuntimeGroup group in CharacterGroups)
                group?.Dispose();
            CharacterGroups = replacement;
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

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MapSceneRuntime));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (MapCharacterRuntimeGroup group in CharacterGroups)
                group?.Dispose();
            Particles.Dispose();
            _hidden.Clear();
        }
    }
}
