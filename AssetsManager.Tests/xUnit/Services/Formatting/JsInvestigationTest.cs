using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Xunit;
using Xunit.Abstractions;
using ZstdSharp;

namespace AssetsManager.Tests.xUnit.Services.Formatting
{
    public class JsInvestigationTest
    {
        private readonly ITestOutputHelper _output;

        public JsInvestigationTest(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task BeautifyAsync_NavigationJs_FormatsFastAndWithoutRunawayLines()
        {
            string oldChunkPath = @"C:\Users\danielpriego\AppData\Local\AssetsManager\wadcomparison\comparison_28082026_212728\wad_chunks\old\Plugins\rcp-fe-lol-navigation\assets.wad\2D07C2D59AEFA2F7.chunk";
            string newChunkPath = @"C:\Users\danielpriego\AppData\Local\AssetsManager\wadcomparison\comparison_28082026_212728\wad_chunks\new\Plugins\rcp-fe-lol-navigation\assets.wad\2D07C2D59AEFA2F7.chunk";

            if (!File.Exists(oldChunkPath) || !File.Exists(newChunkPath))
            {
                _output.WriteLine("Chunk files do not exist in test environment. Skipping chunk test.");
                return;
            }

            byte[] oldCompressed = await File.ReadAllBytesAsync(oldChunkPath);
            byte[] newCompressed = await File.ReadAllBytesAsync(newChunkPath);

            using var decompressor = new Decompressor();
            byte[] oldBytes = decompressor.Unwrap(oldCompressed).ToArray();
            byte[] newBytes = decompressor.Unwrap(newCompressed).ToArray();

            string oldJs = Encoding.UTF8.GetString(oldBytes).Replace("\0", "");
            string newJs = Encoding.UTF8.GetString(newBytes).Replace("\0", "");

            var logger = new Serilog.LoggerConfiguration().CreateLogger();
            var jsBeautifierService = new JsBeautifierService(new LogService(logger));

            var sw = Stopwatch.StartNew();
            string oldFormatted = await jsBeautifierService.BeautifyAsync(oldJs);
            sw.Stop();
            long oldMs = sw.ElapsedMilliseconds;

            sw.Restart();
            string newFormatted = await jsBeautifierService.BeautifyAsync(newJs);
            sw.Stop();
            long newMs = sw.ElapsedMilliseconds;

            _output.WriteLine($"Formatted OLD (1.47MB) in {oldMs} ms, NEW (1.47MB) in {newMs} ms");

            // Formatting 1.47MB of minified JS must complete in under 1000ms (previously took 5+ minutes each)
            Assert.True(oldMs < 1000, $"Formatting OLD took too long: {oldMs} ms");
            Assert.True(newMs < 1000, $"Formatting NEW took too long: {newMs} ms");

            // Ensure no line exceeds safe length (previously line 12180 was 1,000,000+ characters)
            var oldLines = oldFormatted.Split('\n');
            int maxLineLen = 0;
            for (int i = 0; i < oldLines.Length; i++)
            {
                if (oldLines[i].Length > maxLineLen)
                {
                    maxLineLen = oldLines[i].Length;
                }
            }
            _output.WriteLine($"Max line length in formatted output: {maxLineLen} characters");
            Assert.True(maxLineLen < 1500, $"Formatted JS contains excessive line length: {maxLineLen}");

            // Verify DiffPlex executes instantly and finds the changes
            sw.Restart();
            var differ = new Differ();
            var diffBuilder = new SideBySideDiffBuilder(differ);
            var model = diffBuilder.BuildDiffModel(oldFormatted, newFormatted, false);
            sw.Stop();
            _output.WriteLine($"DiffPlex BuildDiffModel took: {sw.ElapsedMilliseconds} ms");

            Assert.True(sw.ElapsedMilliseconds < 500, $"Diff calculation took too long: {sw.ElapsedMilliseconds} ms");
            Assert.True(model.OldText.Lines.Count > 0);
            Assert.True(model.NewText.Lines.Count > 0);
        }

        [Fact]
        public async Task BeautifyAsync_AllModifiedJsFilesInComparison_FormatFastAndCleanly()
        {
            string comparisonDir = @"C:\Users\danielpriego\AppData\Local\AssetsManager\wadcomparison\comparison_28082026_212728";
            string wadChunksDir = Path.Combine(comparisonDir, "wad_chunks");

            if (!Directory.Exists(wadChunksDir))
            {
                _output.WriteLine("Wad chunks directory does not exist. Skipping.");
                return;
            }

            var logger = new Serilog.LoggerConfiguration().CreateLogger();
            var jsBeautifierService = new JsBeautifierService(new LogService(logger));
            using var decompressor = new Decompressor();

            (string path, string wad, ulong hash)[] filesToTest =
            [
                ("rcp-fe-lol-champ-select.js", @"Plugins\rcp-fe-lol-champ-select\assets.wad", 4480939916768048770UL),
                ("rcp-fe-lol-champion-statistics.js", @"Plugins\rcp-fe-lol-champion-statistics\assets.wad", 4323211152065842841UL),
                ("rcp-fe-lol-navigation.js", @"Plugins\rcp-fe-lol-navigation\assets.wad", 3244760597148566263UL),
                ("rcp-fe-lol-premade-voice.js", @"Plugins\rcp-fe-lol-premade-voice\assets.wad", 3999971032860662243UL),
                ("rcp-fe-lol-settings.js", @"Plugins\rcp-fe-lol-settings\assets.wad", 18274640700588661623UL)
            ];

            foreach (var item in filesToTest)
            {
                string chunkHex = item.hash.ToString("X16");
                string oldChunk = Path.Combine(wadChunksDir, "old", item.wad, $"{chunkHex}.chunk");
                string newChunk = Path.Combine(wadChunksDir, "new", item.wad, $"{chunkHex}.chunk");

                if (!File.Exists(oldChunk) || !File.Exists(newChunk))
                {
                    _output.WriteLine($"Chunk {chunkHex} for {item.path} not found. Skipping.");
                    continue;
                }

                byte[] oldCompressed = await File.ReadAllBytesAsync(oldChunk);
                byte[] newCompressed = await File.ReadAllBytesAsync(newChunk);

                string oldJs = Encoding.UTF8.GetString(decompressor.Unwrap(oldCompressed).ToArray()).Replace("\0", "");
                string newJs = Encoding.UTF8.GetString(decompressor.Unwrap(newCompressed).ToArray()).Replace("\0", "");

                var sw = Stopwatch.StartNew();
                string oldF = await jsBeautifierService.BeautifyAsync(oldJs);
                sw.Stop();
                long oldMs = sw.ElapsedMilliseconds;

                sw.Restart();
                string newF = await jsBeautifierService.BeautifyAsync(newJs);
                sw.Stop();
                long newMs = sw.ElapsedMilliseconds;

                _output.WriteLine($"[TEST] {item.path} ({oldJs.Length / 1024} KB) -> Formatted in OLD: {oldMs} ms, NEW: {newMs} ms");

                Assert.True(oldMs < 1000, $"Formatting {item.path} OLD took {oldMs} ms (> 1s)");
                Assert.True(newMs < 1000, $"Formatting {item.path} NEW took {newMs} ms (> 1s)");

                // Check max line lengths
                int maxLineOld = 0;
                foreach (var line in oldF.Split('\n')) if (line.Length > maxLineOld) maxLineOld = line.Length;
                int maxLineNew = 0;
                foreach (var line in newF.Split('\n')) if (line.Length > maxLineNew) maxLineNew = line.Length;

                _output.WriteLine($"       Max line length -> OLD: {maxLineOld}, NEW: {maxLineNew}");
                Assert.True(maxLineOld < 1500, $"Max line in {item.path} OLD was too long: {maxLineOld}");
                Assert.True(maxLineNew < 1500, $"Max line in {item.path} NEW was too long: {maxLineNew}");

                // Test DiffPlex
                sw.Restart();
                var differ = new Differ();
                var diffBuilder = new SideBySideDiffBuilder(differ);
                var model = diffBuilder.BuildDiffModel(oldF, newF, false);
                sw.Stop();
                _output.WriteLine($"       DiffPlex took: {sw.ElapsedMilliseconds} ms (lines: {model.OldText.Lines.Count})");
                Assert.True(sw.ElapsedMilliseconds < 1500, $"DiffPlex took too long for {item.path}: {sw.ElapsedMilliseconds} ms");
            }
        }

        [Fact]
        public async Task BeautifyAsync_StandardJs_FormatsCorrectly()
        {
            var logger = new Serilog.LoggerConfiguration().CreateLogger();
            var jsBeautifierService = new JsBeautifierService(new LogService(logger));

            string minified = "function test(a,b){if(a>0){return a+b;}else{return 0;}}";
            string formatted = await jsBeautifierService.BeautifyAsync(minified);

            Assert.Contains("\n", formatted);
            Assert.Contains("function test", formatted);
            Assert.Contains("return", formatted);
        }
    }
}
