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
        private float _characterTimeSeconds;
        private bool _showStructures = true;
        private bool _showParticles = true;

        internal MapSceneRuntime(
            MapSceneData scene,
            IReadOnlyList<MapCharacterRuntimeGroup> characterGroups,
            MapParticleSceneRuntime particles)
        {
            Scene = scene ?? throw new ArgumentNullException(nameof(scene));
            CharacterGroups = characterGroups ?? Array.Empty<MapCharacterRuntimeGroup>();
            Particles = particles ?? new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>());
        }

        internal MapSceneData Scene { get; }
        internal IReadOnlyList<MapCharacterRuntimeGroup> CharacterGroups { get; }
        internal MapParticleSceneRuntime Particles { get; }
        internal IReadOnlySet<string> Hidden => _hidden;
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

            if (ShowStructures && float.IsFinite(deltaSeconds) && deltaSeconds > 0f)
                _characterTimeSeconds += deltaSeconds;

            if (ShowParticles)
                Particles.Update(viewProjection, deltaSeconds, _hidden);
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
