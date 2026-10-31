using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Services.Parsers;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Utils;
using LeagueToolkit.Core.Wad;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Audio;

public sealed class AudioBankServiceTests
{
    private static AudioBankService Service() => new(new LogService(new Serilog.LoggerConfiguration().CreateLogger()), new BinAudioEventExtractor());

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void KeepsNestedFamiliesSharedSoundsAndCrossBankReferencesForSfxAndVo(bool vo)
    {
        byte[] events = AudioBankFixture.Bank(AudioBankFixture.Event(100, 101, 101), AudioBankFixture.Action(101, 200),
            AudioBankFixture.Container(200, BnkObjectType.ActorMixer, 201, 202),
            AudioBankFixture.Container(201, BnkObjectType.RandomOrSequenceContainer, 300),
            AudioBankFixture.Container(202, BnkObjectType.RandomOrSequenceContainer, 300));
        byte[] sounds = AudioBankFixture.Bank(AudioBankFixture.Sound(300, 500));
        byte[] media = vo ? sounds : sounds.Concat(AudioBankFixture.Media(500)).ToArray();
        var tree = Service().ParseGenericAudioBank(vo ? AudioBankFixture.Package(500) : null, media, events);
        var root = Assert.Single(tree, node => node.Name == "100");
        var mixer = Assert.Single(root.Containers);
        Assert.Equal("[ActorMixer] 200", mixer.Name);
        Assert.Equal(new[] { "[Random] 201", "[Random] 202" }, mixer.Containers.Select(node => node.Name));
        Assert.All(mixer.Containers, family => Assert.Equal(500u, Assert.Single(family.Sounds).Id));
        Assert.Equal(2, root.EnumerateSounds().Count());
        Assert.All(root.EnumerateSounds(), sound => Assert.Equal(vo ? AudioSourceType.Wpk : AudioSourceType.Bnk, sound.Source));
        Assert.DoesNotContain(tree, node => node.Name == "Unknown");
        Assert.Empty(root.Sounds);
    }

    [Fact]
    public void StopActionsDoNotClaimToPlayTheirTargets()
    {
        byte[] events = AudioBankFixture.Bank(AudioBankFixture.Event(100, 101), AudioBankFixture.Action(101, 300, 1), AudioBankFixture.Sound(300, 500));
        var tree = Service().ParseGenericAudioBank(null, AudioBankFixture.Media(500), events);
        Assert.DoesNotContain(tree, node => node.Name == "100");
        Assert.Equal(500u, Assert.Single(Assert.Single(tree, node => node.Name == "Unknown").Sounds).Id);
    }

    [Fact]
    public void CyclesStopWithoutLosingPlayableSiblingsOrFlatteningTheirFamily()
    {
        byte[] events = AudioBankFixture.Bank(AudioBankFixture.Event(100, 101), AudioBankFixture.Action(101, 200),
            AudioBankFixture.Container(200, BnkObjectType.ActorMixer, 201, 300),
            AudioBankFixture.Container(201, BnkObjectType.ActorMixer, 200), AudioBankFixture.Sound(300, 500));
        var tree = Service().ParseGenericAudioBank(null, AudioBankFixture.Media(500), events);
        var root = Assert.Single(tree, node => node.Name == "100");
        var mixer = Assert.Single(root.Containers);
        Assert.Equal(500u, Assert.Single(mixer.Sounds).Id);
        Assert.Empty(Assert.Single(mixer.Containers).Containers);
        Assert.DoesNotContain(tree, node => node.Name == "Unknown");
    }

    [Fact]
    public async Task MixedWpkAudioBankAndEventsBankKeepTheirOwnMediaAndBackupSources()
    {
        byte[] package = AudioBankFixture.Package(500);
        byte[] media = AudioBankFixture.Media(600);
        byte[] events = AudioBankFixture.Bank(AudioBankFixture.Event(100, 101), AudioBankFixture.Action(101, 200),
            AudioBankFixture.Container(200, BnkObjectType.RandomOrSequenceContainer, 300, 301, 302),
            AudioBankFixture.Sound(300, 500), AudioBankFixture.Sound(301, 600), AudioBankFixture.Sound(302, 700))
            .Concat(AudioBankFixture.Media(700)).ToArray();
        var tree = Service().ParseGenericAudioBank(package, media, events);
        var sounds = Assert.Single(tree, node => node.Name == "100").EnumerateSounds().OrderBy(sound => sound.Id).ToArray();
        Assert.Equal(new uint[] { 500, 600, 700 }, sounds.Select(sound => sound.Id));
        Assert.Equal(new[] { AudioSourceType.Wpk, AudioSourceType.Bnk, AudioSourceType.EventsBnk }, sounds.Select(sound => sound.Source));
        Assert.DoesNotContain(tree, node => node.Name == "Unknown");

        string root = Path.Combine(Path.GetTempPath(), "AssetsManagerAudio_" + Guid.NewGuid().ToString("N"));
        try
        {
            const string wad = "Champion.es_ES.wad.client";
            FileSystemNodeModel Source(string name, byte[] data, ulong hash)
            {
                string file = Path.Combine(root, "wad_chunks", "old", wad, $"{hash:X16}.chunk");
                Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, data);
                return new FileSystemNodeModel(name, NodeType.VirtualFile)
                {
                    SourceChunkPathHash = hash, BackupChunkPath = file,
                    ChunkDiff = new SerializableChunkDiff { SourceWadFile = wad, OldPathHash = hash, OldCompressionType = WadChunkCompression.None }
                };
            }
            var linked = new LinkedAudioBank
            {
                WpkNode = Source("bank_audio.wpk", package, 1), AudioBnkNode = Source("bank_audio.bnk", media, 2),
                EventsBnkNode = Source("bank_events.bnk", events, 3)
            };
            var provider = new WadContentProvider(new LogService(new Serilog.LoggerConfiguration().CreateLogger()), null, new DirectoriesCreator(), new SvgParser());
            for (int i = 0; i < sounds.Length; i++)
            {
                var node = TreeBuilderService.CreateSoundNode(sounds[i], null, linked, linked.WpkNode, null);
                Assert.Equal((ulong)i + 1, node.SourceChunkPathHash);
                Assert.Equal(new[] { (byte)sounds[i].Id }, await provider.GetWemFileBytesAsync(node));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
