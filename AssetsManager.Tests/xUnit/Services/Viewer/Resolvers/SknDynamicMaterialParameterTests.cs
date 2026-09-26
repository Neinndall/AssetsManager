using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Resolvers;

public sealed class SknDynamicMaterialParameterTests(ITestOutputHelper output)
{
    private static uint H(string name) => Fnv1a.HashLower(name);
    private static BinTreeStruct Driver(string type, params BinTreeProperty[] fields) => new(H("driver"), H(type), fields);
    private static IReadOnlyList<AssetsManager.Views.Models.Viewer.GameMaterialDynamicParameter> Parse(
        BinTreeStruct driver, bool enabled = true)
    {
        var dynamic = Driver("DynamicMaterialDef", new BinTreeContainer(H("parameters"), BinPropertyType.Embedded,
            new BinTreeProperty[] { new BinTreeEmbedded(0, H("DynamicMaterialParameterDef"), new BinTreeProperty[]
            {
                new BinTreeString(H("name"), "Test"), new BinTreeBool(H("Enabled"), enabled), driver
            }) }));
        return SknDynamicMaterialParser.ReadParameters(new Dictionary<uint, BinTreeProperty> { [H("dynamicMaterial")] = dynamic });
    }

    [Fact]
    public void InactiveBuffUsesSchemaOffDefaultsInsteadOfStaticGlow()
    {
        var buff = new BinTreeStruct(H("BoolDriver"), H("HasBuffDynamicMaterialBoolDriver"), Array.Empty<BinTreeProperty>());
        var vector = Assert.Single(Parse(Driver("LerpVec4LogicDriver", buff,
            new BinTreeVector4(H("OnValue"), new(2, 2, 1, 1)))));
        Assert.Equal(new Vector4(0, 0, 0, 1), vector.Evaluate(0));
        var scalarBuff = new BinTreeStruct(H("mBoolDriver"), buff.ClassHash, Array.Empty<BinTreeProperty>());
        var scalar = Assert.Single(Parse(Driver("LerpMaterialDriver", scalarBuff)));
        Assert.Equal(Vector4.Zero, scalar.Evaluate(0));
        Assert.Empty(Parse(Driver("Float4LiteralMaterialDriver"), enabled: false));
        var unknown = Assert.Single(Parse(Driver("LerpMaterialDriver", Driver("UnknownBool"))));
        Assert.Null(unknown.Evaluate(0));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 7)]
    [InlineData(2, 3)]
    public void SwitchUsesGearBranchAndRestoresDefault(int gear, float expected)
    {
        var condition = new BinTreeStruct(H("mCondition"), H("HasGearDynamicMaterialBoolDriver"),
            new BinTreeProperty[] { new BinTreeU8(H("mGearIndex"), 1) });
        var value = new BinTreeStruct(H("mValue"), H("Float4LiteralMaterialDriver"),
            new BinTreeProperty[] { new BinTreeVector4(H("value"), new(7, 0, 0, 0)) });
        var fallback = new BinTreeStruct(H("mDefaultValue"), H("Float4LiteralMaterialDriver"),
            new BinTreeProperty[] { new BinTreeVector4(H("value"), new(3, 0, 0, 0)) });
        var driver = Driver("SwitchMaterialDriver", fallback,
            new BinTreeContainer(H("mElements"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("SwitchMaterialDriverElement"), new BinTreeProperty[] { condition, value })
            }));
        Assert.Equal(new Vector4(expected, 0, 0, 0), Assert.Single(Parse(driver)).Evaluate(gear));
    }

    [SettDiagnosticFact]
    public void InspectSett66FormGeometryAndSamplers()
    {
        string root = Environment.GetEnvironmentVariable("ASSETSMANAGER_SETT_DIAGNOSTIC_ROOT");
        using var loader = new AssetsManager.Services.Viewer.Vfx.Loading.VfxLoadingService();
        var bundle = loader.Load(Path.Combine(root, "data/characters/sett/skins/skin66.bin"), null);
        var trees = bundle.LoadedBins.Select(path =>
        {
            using var input = File.OpenRead(path);
            return new BinTree(input);
        }).ToArray();
        using var mesh = LeagueToolkit.Core.Mesh.SkinnedMesh.ReadFromSimpleSkin(
            Path.Combine(root, "assets/characters/sett/skins/skin66/sett_skin66.skn"));
        var names = mesh.Ranges.Select(range => range.Material).Distinct().ToDictionary(H, name => name);
        string Name(uint hash) => names.GetValueOrDefault(hash) ?? $"{hash:x8}";
        var paths = Directory.EnumerateFiles(root, "*.tex", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToDictionary(path => XxHash64Ext.Hash(path.ToLowerInvariant()), path => path);
        var metadata = SknMaterialTextureResolver.ReadMetadata(trees, hash => paths.GetValueOrDefault(hash) ?? $"{hash:x16}");
        void Dump(BinTreeProperty property, int depth)
        {
            string value = property.GetType().GetProperty("Value")?.GetValue(property)?.ToString() ?? "";
            output.WriteLine(new string(' ', depth * 2) + $"{property.NameHash:x8} {property.GetType().Name} " +
                (property is BinTreeStruct typed ? $"class={typed.ClassHash:x8} " : "") + value);
            if (depth > 10) return;
            if (property is BinTreeStruct nested) foreach (var child in nested.Properties.Values) Dump(child, depth + 1);
            if (property is BinTreeContainer container) foreach (var child in container.Elements) Dump(child, depth + 1);
        }
        foreach (var obj in trees.SelectMany(tree => tree.Objects.Values).Where(obj => obj.ClassHash == 0x27dd6361))
        {
            output.WriteLine($"GEAR {obj.PathHash:x8}");
            foreach (var property in obj.Properties.Values) Dump(property, 0);
        }
        output.WriteLine("INITIAL HIDDEN: " + string.Join(", ", bundle.OwnerSceneContext.InitialHiddenSubmeshHashes.Select(Name)));
        foreach (var form in bundle.CharacterForms)
            output.WriteLine($"FORM gear={form.GearIndex}: show={string.Join(", ", form.ShowSubmeshHashes.Select(Name))}; hide={string.Join(", ", form.HideSubmeshHashes.Select(Name))}");
        foreach (var clip in bundle.Clips)
        {
            if (clip.UsesEquippedGearParameter)
                output.WriteLine($"GEAR CLIP {clip.ClipName} {clip.OwnerPathHash:x8}: params={string.Join(",", clip.ParametricValues ?? Array.Empty<float?>())} children={string.Join(",",clip.ChildClipHashes ?? Array.Empty<uint>())}");
            foreach (var cue in clip.Events.OfType<AssetsManager.Views.Models.Viewer.AnimationSubmeshVisibilityEventDefinition>())
                output.WriteLine($"VISIBILITY {clip.ClipName} {clip.OwnerPathHash:x8}: show={string.Join(",",cue.ShowSubmeshHashes.Select(Name))}; hide={string.Join(",",cue.HideSubmeshHashes.Select(Name))}");
        }
        foreach (var pair in metadata.OverrideMaterials)
        {
            output.WriteLine($"MATERIAL {pair.Key}: shader={pair.Value?.ShaderHash:x8}");
            if (pair.Value == null) continue;
            foreach (var sampler in pair.Value.Samplers)
                output.WriteLine($"  {sampler.TextureName}: {sampler.TexturePath}");
            output.WriteLine("SWITCHES: " + string.Join(",", pair.Value.SwitchStates));
        }

    }

    [SettDiagnosticFact]
    public void Sett66FormsResolveAuthoredParametersAndAllTextureSamplers()
    {
        string root = Environment.GetEnvironmentVariable("ASSETSMANAGER_SETT_DIAGNOSTIC_ROOT");
        using var stream = File.OpenRead(Path.Combine(root, "data/characters/sett/skins/skin66.bin"));
        var tree = new BinTree(stream);
        var paths = Directory.EnumerateFiles(root, "*.tex", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToDictionary(path => XxHash64Ext.Hash(path.ToLowerInvariant()), path => path);
        var metadata = SknMaterialTextureResolver.ReadMetadata(new[] { tree }, hash => paths.GetValueOrDefault(hash) ?? $"{hash:x16}");
        var keys = paths.Values.Select(Path.GetFileNameWithoutExtension).ToArray();
        var material = SknMaterialTextureResolver.Resolve(metadata, keys).ResolveMaterialDefinition("Base");
        Assert.Equal(10, material.DynamicParameters.Count);
        foreach (int gear in new[] { 0, 1, 2, 0 })
        {
            var values = material.DynamicParameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Evaluate(gear));
            Assert.Equal(Vector4.One, values["TintColor"]);
            Assert.Equal(new Vector4(0, 0, 0, 1), values["IridescentControl"]);
            Assert.Equal(Vector4.Zero, values["Bloom_Intensity"]);
            Assert.Equal(Vector4.Zero, values["AdditiveStrength_R"]);
            Assert.Equal(Vector4.Zero, values["AdditiveStrength_G"]);
            Assert.Equal(Vector4.Zero, values["OutlineThickness"]);
            Assert.Equal(new Vector4(0, gear == 0 ? .5f : gear == 1 ? -1f : 0, 0, 0), values["AdditiveTexScrollSpeed_R"]);
            Assert.Equal(new Vector4(0, gear == 0 ? -.5f : gear == 1 ? .5f : 0, 0, 0), values["AdditiveTexScrollSpeed_G"]);
            Assert.Equal(gear == 0 ? new Vector4(.25f, .4f, .66f, 1) : gear == 1 ? new Vector4(.91f, .43f, .26f, 1) : new Vector4(.7f, .15f, 0, 1), values["OutlineColor"]);
            output.WriteLine($"gear={gear}: diffuse={material.ResolveTextureSwap("Diffuse_Texture", gear)}; iridescence={values["IridescentControl"]}; bloom={values["Bloom_Intensity"]}");
            foreach (var swap in material.TextureSwaps)
                if (swap.Resolve(gear) is string path) Assert.Contains(path, metadata.ReferencedTexturePaths);
            if (gear < 2)
            {
                string form = gear == 0 ? "dark" : "gold";
                Assert.EndsWith($"sett_skin66_{form}_tx_cm.tex", material.ResolveTextureSwap("Diffuse_Texture", gear));
                Assert.EndsWith($"sett_skin66_{form}_additivescrollmask_tx_cm.tex", material.ResolveTextureSwap("Color_Mask_Texture", gear));
                Assert.Equal(material.ResolveTextureSwap("Color_Mask_Texture", gear), material.ResolveTextureSwap("AdditiveScroll_Mask", gear));
            }
        }
    }
}

public sealed class SettDiagnosticFactAttribute : FactAttribute
{
    public SettDiagnosticFactAttribute()
    {
        string root = Environment.GetEnvironmentVariable("ASSETSMANAGER_SETT_DIAGNOSTIC_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            Skip = "Set ASSETSMANAGER_SETT_DIAGNOSTIC_ROOT to an extracted Sett WAD folder.";
    }
}
