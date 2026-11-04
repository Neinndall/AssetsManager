using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Tests.Support;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Audio;

public sealed class AudioBankLinkerServiceTests
{
    [Theory]
    [InlineData("sonadjgenre01", "Sona.wad.client")]
    [InlineData("sonadjgenre02", "Sona.wad.client")]
    [InlineData("sonadjgenre03", "Sona.wad.client")]
    [InlineData("sonadjgenre03", "Sona.es_ES.wad.client")]
    public void CharacterFormsKeepTheirBinPathInsideTheOwningChampionWad(string character, string wad)
    {
        using var bridge = new AssetsManagerTestBridge();
        var linker = new AudioBankLinkerService(null, null, bridge.LogService, null, null, null, null);
        var strategy = linker.GetBinFileSearchStrategy(
            $"assets/sounds/wwise2016/sfx/characters/{character}/skins/skin06/{character}_skin06_sfx_audio_async.bnk", wad);
        Assert.Equal($"data/characters/{character}/skins/skin6.bin", strategy.BinPath);
        Assert.Equal("sona.wad.client", strategy.TargetWadName, ignoreCase: true);
    }

    [InstalledChampionFact("Sona")]
    public void InstalledDjSonaBanksRecoverEventNamesFromTheirOwnSkinBins()
    {
        using var bridge = new AssetsManagerTestBridge();
        var linker = new AudioBankLinkerService(null, null, bridge.LogService, null, null, null, null);
        var parser = new AudioBankService(bridge.LogService, new BinAudioEventExtractor());
        string wadDirectory = Path.Combine(InstalledSkins.FindInstall(), "Game", "DATA", "FINAL", "Champions");
        using var wad = new WadFile(Path.Combine(wadDirectory, "Sona.wad.client"));
        foreach (string character in new[] { "sonadjgenre01", "sonadjgenre02", "sonadjgenre03" })
        {
            string bank = character + "_skin06_sfx";
            string audioPath = $"assets/sounds/wwise2016/sfx/characters/{character}/skins/skin06/{bank}_audio_async.bnk";
            string eventsPath = PathUtils.GetBankSiblingName(audioPath, "_events.bnk");
            var strategy = linker.GetBinFileSearchStrategy(audioPath, Path.Combine(wadDirectory, "Sona.wad.client"));
            Assert.True(File.Exists(Path.Combine(wadDirectory, strategy.TargetWadName)), strategy.TargetWadName);
            using var bin = wad.LoadChunkDecompressed(strategy.BinPath);
            using var audio = wad.LoadChunkDecompressed(audioPath);
            using var events = wad.LoadChunkDecompressed(eventsPath);
            var tree = parser.ParseAudioBank(null, audio.Span.ToArray(), events.Span.ToArray(), bin.Span.ToArray(), bank);
            Assert.Contains(tree, node => node.Name.StartsWith("Play_sfx_SonaSkin06Genre", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("", "audio")]
    [InlineData("", "events")]
    [InlineData("_async", "audio")]
    [InlineData("_async", "events")]
    [InlineData("_streamed", "audio")]
    [InlineData("_streamed", "events")]
    [InlineData("_future_2027_part02", "audio")]
    [InlineData("_future_2027_part02", "events")]
    public async Task LinksMatchingFamilyInCurrentAndBackupTreesAndParsesLocalDiff(string variant, string role)
    {
        using var bridge = new AssetsManagerTestBridge();
        const string bank = "sonadjgenre01_skin06_sfx";
        const string directory = "assets/sounds/wwise2016/sfx/characters/sonadjgenre01/skins/skin06/";
        string audioName = bank + "_audio" + variant + ".bnk";
        string eventsName = bank + "_events" + variant + ".bnk";
        string clickedName = bank + "_" + role + variant + ".bnk";
        const string binPath = "data/characters/sonadjgenre01/skins/skin6.bin";
        const string eventName = "Play_sfx_SonaSkin06Genre01_Recall_leadin";
        var binTree = new BinTree(new[]
        {
            new BinTreeObject(Fnv1a.HashLower("Characters/SonaDJGenre01/Skins/Skin6"), Fnv1a.HashLower("SkinCharacterDataProperties"), new BinTreeProperty[]
            {
                new BinTreeEmbedded(Fnv1a.HashLower("skinAudioProperties"), Fnv1a.HashLower("SkinAudioProperties"), new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(Fnv1a.HashLower("bankUnits"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, Fnv1a.HashLower("BankUnit"), new BinTreeProperty[]
                        {
                            new BinTreeContainer(Fnv1a.HashLower("events"), BinPropertyType.String, new BinTreeProperty[] { new BinTreeString(0, eventName) })
                        })
                    })
                })
            })
        }, Array.Empty<string>());
        using var binStream = new MemoryStream();
        binTree.Write(binStream);
        byte[] binData = binStream.ToArray();
        uint eventId = Assert.Single(new BinAudioEventExtractor().Extract(binData, bank, bridge.LogService)).Key;
        byte[] audio = AudioBankFixture.Bank(AudioBankFixture.Sound(300, 500)).Concat(AudioBankFixture.Media(500)).ToArray();
        byte[] events = AudioBankFixture.Bank(AudioBankFixture.Event(eventId, 101), AudioBankFixture.Action(101, 300));
        string wadPath = Path.Combine(bridge.RootPath, "Sona.wad.client");
        string[] names = new[] { audioName, eventsName, bank + "_audio.bnk", bank + "_events.bnk", bank + "_audio_async.bnk", bank + "_events_async.bnk" }
            .Distinct().ToArray();
        WadBuilder.Bake(names.Select(name => new WadBakeEntry(directory + name,
            () => new MemoryStream(name == audioName ? audio : name == eventsName ? events : Array.Empty<byte>()),
            WadChunkCompression.None)).Append(new WadBakeEntry(binPath, () => new MemoryStream(binData), WadChunkCompression.None)).ToList(), wadPath, new WadBakeSettings());
        Directory.CreateDirectory(bridge.Directories.HashesPath);
        File.WriteAllLines(Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"),
            names.Select(name => $"{XxHash64Ext.Hash(directory + name):x16} {directory + name}")
                .Append($"{XxHash64Ext.Hash(binPath):x16} {binPath}"));
        using var hashes = new HashResolverService(bridge.Directories, bridge.LogService);
        await hashes.LoadAllHashesAsync();
        var loader = new WadNodeLoaderService(hashes, bridge.LogService);
        var provider = new WadContentProvider(bridge.LogService, null, bridge.Directories, new SvgParser());
        var parser = new AudioBankService(bridge.LogService, new BinAudioEventExtractor());
        var linker = new AudioBankLinkerService(provider, null, bridge.LogService, null, hashes, loader, parser);
        var clicked = new FileSystemNodeModel(clickedName, NodeType.SoundBank)
        {
            VirtualPath = directory + clickedName, SourceWadPath = wadPath
        };

        var linked = await linker.LinkAudioBankAsync(clicked, null, bridge.RootPath);
        Assert.Equal(bank, linked.BaseName);
        Assert.Equal(audioName, linked.AudioBnkNode?.Name);
        Assert.Equal(eventsName, linked.EventsBnkNode?.Name);
        var tree = parser.ParseGenericAudioBank(null,
            await provider.GetVirtualFileBytesAsync(linked.AudioBnkNode),
            await provider.GetVirtualFileBytesAsync(linked.EventsBnkNode));
        Assert.Equal(500u, Assert.Single(Assert.Single(tree, node => node.Name == eventId.ToString()).EnumerateSounds()).Id);

        var siblings = linker.GetAudioBankSiblings(clicked.VirtualPath, "Sona.wad.client");
        Assert.Equal(2, siblings.Count);
        Assert.Contains(siblings, dep => dep.Path == directory + (role == "audio" ? eventsName : audioName));
        Assert.All(siblings, dep => Assert.Equal(XxHash64Ext.Hash(dep.Path), dep.PathHash));

        var diff = new SerializableChunkDiff { NewPath = clicked.VirtualPath, SourceWadFile = "Sona.wad.client" };
        var parsed = await linker.ResolveLiveAudioBankDiffAsync(diff, null, bridge.RootPath, null, null);
        Assert.Equal(500u, Assert.Single(Assert.Single(parsed.NewNodes, node => node.Name == eventName).EnumerateSounds()).Id);

        clicked.ChunkDiff = diff;
        linked = await linker.LinkAudioBankAsync(clicked, new ObservableRangeCollection<FileSystemNodeModel>
        {
            linked.AudioBnkNode, linked.EventsBnkNode,
            new FileSystemNodeModel("skin6.bin", false, binPath, wadPath) { SourceChunkPathHash = XxHash64Ext.Hash(binPath) },
            new FileSystemNodeModel(bank + "_audio" + (variant == "" ? "_async" : "") + ".bnk", NodeType.SoundBank)
        }, null);
        Assert.Equal(audioName, linked.AudioBnkNode?.Name);
        Assert.Equal(eventsName, linked.EventsBnkNode?.Name);
        Assert.Equal(binData, linked.BinData);
    }

    [Theory]
    [InlineData("sona_sfx_future_audio_custom_v2.bnk", "sona_sfx_future_events_custom_v2.bnk")]
    [InlineData("sona_sfx_audio_custom_v2.wpk", "sona_sfx_events_custom_v2.bnk")]
    [InlineData("SONA_SFX_AUDIO_CUSTOM_V2.BNK", "SONA_SFX_events_CUSTOM_V2.bnk")]
    [InlineData("sona_audiofeature_sfx_audio_custom.bnk", "sona_audiofeature_sfx_events_custom.bnk")]
    public void ReplacesOnlyTheRoleAndExtension(string fileName, string expected)
    {
        Assert.Equal(expected, PathUtils.GetBankSiblingName(fileName, "_events.bnk"));
    }

    [Theory]
    [InlineData("sona_sfx_audiofeature_custom.bnk")]
    [InlineData("sona_sfx_eventstuff_custom.bnk")]
    [InlineData("sona_sfx_audio_custom.txt")]
    [InlineData("assets/audio_directory/unknown.bnk")]
    public void DoesNotInferRolesFromPartialTokensOrOtherFormats(string fileName)
    {
        Assert.Equal(fileName, PathUtils.NormalizeBankFileName(fileName));
    }
}
