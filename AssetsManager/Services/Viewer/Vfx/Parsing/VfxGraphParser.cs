using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;


namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    internal static class VfxParsingHash
    {
        internal static uint Fnv1a(string text) =>
            text == null ? 0 : LeagueToolkit.Hashing.Fnv1a.HashLower(text);
    }

    /// <summary>
    /// Parses VFX and animation metadata from companion BIN documents.
    /// </summary>
    public static class VfxGraphParser
    {

        private static BinTree ParseTree(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            using var stream = new MemoryStream(data, writable: false);
            return new BinTree(stream);
        }


        internal static VfxBinDocument ParseDocument(
            byte[] data,
            Func<uint, string> graphHashNameResolver = null,
            Func<uint, string> graphClassNameResolver = null)
        {
            BinTree tree = ParseTree(data);
            IReadOnlyDictionary<uint, uint> resourceMap = VfxResourceParser.ExtractResourceMap(tree);
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems = VfxSystemParser.ExtractAll(tree)
                .ToDictionary(
                    static pair => pair.Key,
                    pair => pair.Value with { ResourceMap = resourceMap });
            IReadOnlyList<AnimationGraphDefinition> animationGraphs =
                VfxAnimationParser.ExtractAnimationGraphs(tree, graphHashNameResolver, graphClassNameResolver);
            return new VfxBinDocument(
                systems,
                resourceMap,
                VfxResourceParser.ExtractSkinResourceMap(tree),
                tree.Dependencies.ToArray(),
                VfxAnimationParser.ExtractEventSequences(tree, animationGraphs),
                VfxAnimationParser.ExtractOwnerSceneContext(tree),
                VfxAnimationParser.ExtractIdleEffects(tree),
                animationGraphs);
        }
    }
}
