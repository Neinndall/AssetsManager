using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetsManager.Services.Viewer.Vfx.Resources
{
    /// <summary>
    /// Six full-resolution RGBA faces of a League reflection cube map in DDS order:
    /// +X, -X, +Y, -Y, +Z, -Z.
    /// </summary>
    internal sealed record VfxCubeMapData(int Width, int Height, IReadOnlyList<byte[]> Faces)
    {
        internal bool IsValid => Width > 0 && Height > 0 && Faces is { Count: 6 } &&
                                 Faces.All(face => face is { Length: > 0 });
    }
}
