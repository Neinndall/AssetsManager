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

    // Aatrox Skin40 body: Dissolve_Bias = max(curve(isDead), curve(isPlaying "Passive_Death")), resting at curve(0).
    [Fact]
    public void FloatGraphDriversUnderMaxRestAtTheCurveStart()
    {
        static BinTreeStruct Graph(string condition, float first) => new(0, Hash("FloatGraphMaterialDriver"), new BinTreeProperty[]
        {
            new BinTreeStruct(Hash("driver"), Hash("LerpMaterialDriver"), new BinTreeProperty[]
            {
                new BinTreeStruct(Hash("mBoolDriver"), Hash(condition), Array.Empty<BinTreeProperty>())
            }),
            new BinTreeStruct(Hash("graph"), Hash("VfxAnimatedFloatVariableData"), new BinTreeProperty[]
            {
                new BinTreeContainer(Hash("times"), BinPropertyType.F32, new BinTreeProperty[] { new BinTreeF32(0, 0f), new BinTreeF32(0, 1f) }),
                new BinTreeContainer(Hash("values"), BinPropertyType.F32, new BinTreeProperty[] { new BinTreeF32(0, first), new BinTreeF32(0, 0.9f) })
            })
        });
        var max = new BinTreeStruct(Hash("driver"), Hash("MaxMaterialDriver"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("mDrivers"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                Graph("IsDeadDynamicMaterialBoolDriver", -0.22f),
                Graph("IsAnimationPlayingDynamicMaterialBoolDriver", -0.23f)
            })
        });
        var dynamicMaterial = new BinTreeStruct(Hash("dynamicMaterial"), Hash("DynamicMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("parameters"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, Hash("DynamicMaterialParameterDef"), new BinTreeProperty[]
                {
                    new BinTreeString(Hash("name"), "Dissolve_Bias"),
                    max
                })
            })
        });

        GameMaterialDynamicParameter parameter = Assert.Single(SknDynamicMaterialParser.ReadParameters(
            new Dictionary<uint, BinTreeProperty> { [dynamicMaterial.NameHash] = dynamicMaterial }));

        Assert.Equal("Dissolve_Bias", parameter.Name);
        // Float drivers fill every component, as a colour parameter reads them.
        System.Numerics.Vector4 value = parameter.Evaluate(0)!.Value;
        Assert.Equal(-0.22f, value.X, 5);
        Assert.Equal(value.X, value.W);
    }

    // Aatrox Skin33 sword fire: DissolveValue = Recall playing ? 1 : (AatroxInCombat ? 1 : 0).
    [Fact]
    public void BuffAndAnimationConditionsFollowTheGameState()
    {
        var recall = new BinTreeStruct(Hash("mCondition"), Hash("IsAnimationPlayingDynamicMaterialBoolDriver"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("mAnimationNames"), BinPropertyType.Hash, new BinTreeProperty[] { new BinTreeHash(0, Hash("Recall")) })
        });
        var combat = new BinTreeStruct(Hash("mDefaultValue"), Hash("LerpMaterialDriver"), new BinTreeProperty[]
        {
            new BinTreeStruct(Hash("mBoolDriver"), Hash("HasBuffDynamicMaterialBoolDriver"), new BinTreeProperty[]
            {
                new BinTreeString(Hash("mScriptName"), "AatroxInCombat")
            })
        });
        var driver = new BinTreeStruct(Hash("driver"), Hash("SwitchMaterialDriver"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("mElements"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, Hash("SwitchMaterialDriverElement"), new BinTreeProperty[]
                {
                    recall,
                    new BinTreeStruct(Hash("mValue"), Hash("Float4LiteralMaterialDriver"), Array.Empty<BinTreeProperty>())
                })
            }),
            combat
        });
        var dynamicMaterial = new BinTreeStruct(Hash("dynamicMaterial"), Hash("DynamicMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("parameters"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, Hash("DynamicMaterialParameterDef"), new BinTreeProperty[]
                {
                    new BinTreeString(Hash("name"), "DissolveValue"),
                    driver
                })
            })
        });

        GameMaterialDynamicParameter parameter = Assert.Single(SknDynamicMaterialParser.ReadParameters(
            new Dictionary<uint, BinTreeProperty> { [dynamicMaterial.NameHash] = dynamicMaterial }));

        Assert.Equal(new[] { "AatroxInCombat" }, parameter.Buffs);
        Assert.Equal(0f, parameter.Evaluate(GameMaterialState.Resting)!.Value.X);
        Assert.Equal(1f, parameter.Evaluate(GameMaterialState.From(0, new[] { "aatroxincombat" }, null))!.Value.X);
        Assert.Equal(1f, parameter.Evaluate(GameMaterialState.From(0, null, new[] { GameMaterialState.AnimationHash("Recall") }))!.Value.X);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(0.25f, 5f)]
    [InlineData(0.75f, 15f)]
    [InlineData(2f, 20f)]
    public void CurveInterpolatesBetweenKeysAndHoldsItsEnds(float at, float expected)
    {
        Assert.Equal(expected, SknDynamicMaterialParser.SampleCurve(new[] { 0f, 0.5f, 1f }, new[] { 0f, 10f, 20f }, at), 5);
    }
}
