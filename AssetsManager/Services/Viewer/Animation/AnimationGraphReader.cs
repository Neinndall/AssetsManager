using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Services.Viewer.Animation
{
    /// <summary>
    /// Viewer-level facade over the audited AnimationGraph decoder. MAP and VFX consume the
    /// same graph semantics without making MAP code depend on the VFX Studio parsing namespace.
    /// </summary>
    internal static class AnimationGraphReader
    {
        internal static AnimationGraphDefinition Read(
            IEnumerable<BinTree> documents,
            uint graphPathHash,
            Func<uint, string> hashNameResolver,
            Func<uint, string> classNameResolver)
        {
            if (graphPathHash == 0 || documents == null)
                return null;

            foreach (BinTree document in documents)
            {
                if (document == null) continue;
                AnimationGraphDefinition graph = VfxAnimationParser
                    .ExtractAnimationGraphs(document, hashNameResolver, classNameResolver)
                    .FirstOrDefault(candidate => candidate.PathHash == graphPathHash);
                if (graph != null)
                    return graph;
            }

            return null;
        }
    }
}
