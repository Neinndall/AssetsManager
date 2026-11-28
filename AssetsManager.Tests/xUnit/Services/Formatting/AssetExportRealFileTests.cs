using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Formatting;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Explorer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Formatting
{
    public sealed class AssetExportRealFileTests
    {
        [Fact]
        public async Task ReadNodeBytesAsyncReadsLooseRealFileFromDisk()
        {
            using var bridge = new AssetsManagerTestBridge();
            string filePath = Path.Combine(bridge.RootPath, "sample.js");
            byte[] expected = Encoding.UTF8.GetBytes("function test(){return 1;}");
            await File.WriteAllBytesAsync(filePath, expected);

            var service = new AssetExportService(null, null, null, null, null, null, null, null);
            var node = new FileSystemNodeModel("sample.js", NodeType.RealFile)
            {
                VirtualPath = filePath
            };

            byte[] actual = await service.ReadNodeBytesAsync(node, CancellationToken.None);

            Assert.Equal(expected, actual);
        }
    }
}
