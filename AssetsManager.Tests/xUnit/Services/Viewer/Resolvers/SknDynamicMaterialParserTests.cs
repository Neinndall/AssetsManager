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
        // Each state lands a second apart, past the fade window, so it settles at its value.
        Assert.Equal(1f, parameter.Evaluate(GameMaterialState.From(0, new[] { "aatroxincombat" }, null) with { Time = 1f })!.Value.X);
        Assert.Equal(1f, parameter.Evaluate(GameMaterialState.From(0, null, new[] { GameMaterialState.AnimationHash("Recall") }) with { Time = 2f })!.Value.X);
    }

    [Fact]
    public void PreviewStateCombinesAnActorsBuffsWithItsOwnClip()
    {
        AnimationClipCatalogItem Clip(string name) => new(name, name, "", 1f, null, null, null, Array.Empty<AnimationClipTimedCue>(), 0, false, "");

        Assert.Null(GameMaterialState.Preview(null, null));
        Assert.Null(GameMaterialState.Preview(Array.Empty<string>(), AnimationClipCatalogItem.CreateBindPoseItem()));

        GameMaterialState recall = GameMaterialState.Preview(new[] { "AatroxInCombat" }, Clip("Recall"));
        Assert.True(recall.HasBuff("aatroxincombat"));
        Assert.True(recall.IsPlaying(GameMaterialState.AnimationHash("Recall")));
        Assert.False(GameMaterialState.Preview(new[] { "AatroxInCombat" }, null).IsPlaying(GameMaterialState.AnimationHash("Recall")));
    }

    private static GameMaterialDynamicParameter Single(BinTreeStruct driver)
    {
        var dynamicMaterial = new BinTreeStruct(Hash("dynamicMaterial"), Hash("DynamicMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("parameters"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, Hash("DynamicMaterialParameterDef"), new BinTreeProperty[] { new BinTreeString(Hash("name"), "Value"), driver })
            })
        });
        return Assert.Single(SknDynamicMaterialParser.ReadParameters(
            new Dictionary<uint, BinTreeProperty> { [dynamicMaterial.NameHash] = dynamicMaterial }));
    }

    private static BinTreeStruct Buff(uint field, string script) => new(field, Hash("HasBuffDynamicMaterialBoolDriver"),
        new BinTreeProperty[] { new BinTreeString(Hash("mScriptName"), script) });

    [Fact]
    public void ColorChooserOverOneTruePicksTheOnColorWhileAnyBuffIsOn()
    {
        var anyBuff = new BinTreeStruct(Hash("mBoolDriver"), Hash("OneTrueMaterialDriver"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("mDrivers"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                Buff(0, "First"),
                new BinTreeStruct(0, Hash("IsInGrassDynamicMaterialBoolDriver"), Array.Empty<BinTreeProperty>())
            })
        });
        GameMaterialDynamicParameter parameter = Single(new BinTreeStruct(Hash("driver"), Hash("ColorChooserMaterialDriver"), new BinTreeProperty[]
        {
            anyBuff,
            new BinTreeVector4(Hash("mColorOn"), new System.Numerics.Vector4(1, 1, 0, 1))
        }));

        Assert.Equal(new[] { "First" }, parameter.Buffs);
        // mColorOff keeps LeagueToolkit's default, blue.
        Assert.Equal(new System.Numerics.Vector4(0, 0, 1, 1), parameter.Evaluate(GameMaterialState.Resting));
        Assert.Equal(new System.Numerics.Vector4(1, 1, 0, 1), parameter.Evaluate(GameMaterialState.From(0, new[] { "First" }, null) with { Time = 1f }));
    }

    [Fact]
    public void RemapOfABuffCounterMapsItsStackIntoTheOutputRange()
    {
        GameMaterialDynamicParameter parameter = Single(new BinTreeStruct(Hash("driver"), Hash("RemapFloatMaterialDriver"), new BinTreeProperty[]
        {
            new BinTreeStruct(Hash("mDriver"), Hash("BuffCounterDynamicMaterialFloatDriver"), new BinTreeProperty[]
            {
                new BinTreeString(Hash("mScriptName"), "Stacks")
            }),
            new BinTreeF32(Hash("mOutputMinValue"), 0.25f),
            new BinTreeF32(Hash("mOutputMaxValue"), 0.75f)
        }));

        Assert.Equal(new[] { "Stacks" }, parameter.Buffs);
        Assert.Equal(0.25f, parameter.Evaluate(GameMaterialState.Resting)!.Value.X, 5);
        Assert.Equal(0.75f, parameter.Evaluate(GameMaterialState.From(0, new[] { "Stacks" }, null) with { Time = 1f })!.Value.X, 5);
        Assert.Equal(1f, SknDynamicMaterialParser.Remap(5f, 0f, 1f, 0f, 1f));
    }

    // Aatrox Skin33's combat glow: Lerp(0 > sin(2 pi 5 t)) from 0 to 10, turning off in 0.5 s and on in the default 1 s.
    [Fact]
    public void TimeDrivenComparisonPulsesAndLerpFadesTowardIt()
    {
        var sine = new BinTreeStruct(Hash("mValueB"), Hash("SineMaterialDriver"), new BinTreeProperty[]
        {
            new BinTreeStruct(Hash("mDriver"), Hash("TimeMaterialDriver"), new BinTreeProperty[] { new BinTreeBool(Hash("LoopTimeAsFraction"), false) }),
            new BinTreeF32(Hash("mFrequency"), 5f)
        });
        var comparison = new BinTreeStruct(Hash("mBoolDriver"), Hash("FloatComparisonMaterialDriver"), new BinTreeProperty[]
        {
            new BinTreeU32(Hash("mOperator"), 1),
            new BinTreeStruct(Hash("mValueA"), Hash("FloatLiteralMaterialDriver"), Array.Empty<BinTreeProperty>()),
            sine
        });
        GameMaterialDynamicParameter parameter = Single(new BinTreeStruct(Hash("driver"), Hash("LerpMaterialDriver"), new BinTreeProperty[]
        {
            comparison,
            new BinTreeF32(Hash("mOnValue"), 10f),
            new BinTreeF32(Hash("mTurnOffTimeSec"), 0.5f)
        }));

        // At 0.15 s the 5 Hz sine is at its trough, so 0 > sin holds: the first value settles on.
        Assert.Equal(10f, parameter.Evaluate(GameMaterialState.Resting with { Time = 0.15f })!.Value.X, 3);
        // At 0.25 s it peaks: 0.1 s at the 0.5 s turn-off rate drops a fifth of the way.
        Assert.Equal(8f, parameter.Evaluate(GameMaterialState.Resting with { Time = 0.25f })!.Value.X, 3);
        // At 0.35 s it is back at a trough: 0.1 s at the 1 s turn-on time closes a tenth of the gap to 10.
        Assert.Equal(8.2f, parameter.Evaluate(GameMaterialState.Resting with { Time = 0.35f })!.Value.X, 3);

        // Ten seconds at 60 fps: the fast square wave under the fades holds a steady third of the peak (the game's
        // steady "ghost" glow), never flickering back to 0.
        float low = float.MaxValue, high = float.MinValue;
        for (int frame = 1; frame <= 600; frame++)
        {
            float value = parameter.Evaluate(GameMaterialState.Resting with { Time = 0.35f + frame / 60f })!.Value.X;
            if (frame > 540)
            {
                low = MathF.Min(low, value);
                high = MathF.Max(high, value);
            }
        }
        Assert.InRange(low, 2.8f, 3.9f);
        Assert.InRange(high, 2.8f, 3.9f);
    }

    [Theory]
    [InlineData(0u, 4f, true)]
    [InlineData(1u, 4f, false)]
    [InlineData(2u, 4f, true)]
    [InlineData(2u, 5f, false)]
    [InlineData(3u, 5f, true)]
    [InlineData(5u, 4f, false)]
    public void ComparisonOperatorsFollowTheGameData(uint op, float right, bool expected)
    {
        Func<GameMaterialState, System.Numerics.Vector4?> four = _ => new System.Numerics.Vector4(4f);
        Func<GameMaterialState, System.Numerics.Vector4?> other = _ => new System.Numerics.Vector4(right);
        var condition = new GameMaterialBoolCondition(GameMaterialBoolKind.Compare, Left: four, Right: other, Operator: op);
        Assert.Equal(expected, condition.Evaluate(GameMaterialState.Resting));
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
