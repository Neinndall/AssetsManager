using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameMapMaterialPatternsTests
{
    [Theory]
    [InlineData("Maps/KitPieces/TFT/Set21/Materials/Default/Example_Mat")]
    [InlineData("ASSETS/Maps/KitPieces/TFT/Set21/Materials/Default/Nested/Example_Mat")]
    public void GeometryMaterialNamesResolveTexturesAndKeepGenericGrep(string material)
    {
        const string texture = "assets/maps/kitpieces/tft/set21/textures/example_tx.tex";
        const string dds = "assets/maps/kitpieces/tft/set21/textures/example_tx.dds";
        const string generic = "assets/maps/other/independent.tex";
        var engine = Engine(texture, dds, generic, texture + ".wrong");
        var guesser = new GameHashGuesser();
        byte[] payload = Geometry(material, "ASSETS/Maps/Other/Independent.tex");
        guesser.GrepWad(engine, new ArraySegment<byte>(payload), "data/maps/mapgeometry/map22/example.mapgeo",
            "Map22.wad.client", 123, CancellationToken.None);
        Assert.Equal(new[] { dds, texture, generic }.Order(), engine.Matches.Values.Select(m => m.Path).Order());
        Assert.All(engine.Matches.Values, m => { Assert.Equal("Map22.wad.client", m.SourceWadPath); Assert.Equal(123UL, m.SourceChunkHash); });
        Assert.Equal(1, engine.RemainingUnknownCount);
    }

    [Theory]
    [InlineData("Maps/KitPieces/TFT/Set21/Materials/Default/Example_Mat", true)]
    [InlineData("Maps/KitPieces/TFT/Set21/Materials/Default/Example_Mat", false, "ABCD")]
    [InlineData("Maps/KitPieces/TFT/Set21/Materials/Default/Example", false)]
    [InlineData("Maps/KitPieces/TFT/Set21/Materials/../Example_Mat", false)]
    public void RequiresGeometryHeaderAndSizedMaterialName(string material, bool badLength, string header = "OEGM")
    {
        var engine = Engine("assets/maps/kitpieces/tft/set21/textures/example_tx.tex");
        byte[] payload = Geometry(material);
        Encoding.ASCII.GetBytes(header).CopyTo(payload, 0);
        if (badLength) payload[4] = 0;
        new GameHashGuesser().GrepWad(engine, new ArraySegment<byte>(payload), "example.mapgeo", "map", 1, CancellationToken.None);
        Assert.Empty(engine.Matches);
    }

    private static byte[] Geometry(params string[] strings)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("OEGM"u8);
        foreach (string value in strings)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            writer.Write((byte)0);
        }
        return output.ToArray();
    }

    private static HashGuessEngine Engine(params string[] paths) =>
        new(HashGuessDomain.Game, paths.Select(p => XxHash64Ext.Hash(p)).ToHashSet());
}
