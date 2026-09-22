using System.Collections.Generic;
using System.Numerics;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// One authored placeable from a MapPlaceableContainer.
    /// The transform keeps LeagueToolkit's row-major Matrix4x4 field order, which is value-for-value
    /// equivalent to the transposed column-major array exported by LTK Manager 1.20.0 and leaves translation in M41-M43.
    /// </summary>
    internal sealed record MapPlaceableData(
        uint ChunkHash,
        uint KeyHash,
        uint ClassHash,
        string Name,
        Matrix4x4 Transform,
        byte Visibility,
        uint? VisibilityController,
        IReadOnlyDictionary<uint, BinTreeProperty> Properties)
    {
        public const byte EveryLayer = byte.MaxValue;

        public Vector3 Position => new(Transform.M41, Transform.M42, Transform.M43);

        public bool IsVisibleOnLayer(int layer)
        {
            if ((uint)layer >= 8u)
                return false;
            return (Visibility & (1 << layer)) != 0;
        }
    }

    /// <summary>
    /// One MapPlaceableContainer and its items in authored file order.
    /// </summary>
    internal sealed record MapPlaceableChunkData(
        uint EntryHash,
        string Name,
        IReadOnlyList<MapPlaceableData> Items);
}
