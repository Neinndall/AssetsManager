using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Resolvers;
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

        internal static BinTree ParseTree(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            using var stream = new MemoryStream(data, writable: false);
            return new BinTree(stream);
        }


        internal static VfxBinDocument ParseDocument(
            byte[] data,
            Func<uint, string> graphHashNameResolver = null,
            Func<uint, string> graphClassNameResolver = null,
            Func<ulong, string> wadChunkPathResolver = null,
            Func<uint, string> binEntryResolver = null)
            => ParseDocument(
                ParseTree(data),
                graphHashNameResolver,
                graphClassNameResolver,
                wadChunkPathResolver,
                binEntryResolver);

        internal static VfxBinDocument ParseDocument(
            BinTree tree,
            Func<uint, string> graphHashNameResolver = null,
            Func<uint, string> graphClassNameResolver = null,
            Func<ulong, string> wadChunkPathResolver = null,
            Func<uint, string> binEntryResolver = null)
        {
            ArgumentNullException.ThrowIfNull(tree);
            IReadOnlyDictionary<uint, uint> resourceMap = VfxResourceParser.ExtractResourceMap(tree);
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems = VfxSystemParser.ExtractAll(tree)
                .ToDictionary(
                    static pair => pair.Key,
                    pair => ResolveCustomMaterials(
                        pair.Value with { ResourceMap = resourceMap },
                        tree,
                        wadChunkPathResolver,
                        binEntryResolver));
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

        private static VfxSystemDefinition ResolveCustomMaterials(
            VfxSystemDefinition system,
            BinTree tree,
            Func<ulong, string> wadChunkPathResolver,
            Func<uint, string> binEntryResolver)
        {
            VfxEmitterDefinition[] emitters = null;
            for (int index = 0; index < system.Emitters.Count; index++)
            {
                VfxEmitterDefinition emitter = system.Emitters[index];
                if (emitter.CustomMaterialPathHash == 0) continue;

                ModelMaterialDefinition material = SknMaterialTextureResolver.ResolveLinkedMaterialPreview(
                    tree,
                    emitter.CustomMaterialPathHash,
                    out VfxCustomMaterialBlendFactor sourceBlendFactor,
                    out VfxCustomMaterialBlendFactor destinationBlendFactor,
                    wadChunkPathResolver,
                    binEntryResolver);
                VfxEmitterDefinition resolved = emitter with
                {
                    CustomMaterial = material,
                    CustomMaterialSourceBlendFactor = sourceBlendFactor,
                    CustomMaterialDestinationBlendFactor = destinationBlendFactor
                };
                if (material.BindingKind != ModelMaterialBindingKind.Missing)
                    resolved = resolved with { TexturePath = material.BaseTextureName };

                emitters ??= system.Emitters.ToArray();
                emitters[index] = resolved;
            }

            return emitters is null ? system : system with { Emitters = emitters };
        }
    }
}
