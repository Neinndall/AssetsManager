using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// One MapParticle placeable and the VfxSystemDefinitionData object it plays.
    /// </summary>
    internal sealed record MapParticleData(
        MapPlaceableData Placeable,
        uint SystemHash,
        bool Transitional,
        bool StartDisabled)
    {
        public uint ChunkHash => Placeable.ChunkHash;
        public uint KeyHash => Placeable.KeyHash;
        public string Name => Placeable.Name;
        public Matrix4x4 Transform => Placeable.Transform;
        public byte Visibility => Placeable.Visibility;
        public uint? VisibilityController => Placeable.VisibilityController;
        public Vector3 Position => Placeable.Position;
    }

    /// <summary>
    /// Every placement of one map VFX system, preserving the order the system is first met.
    /// </summary>
    internal sealed record MapParticleGroupData(
        uint SystemHash,
        IReadOnlyList<MapParticleData> Particles);

    /// <summary>
    /// One resolved VFX system and every MapParticle placement that plays it.
    /// </summary>
    internal sealed record MapParticleSystemGroupData(
        uint SystemHash,
        VfxSystemDefinition System,
        IReadOnlyList<MapParticleData> Particles);

    /// <summary>
    /// VFX definitions available to one map materials document and the root systems it places.
    /// All systems remain available so child-system links can resolve through the same ResourceMap.
    /// </summary>
    internal sealed record MapParticleSystemCatalog(
        IReadOnlyDictionary<uint, VfxSystemDefinition> Systems,
        IReadOnlyDictionary<uint, uint> ResourceMap,
        IReadOnlyList<MapParticleSystemGroupData> Groups);
}
