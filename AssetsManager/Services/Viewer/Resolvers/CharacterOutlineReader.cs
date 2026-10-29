using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Resolvers
{
    internal sealed record CharacterOutlineLink(uint Category, uint Material, IReadOnlyList<string> Submeshes);

    /// <summary>Reads skin inking links and their material/shader defaults without changing surface materials.</summary>
    internal static class CharacterOutlineReader
    {
        private static uint H(string name) => Fnv1a.HashLower(name);
        internal static uint Link(BinTreeStruct fields, string name) =>
            fields.Properties.TryGetValue(H(name), out var value) && value is BinTreeObjectLink link ? link.Value : 0;

        internal static IReadOnlyList<CharacterOutlineLink> Links(IEnumerable<BinTreeStruct> meshes)
        {
            var field = meshes.FirstOrDefault(mesh => mesh?.Properties.ContainsKey(H("OutlineCategorySubmeshes")) == true);
            if (field == null || field.Properties[H("OutlineCategorySubmeshes")] is not BinTreeContainer entries)
                return Array.Empty<CharacterOutlineLink>();
            return entries.Elements.OfType<BinTreeStruct>().Select(entry => new CharacterOutlineLink(
                Link(entry, "category"), Link(entry, "materialOverride"),
                entry.Properties.TryGetValue(H("Submeshes"), out var names) && names is BinTreeString text
                    ? text.Value.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    : Array.Empty<string>())).ToArray();
        }

        internal static IReadOnlyDictionary<string, CharacterOutlineDefinition> Read(
            IReadOnlyList<CharacterOutlineLink> links, IReadOnlyList<BinTree> trees, IReadOnlyList<BinTree> shaders,
            Func<ulong, string> paths, Func<uint, string> names)
        {
            var result = new Dictionary<string, CharacterOutlineDefinition>(StringComparer.OrdinalIgnoreCase);
            BinTreeObject Object(uint hash) => trees.Select(tree => tree.Objects.GetValueOrDefault(hash)).FirstOrDefault(obj => obj != null);
            foreach (var link in links)
            {
                uint materialHash = link.Material;
                if (materialHash == 0 && Object(link.Category) is { } category &&
                    category.Properties.TryGetValue(H("Material"), out var property) && property is BinTreeObjectLink materialLink)
                    materialHash = materialLink.Value;
                var material = SknMaterialTextureResolver.ResolveLinkedMaterialDefinition(trees, materialHash, paths, names, shaders);
                if (material == null) continue;
                var shader = SknMaterialTextureResolver.ResolveLinkedShaderDefinition(shaders, material.ShaderHash, paths, names);
                Vector4 Parameter(string name, Vector4 fallback)
                {
                    if (shader != null && !shader.DefaultParameters.ContainsKey(name)) return fallback;
                    if (material.Parameters.TryGetValue(name, out var value)) return value;
                    if (material.Pass?.Parameters?.TryGetValue(name, out var passValue) == true) return passValue;
                    return shader?.DefaultParameters.GetValueOrDefault(name, fallback) ?? fallback;
                }
                var mask = material.Samplers.FirstOrDefault(s => s.TextureName == "Outline_Mask")
                    ?? shader?.DefaultSamplers.FirstOrDefault(s => s.TextureName == "Outline_Mask");
                if (string.IsNullOrWhiteSpace(mask?.TexturePath) || mask.UsesShaderDefaultTexture)
                    mask = shader?.DefaultSamplers.FirstOrDefault(s => s.TextureName == "Outline_Mask");
                var color = Parameter("Outline_Color", new(0, 0, 0, 1));
                var outline = new CharacterOutlineDefinition(materialHash, new(color.X, color.Y, color.Z),
                    Parameter("Outline_Width", new(.25f, 0, 0, 0)).X,
                    Parameter("Outline_CameraDistanceMod", new(600, 0, 0, 0)).X,
                    Parameter("Outline_CameraDepthOffset", new(20, 0, 0, 0)).X,
                    Parameter("Outline_FinalAlphaMult", new(1, 0, 0, 0)).X,
                    mask?.TexturePath, Parameter("Outline_Mask_ChannelMask", new(1, 0, 0, 0)),
                    SknStaticMaterialResolver.ResolveAuthoredPreview(material, shader) with
                    { DynamicParameters = Array.Empty<GameMaterialDynamicParameter>() });
                foreach (string submesh in link.Submeshes) result.TryAdd(submesh, outline);
            }
            return result;
        }
    }
}
