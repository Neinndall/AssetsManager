using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Home;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Wad;
using LeagueToolkit.Core.Wad;
using Newtonsoft.Json;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Formatting
{
    public sealed class VideoExportTests
    {
        [Theory]
        [InlineData("video.WEBM", ConverterFileType.Video)]
        [InlineData("sound.wem", ConverterFileType.Audio)]
        [InlineData("sound.ogg", ConverterFileType.Audio)]
        [InlineData("texture.dds", ConverterFileType.Image)]
        public void UniversalConverterRoutesDroppedFilesToTheirMediaEngine(string path, ConverterFileType expected)
            => Assert.Equal(expected, ConverterModel.GetFileType(path));

        [Fact]
        public void UniversalConverterVideoOptionsFollowTheQueue()
        {
            var model = new ConverterModel();
            Assert.False(model.HasVideo);
            Assert.Null(ConverterModel.GetFileType("unsupported.txt"));
            model.Items.Add(new ConverterItem { FileType = ConverterFileType.Video });
            Assert.True(model.HasVideo);
            Assert.False(model.HasAudio);
            model.Items.Clear();
            Assert.False(model.HasVideo);
        }

        [Fact]
        public void VideoPreferenceDefaultsToOriginalAndSurvivesSerialization()
        {
            Assert.Equal(VideoExportFormat.Original, JsonConvert.DeserializeObject<AppSettings>("{}").VideoExportFormat);
            Assert.Equal(VideoExportFormat.Original, AppSettings.GetDefaultSettings().VideoExportFormat);
            var settings = new AppSettings { VideoExportFormat = VideoExportFormat.Mp4 };
            Assert.Equal(VideoExportFormat.Mp4, JsonConvert.DeserializeObject<AppSettings>(JsonConvert.SerializeObject(settings)).VideoExportFormat);
            settings.ResetToDefaults();
            Assert.Equal(VideoExportFormat.Original, settings.VideoExportFormat);
        }

        [Fact]
        public void AutomaticNewAssetExtractionKeepsVideosOriginalEvenWhenMp4IsSelected()
        {
            using var bridge = new AssetsManagerTestBridge();
            var settings = new AppSettings { VideoExportFormat = VideoExportFormat.Mp4 };
            var extraction = new ExtractionService(settings, bridge.LogService, bridge.Directories, CreateExporter(bridge));
            Assert.Equal(WadExportMode.Original, extraction.GetModeFromSettings(new SerializableChunkDiff { Type = ChunkDiffType.New, NewPath = "videos/intro.webm" }));
        }

        [Theory]
        [InlineData(3000003000u, 100000000u, 30u, 1u)]
        [InlineData(29970000u, 1000000u, 30000u, 1001u)]
        [InlineData(24000u, 1001u, 24000u, 1001u)]
        [InlineData(27000u, 1000u, 27u, 1u)]
        public void WebMTimestampFrameRatesRemainUsableByTheEncoder(uint numerator, uint denominator, uint expectedNumerator, uint expectedDenominator)
        {
            ulong normalized = WindowsVideoTranscoder.NormalizeFrameRate(((ulong)numerator << 32) | denominator);
            Assert.Equal(((ulong)expectedNumerator << 32) | expectedDenominator, normalized);
        }

        [Fact]
        public async Task OriginalSmartAndRawExtractionPreserveWebMBytes()
        {
            using var bridge = new AssetsManagerTestBridge();
            byte[] bytes = Encoding.UTF8.GetBytes("original WebM bytes are never decoded");
            string path = Path.Combine(bridge.RootPath, "source.webm");
            await File.WriteAllBytesAsync(path, bytes);
            var node = new FileSystemNodeModel(path);
            var exporter = CreateExporter(bridge);
            string destination = bridge.CreateDirectory("original");
            await exporter.ExportSmartAsync(node, destination, null, null, CancellationToken.None, null, ExportFormats.ExplorerSmart(AudioExportFormat.Ogg));
            await exporter.ExportAsync(node, destination, CancellationToken.None);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(destination, "source.webm")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(destination, "source (1).webm")));
            Assert.Empty(Directory.GetFiles(destination, "*.mp4"));
        }

        [Theory]
        [InlineData("vp8.webm", 64, 64, false)]
        [InlineData("vp9-opus.webm", 96, 64, true)]
        public async Task ExplorerSmartConvertsVideoAndAudioWithoutOverwritingFiles(string fixture, int width, int height, bool hasAudio)
        {
            using var bridge = new AssetsManagerTestBridge();
            byte[] bytes = ReadFixture(fixture);
            string path = Path.Combine(bridge.RootPath, fixture);
            await File.WriteAllBytesAsync(path, bytes);
            string destination = bridge.CreateDirectory("mp4");
            string firstOutput = Path.Combine(destination, Path.ChangeExtension(fixture, ".mp4"));
            await File.WriteAllTextAsync(firstOutput, "keep existing output");
            var exporter = CreateExporter(bridge);
            var settings = new AppSettings { VideoExportFormat = VideoExportFormat.Mp4 };
            var extraction = new ExtractionService(settings, bridge.LogService, bridge.Directories, exporter);
            var saved = new List<string>();

            await extraction.SaveNodesAsync(new List<FileSystemNodeModel> { new(path) }, destination,
                new ObservableRangeCollection<FileSystemNodeModel>(), bridge.RootPath, CancellationToken.None, saved.Add);

            string output = Assert.Single(saved);
            Assert.NotEqual(firstOutput, output);
            Assert.Equal("keep existing output", await File.ReadAllTextAsync(firstOutput));
            byte[] mp4 = await File.ReadAllBytesAsync(output);
            Assert.Equal("ftyp", Encoding.ASCII.GetString(mp4, 4, 4));
            string content = Encoding.Latin1.GetString(mp4);
            Assert.Contains("avc1", content);
            Assert.Equal(hasAudio, content.Contains("mp4a", StringComparison.Ordinal));
            var moov = FindBox(mp4, 0, mp4.Length, "moov");
            var track = FindBox(mp4, moov.Offset + 8, moov.Offset + moov.Size, "trak");
            var header = FindBox(mp4, track.Offset + 8, track.Offset + track.Size, "tkhd");
            Assert.Equal(width, (int)(BinaryPrimitives.ReadUInt32BigEndian(mp4.AsSpan(header.Offset + header.Size - 8)) >> 16));
            Assert.Equal(height, (int)(BinaryPrimitives.ReadUInt32BigEndian(mp4.AsSpan(header.Offset + header.Size - 4)) >> 16));
            var movieHeader = FindBox(mp4, moov.Offset + 8, moov.Offset + moov.Size, "mvhd");
            Assert.Equal(0, mp4[movieHeader.Offset + 8]);
            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mp4.AsSpan(movieHeader.Offset + 20));
            uint duration = BinaryPrimitives.ReadUInt32BigEndian(mp4.AsSpan(movieHeader.Offset + 24));
            Assert.InRange((double)duration / timescale, 0.38, 0.48);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, Directory.GetFiles(destination).Length);
        }

        [Fact]
        public async Task WadVideoUsesTheSelectedFormatDuringRecursiveSmartSave()
        {
            using var bridge = new AssetsManagerTestBridge();
            byte[] bytes = ReadFixture("vp8.webm");
            string wadPath = Path.Combine(bridge.RootPath, "video.wad.client");
            WadBuilder.Bake(new[] { new WadBakeEntry("videos/sample.webm", () => new MemoryStream(bytes), WadChunkCompression.None) }, wadPath, new WadBakeSettings());
            var loader = new WadNodeLoaderService(new HashResolverService(bridge.Directories, bridge.LogService), bridge.LogService);
            var provider = new WadContentProvider(bridge.LogService, loader, bridge.Directories, new SvgParser());
            var exporter = CreateExporter(bridge, loader, provider);
            var node = new FileSystemNodeModel("sample.webm", false, "videos/sample.webm", wadPath)
            {
                Type = NodeType.VirtualFile,
                SourceChunkPathHash = LeagueToolkit.Hashing.XxHash64Ext.Hash("videos/sample.webm")
            };
            var directory = new FileSystemNodeModel("videos") { Type = NodeType.VirtualDirectory };
            directory.Children.Add(node);
            string destination = bridge.CreateDirectory("wad-output");
            var saved = new List<string>();
            await exporter.ExportSmartAsync(directory, destination, null, null, CancellationToken.None, saved.Add,
                ExportFormats.ExplorerSmart(AudioExportFormat.Ogg, VideoExportFormat.Mp4));
            Assert.EndsWith("sample.mp4", Assert.Single(saved));
            Assert.True(new FileInfo(saved[0]).Length > 0);
        }

        [Fact]
        public async Task FailedConversionDoesNotReportSuccessOrLeavePartialFiles()
        {
            using var bridge = new AssetsManagerTestBridge();
            string path = Path.Combine(bridge.RootPath, "broken.webm");
            await File.WriteAllTextAsync(path, "invalid video");
            string destination = bridge.CreateDirectory("failed");
            var saved = new List<string>();
            var error = await Assert.ThrowsAsync<IOException>(() => CreateExporter(bridge).ExportSmartAsync(
                new FileSystemNodeModel(path), destination, null, null, CancellationToken.None, saved.Add,
                ExportFormats.ExplorerSmart(AudioExportFormat.Ogg, VideoExportFormat.Mp4)));
            Assert.Contains("Select Original in Settings", error.Message);
            Assert.NotNull(error.InnerException);
            Assert.Empty(saved);
            Assert.Empty(Directory.GetFiles(destination));
        }

        [Fact]
        public async Task CancelledConversionDoesNotReportSuccessOrLeavePartialFiles()
        {
            using var bridge = new AssetsManagerTestBridge();
            string path = Path.Combine(bridge.RootPath, "cancel.webm");
            await File.WriteAllBytesAsync(path, ReadFixture("vp9-opus.webm"));
            string destination = bridge.CreateDirectory("cancelled");
            var saved = new List<string>();
            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(10);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateExporter(bridge).ExportSmartAsync(
                new FileSystemNodeModel(path), destination, null, null, cancellation.Token, saved.Add,
                ExportFormats.ExplorerSmart(AudioExportFormat.Ogg, VideoExportFormat.Mp4)));
            Assert.Empty(saved);
            Assert.Empty(Directory.GetFiles(destination));
        }

        private static byte[] ReadFixture(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AssetsManager.Tests.xUnit.Services.Formatting.Fixtures.{name}");
            Assert.NotNull(stream);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static (int Offset, int Size) FindBox(byte[] bytes, int start, int end, string type)
        {
            for (int offset = start; offset + 8 <= end;)
            {
                int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)));
                Assert.InRange(size, 8, end - offset);
                if (Encoding.ASCII.GetString(bytes, offset + 4, 4) == type) return (offset, size);
                offset += size;
            }
            throw new InvalidDataException($"MP4 box {type} was not found.");
        }

        private static AssetExportService CreateExporter(AssetsManagerTestBridge bridge, WadNodeLoaderService loader = null, WadContentProvider provider = null)
        {
            loader ??= new WadNodeLoaderService(new HashResolverService(bridge.Directories, bridge.LogService), bridge.LogService);
            provider ??= new WadContentProvider(bridge.LogService, loader, bridge.Directories, new SvgParser());
            return new AssetExportService(bridge.LogService, provider, loader, bridge.Directories, null, null, null, null);
        }
    }
}
