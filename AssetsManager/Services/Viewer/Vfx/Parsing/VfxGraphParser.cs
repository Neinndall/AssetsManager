using AssetsManager.Services.Viewer.Parsing;
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
            Func<uint, string> binEntryResolver = null,
            IEnumerable<BinTree> shaderTrees = null)
            => ParseDocument(
                ParseTree(data),
                graphHashNameResolver,
                graphClassNameResolver,
                wadChunkPathResolver,
                binEntryResolver,
                shaderTrees);

        internal static VfxBinDocument ParseDocument(
            BinTree tree,
            Func<uint, string> graphHashNameResolver = null,
            Func<uint, string> graphClassNameResolver = null,
            Func<ulong, string> wadChunkPathResolver = null,
            Func<uint, string> binEntryResolver = null,
            IEnumerable<BinTree> shaderTrees = null)
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
                        binEntryResolver,
                        shaderTrees));
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
                animationGraphs,
                CharacterFormParser.ParseDocument(tree));
        }

        internal static VfxSystemDefinition ResolveCustomMaterials(
            VfxSystemDefinition system,
            BinTree tree,
            Func<ulong, string> wadChunkPathResolver = null,
            Func<uint, string> binEntryResolver = null,
            IEnumerable<BinTree> shaderTrees = null)
        {
            VfxEmitterDefinition[] emitters = null;
            for (int index = 0; index < system.Emitters.Count; index++)
            {
                VfxEmitterDefinition emitter = system.Emitters[index];
                if (emitter.Shimmer is { } component &&
                    tree.Objects.TryGetValue(system.PathHash, out var rawSystem) &&
                    rawSystem.Properties.GetValueOrDefault(component.SourceList) is BinTreeContainer list &&
                    component.SourceIndex >= 0 && component.SourceIndex < list.Elements.Count &&
                    list.Elements[component.SourceIndex] is BinTreeStruct rawEmitter &&
                    rawEmitter.Properties.GetValueOrDefault(VfxParsingHash.Fnv1a("VfxComponents")) is BinTreeStruct components &&
                    FindEmbeddedMaterial(components.Properties.GetValueOrDefault(VfxParsingHash.Fnv1a("RenderComponent"))) is { } embedded)
                {
                    var trees = shaderTrees == null ? new[] { tree } : new[] { tree }.Concat(shaderTrees).ToArray();
                    var material = SknMaterialTextureResolver.ResolveEmbeddedMaterialPreview(embedded, trees,
                        out var sourceBlend, out var destinationBlend, wadChunkPathResolver, binEntryResolver);
                    emitters ??= system.Emitters.ToArray();
                    emitters[index] = emitter with { CustomMaterial = material, TexturePath = material.BaseTextureName,
                        CustomMaterialSourceBlendFactor = sourceBlend, CustomMaterialDestinationBlendFactor = destinationBlend };
                    continue;
                }
                if (emitter.CustomMaterialPathHash == 0) continue;

                emitters ??= system.Emitters.ToArray();
                emitters[index] = ResolveCustomMaterial(emitter, tree, wadChunkPathResolver, binEntryResolver, shaderTrees);
            }

            return emitters is null ? system : system with { Emitters = emitters };
        }

        private static BinTreeStruct FindEmbeddedMaterial(BinTreeProperty value, int depth = 0)
        {
            if (value == null || depth > 24) return null;
            if (value is BinTreeOptional optional) return FindEmbeddedMaterial(optional.Value, depth + 1);
            if (value is BinTreeStruct node)
            {
                if (node.ClassHash == VfxParsingHash.Fnv1a("StaticMaterialDef")) return node;
                foreach (var field in node.Properties.Values)
                    if (FindEmbeddedMaterial(field, depth + 1) is { } found) return found;
            }
            else if (value is BinTreeContainer container)
                foreach (var item in container.Elements)
                    if (FindEmbeddedMaterial(item, depth + 1) is { } found) return found;
            return null;
        }

        /// <summary>
        /// Resolves the custom materials the BIN declaring <paramref name="system"/> left missing in the first of
        /// <paramref name="linkedTrees"/> that declares them: a skin's VFX often link a material its linked BIN holds
        /// (Akali Skin32's <c>Avatar</c> reads it from <c>Akali_Multi_Skins_*.bin</c>).
        /// </summary>
        internal static VfxSystemDefinition ResolveLinkedCustomMaterials(
            VfxSystemDefinition system,
            IReadOnlyList<BinTree> linkedTrees,
            Func<ulong, string> wadChunkPathResolver = null,
            Func<uint, string> binEntryResolver = null,
            IEnumerable<BinTree> shaderTrees = null)
        {
            if (system?.Emitters == null || linkedTrees == null || linkedTrees.Count == 0)
                return system;

            VfxEmitterDefinition[] emitters = null;
            for (int index = 0; index < system.Emitters.Count; index++)
            {
                VfxEmitterDefinition emitter = system.Emitters[index];
                if (emitter.CustomMaterialPathHash == 0 || emitter.HasResolvedCustomMaterial) continue;
                BinTree declaring = linkedTrees.FirstOrDefault(tree => tree?.Objects.ContainsKey(emitter.CustomMaterialPathHash) == true);
                if (declaring == null) continue;

                emitters ??= system.Emitters.ToArray();
                emitters[index] = ResolveCustomMaterial(emitter, declaring, wadChunkPathResolver, binEntryResolver, shaderTrees);
            }

            return emitters is null ? system : system with { Emitters = emitters };
        }

        private static VfxEmitterDefinition ResolveCustomMaterial(
            VfxEmitterDefinition emitter,
            BinTree tree,
            Func<ulong, string> wadChunkPathResolver,
            Func<uint, string> binEntryResolver,
            IEnumerable<BinTree> shaderTrees)
        {
            ModelMaterialDefinition material = SknMaterialTextureResolver.ResolveLinkedMaterialPreview(
                tree,
                emitter.CustomMaterialPathHash,
                out VfxCustomMaterialBlendFactor sourceBlendFactor,
                out VfxCustomMaterialBlendFactor destinationBlendFactor,
                wadChunkPathResolver,
                binEntryResolver,
                shaderTrees);
            VfxEmitterDefinition resolved = emitter with
            {
                CustomMaterial = material,
                CustomMaterialSourceBlendFactor = sourceBlendFactor,
                CustomMaterialDestinationBlendFactor = destinationBlendFactor
            };
            return material.BindingKind != ModelMaterialBindingKind.Missing
                ? resolved with { TexturePath = material.BaseTextureName }
                : resolved;
        }
    }
}
