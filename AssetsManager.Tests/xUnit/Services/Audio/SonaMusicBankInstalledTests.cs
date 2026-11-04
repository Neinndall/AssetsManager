using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Core;
using AssetsManager.Services.Parsers;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Audio;
using LeagueToolkit.Core.Wad;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Audio;

public sealed class SonaMusicBankInstalledTests(ITestOutputHelper output)
{
    [InstalledChampionFact("Sona")]
    public void Skin77MusicInventoryMatchesTheBankHierarchy()
    {
        var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
        using var wad = new WadFile(Path.Combine(InstalledSkins.FindInstall(), "Game", "DATA", "FINAL", "Champions", "Sona.wad.client"));
        const string bank = "assets/sounds/wwise2016/sfx/characters/sona/skins/skin77/sona_skin77_mus";
        byte[] Read(string path) { using var chunk = wad.LoadChunkDecompressed(path); return chunk.Span.ToArray(); }
        byte[] bin = Read("data/characters/sona/skins/skin77.bin");
        byte[] audio = Read(bank + "_audio.bnk");
        byte[] events = Read(bank + "_events.bnk");
        byte[] package = Read(bank + "_audio.wpk");
        var names = new BinAudioEventExtractor().Extract(bin, "sona_skin77_mus", log);
        using var eventsStream = new MemoryStream(events);
        using var audioStream = new MemoryStream(audio);
        using var packageStream = new MemoryStream(package);
        var eventBank = BnkParser.Parse(eventsStream, log);
        var audioBank = BnkParser.Parse(audioStream, log);
        var media = WpkParser.Parse(packageStream, log);
        var objects = (eventBank.Hirc?.Objects ?? new List<BnkObject>()).Concat(audioBank.Hirc?.Objects ?? new List<BnkObject>()).GroupBy(obj => obj.Id).ToDictionary(group => group.Key, group => group.First());
        var bankEvents = objects.Values.Where(obj => obj.Data is EventBnkObjectData).ToArray();
        Assert.All(bankEvents, obj => Assert.True(names.ContainsKey(obj.Id), $"Event name missing for {obj.Id}."));
        var playableEvents = bankEvents.Where(obj => ((EventBnkObjectData)obj.Data).ActionIds
            .Any(id => objects[id].Data is ActionBnkObjectData { Type: 4 })).ToArray();
        const string eventName = "Play_mus_sona_skin77_genreselect";
        Assert.Equal(eventName, names[Assert.Single(playableEvents).Id]);
        var tree = new AudioBankService(log, new BinAudioEventExtractor()).ParseAudioBank(package, audio, events, bin, "sona_skin77_mus");
        var music = Assert.Single(tree, root => !root.IsTechnicalNode);
        Assert.Equal(eventName, music.Name);
        Assert.NotEmpty(music.Containers);
        var linkedMedia = music.EnumerateSounds().Select(sound => sound.Id).ToHashSet();
        Assert.True(linkedMedia.SetEquals(media.Wems.Select(sound => sound.Id)));
        output.WriteLine($"Events={bankEvents.Length}; playable events={playableEvents.Length}; WPK media={media.Wems.Count}; linked media={linkedMedia.Count}.");
    }
}
