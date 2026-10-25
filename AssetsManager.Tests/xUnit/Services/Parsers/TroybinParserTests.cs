using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Tests.xUnit.Infrastructure;
using LeagueToolkit.IO.Inibin;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Parsers;

public sealed class TroybinParserTests
{
    [Fact]
    public async Task InibinKeysRemainHexadecimalWhenBinFieldCatalogContainsTheSameNumbers()
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binfields.txt"),
            "095afcc6 UnrelatedBinField\n8df40d02 AnotherBinField\n");
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        await resolver.LoadAllHashesAsync();
        Assert.Equal("UnrelatedBinField", resolver.ResolveBinField(0x095afcc6));
        Assert.Equal("AnotherBinField", resolver.ResolveBinField(0x8df40d02));

        var input = new InibinFile();
        input.AddValue(0x095afcc6, "HeroKilledByPlayer");
        input.AddValue(0x8df40d02, (byte)39);
        input.AddValue(0x00000001, long.MaxValue);
        using var binary = new MemoryStream();
        input.Write(binary, leaveOpen: true);
        binary.Position = 0;
        var decoded = new InibinFile(binary, leaveOpen: true);

        using var output = new MemoryStream();
        await new TroybinParser().WriteInibinAsJsonAsync(output, decoded);
        output.Position = 0;
        using var json = await JsonDocument.ParseAsync(output);
        Assert.Equal("HeroKilledByPlayer", json.RootElement.GetProperty("StringList").GetProperty("095afcc6").GetString());
        Assert.Equal(39, json.RootElement.GetProperty("Int8List").GetProperty("8df40d02").GetInt32());
        Assert.Equal(long.MaxValue, json.RootElement.GetProperty("Int64List").GetProperty("00000001").GetInt64());
    }
}
