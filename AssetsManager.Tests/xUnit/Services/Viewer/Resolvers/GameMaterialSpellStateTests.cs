using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Resolvers;

public sealed class GameMaterialSpellStateTests
{
    private static uint Hash(string name) => Fnv1a.HashLower(name);
    private static readonly uint Spell = Hash("Characters/Test/Spells/Ultimate");
    private static readonly IReadOnlyDictionary<uint, VfxSpellPreview> Spells = new Dictionary<uint, VfxSpellPreview>
    {
        [Spell] = new(null, null, null, 0, null, null, "Spell4", null, 0, null, Array.Empty<VfxSpellIssue>())
            { ScriptName = "Ultimate" }
    };

    private static AnimationClipCatalogItem Clip(string name) => new(name, name, null, 1, null, null, null,
        Array.Empty<AnimationClipTimedCue>(), 0, false, null);

    [Fact]
    public void SpellHashBuffConditionFollowsTheAuthoredCastClipAndReturnsToRest()
    {
        var driver = new BinTreeStruct(Hash("driver"), Hash("HasBuffDynamicMaterialBoolDriver"),
            new BinTreeProperty[] { new BinTreeHash(Hash("Spell"), Spell) });
        GameMaterialBoolCondition condition = SknDynamicMaterialParser.ReadBoolDriver(driver);

        Assert.Equal(GameMaterialState.SpellBuffKey(Spell), Assert.Single(condition.Buffs()));
        Assert.False(condition.Evaluate(GameMaterialState.Resting));
        Assert.True(condition.Evaluate(GameMaterialState.Preview(null, Clip("Spell4"), Spells)));
        Assert.False(condition.Evaluate(GameMaterialState.Preview(null, Clip("Idle"), Spells)));
        Assert.Null(GameMaterialState.Preview(null, AnimationClipCatalogItem.CreateBindPoseItem(), Spells));
        Assert.Null(GameMaterialState.Preview(null, null, Spells));
    }

    [Fact]
    public void SameClipNameDoesNotActivateAnUnrelatedSpellAndManualStatesStayIndependent()
    {
        string[] manual = { "InCombat" };
        GameMaterialState state = GameMaterialState.Preview(manual, Clip("Spell4"), Spells);
        Assert.True(state.HasBuff("InCombat"));
        Assert.True(state.HasBuff(GameMaterialState.SpellBuffKey(Spell)));
        Assert.False(state.HasBuff(GameMaterialState.SpellBuffKey(Spell + 1)));
        Assert.False(state.HasBuff("Ultimate"));
        Assert.Equal(new[] { "InCombat" }, manual);
        Assert.False(GameMaterialState.Preview(manual, Clip("Idle"), Spells).HasBuff(GameMaterialState.SpellBuffKey(Spell)));
    }

    [Fact]
    public void ExplicitScriptAndHashTogglesCanActivateSpellKeyedConditionsWithoutAClip()
    {
        string key = GameMaterialState.SpellBuffKey(Spell);
        Assert.True(GameMaterialState.Preview(new[] { "ultimate" }, null, Spells).HasBuff(key));
        Assert.True(GameMaterialState.Preview(new[] { key }, null, Spells).HasBuff(key));
        Assert.False(GameMaterialState.TrySpellBuffHash("Ultimate", out _));
        Assert.True(GameMaterialState.TrySpellBuffHash(key, out uint hash));
        Assert.Equal(Spell, hash);
    }

    [Fact]
    public void DelayedSpellHashDriverSelectsUnresolvedTextureAndAnimationAlternativeStillWorks()
    {
        const ulong textureHash = 0xfd92a016617e35f6;
        var delayed = new BinTreeStruct(Hash("driver"), Hash("DelayedBoolMaterialDriver"), new BinTreeProperty[]
        {
            new BinTreeStruct(Hash("mBoolDriver"), Hash("HasBuffDynamicMaterialBoolDriver"),
                new BinTreeProperty[] { new BinTreeHash(Hash("Spell"), Spell) }),
            new BinTreeF32(Hash("mDelayOff"), 6.5f)
        });
        var animation = new BinTreeStruct(Hash("driver"), Hash("IsAnimationPlayingDynamicMaterialBoolDriver"),
            new BinTreeProperty[] { new BinTreeContainer(Hash("mAnimationNames"), BinPropertyType.Hash,
                new BinTreeProperty[] { new BinTreeHash(0, Hash("Dance")) }) });
        var dynamic = new BinTreeStruct(Hash("dynamicMaterial"), Hash("DynamicMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("textures"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, Hash("DynamicMaterialTextureSwapDef"), new BinTreeProperty[]
                {
                    new BinTreeString(Hash("name"), "Main_Texture"),
                    new BinTreeContainer(Hash("options"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        Option(delayed, textureHash), Option(animation, textureHash)
                    })
                })
            })
        });
        var swap = Assert.Single(SknDynamicMaterialParser.Read(
            new Dictionary<uint, BinTreeProperty> { [dynamic.NameHash] = dynamic }, null));
        Assert.Null(swap.Resolve(GameMaterialState.Resting));
        Assert.Equal("fd92a016617e35f6", swap.Resolve(GameMaterialState.Preview(null, Clip("Spell4"), Spells)));
        Assert.Equal("fd92a016617e35f6", swap.Resolve(GameMaterialState.Preview(null, Clip("Dance"), Spells)));
        Assert.Null(swap.Resolve(GameMaterialState.Preview(null, Clip("Idle"), Spells)));
    }

    [Fact]
    public void SpellReaderKeepsManualStateLabelEvenForBuffOnlyObjects()
    {
        var spell = new BinTreeObject(Spell, Hash("SpellObject"), new BinTreeProperty[]
        {
            new BinTreeString(Hash("mScriptName"), "Ultimate"),
            new BinTreeStruct(Hash("mBuff"), Hash("BuffData"), Array.Empty<BinTreeProperty>())
        });
        Assert.Equal("Ultimate", VfxSpellPreviewReader.Read(spell).ScriptName);
    }

    [Fact]
    public void SpellHashBuffCounterFeedsDynamicParameters()
    {
        var driver = new BinTreeStruct(Hash("driver"), Hash("BuffCounterDynamicMaterialFloatDriver"),
            new BinTreeProperty[] { new BinTreeHash(Hash("Spell"), Spell) });
        var dynamic = new BinTreeStruct(Hash("dynamicMaterial"), Hash("DynamicMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("parameters"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, Hash("DynamicMaterialParameterDef"), new BinTreeProperty[]
                {
                    new BinTreeString(Hash("name"), "Value"), driver
                })
            })
        });
        var parameter = Assert.Single(SknDynamicMaterialParser.ReadParameters(
            new Dictionary<uint, BinTreeProperty> { [dynamic.NameHash] = dynamic }));
        Assert.Equal(0f, parameter.Evaluate(GameMaterialState.Resting).Value.X);
        Assert.Equal(SknDynamicMaterialParser.FullStacks,
            parameter.Evaluate(GameMaterialState.Preview(null, Clip("Spell4"), Spells)).Value.X);
    }

    private static BinTreeEmbedded Option(BinTreeStruct driver, ulong texture) => new(0,
        Hash("DynamicMaterialTextureSwapOption"), new BinTreeProperty[]
        {
            driver, new BinTreeWadChunkLink(Hash("TextureName"), texture)
        });
}
