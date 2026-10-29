using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Resolvers;

public sealed class CharacterOutlineReaderTests
{
    private static uint H(string name) => Fnv1a.HashLower(name);
    private static BinTreeObject Material(string path, params (string, Vector4)[] parameters) => new(path, "StaticMaterialDef", new BinTreeProperty[]
    {
        new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded, parameters.Select(pair =>
            new BinTreeEmbedded(0, H("StaticMaterialShaderParamDef"), new BinTreeProperty[]
            { new BinTreeString(H("name"), pair.Item1), new BinTreeVector4(H("value"), pair.Item2) })))
    });
    private static BinTreeEmbedded Entry(string category, string submeshes, string material = null) =>
        new(0, H("CharacterOutlineSubmeshes"), new BinTreeProperty[]
        {
            new BinTreeObjectLink(H("category"), H(category)), new BinTreeString(H("Submeshes"), submeshes),
            new BinTreeObjectLink(H("materialOverride"), material == null ? 0 : H(material))
        });
    private static BinTree Tree(params BinTreeObject[] objects) => new(objects, Array.Empty<string>());
    private static BinTreeObject Skin(params BinTreeEmbedded[] entries) => new("Characters/Test/Skins/Skin1", "SkinCharacterDataProperties", new BinTreeProperty[]
    {
        new BinTreeStruct(H("skinMeshProperties"), H("SkinMeshDataProperties"), new BinTreeProperty[]
        { new BinTreeUnorderedContainer(H("OutlineCategorySubmeshes"), BinPropertyType.Embedded, entries) })
    });

    [Fact]
    public void CategoriesOutsideSkinLinksResolveAndFirstValidEntryWinsWithoutReplacingTheSurface()
    {
        var skin = Tree(Skin(Entry("Absent", "Body"), Entry("Category", "Body Hood,Cape"), Entry("Category", "BODY,Wings", "Override")));
        var map = Tree(new BinTreeObject("Category", "CharacterOutlineCategory", new BinTreeProperty[] { new BinTreeObjectLink(H("Material"), H("Outline")) }),
            Material("Outline", ("Outline_Width", new(5, 0, 0, 0)), ("Outline_CameraDistanceMod", new(4500, 0, 0, 0)),
                ("Outline_CameraDepthOffset", new(-20, 0, 0, 0)), ("Outline_Color", new(.02f, .09f, .05f, 0))),
            Material("Override", ("Outline_Width", new(2, 0, 0, 0))));
        var metadata = SknMaterialTextureResolver.ReadMetadata(new[] { skin, map });
        Assert.Equal(4, metadata.Outlines.Count);
        var resolved = SknMaterialTextureResolver.Resolve(metadata, Array.Empty<string>());
        var body = resolved.ResolveMaterialDefinition("bOdY");
        Assert.Equal(ModelMaterialBindingKind.TextureOnly, body.BindingKind);
        Assert.Equal(H("Outline"), body.Outline.MaterialHash);
        Assert.Equal(5, body.Outline.Width);
        Assert.Equal(4500, body.Outline.CameraDistance);
        Assert.Equal(-20, body.Outline.DepthOffset);
        Assert.Equal(new Vector3(.02f, .09f, .05f), body.Outline.Color);
        Assert.Equal(1, body.Outline.Alpha);
        Assert.Equal(new Vector4(1, 0, 0, 0), body.Outline.MaskChannels);
        Assert.Equal(2, resolved.ResolveMaterialDefinition("Wings").Outline.Width);
        Assert.Equal(600, resolved.ResolveMaterialDefinition("Wings").Outline.CameraDistance);
        Assert.Null(resolved.ResolveMaterialDefinition("Tail").Outline);
    }

    [Fact]
    public void AnUnresolvedOverrideDoesNotFallBackToTheCategoryAndProjectObjectsWin()
    {
        var skin = Tree(Skin(Entry("Category", "Body", "MissingOverride"), Entry("Category", "Wings")),
            new BinTreeObject("Category", "CharacterOutlineCategory", new BinTreeProperty[] { new BinTreeObjectLink(H("Material"), H("Outline")) }),
            Material("Outline", ("Outline_Width", new(3, 0, 0, 0))));
        var installed = Tree(Material("Outline", ("Outline_Width", new(7, 0, 0, 0))));
        var metadata = SknMaterialTextureResolver.ReadMetadata(new[] { skin, installed });
        Assert.False(metadata.Outlines.ContainsKey("Body"));
        Assert.Equal(3, metadata.Outlines["Wings"].Width);
    }

    [Fact]
    public void ExplicitZeroParametersAreNotReplacedByDefaults()
    {
        var skin = Tree(Skin(Entry("Unused", "Body", "Outline")), Material("Outline",
            ("Outline_Width", Vector4.Zero), ("Outline_FinalAlphaMult", Vector4.Zero), ("Outline_Mask_ChannelMask", Vector4.Zero)));
        var outline = SknMaterialTextureResolver.ReadMetadata(skin).Outlines["Body"];
        Assert.Equal(0, outline.Width);
        Assert.Equal(0, outline.Alpha);
        Assert.Equal(Vector4.Zero, outline.MaskChannels);
        Assert.Equal(20, outline.DepthOffset);
    }

    [Fact]
    public void MaterialWinsOverPassThenShaderDefaultsAndUndeclaredParametersUseThePublishedDefault()
    {
        var material = Material("Outline", ("Outline_Width", new(5, 0, 0, 0)));
        material.Properties[H("techniques")] = new BinTreeUnorderedContainer(H("techniques"), BinPropertyType.Embedded, new[]
        {
            new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
            {
                new BinTreeString(H("name"), "normal"),
                new BinTreeUnorderedContainer(H("passes"), BinPropertyType.Embedded, new[]
                {
                    new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
                    {
                        new BinTreeObjectLink(H("shader"), H("Shader")),
                        Material("Unused", ("Outline_Width", new(2, 0, 0, 0))).Properties[H("paramValues")]
                    })
                })
            })
        });
        var shader = new BinTreeObject("Shader", "CustomShaderDef", new BinTreeProperty[]
        {
            new BinTreeUnorderedContainer(H("parameters"), BinPropertyType.Embedded, new[]
            {
                new BinTreeEmbedded(0, 1, new BinTreeProperty[]
                { new BinTreeString(H("name"), "Outline_CameraDistanceMod"), new BinTreeVector4(H("data"), new(1200, 0, 0, 0)) }),
                new BinTreeEmbedded(0, 1, new BinTreeProperty[]
                { new BinTreeString(H("name"), "Outline_Width"), new BinTreeVector4(H("data"), new(9, 0, 0, 0)) })
            }),
            new BinTreeUnorderedContainer(H("textures"), BinPropertyType.Embedded, new[]
            {
                new BinTreeEmbedded(0, 1, new BinTreeProperty[]
                { new BinTreeString(H("name"), "Outline_Mask"), new BinTreeString(H("defaultTexturePath"), "assets/default_mask.tex") })
            })
        });
        var skin = Tree(Skin(Entry("Unused", "Body", "Outline")), material);
        SknMaterialTextureMetadata Read() => SknMaterialTextureResolver.ReadMetadata(new[] { skin, Tree(shader) });
        var metadata = Read();
        var outline = metadata.Outlines["Body"];
        Assert.Equal(5, outline.Width);
        Assert.Equal(1200, outline.CameraDistance);
        Assert.Equal("assets/default_mask.tex", outline.MaskPath);
        Assert.Contains(outline.MaskPath, metadata.ReferencedTexturePaths);
        material.Properties.Remove(H("paramValues"));
        Assert.Equal(2, Read().Outlines["Body"].Width);
        var technique = (BinTreeStruct)((BinTreeContainer)material.Properties[H("techniques")]).Elements[0];
        var pass = (BinTreeStruct)((BinTreeContainer)technique.Properties[H("passes")]).Elements[0];
        pass.Properties.Remove(H("paramValues"));
        Assert.Equal(9, Read().Outlines["Body"].Width);
        shader.Properties.Remove(H("parameters"));
        material.Properties[H("paramValues")] = Material("Unused", ("Outline_Width", new(17, 0, 0, 0))).Properties[H("paramValues")];
        Assert.Equal(.25f, Read().Outlines["Body"].Width);
        foreach (var texture in new BinTreeProperty[]
        {
            new BinTreeWadChunkLink(H("texturePath"), 0ul),
            new BinTreeString(H("texturePath"), string.Empty),
            new BinTreeString(H("texturePath"), "legacy-string-path.tex")
        })
        {
            material.Properties[H("samplerValues")] = new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded, new[]
            {
                new BinTreeEmbedded(0, 1, new BinTreeProperty[] { new BinTreeString(H("TextureName"), "Outline_Mask"), texture })
            });
            Assert.Equal("assets/default_mask.tex", Read().Outlines["Body"].MaskPath);
        }
    }
}
