using System;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>The authored shell of one character submesh, independent of its surface material.</summary>
    internal sealed record CharacterOutlineDefinition(
        uint MaterialHash, Vector3 Color, float Width, float CameraDistance,
        float DepthOffset, float Alpha, string MaskPath, Vector4 MaskChannels,
        ModelMaterialDefinition Material);
}
