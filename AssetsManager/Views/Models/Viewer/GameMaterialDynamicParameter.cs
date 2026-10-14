using System;
using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    // Parsed driver evaluators retain values and conditions, never the source BIN graph.
    /// <param name="Buffs">The buff scripts the driver reads, which the preview offers as game states.</param>
    internal sealed record GameMaterialDynamicParameter(
        string Name,
        Func<GameMaterialState, Vector4?> Evaluate,
        IReadOnlyList<string> Buffs = null);
}
