using System;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    // Parsed driver evaluators retain values and conditions, never the source BIN graph.
    internal sealed record GameMaterialDynamicParameter(string Name, Func<int, Vector4?> Evaluate);
}
