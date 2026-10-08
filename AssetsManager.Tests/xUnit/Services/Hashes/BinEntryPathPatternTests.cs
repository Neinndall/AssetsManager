using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class BinEntryPathPatternTests
{
    private static readonly HashSet<string> Structures = new() { "bin-context-structures" };
    private static uint Hash(string value) => Fnv1a.HashLower(value);
    private static BinTree Tree(params BinTreeObject[] entries) => new(entries, Array.Empty<string>());
    private static BinTreeObject Entry(string path, string type, params BinTreeProperty[] fields) => new(Hash(path), Hash(type), fields);
    private static BinTreeString Text(string field, string value) => new(Hash(field), value);
    private static InternalHashEvidenceMatcher Matcher(InternalHashKind kind, params string[] values) =>
        new(new Dictionary<InternalHashKind, HashSet<ulong>> { [kind] = values.Select(value => (ulong)Hash(value)).ToHashSet() });
    private static void Scan(BinTree tree, InternalHashEvidenceMatcher matcher, HashResolverService resolver = null,
        BinEntryPathPatternSource patterns = null, string source = "synthetic.bin") =>
        BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, source, "test.wad.client", resolver, Structures, entryPatterns: patterns);

    [Theory]
    [InlineData("PetBubbleTeaBear_Skin1_Emote_taunt_star", "PetBubbleTeaBear_Emote_taunt_star")]
    [InlineData("ahri_skin04_emote_dance_sound", "ahri_emote_dance_sound")]
    [InlineData("Jade_Corki_Skin32_Jade_CorkiBomb_Bomb", "Jade_CorkiBomb_Bomb")]
    [InlineData("Ashe_Skin52_idle", "Skin52_idle")]
    [InlineData("Jinx_Skin60_Emote_SlotMachine_Win_v1", "Jinx_Skin60_Emote_SlotMachine_Win")]
    [InlineData("Prefix_bAsE_Trail_V123", "Prefix_Trail")]
    [InlineData("Unit_SkIn000123_Fx_42", "Unit_Fx")]
    [InlineData("Skin0_Trail", "Trail")]
    [InlineData("UnitSkin123_FxSkin2_Trail", "UnitFxTrail")]
    [InlineData("Base_Base_Trail", "Base_Trail")]
    [InlineData("PrefixSkin01_Skin02_Trail", "PrefixSkin02_Trail")]
    [InlineData("Trail", "Trail_BV2")]
    [InlineData("Trail", "Trail")]
    [InlineData("Unit_sKiN100_Effect", "Unit_Base_Effect")]
    [InlineData("Unit_Skin1_EffectSkin2_Trail", "Unit_Base_EffectBase_Trail")]
    [InlineData("Prefix_Middle_Final_Trail", "Final_Trail")]
    [InlineData("Prefix_Middle_Final_Trail", "Trail")]
    [InlineData("Prefix_Middle_Final_Trail", "Prefix_Final_Trail")]
    [InlineData("Prefix_Middle_Final_Trail", "Prefix_Middle_Trail")]
    [InlineData("Prefix_Middle_Final_Trail", "Prefix_Trail")]
    [InlineData("Prefix_Middle_Final_Trail", "Prefix_Middle")]
    [InlineData("Prefix__Trail", "Prefix_Trail")]
    public void ResolverKeysKeepLegacyAndNewCandidatesTargeted(string basename, string expected)
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        string target = $"Characters/Synthetic/Particles/{basename}";
        File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"), $"{Hash(target):x8} {target}\n");
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        resolver.LoadBinHashes();
        var matcher = Matcher(InternalHashKind.BinHashes, expected, "UnrelatedKey");
        var map = new BinTreeMap(Hash("resourceMap"), BinPropertyType.Hash, BinPropertyType.ObjectLink,
            new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, Hash(expected)), new BinTreeObjectLink(0, Hash(target))) });
        Scan(Tree(Entry("Resources/Synthetic", "ResourceResolver", map)), matcher, resolver);
        var match = Assert.Single(matcher.Matches);
        Assert.Equal(expected, match.Value);
        Assert.True(match.CanPromote);
        Assert.Equal(1, matcher.Remaining);
    }

    [Fact]
    public void ResourceTokensRequireAsciiDigitsAndCompleteSuffixes()
    {
        Assert.DoesNotContain("Unit_Base_Trail", BinEntryPathPatternSource.ResourceKeyCandidates("Unit_Skin١_Trail"));
        Assert.DoesNotContain("Unit_Base_Trail", BinEntryPathPatternSource.ResourceKeyCandidates("Unit_Skin_Trail"));
        Assert.DoesNotContain("UnitTrail", BinEntryPathPatternSource.ResourceKeyCandidates("UnitSkin1_Trail_v١"));
        Assert.DoesNotContain("UnitTrail", BinEntryPathPatternSource.ResourceKeyCandidates("UnitSkin1_Trail_v"));
        Assert.Contains("Unit_Trail", BinEntryPathPatternSource.ResourceKeyCandidates("Unit_SKIN999999999999999999_Trail"));
    }

    [Fact]
    public void ResolverOptimizationKeepsCollisionChecksForAlreadyMatchedKeys()
    {
        Assert.Equal(Hash("costarring"), Hash("liquid"));
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        string[] paths = { "Particles/costarring", "Particles/liquid" };
        File.WriteAllLines(Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"), paths.Select(path => $"{Hash(path):x8} {path}"));
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        resolver.LoadBinHashes();
        var matcher = Matcher(InternalHashKind.BinHashes, "costarring");
        var entries = paths.Select((path, index) => Entry($"Resources/{index}", "ResourceResolver",
            new BinTreeMap(Hash("resourceMap"), BinPropertyType.Hash, BinPropertyType.ObjectLink,
                new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, Hash("costarring")), new BinTreeObjectLink(0, Hash(path))) }))).ToArray();
        Scan(Tree(entries), matcher, resolver);
        Assert.Equal(2, matcher.Matches.Count);
        Assert.Contains(matcher.Matches, match => match.Value == "liquid" && !match.CanPromote);
        Assert.False(matcher.TryGetVerifiedValue(InternalHashKind.BinHashes, Hash("costarring"), out _));
    }

    [Fact]
    public void ClassPrefixesFollowAsciiWordBoundariesAndSpellPrefixesPreserveUnicode()
    {
        Assert.Equal(new[] { "Guest", "GuestOf", "GuestOfHonor", "GuestOfHonorList", "GuestOfHonorListData" },
            BinEntryPathPatternSource.WordPrefixes("GuestOfHonorListData"));
        Assert.Equal(new[] { "TFTMode", "TFTModeData" }, BinEntryPathPatternSource.WordPrefixes("TFTModeData"));
        Assert.Equal(new[] { "Unit2", "Unit2DConfig" }, BinEntryPathPatternSource.WordPrefixes("Unit2DConfig"));
        Assert.Equal(new[] { "A", "A😀" }, BinEntryPathPatternSource.CharacterPrefixes("A😀B"));
    }

    [Fact]
    public void AudioPatternsIncludeEveryUpstreamMapId()
    {
        int[] maps = { 11, 12, 21, 22, 30, 33, 35, 453 };
        Assert.Equal(maps, BinEntryPathPatternSource.MapIds);
        foreach (int map in maps)
        {
            string path = $"Maps/Shipping/Map{map}/Audio";
            var matcher = Matcher(InternalHashKind.BinEntries, path);
            Scan(Tree(Entry(path, "MapAudioDataProperties")), matcher);
            Assert.Equal(path, Assert.Single(matcher.Matches).Value);
        }
    }

    [Theory]
    [InlineData("MapAudioDataProperties", null, null, "Maps/Shipping/Map11/Audio")]
    [InlineData("MapAudioDataProperties", null, null, "Maps/Shipping/Map453/Audio")]
    [InlineData("TftUnitPropertyDefinition", "name", "TestProperty", "Maps/Shipping/Map22/UnitProperties/TestProperty")]
    [InlineData("AnvilData", "AugmentNameId", "TestAnvil", "Maps/Shipping/Map30/Anvils/TestAnvil")]
    [InlineData("AnvilData", "AugmentNameId", "TestAnvil", "Maps/Shipping/Map453/Anvils/TestAnvil")]
    [InlineData("MapSkin", "name", "Default", "Maps/Shipping/Map453/MapSkins/Default")]
    [InlineData("AugmentData", "AugmentNameId", "TestAugment", "Maps/Shipping/Map453/AugmentTags/TestAugment")]
    [InlineData("CharScript", "ScriptName", "CHARscriptTest", "Characters/Test/Scripts/CHARscriptTest")]
    [InlineData("BuffScript", "ScriptName", "TestBuff", "Maps/Shipping/Map453/Scripts/TestBuff")]
    [InlineData("LolSpellScript", "ScriptName", "TestSpell", "Maps/Shipping/Map12/Scripts/TestSpell")]
    [InlineData("LevelControlScript", "ScriptName", "TestLevel", "Maps/Shipping/Common/Scripts/TestLevel")]
    [InlineData("ScriptDataObject", "mName", "TestData", "Maps/Shipping/Map35/ScriptData/TestData")]
    [InlineData("ScriptDataObject", "mName", "TestData", "Maps/Shipping/Map22/Sets/TFTSet1/ScriptData/TestData")]
    [InlineData("ScriptDataObject", "mName", "TestData", "Maps/Shipping/Map22/Sets/TFTSet29/ScriptData/TestData")]
    [InlineData("SpellObject", "mScriptName", "TestChild", "Maps/Shipping/Map453/Spells/Test/TestChild")]
    [InlineData("SpellObject", "mScriptName", "A😀Child", "Maps/Shipping/Map22/Spells/A😀/A😀Child")]
    public void ImmediatePatternsResolveTheirOwnEntry(string type, string field, string value, string expected)
    {
        var matcher = Matcher(InternalHashKind.BinEntries, expected, "Unrelated/Entry");
        Scan(Tree(Entry(expected, type, field == null ? Array.Empty<BinTreeProperty>() : new[] { Text(field, value) })), matcher);
        var match = Assert.Single(matcher.Matches);
        Assert.Equal(expected, match.Value);
        Assert.True(match.CanPromote);
        Assert.Equal(1, matcher.Remaining);
    }

    [Fact]
    public void MapChildSpellStillResolvesItsObjectPath()
    {
        const string path = "Maps/Shipping/Map11/Spells/Parent/ParentChild";
        var targets = new Dictionary<InternalHashKind, HashSet<ulong>>
        {
            [InternalHashKind.BinEntries] = new() { Hash(path) }, [InternalHashKind.BinHashes] = new() { Hash(path) }
        };
        var matcher = new InternalHashEvidenceMatcher(targets);
        Scan(Tree(Entry(path, "SpellObject", Text("mScriptName", "ParentChild"), new BinTreeHash(Hash("objectPath"), Hash(path)))), matcher);
        Assert.Equal(2, matcher.Matches.Count);
        Assert.All(matcher.Matches, match => Assert.True(match.CanPromote));
        Assert.Equal(0, matcher.Remaining);
    }

    [Fact]
    public void ResolvedNumericItemSpellsSkipMapChildProbes()
    {
        string name = "123" + new string('A', 140);
        string item = $"Items/123/Spells/{name}";
        string child = $"Maps/Shipping/Map11/Spells/123/{name}";
        var matcher = Matcher(InternalHashKind.BinEntries, item, child);
        Scan(Tree(Entry(item, "SpellObject", Text("mScriptName", name))), matcher);
        Assert.Equal(item, Assert.Single(matcher.Matches).Value);
        Assert.Equal(1, matcher.Remaining);
        Assert.True(matcher.CheckedCandidates < 64, $"Unexpected child spell probes: {matcher.CheckedCandidates}");
    }

    [Theory]
    [InlineData(false, "CharacterRecord")]
    [InlineData(true, "TFTCharacterRecord")]
    public void CharacterHooksWorkAcrossFilesInEitherOrder(bool recordsFirst, string recordType)
    {
        string[] paths =
        {
            "Characters/Jade_Test/Scripts/charscriptBorrowed", "Characters/Jade_Test/Spells/jade_testQ",
            "Characters/Jade_Test/Spells/Attacks/jade_testAttack", "Characters/Jade_Test/Spells/jade_testQAbility/jade_testQChild",
            "Characters/Jade_Test/ScriptData/jade_testData"
        };
        var targets = new Dictionary<InternalHashKind, HashSet<ulong>>
        {
            [InternalHashKind.BinEntries] = paths.Select(value => (ulong)Hash(value)).ToHashSet(),
            [InternalHashKind.BinHashes] = new() { Hash(paths[0]) }
        };
        var matcher = new InternalHashEvidenceMatcher(targets);
        var patterns = new BinEntryPathPatternSource();
        var records = Tree(Entry("Synthetic/Character", recordType, Text("mCharacterName", "Jade_Test")));
        var content = Tree(Entry(paths[0], "CharScript", Text("ScriptName", "charscriptBorrowed")),
            Entry(paths[1], "SpellObject", Text("mScriptName", "jade_testQ")),
            Entry(paths[2], "SpellObject", Text("mScriptName", "jade_testAttack")),
            Entry(paths[3], "SpellObject", Text("mScriptName", "jade_testQChild")),
            Entry(paths[4], "ScriptDataObject", Text("mName", "jade_testData")));
        Scan(recordsFirst ? records : content, matcher, patterns: patterns, source: recordsFirst ? "records.bin" : "scripts.bin");
        Scan(recordsFirst ? content : records, matcher, patterns: patterns, source: recordsFirst ? "scripts.bin" : "records.bin");
        Assert.Empty(matcher.Matches);
        patterns.Apply(matcher);
        Assert.Equal(6, matcher.Matches.Count);
        Assert.Equal(0, matcher.Remaining);
        Assert.All(matcher.Matches, match => { Assert.True(match.CanPromote); Assert.Equal("scripts.bin", match.Source); });
    }

    [Theory]
    [InlineData("BuffScript")]
    [InlineData("LolSpellScript")]
    [InlineData("LevelControlScript")]
    [InlineData("CharScript")]
    public void KnownScriptPathsPromoteHashValuesWithoutAnUnknownEntry(string type)
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        const string path = "Synthetic/Script/AlreadyKnown";
        File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"), $"{Hash(path):x8} {path}\n");
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        resolver.LoadBinHashes();
        var matcher = Matcher(InternalHashKind.BinHashes, path);
        Scan(Tree(Entry(path, type, Text("ScriptName", "TestName"), new BinTreeHash(Hash("path"), Hash(path)))), matcher, resolver);
        Assert.Equal(path, Assert.Single(matcher.Matches).Value);
    }

    [Theory]
    [InlineData("Maps/Shipping/Map12/GameModeConfigs/StatsUiData_KIWI_JADE", false, "mStatsUiData")]
    [InlineData("Maps/Shipping/Map12/GameModeConfigs/StatsUiData", false, "mStatsUiData")]
    [InlineData("Maps/Shipping/Map12/Configs/StatsUiData", false, "mStatsUiData")]
    [InlineData("Maps/Shipping/Map12/StatsUiData", false, "mStatsUiData")]
    [InlineData("Maps/Shipping/Common/StatsUiData", false, "mStatsUiData")]
    [InlineData("UX/HUD/Globals/StatsUiData", false, "mStatsUiData")]
    [InlineData("Maps/Shipping/Map12/Configs/mstats", false, "mstats")]
    [InlineData("Maps/Shipping/Map12/GameModeConfigs/GuestOfHonorList", true, "unknownField")]
    [InlineData("Maps/Shipping/Map12/GameModeConfigs/GuestOfHonorListData", true, "unknownField")]
    public void ModeLinksUseFieldsDynamicClassNamesAndAllObservedModes(string path, bool list, string field)
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        const string owner = "Maps/Shipping/Map12/Modes/CLASSIC";
        const string other = "Maps/Shipping/Map30/Modes/KIWI_JADE";
        File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"), $"{Hash(owner):x8} {owner}\n{Hash(other):x8} {other}\n");
        File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binfields.txt"), $"{Hash(field):x8} {field}\n");
        const string type = "GuestOfHonorListData";
        File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.bintypes.txt"), $"{Hash(type):x8} {type}\n");
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        resolver.LoadBinHashes();
        var matcher = Matcher(InternalHashKind.BinEntries, path);
        var patterns = new BinEntryPathPatternSource(resolver);
        BinTreeProperty link = list
            ? new BinTreeContainer(Hash(field), BinPropertyType.ObjectLink, new BinTreeProperty[] { new BinTreeObjectLink(0, Hash(path)) })
            : new BinTreeObjectLink(Hash(field), Hash(path));
        Scan(Tree(Entry(owner, "GameModeMapData", link)), matcher, resolver, patterns, "mode.bin");
        Scan(Tree(Entry(other, "GameModeMapData"), Entry(path, type)), matcher, resolver, patterns, "target.bin");
        patterns.Apply(matcher);
        var match = Assert.Single(matcher.Matches);
        Assert.Equal(path, match.Value);
        Assert.Equal("mode.bin", match.Source);
        Assert.True(match.CanPromote);
    }

    public static IEnumerable<object[]> EntryPathTypes => new[]
    {
        "EsportsBannerConfiguration", "GameModeChampionList", "KillCalloutsViewController", "OffScreenPOIViewController",
        "PingRadialViewController", "PlayerReportViewController", "PracticeToolViewController", "RewardGroup",
        "TFTModeData", "TftPlaybook", "UnitFloatingInfoBarData"
    }.Select(type => new object[] { Hash(type) }).Append(new object[] { 0x409a5657u });

    [Theory]
    [MemberData(nameof(EntryPathTypes))]
    public void SelectedTypesResolveNestedHashValuesFromEntriesLearnedLater(uint type)
    {
        const string path = "Maps/Shipping/Map22/UnitProperties/TestProperty";
        var targets = new Dictionary<InternalHashKind, HashSet<ulong>>
        {
            [InternalHashKind.BinEntries] = new() { Hash(path) }, [InternalHashKind.BinHashes] = new() { Hash(path) }
        };
        var matcher = new InternalHashEvidenceMatcher(targets);
        var patterns = new BinEntryPathPatternSource();
        var nested = new BinTreeOptional(Hash("nested"), new BinTreeStruct(0, Hash("Nested"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("values"), BinPropertyType.Hash, new BinTreeProperty[] { new BinTreeHash(0, Hash(path)) })
        }));
        Scan(Tree(new BinTreeObject(0x12345678, type, new BinTreeProperty[] { nested })), matcher, patterns: patterns, source: "globals");
        Scan(Tree(Entry(path, "TftUnitPropertyDefinition", Text("name", "TestProperty"))), matcher, patterns: patterns);
        patterns.Apply(matcher);
        Assert.Equal(2, matcher.Matches.Count);
        Assert.Equal("globals", Assert.Single(matcher.Matches, match => match.Kind == InternalHashKind.BinHashes).Source);
    }

    [Fact]
    public void UnrelatedClassesAndCharactersDoNotPromoteMatchingCandidates()
    {
        const string entryPath = "Maps/Shipping/Map22/UnitProperties/TestProperty";
        const string spellPath = "Characters/Other/Spells/NotOtherSpell";
        var targets = new Dictionary<InternalHashKind, HashSet<ulong>>
        {
            [InternalHashKind.BinEntries] = new() { Hash(entryPath), Hash(spellPath) }, [InternalHashKind.BinHashes] = new() { Hash(entryPath) }
        };
        var matcher = new InternalHashEvidenceMatcher(targets);
        Scan(Tree(Entry("Synthetic/Unrelated", "UnrelatedType", new BinTreeHash(Hash("value"), Hash(entryPath))),
            Entry(entryPath, "TftUnitPropertyDefinition", Text("name", "TestProperty")),
            Entry("Characters/Other", "CharacterRecord", Text("mCharacterName", "Other")),
            Entry(spellPath, "SpellObject", Text("mScriptName", "NotOtherSpell"))), matcher);
        Assert.Single(matcher.Matches);
        Assert.Equal(2, matcher.Remaining);
    }

    [Fact]
    public void DisabledStructuresAndCancelledHooksProduceNoFindings()
    {
        const string path = "Maps/Shipping/Map22/UnitProperties/TestProperty";
        var tree = Tree(Entry(path, "TftUnitPropertyDefinition", Text("name", "TestProperty")));
        var matcher = Matcher(InternalHashKind.BinEntries, path);
        BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "test.bin", selectedSubMethods: new HashSet<string>());
        Assert.Empty(matcher.Matches);
        var patterns = new BinEntryPathPatternSource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => patterns.Observe(tree, matcher, "test.bin", null, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => patterns.Apply(matcher, cancellation.Token));
        Assert.Empty(matcher.Matches);
    }

    [Fact]
    public void RemainingCountOnlyDropsOncePerVerifiedHash()
    {
        var matcher = Matcher(InternalHashKind.BinEntries, "First", "Second", "Third");
        string[] names = { "First", "Second", "Third" };
        for (int index = 0; index < names.Length; index++)
        {
            matcher.CheckContextualCandidate(InternalHashKind.BinEntries, names[index], "test.bin", observedHash: Hash(names[index]));
            Assert.Equal(2 - index, matcher.Remaining);
            Assert.Equal(2 - index, matcher.GetRemainingCount(InternalHashKind.BinEntries));
        }
    }

    [Fact]
    public void UnverifiedResearchFindingsStayInTheRemainingCount()
    {
        var matcher = Matcher(InternalHashKind.BinEntries, "ResearchCandidate");
        matcher.CheckResearchCandidate(InternalHashKind.BinEntries, "ResearchCandidate", InternalHashGuessStrategy.BinContent,
            "research", InternalHashEvidence.SemanticReference, verified: false);
        Assert.False(Assert.Single(matcher.Matches).CanPromote);
        Assert.Equal(1, matcher.Remaining);
        Assert.Equal(1, matcher.GetRemainingCount(InternalHashKind.BinEntries));
    }

    [Fact]
    public void NamedObservedHashesDoNotConsumeTheGuessingBudget()
    {
        var matcher = Matcher(InternalHashKind.BinEntries, "Pending");
        Assert.False(matcher.CheckContextualCandidate(InternalHashKind.BinEntries, "AlreadyNamed", "test.bin", observedHash: Hash("AlreadyNamed")));
        Assert.Equal(0, matcher.CheckedCandidates);
        Assert.False(matcher.CheckContextualCandidate(InternalHashKind.BinEntries, "WrongCandidate", "test.bin", observedHash: Hash("Pending")));
        Assert.Equal(1, matcher.CheckedCandidates);
        Assert.True(matcher.CheckContextualCandidate(InternalHashKind.BinEntries, "Pending", "test.bin", observedHash: Hash("Pending")));
        Assert.Equal(2, matcher.CheckedCandidates);
    }

    [Theory]
    [InlineData(WadChunkCompression.None, "globals", true)]
    [InlineData(WadChunkCompression.Zstd, "globals", true)]
    [InlineData(WadChunkCompression.None, "globals", false)]
    [InlineData(WadChunkCompression.Zstd, "globals.bin", true)]
    [InlineData(WadChunkCompression.Zstd, "globals.tex", true)]
    public async Task ProductionGuessingReadsBinContentRegardlessOfExplorerExtension(WadChunkCompression compression, string wadEntryPath, bool named)
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        string root = bridge.CreateDirectory("Game");
        const string target = "LCU/Collectibles/EsportsTeams/Synthetic";
        var tree = Tree(Entry("Synthetic/Banner", "EsportsBannerConfiguration", new BinTreeHash(Hash("esportsTeam"), Hash(target))));
        using var output = new MemoryStream();
        tree.Write(output);
        byte[] data = output.ToArray();
        WadBuilder.Bake(new[] { new WadBakeEntry(wadEntryPath, () => new MemoryStream(data), compression) },
            Path.Combine(root, "Global.wad.client"), new WadBakeSettings());
        if (named) File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"), $"{XxHash64Ext.Hash(wadEntryPath):x16} {wadEntryPath}\n");
        File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"), $"{Hash(target):x8} {target}\n");
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var store = new BinRstHashGuessingStore(bridge.Directories);
        var persistence = new HashGuessPersistenceService(new HashGuessingStore(bridge.Directories), store);
        using var http = new HttpClient(new EmptySchemaHandler());
        var service = new BinRstHashGuessingService(store, persistence, resolver, bridge.Directories, bridge.LogService,
            new MetaSchemaHashSource(http, bridge.Directories, bridge.LogService));
        var inventory = await service.BuildInventoryAsync(root, true, false, null, CancellationToken.None);
        Assert.Equal(1, inventory.ScannedBins);
        Assert.Contains((ulong)Hash(target), await store.LoadUnknownAsync(InternalHashKind.BinHashes, CancellationToken.None));
        var result = await service.RunContentGuessingAsync(root, true, false, null, CancellationToken.None, selectedSubMethods: Structures);
        Assert.Equal(target, Assert.Single(result.Matches).Value);
        Assert.DoesNotContain((ulong)Hash(target), await store.LoadUnknownAsync(InternalHashKind.BinHashes, CancellationToken.None));
    }

    private sealed class EmptySchemaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage { Content = new StringContent("{\"latest\":\"test\",\"classes\":{}}") });
    }
}
