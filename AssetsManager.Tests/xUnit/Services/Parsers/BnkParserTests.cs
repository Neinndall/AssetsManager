using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AssetsManager.Services.Core;
using AssetsManager.Services.Parsers;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Audio;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Parsers;

public sealed class BnkParserTests
{
    private static BnkFile Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return BnkParser.Parse(stream, new LogService(new Serilog.LoggerConfiguration().CreateLogger()));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(127)] [InlineData(128)] [InlineData(129)] [InlineData(16384)]
    public void EventActionCountsKeepAllIdsAcrossVariableLengthBoundaries(int count)
    {
        uint[] actions = Enumerable.Range(1000, count).Select(i => (uint)i).ToArray();
        var bank = Parse(AudioBankFixture.Bank(AudioBankFixture.Event(100, actions)));
        Assert.Equal(actions, Assert.IsType<EventBnkObjectData>(Assert.Single(bank.Hirc.Objects).Data).ActionIds);
    }

    [Fact]
    public void Legacy58KeepsItsFixedWidthEventCount()
    {
        byte[] bytes = AudioBankFixture.Bank((BnkObjectType.Event, AudioBankFixture.Payload(100, writer =>
        { writer.Write(1u); writer.Write(200u); })));
        BitConverter.GetBytes(58u).CopyTo(bytes, 8);
        Assert.Equal(new uint[] { 200 }, Assert.IsType<EventBnkObjectData>(Parse(bytes).Hirc.Objects[0].Data).ActionIds);
    }

    [Theory]
    [InlineData(0u)] [InlineData(3u)] [InlineData(1000u)]
    public void ObjectPayloadMustContainItsIdAndFitInsideHirc(uint size)
    {
        byte[] bytes = AudioBankFixture.Bank(((BnkObjectType)250, BitConverter.GetBytes(123u)));
        BitConverter.GetBytes(size).CopyTo(bytes, 29);
        Assert.Throws<InvalidDataException>(() => Parse(bytes));
    }

    [Fact]
    public void EventCannotReadItsMissingActionIdFromTheNextObject()
    {
        var broken = (BnkObjectType.Event, AudioBankFixture.Payload(1, writer =>
        { AudioBankFixture.Variable(writer, 2); writer.Write(100u); }));
        Assert.Throws<InvalidDataException>(() => Parse(AudioBankFixture.Bank(broken, AudioBankFixture.Event(2, 200))));
    }

    [Theory]
    [InlineData(new byte[] { 0x81 })]
    [InlineData(new byte[] { 0x90, 0x80, 0x80, 0x80, 0 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 })]
    public void RejectsTruncatedOverflowingAndUnterminatedVariableCounts(byte[] count)
    {
        var obj = (BnkObjectType.Event, AudioBankFixture.Payload(1, writer => writer.Write(count)));
        var error = Record.Exception(() => Parse(AudioBankFixture.Bank(obj)));
        Assert.True(error is InvalidDataException or EndOfStreamException);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void PositioningOnlyConsumesAutomationForModesOneAndTwo(int mode)
    {
        byte[] bytes = AudioBankFixture.Bytes(writer =>
        {
            writer.Write((byte)(3 | mode << 5)); writer.Write((byte)0);
            if (mode is 1 or 2) writer.Write(new byte[13]);
        });
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        BnkParseHelper.SkipPosParams(reader, 145);
        Assert.Equal(bytes.Length, stream.Position);
    }

    [Fact]
    public void UnknownKindsDoNotPreventReadingTheFollowingObject()
    {
        var unknown = ((BnkObjectType)250, AudioBankFixture.Payload(1, writer => writer.Write(new byte[25])));
        var bank = Parse(AudioBankFixture.Bank(unknown, AudioBankFixture.Event(2, 200)));
        Assert.Null(bank.Hirc.Objects[0].Data);
        Assert.Equal(new uint[] { 200 }, Assert.IsType<EventBnkObjectData>(bank.Hirc.Objects[1].Data).ActionIds);
    }

    [Fact]
    public void ParsedJsonShowsTypedActionTargetsAndChangesForDiff()
    {
        string before = JsonSerializer.Serialize(Parse(AudioBankFixture.Bank(AudioBankFixture.Action(1, 100))));
        string after = JsonSerializer.Serialize(Parse(AudioBankFixture.Bank(AudioBankFixture.Action(1, 200))));
        using var json = JsonDocument.Parse(before);
        Assert.Equal(100u, json.RootElement.GetProperty("Hirc").GetProperty("Objects")[0].GetProperty("Data").GetProperty("ObjectId").GetUInt32());
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ModernStateGroupsKeepVariableLengthCountsAndPropertyIdsAligned()
    {
        byte[] bytes = AudioBankFixture.Bytes(writer =>
        {
            writer.Write(new byte[6]);
            AudioBankFixture.Variable(writer, 128);
            for (int i = 0; i < 128; i++) { AudioBankFixture.Variable(writer, 128); writer.Write((ushort)0); }
            AudioBankFixture.Variable(writer, 1); writer.Write(1u); writer.Write((byte)0);
            AudioBankFixture.Variable(writer, 128); writer.Write(new byte[128 * 8]);
        });
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        BnkParseHelper.SkipStateGroups(reader, 145);
        Assert.Equal(bytes.Length, stream.Position);
    }

    [Fact]
    public void ModernRtpcParameterIdsUseVariableLengthEncoding()
    {
        byte[] bytes = AudioBankFixture.Bytes(writer =>
        {
            writer.Write((ushort)1); writer.Write(new byte[6]); AudioBankFixture.Variable(writer, 128);
            writer.Write(new byte[5]); writer.Write((ushort)1); writer.Write(new byte[12]);
        });
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        BnkParseHelper.SkipRtpc(reader, 145);
        Assert.Equal(bytes.Length, stream.Position);
    }
}
