using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public sealed class WadHierarchyExtractionTests
    {
        [Fact]
        public void GetWadHierarchy_ResolvesContainerAndRelativePathsCorrectly()
        {
            using var bridge = new AssetsManagerTestBridge();
            var exporter = CreateExporter(bridge);

            // 1. Root WAD node
            var wadNode = new FileSystemNodeModel("Companions.wad.client")
            {
                Type = NodeType.WadFile
            };

            var wadResult = exporter.GetWadHierarchy(wadNode);
            Assert.NotNull(wadResult);
            Assert.Equal("Companions.wad.client", wadResult.Value.WadContainerName);
            Assert.Equal(string.Empty, wadResult.Value.RelativePathInWad);

            // 2. assets
            var assetsNode = new FileSystemNodeModel("assets", true, "assets", "Companions.wad.client")
            {
                Type = NodeType.VirtualDirectory,
                Parent = wadNode
            };
            wadNode.Children.Add(assetsNode);

            // 3. characters
            var charactersNode = new FileSystemNodeModel("characters", true, "assets/characters", "Companions.wad.client")
            {
                Type = NodeType.VirtualDirectory,
                Parent = assetsNode
            };
            assetsNode.Children.Add(charactersNode);

            // 4. petchibizoe
            var petchibizoeNode = new FileSystemNodeModel("petchibizoe", true, "assets/characters/petchibizoe", "Companions.wad.client")
            {
                Type = NodeType.VirtualDirectory,
                Parent = charactersNode
            };
            charactersNode.Children.Add(petchibizoeNode);

            var petchibizoeHierarchy = exporter.GetWadHierarchy(petchibizoeNode);
            Assert.NotNull(petchibizoeHierarchy);
            Assert.Equal("Companions.wad.client", petchibizoeHierarchy.Value.WadContainerName);
            Assert.Equal("assets/characters/petchibizoe", petchibizoeHierarchy.Value.RelativePathInWad);

            // 5. Single file inside petchibizoe
            var fileNode = new FileSystemNodeModel("petchibizoe_base_tier1.skn", false, "assets/characters/petchibizoe/petchibizoe_base_tier1.skn", "Companions.wad.client")
            {
                Type = NodeType.VirtualFile,
                Parent = petchibizoeNode
            };
            petchibizoeNode.Children.Add(fileNode);

            var fileHierarchy = exporter.GetWadHierarchy(fileNode);
            Assert.NotNull(fileHierarchy);
            Assert.Equal("Companions.wad.client", fileHierarchy.Value.WadContainerName);
            Assert.Equal("assets/characters/petchibizoe/petchibizoe_base_tier1.skn", fileHierarchy.Value.RelativePathInWad);

            // 6. Loose real file returns null
            var looseFile = new FileSystemNodeModel("loose.bin") { Type = NodeType.RealFile };
            Assert.Null(exporter.GetWadHierarchy(looseFile));
        }

        [Fact]
        public void ResolveNodeDestinationDirectory_ComputesExpectedParentDirectory()
        {
            using var bridge = new AssetsManagerTestBridge();
            var exporter = CreateExporter(bridge);
            string baseDest = bridge.CreateDirectory("export_root");

            var wadNode = new FileSystemNodeModel("Companions.wad.client") { Type = NodeType.WadFile };
            var assetsNode = new FileSystemNodeModel("assets", true, "assets", "Companions.wad.client") { Type = NodeType.VirtualDirectory, Parent = wadNode };
            var charsNode = new FileSystemNodeModel("characters", true, "assets/characters", "Companions.wad.client") { Type = NodeType.VirtualDirectory, Parent = assetsNode };
            var chibiNode = new FileSystemNodeModel("petchibizoe", true, "assets/characters/petchibizoe", "Companions.wad.client") { Type = NodeType.VirtualDirectory, Parent = charsNode };

            // When extracting petchibizoe, destination passed to ExportAsync must be baseDest/Companions.wad.client/assets/characters
            // so ExportAsync creates .../petchibizoe inside it.
            string resolvedDir = exporter.ResolveNodeDestinationDirectory(chibiNode, baseDest);
            string expectedDir = Path.Combine(baseDest, "Companions.wad.client", "assets", "characters");
            Assert.Equal(expectedDir, resolvedDir);
            Assert.True(Directory.Exists(resolvedDir));

            // Target path for logging/revealing
            string targetPath = exporter.GetNodeTargetPath(chibiNode, baseDest);
            string expectedTargetPath = Path.Combine(baseDest, "Companions.wad.client", "assets", "characters", "petchibizoe");
            Assert.Equal(expectedTargetPath, targetPath);
        }

        [Fact]
        public async Task ExportNodesAsync_PreservesWadRootHierarchyOnDisk()
        {
            using var bridge = new AssetsManagerTestBridge();
            var exporter = CreateExporter(bridge);
            string baseDest = bridge.CreateDirectory("extract_output");

            // Build hierarchy
            var wadNode = new FileSystemNodeModel("Companions.wad.client") { Type = NodeType.WadFile };
            var assetsNode = new FileSystemNodeModel("assets", true, "assets", "Companions.wad.client") { Type = NodeType.VirtualDirectory, Parent = wadNode };
            var charsNode = new FileSystemNodeModel("characters", true, "assets/characters", "Companions.wad.client") { Type = NodeType.VirtualDirectory, Parent = assetsNode };
            var chibiNode = new FileSystemNodeModel("petchibizoe", true, "assets/characters/petchibizoe", "Companions.wad.client") { Type = NodeType.VirtualDirectory, Parent = charsNode };

            // Real file payload backing the virtual leaf file
            byte[] filePayload = Encoding.UTF8.GetBytes("chibi mesh test");
            string payloadSource = Path.Combine(bridge.RootPath, "source_payload.bin");
            await File.WriteAllBytesAsync(payloadSource, filePayload);

            var leafFile = new FileSystemNodeModel("petchibizoe.skn", false, payloadSource, "Companions.wad.client")
            {
                Type = NodeType.RealFile, // Uses ReadAllBytesAsync from VirtualPath
                Parent = chibiNode
            };
            chibiNode.Children.Add(leafFile);

            // Extract the petchibizoe folder node
            var selectedNodes = new List<FileSystemNodeModel> { chibiNode };
            await exporter.ExportNodesAsync(
                selectedNodes,
                baseDest,
                new ObservableRangeCollection<FileSystemNodeModel>(),
                bridge.RootPath,
                CancellationToken.None);

            // Verify full hierarchy: baseDest / Companions.wad.client / assets / characters / petchibizoe / petchibizoe.skn
            string expectedFilePath = Path.Combine(baseDest, "Companions.wad.client", "assets", "characters", "petchibizoe", "petchibizoe.skn");
            Assert.True(File.Exists(expectedFilePath));
            Assert.Equal(filePayload, await File.ReadAllBytesAsync(expectedFilePath));
        }

        private static AssetExportService CreateExporter(AssetsManagerTestBridge bridge)
        {
            var loader = new WadNodeLoaderService(new HashResolverService(bridge.Directories, bridge.LogService), bridge.LogService);
            var provider = new WadContentProvider(bridge.LogService, loader, bridge.Directories, new SvgParser());
            return new AssetExportService(
                bridge.LogService,
                provider,
                loader,
                bridge.Directories,
                null!,
                null!,
                null!,
                null!);
        }
    }
}
