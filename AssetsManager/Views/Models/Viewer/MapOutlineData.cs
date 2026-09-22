using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    public enum MapOutlineItemKind
    {
        Particle,
        Character,
        Locator,
        Group,
        Audio,
        Other
    }

    internal sealed record MapOutlineItemData(
        string Id,
        uint ChunkHash,
        uint KeyHash,
        string Name,
        string ClassName,
        MapOutlineItemKind Kind,
        Vector3 Position,
        byte Visibility,
        uint? VisibilityController)
    {
        internal bool IsDrawable => Kind is MapOutlineItemKind.Particle or MapOutlineItemKind.Character;
    }

    internal sealed record MapOutlineChunkData(
        string Id,
        uint EntryHash,
        string Name,
        string Label,
        IReadOnlyList<MapOutlineItemData> Items);
}