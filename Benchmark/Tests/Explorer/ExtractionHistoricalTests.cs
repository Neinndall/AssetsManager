using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.BenchmarkTests.Infrastructure;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using LeagueToolkit.Core.Wad;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Explorer
{
    public sealed class ExtractionHistoricalTests
    {
        [Fact]
        public async Task RealFileExtractionPreservesOriginalBytes()
        {
            using var bridge = new AssetsManagerTestBridge();
            byte[] expected = Encoding.UTF8.GetBytes("loose file payload");
            string source = Path.Combine(bridge.RootPath, "source.bin");
            await File.WriteAllBytesAsync(source, expected);
            string destination = bridge.CreateDirectory("export");

            await CreateExporter(bridge).ExportAsync(new FileSystemNodeModel(source), destination, CancellationToken.None);

            Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(destination, "source.bin")));
        }

        [Fact]
        public async Task HistoricalTreeExportsArchivedChunkBytes()
        {
            using var bridge = new AssetsManagerTestBridge();
            const string sourceWad = "game/test.wad.client";
            const string virtualPath = "assets/test/archive.json";
            const ulong hash = 0x1234UL;
            byte[] expected = Encoding.UTF8.GetBytes("archived payload");
            string backup = bridge.CreateDirectory("backup");
            string chunkDirectory = Path.Combine(backup, "wad_chunks", "new", sourceWad);
            Directory.CreateDirectory(chunkDirectory);
            await File.WriteAllBytesAsync(Path.Combine(chunkDirectory, $"{hash:X16}.chunk"), expected);
            string jsonPath = Path.Combine(backup, "wadcomparison.json");
            await WriteComparisonAsync(jsonPath, new SerializableChunkDiff
            {
                Type = ChunkDiffType.New,
                NewPath = virtualPath,
                NewPathHash = hash,
                NewCompressionType = WadChunkCompression.None,
                NewUncompressedSize = (ulong)expected.Length,
                SourceWadFile = sourceWad
            });

            var loader = CreateLoader(bridge);
            var result = await loader.LoadFromBackupAsync(jsonPath, true, CancellationToken.None);
            FileSystemNodeModel file = Flatten(result.Nodes).Single(node => node.Type == NodeType.VirtualFile);
            string destination = bridge.CreateDirectory("historical-export");

            await CreateExporter(bridge, loader).ExportAsync(file, destination, CancellationToken.None);

            Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(destination, "archive.json")));
        }

        [Fact]
        public async Task BatchExtractionStopsBeforeNextFileAfterCancellation()
        {
            using var bridge = new AssetsManagerTestBridge();
            string first = Path.Combine(bridge.RootPath, "first.txt");
            string second = Path.Combine(bridge.RootPath, "second.txt");
            await File.WriteAllTextAsync(first, "first");
            await File.WriteAllTextAsync(second, "second");
            string destination = bridge.CreateDirectory("cancelled-export");
            using var cancellation = new CancellationTokenSource();

            await Assert.ThrowsAsync<OperationCanceledException>(() => CreateExporter(bridge).ExportNodesAsync(
                new List<FileSystemNodeModel> { new(first), new(second) },
                destination,
                new ObservableRangeCollection<FileSystemNodeModel>(),
                bridge.RootPath,
                cancellation.Token,
                onFileSaved: _ => cancellation.Cancel()));

            Assert.True(File.Exists(Path.Combine(destination, "first.txt")));
            Assert.False(File.Exists(Path.Combine(destination, "second.txt")));
        }

        [Fact]
        public async Task CorruptHistoricalChunkIsRejectedWithoutExportingBytes()
        {
            using var bridge = new AssetsManagerTestBridge();
            string backup = bridge.CreateDirectory("corrupt-backup");
            const string sourceWad = "corrupt.wad.client";
            const ulong hash = 0xAAUL;
            string chunkDirectory = Path.Combine(backup, "wad_chunks", "new", sourceWad);
            Directory.CreateDirectory(chunkDirectory);
            await File.WriteAllBytesAsync(Path.Combine(chunkDirectory, $"{hash:X16}.chunk"), Encoding.UTF8.GetBytes("not zstd"));

            byte[] result = await CreateProvider(bridge).GetBackupChunkBytesAsync(
                backup, sourceWad, hash, WadChunkCompression.Zstd, false,
                CancellationToken.None, 128);

            Assert.Null(result);
        }

        [Fact]
        public async Task CorruptHistoricalIndexDoesNotProduceAnEmptyTree()
        {
            using var bridge = new AssetsManagerTestBridge();
            string jsonPath = Path.Combine(bridge.RootPath, "wadcomparison.json");
            await File.WriteAllTextAsync(jsonPath, "{ invalid json");

            await Assert.ThrowsAsync<JsonException>(() =>
                CreateLoader(bridge).LoadFromBackupAsync(jsonPath, true, CancellationToken.None));
        }

        [Fact]
        public async Task HistoricalReconstructionPropagatesCancellation()
        {
            using var bridge = new AssetsManagerTestBridge();
            string jsonPath = Path.Combine(bridge.RootPath, "wadcomparison.json");
            await WriteComparisonAsync(jsonPath, new SerializableChunkDiff
            {
                Type = ChunkDiffType.New,
                NewPath = "assets/test/cancelled.json",
                NewPathHash = 1,
                SourceWadFile = "test.wad.client"
            });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CreateLoader(bridge).LoadFromBackupAsync(jsonPath, true, cancellation.Token));
        }

        [Fact]
        public async Task WadsWithoutPathHashCatalogsDoNotProbeChunkContent()
        {
            using var bridge = new AssetsManagerTestBridge();
            string wadPath = Path.Combine(bridge.RootPath, "empty-hashes.wad");
            byte[] pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

            WadBuilder.Bake(
                new[]
                {
                    new WadBakeEntry(
                        "assets/unknown.bin",
                        () => new MemoryStream(pngSignature),
                        WadChunkCompression.None)
                },
                wadPath,
                new WadBakeSettings());

            ulong pathHash;
            using (var wad = new WadFile(wadPath))
            {
                pathHash = Assert.Single(wad.Chunks.Values).PathHash;
            }

            var children = await CreateLoader(bridge).LoadChildrenAsync(
                new FileSystemNodeModel(wadPath),
                CancellationToken.None);
            FileSystemNodeModel file = Flatten(children).Single(node => node.Type == NodeType.VirtualFile);

            Assert.Equal(pathHash.ToString("x16"), file.Name);
        }

        private static AssetExportService CreateExporter(AssetsManagerTestBridge bridge, WadNodeLoaderService loader = null)
        {
            loader ??= CreateLoader(bridge);
            return new AssetExportService(
                bridge.LogService,
                CreateProvider(bridge, loader),
                loader,
                bridge.Directories,
                null!,
                null!,
                null!,
                null!);
        }

        private static WadContentProvider CreateProvider(AssetsManagerTestBridge bridge, WadNodeLoaderService loader = null) =>
            new(bridge.LogService, loader, bridge.Directories, new SvgParser());

        private static WadNodeLoaderService CreateLoader(AssetsManagerTestBridge bridge) =>
            new(new HashResolverService(bridge.Directories, bridge.LogService), bridge.LogService);

        private static IEnumerable<FileSystemNodeModel> Flatten(IEnumerable<FileSystemNodeModel> nodes)
        {
            foreach (FileSystemNodeModel node in nodes)
            {
                yield return node;
                if (node.LoadedChildren == null) continue;
                foreach (FileSystemNodeModel child in Flatten(node.LoadedChildren)) yield return child;
            }
        }

        private static Task WriteComparisonAsync(string path, SerializableChunkDiff diff)
        {
            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            return File.WriteAllTextAsync(path, JsonSerializer.Serialize(new WadComparisonData
            {
                OldLolPath = "old",
                NewLolPath = "new",
                Version = "test",
                Diffs = new List<SerializableChunkDiff> { diff }
            }, options));
        }
    }
}
