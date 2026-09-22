using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// One structure or level prop the map stands as a character skin.
    /// </summary>
    internal sealed record MapCharacterData(
        MapPlaceableData Placeable,
        string Skin,
        uint? Team,
        string Animation)
    {
        public uint ChunkHash => Placeable.ChunkHash;
        public uint KeyHash => Placeable.KeyHash;
        public string Name => Placeable.Name;
        public Matrix4x4 Transform => Placeable.Transform;
        public byte Visibility => Placeable.Visibility;
        public uint? VisibilityController => Placeable.VisibilityController;
    }
}
