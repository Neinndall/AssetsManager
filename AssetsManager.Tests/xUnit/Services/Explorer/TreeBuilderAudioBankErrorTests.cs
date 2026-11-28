using System.Threading.Tasks;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public sealed class TreeBuilderAudioBankErrorTests
    {
        [Fact]
        public async Task ExpandAudioBankAsyncClearsLoadingPlaceholderOnMalformedBankWithoutThrowing()
        {
            using var bridge = new AssetsManagerTestBridge();
            string wadDir = bridge.CreateDirectory("game");
            const string virtualPath = "assets/sounds/wwise2016/sfx/broken_audio.bnk";
            string wadPath = bridge.BakeWad(wadDir, "Broken.wad.client", (virtualPath, "NOT_A_VALID_BNK_HEADER"));

            var contentProvider = new WadContentProvider(bridge.LogService, null, bridge.Directories, null);
            var audioBankService = new AudioBankService(bridge.LogService, null);
            var linker = new AudioBankLinkerService(contentProvider, null, bridge.LogService, null, null, null, audioBankService);
            var treeBuilder = new TreeBuilderService(null, null, bridge.LogService, linker, contentProvider, audioBankService);

            var wadNode = new FileSystemNodeModel("Broken.wad.client", NodeType.WadFile)
            {
                VirtualPath = wadPath,
                SourceWadPath = wadPath
            };
            var bankNode = new FileSystemNodeModel("broken_audio.bnk", NodeType.SoundBank)
            {
                VirtualPath = virtualPath,
                SourceWadPath = wadPath,
                SourceChunkPathHash = XxHash64Ext.Hash(virtualPath),
                Parent = wadNode
            };
            bankNode.Children.Add(new FileSystemNodeModel("Loading...", NodeType.VirtualFile));
            wadNode.Children.Add(bankNode);

            var roots = new ObservableRangeCollection<FileSystemNodeModel> { wadNode };

            await treeBuilder.ExpandAudioBankAsync(bankNode, roots, wadDir);

            Assert.Empty(bankNode.Children);
        }
    }
}
