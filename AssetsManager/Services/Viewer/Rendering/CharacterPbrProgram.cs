using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Rendering;

/// <summary>Selects the translated texture-only skin permutation from resources available for this draw.</summary>
internal static class CharacterPbrProgram
{
    private static readonly ConditionalWeakTable<ModelMaterialDefinition, Dictionary<(string, string), ModelMaterialDefinition>> Variants = new();

    internal static ModelMaterialDefinition Resolve(ModelMaterialDefinition material, bool tangents, Func<string, uint?> texture)
    {
        if (material?.BindingKind != ModelMaterialBindingKind.TextureOnly || material.Pbr == null)
            return material;

        string Available(IReadOnlyList<string> candidates, string fallback)
        {
            if (candidates != null)
                foreach (string path in candidates)
                    if (!string.IsNullOrEmpty(path) && texture(path) is uint id && id != 0) return path;
            return !string.IsNullOrEmpty(fallback) && texture(fallback) is uint held && held != 0 ? fallback : null;
        }
        var maps = material.Pbr;
        string rma = Available(maps.RmaPaths, maps.Rma);
        string normal = rma != null && tangents ? Available(maps.NormalPaths, maps.Normal) : null;
        var variants = Variants.GetOrCreateValue(material);
        lock (variants)
        {
            var key = (rma, normal);
            if (!variants.TryGetValue(key, out var variant))
            {
                variant = material with
                {
                    Program = GameShaderProgramResolver.CreateDefaultSkinnedProgram(
                        material.BaseTextureName,
                        material.EmissiveTexturePath, rma, normal)
                };
                variants.Add(key, variant);
            }
            return variant;
        }
    }
}
