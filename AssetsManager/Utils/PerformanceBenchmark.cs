using System;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Xml;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DiffPlex.Chunkers;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Collections.Generic;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Utils
{
    public static class PerformanceBenchmark
    {
        public static async Task RunBinDiffPipelineTestAsync(LogService logService, string oldWadPath, string newWadPath, string virtualPath)
        {
            long startMem = GC.GetTotalMemory(true);
            
            void LogStatus(string msg, Stopwatch sw = null)
            {
                string time = sw != null ? $" ({sw.ElapsedMilliseconds}ms)" : "";
                long currentMem = GC.GetTotalMemory(true);
                long mb = currentMem / 1024 / 1024;
                long diff = (currentMem - startMem) / 1024 / 1024;
                Console.WriteLine($"[BENCHMARK] {msg}{time} | RAM: {mb} MB (Delta: +{diff} MB)");
            }

            var swTotal = Stopwatch.StartNew();
            var swStep = new Stopwatch();

            // 1. Initialization
            swStep.Start();
            var dirCreator = new DirectoriesCreator();
            var hashResolver = new HashResolverService(dirCreator, logService);
            await hashResolver.LoadAllHashesAsync();
            var propertyParser = new BinPropertyParser(hashResolver);
            var binParser = new BinParser(hashResolver, propertyParser);
            LogStatus("Services Initialized", swStep);

            // 2. Extraction from WAD
            swStep.Restart();
            ulong targetHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(virtualPath.ToLowerInvariant());
            byte[] oldData = null; byte[] newData = null;

            using (var wad = new WadFile(oldWadPath))
                if (wad.Chunks.TryGetValue(targetHash, out var chunk))
                    using (var owner = wad.LoadChunk(chunk)) oldData = WadChunkUtils.DecompressChunk(owner.Span, chunk.Compression);

            using (var wad = new WadFile(newWadPath))
                if (wad.Chunks.TryGetValue(targetHash, out var chunk))
                    using (var owner = wad.LoadChunk(chunk)) newData = WadChunkUtils.DecompressChunk(owner.Span, chunk.Compression);

            LogStatus($"Extracted BIN data (Old: {oldData?.Length} B, New: {newData?.Length} B)", swStep);

            // 3. Serialization to JSON (Streaming)
            swStep.Restart();
            using var oldJsonMs = new MemoryStream();
            using (var oldBinMs = new MemoryStream(oldData)) await propertyParser.WriteBinTreeAsJsonStreamingAsync(oldJsonMs, oldBinMs);
            string oldText = Encoding.UTF8.GetString(oldJsonMs.ToArray());
            oldJsonMs.SetLength(0); // Clear buffer

            using var newJsonMs = new MemoryStream();
            using (var newBinMs = new MemoryStream(newData)) await propertyParser.WriteBinTreeAsJsonStreamingAsync(newJsonMs, newBinMs);
            string newText = Encoding.UTF8.GetString(newJsonMs.ToArray());
            newJsonMs.SetLength(0); // Clear buffer
            
            LogStatus($"JSON Strings Created (Total Length: {(oldText.Length + newText.Length) / 1024 / 1024} MB)", swStep);

            // 4. DiffPlex (Fast Mode Logic)
            swStep.Restart();
            Console.WriteLine("[BENCHMARK] Running BuildFastLinearDiffModel logic...");
            var model = new SideBySideDiffModel();
            
            using (var oldReader = new StringReader(oldText))
            using (var newReader = new StringReader(newText))
            {
                string o, n;
                int line = 1;
                while (true)
                {
                    o = oldReader.ReadLine();
                    n = newReader.ReadLine();
                    if (o == null && n == null) break;

                    if (o == n)
                    {
                        model.OldText.Lines.Add(new DiffPiece { Type = ChangeType.Unchanged, Position = line });
                        model.NewText.Lines.Add(new DiffPiece { Type = ChangeType.Unchanged, Position = line });
                    }
                    else
                    {
                        model.OldText.Lines.Add(new DiffPiece { Text = o, Type = ChangeType.Modified, Position = line });
                        model.NewText.Lines.Add(new DiffPiece { Text = n, Type = ChangeType.Modified, Position = line });
                    }
                    line++;
                }
            }
            LogStatus("Diff Model Built (Optimized - No text duplication)", swStep);

            // 5. AvalonEdit Documents
            swStep.Restart();
            var oldDoc = new TextDocument(oldText);
            var newDoc = new TextDocument(newText);
            
            // PILLAR 1: CRITICAL - Null out raw strings after AvalonEdit takes them
            oldText = null; newText = null;
            GC.Collect(2, GCCollectionMode.Forced, true);
            
            LogStatus("AvalonEdit TextDocuments Created & Raw Strings Freed", swStep);

            // 6. Simulation: Lean Background Rendering
            swStep.Restart();
            // In Large File Mode, we just use the Dictionary map, which we already built in Step 4.
            // No objects are created here.
            LogStatus("Lean Background Rendering Ready (No overhead)", swStep);

            LogStatus("TOTAL TIME", swTotal);
        }

        public static async Task RunSyntheticDiffBenchmarkAsync(LogService logService)
        {
            long startMem = GC.GetTotalMemory(true);
            
            void LogStatus(string msg, Stopwatch sw = null)
            {
                string time = sw != null ? $" ({sw.ElapsedMilliseconds}ms)" : "";
                long currentMem = GC.GetTotalMemory(true);
                long mb = currentMem / 1024 / 1024;
                long diff = (currentMem - startMem) / 1024 / 1024;
                Console.WriteLine($"[BENCHMARK] {msg}{time} | RAM: {mb} MB (Delta: +{diff} MB)");
            }

            var swTotal = Stopwatch.StartNew();
            var swStep = new Stopwatch();

            // 1. Generate large synthetic JSON strings (e.g., ~3-4MB each, ~50k lines)
            swStep.Start();
            int lineCount = 50000;
            var oldSb = new StringBuilder(lineCount * 80);
            var newSb = new StringBuilder(lineCount * 80);

            oldSb.AppendLine("{");
            newSb.AppendLine("{");
            for (int i = 0; i < lineCount; i++)
            {
                // Most lines are unchanged, some modified
                if (i % 20 == 0)
                {
                    oldSb.AppendLine($"  \"property_{i}\": \"old_value_{i}\",");
                    newSb.AppendLine($"  \"property_{i}\": \"new_value_{i}\",");
                }
                else
                {
                    string line = $"  \"property_{i}\": \"value_{i}\",";
                    oldSb.AppendLine(line);
                    newSb.AppendLine(line);
                }
            }
            oldSb.AppendLine("}");
            newSb.AppendLine("}");

            string oldText = oldSb.ToString();
            string newText = newSb.ToString();
            oldSb.Clear();
            newSb.Clear();
            
            LogStatus($"Generated synthetic JSON strings (Old: {oldText.Length / 1024 / 1024} MB, New: {newText.Length / 1024 / 1024} MB, {lineCount} lines)", swStep);

            // 2. Build Side-by-Side Diff Model (DiffPlex calculation)
            swStep.Restart();
            var differ = new Differ();
            var sideBySideBuilder = new SideBySideDiffBuilder(differ);
            var model = sideBySideBuilder.BuildDiffModel(oldText, newText, false);
            LogStatus("DiffPlex SideBySide Model Built", swStep);

            // 3. Prune Unchanged Lines (Memory Optimization)
            swStep.Restart();
            int unchangedCount = 0;
            foreach (var line in model.OldText.Lines)
            {
                if (line.Type == ChangeType.Unchanged)
                {
                    line.Text = null;
                    unchangedCount++;
                }
            }
            foreach (var line in model.NewText.Lines)
            {
                if (line.Type == ChangeType.Unchanged)
                {
                    line.Text = null;
                }
            }
            LogStatus($"Pruned {unchangedCount} unchanged line strings in DiffModel", swStep);

            // 4. Split Text and Build TextDocument (NormalizeTextForAlignment with fallbacks)
            swStep.Restart();
            string[] oldLinesArr = oldText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            string[] newLinesArr = newText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            var jsonFormatterService = new AssetsManager.Services.Formatting.JsonFormatterService();
            var normalizedOld = jsonFormatterService.NormalizeTextForAlignment(model.OldText, oldLinesArr);
            var normalizedNew = jsonFormatterService.NormalizeTextForAlignment(model.NewText, newLinesArr);

            var oldDoc = new TextDocument(normalizedOld);
            var newDoc = new TextDocument(normalizedNew);

            // Clear raw variables
            oldText = null;
            newText = null;
            oldLinesArr = null;
            newLinesArr = null;
            normalizedOld = null;
            normalizedNew = null;
            model = null;

            GC.Collect(2, GCCollectionMode.Forced, true);
            LogStatus("TextDocuments Created & Temporary Strings/Models garbage collected", swStep);

            LogStatus("SYNTHETIC BENCHMARK TOTAL TIME", swTotal);
        }
    }
}
