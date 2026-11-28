using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Formatting;
using AssetsManager.Tests.xUnit.Infrastructure;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Formatting;

public sealed class JsBeautifierServiceTests
{
    [Theory]
    [InlineData('"')]
    [InlineData('\'')]
    [InlineData('`')]
    public async Task BeautifyAsync_QuickFormat_DoesNotSplitLongStringLiteralsWithRawNewlines(char quote)
    {
        using var bridge = new AssetsManagerTestBridge();
        var service = new JsBeautifierService(bridge.LogService);

        string longPayload = string.Join(", ", Enumerable.Repeat("alpha beta gamma delta", 60));
        string literal = $"{quote}{longPayload}{quote}";
        string js = $"function test(){{const text={literal};return text;}}";

        string formatted = await service.BeautifyAsync(js);

        Assert.Contains(literal, formatted);
    }
}
