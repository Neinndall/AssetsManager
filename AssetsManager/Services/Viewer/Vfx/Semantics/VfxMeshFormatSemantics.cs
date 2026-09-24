using System;
using System.Collections.Generic;
using System.IO;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    /// <summary>
    /// Central format policy for authored VFX meshes. The BIN accepts SCB/TMESH/GMESH in the
    /// simple slot and SKN in the skinned pair. SCO remains a decoder capability for direct/static
    /// mesh tooling, but the particle BIN never selects it from mSimpleMeshName.
    /// </summary>
    internal static class VfxMeshFormatSemantics
    {
        internal static readonly IReadOnlyList<string> AuthoredSimpleExtensions =
            new[] { ".scb", ".tmesh", ".gmesh" };

        internal static readonly IReadOnlyList<string> SkinnedExtensions =
            new[] { ".skn" };

        internal static readonly IReadOnlyList<string> SceneExtensions =
            new[] { ".scb", ".tmesh", ".gmesh", ".skn" };

        // The resolver can also decode SCO for AssetsManager's generic/static mesh tooling. That
        // does not make SCO a legal VFX mSimpleMeshName value.
        internal static readonly IReadOnlyList<string> ResolverExtensions =
            new[] { ".scb", ".tmesh", ".gmesh", ".sco", ".skn" };

        internal static bool IsAuthoredSimplePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string extension = Path.GetExtension(path);
            foreach (string supported in AuthoredSimpleExtensions)
            {
                if (extension.Equals(supported, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
