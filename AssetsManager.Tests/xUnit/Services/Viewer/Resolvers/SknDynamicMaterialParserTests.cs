using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Resolvers;

public sealed class SknDynamicMaterialParserTests
{
    private static uint Hash(string name) => Fnv1a.HashLower(name);
    private static BinTreeStruct Gear(byte gear) => new(Hash("driver"), Hash("HasGearDynamicMaterialBoolDriver"),
        new BinTreeProperty[] { new BinTreeU8(Hash("mGearIndex"), gear) });
    private static BinTreeEmbedded Option(BinTreeStruct driver, BinTreeProperty texture) =>
        new(0, Hash("DynamicMaterialTextureSwapOption"), new BinTreeProperty[] { driver, texture });
    private static IReadOnlyList<GameMaterialTextureSwap> Parse(bool? enabled = null, bool hashed = false)
    {
        var buff = new BinTreeStruct(0, Hash("HasBuffDynamicMaterialBoolDriver"), Array.Empty<BinTreeProperty>());
        var all = new BinTreeStruct(Hash("driver"), Hash("AllTrueMaterialDriver"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("mDrivers"), BinPropertyType.Struct, new BinTreeProperty[] { buff, Gear(2) })
        });
        var properties = new List<BinTreeProperty>
        {
            new BinTreeString(Hash("name"), "Main_Texture"),
            new BinTreeContainer(Hash("options"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                Option(all, new BinTreeString(Hash("TextureName"), "buff.tex")),
                Option(Gear(2), hashed ? new BinTreeWadChunkLink(Hash("TextureName"), 42) :
                    new BinTreeString(Hash("TextureName"), "form3.tex")),
                Option(Gear(1), new BinTreeString(Hash("TextureName"), "form2.tex"))
            })
        };
        if (enabled.HasValue) properties.Add(new BinTreeBool(Hash("Enabled"), enabled.Value));
        var dynamicMaterial = new BinTreeStruct(Hash("dynamicMaterial"), Hash("DynamicMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("textures"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, Hash("DynamicMaterialTextureSwapDef"), properties)
            })
        });
        return SknDynamicMaterialParser.Read(new Dictionary<uint, BinTreeProperty>
            { [dynamicMaterial.NameHash] = dynamicMaterial }, hash => hash == 42 ? "form3.tex" : null);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "form2.tex")]
    [InlineData(2, "form3.tex")]
    [InlineData(3, null)]
    public void AuthoredGearSelectsTextureWithoutActivatingGameplayBuffs(int gear, string expected)
    {
        var swap = Assert.Single(Parse());
        Assert.Equal("Main_Texture", swap.SamplerName);
        Assert.Equal(expected, swap.Resolve(gear));
    }

    [Fact]
    public void DisabledSwapIsIgnoredAndWadLinksResolveThroughProjectResolver()
    {
        Assert.Empty(Parse(enabled: false));
        Assert.Equal("form3.tex", Assert.Single(Parse(hashed: true)).Resolve(2));
    }

    [Fact]
    public void UnknownDriversRemainUnknownUnderNegationAndConjunction()
    {
        var unknown = new GameMaterialBoolCondition(GameMaterialBoolKind.Unsupported);
        var not = new GameMaterialBoolCondition(GameMaterialBoolKind.Not, Children: new[] { unknown });
        var all = new GameMaterialBoolCondition(GameMaterialBoolKind.All,
            Children: new[] { unknown, new GameMaterialBoolCondition(GameMaterialBoolKind.Gear, 1) });
        Assert.Null(not.Evaluate(1));
        Assert.Null(all.Evaluate(1));
        Assert.False(all.Evaluate(2));
    }
}
