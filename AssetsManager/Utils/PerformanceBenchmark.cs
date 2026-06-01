using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Explorer;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Utils
{
    public static class PerformanceBenchmark
    {
        public static async Task RunTargetedBinStressTest(string wadPath, int targetCount, LogService log)
        {
            if (!File.Exists(wadPath))
            {
                log.LogError(null, "Stress test failed: WAD file does not exist.");
                return;
            }

            log.Log("=== TARGETED BIN STRESS TEST ===");
            log.Log($"Scanning WAD: {Path.GetFileName(wadPath)}");

            using var wad = new WadFile(wadPath);
            var chunks = wad.Chunks.Values.Where(x => x.CompressedSize > 500).ToList();
            
            log.Log($"Searching for {targetCount} valid BINs among {chunks.Count} potential chunks...");
            
            int successCount = 0;
            long totalObjects = 0;
            
            long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var chunk in chunks)
            {
                if (successCount >= targetCount) break;

                try
                {
                    // Use the library's internal decompression for speed and reliability
                    using var memoryOwner = wad.LoadChunkDecompressed(chunk);
                    var data = memoryOwner.Span;

                    if (data.Length >= 4)
                    {
                        string magic = Encoding.ASCII.GetString(data[..4]);
                        if (magic == "PROP" || magic == "PTCH")
                        {
                            using var ms = new MemoryStream(memoryOwner.Memory.ToArray());
                            var bin = new BinTree(ms);
                            totalObjects += bin.Objects.Count;
                            successCount++;
                            
                            if (successCount % 10 == 0)
                                log.Log($"... processed {successCount}/{targetCount} BINs");
                        }
                    }
                }
                catch { /* Skip */ }
            }

            sw.Stop();
            long allocatedAtEnd = GC.GetAllocatedBytesForCurrentThread();
            double totalAllocatedMb = (allocatedAtEnd - allocatedAtStart) / 1024.0 / 1024.0;

            log.LogSuccess($"Targeted Stress Test Finished!");
            log.Log($"- Valid BINs Parsed: {successCount}");
            log.Log($"- Total Objects Built: {totalObjects}");
            log.Log($"- Total Execution Time: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalMilliseconds / Math.Max(1, successCount):F2}ms per file)");
            log.Log($"- Total RAM Allocated by this thread: {totalAllocatedMb:F2} MB");
            log.Log("=================================");
        }

        public static async Task RunMassiveBinStressTest(string wadPath, LogService log)
        {
            if (!File.Exists(wadPath))
            {
                log.LogError(null, "Stress test failed: WAD file does not exist.");
                return;
            }

            log.Log("=== MASSIVE BIN STRESS TEST ===");
            log.Log($"Scanning WAD: {Path.GetFileName(wadPath)}");

            using var wad = new WadFile(wadPath);
            var chunks = wad.Chunks.Values.Where(x => x.Compression == WadChunkCompression.Zstd).Take(100).ToList();
            
            log.Log($"Starting stress test on {chunks.Count} potential BIN files...");
            
            int successCount = 0;
            long totalObjects = 0;
            
            // Measure memory allocation baseline
            long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var chunk in chunks)
            {
                try
                {
                    // wad.OpenChunk handles decompression correctly
                    using var binStream = wad.OpenChunk(chunk);
                    
                    // Read first 4 bytes to identify if it's a BIN
                    byte[] magicBytes = new byte[4];
                    if (binStream.Read(magicBytes, 0, 4) == 4)
                    {
                        string magic = Encoding.ASCII.GetString(magicBytes);
                        if (magic == "PROP" || magic == "PTCH")
                        {
                            binStream.Position = 0;
                            var bin = new BinTree(binStream);
                            totalObjects += bin.Objects.Count;
                            successCount++;
                        }
                    }
                }
                catch
                {
                    // Skip invalid or corrupted chunks
                }
            }

            sw.Stop();
            long allocatedAtEnd = GC.GetAllocatedBytesForCurrentThread();
            
            double totalAllocatedMb = (allocatedAtEnd - allocatedAtStart) / 1024.0 / 1024.0;

            log.LogSuccess($"Stress Test Finished!");
            log.Log($"- Valid BINs Parsed: {successCount}");
            log.Log($"- Total Objects Built: {totalObjects}");
            log.Log($"- Execution Time: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalMilliseconds / Math.Max(1, successCount):F2}ms per file)");
            log.Log($"- Total RAM Allocated by this thread: {totalAllocatedMb:F2} MB");
            log.Log("=================================");
        }

        public static async Task RunBinPerformanceTest(string binPath, LogService log)
        {
            if (!File.Exists(binPath))
            {
                log.LogError(null, "Benchmark failed: BIN file does not exist.");
                return;
            }

            log.Log("=== BIN PERFORMANCE BENCHMARK ===");
            log.Log($"Target BIN: {Path.GetFileName(binPath)}");
            log.Log($"File Size: {new FileInfo(binPath).Length / 1024.0:F2} KB");

            // 1. Test BIN Parsing Speed
            log.Log("Testing BIN Parsing (Turbo Span Engine)...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var stream = File.OpenRead(binPath);
            var bin = new BinTree(stream);
            sw.Stop();
            log.LogSuccess($"BIN Parsed: {bin.Objects.Count} objects in {sw.Elapsed.TotalMilliseconds:F2}ms");

            // 2. Measure Memory
            long memoryAfter = GC.GetTotalMemory(true);
            log.Log($"Current Managed Memory: {memoryAfter / 1024 / 1024} MB");

            log.Log("Benchmark completed successfully.");
            log.Log("=================================");
        }

        public static async Task RunWadPerformanceTest(string oldWadPath, string newWadPath, LogService log)
        {
            if (!File.Exists(oldWadPath) || !File.Exists(newWadPath))
            {
                log.LogError(null, "Benchmark failed: One or both WAD files do not exist.");
                return;
            }

            log.Log("=== WAD PERFORMANCE BENCHMARK ===");
            log.Log($"Old WAD: {Path.GetFileName(oldWadPath)}");
            log.Log($"New WAD: {Path.GetFileName(newWadPath)}");

            log.Log("Testing TOC Loading Speed (New Single-Read Engine)...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var oldWad = new WadFile(oldWadPath);
            using var newWad = new WadFile(newWadPath);
            sw.Stop();
            log.LogSuccess($"TOC Loaded: {oldWad.Chunks.Count + newWad.Chunks.Count} chunks in {sw.ElapsedMilliseconds}ms");

            log.Log("Testing Checksum Extraction (Zero-Boxing/Direct Access)...");
            sw.Restart();
            var oldChecksums = new Dictionary<ulong, ulong>();
            foreach (var chunk in oldWad.Chunks.Values)
            {
                oldChecksums[chunk.PathHash] = chunk.Checksum;
            }
            var newChecksums = new Dictionary<ulong, ulong>();
            foreach (var chunk in newWad.Chunks.Values)
            {
                newChecksums[chunk.PathHash] = chunk.Checksum;
            }
            sw.Stop();
            log.LogSuccess($"Checksums Extracted: {oldChecksums.Count + newChecksums.Count} entries in {sw.Elapsed.TotalMilliseconds:F2}ms (Zero Reflection overhead)");

            long memoryBefore = GC.GetTotalMemory(true);
            log.Log($"Current Managed Memory: {memoryBefore / 1024 / 1024} MB");

            log.Log("Benchmark completed successfully.");
            log.Log("=================================");
        }

        public static async Task RunExplorerSearchStressTest(int nodeCount, LogService log)
        {
            log.Log("=== EXPLORER SEARCH STRESS TEST ===");
            log.Log($"Simulating tree with {nodeCount} nodes...");

            var rootNodes = new AssetsManager.Utils.Framework.ObservableRangeCollection<FileSystemNodeModel>();
            var random = new Random();

            // 1. Generate Massive Tree
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 5; i++)
            {
                var root = new FileSystemNodeModel($"Plugins_{i}", NodeType.RealDirectory);
                rootNodes.Add(root);
                GenerateDummyNodes(root, nodeCount / 5, random, 0);
            }
            sw.Stop();
            log.LogSuccess($"Tree Generated: {nodeCount} nodes in {sw.ElapsedMilliseconds}ms");

            var searchService = new AssetsManager.Services.Explorer.WadSearchBoxService();
            var treeManager = new AssetsManager.Services.Explorer.Tree.TreeUIManager();

            // 2. Test Search Performance (Filter)
            log.Log("Testing Search Filter (searchText: 'icon')...");
            sw.Restart();
            await searchService.FilterTreeAsync(rootNodes, "icon", System.Threading.CancellationToken.None);
            sw.Stop();
            log.LogSuccess($"Search Filter: {sw.ElapsedMilliseconds}ms");

            // 3. Test Select Deep Node Path Finding (NOW O(depth))
            log.Log("Finding Deep Node Path...");
            FileSystemNodeModel deepNode = null;
            var current = rootNodes[0];
            while (current.Children != null && current.Children.Count > 0)
            {
                deepNode = current.Children[random.Next(0, current.Children.Count)];
                current = deepNode;
            }

            sw.Restart();
            var path = treeManager.FindNodePath(rootNodes, deepNode);
            sw.Stop();
            log.LogSuccess($"Path Finding (depth {path?.Count ?? 0}): {sw.Elapsed.TotalMilliseconds:F4}ms (Zero Reflection overhead)");

            // 4. Test Search Clear Performance (Massive Visibility Reset)
            log.Log("Testing Search Clear (Resetting Visibility)...");
            sw.Restart();
            // This now returns almost instantly because of the background reset split
            await searchService.FilterTreeAsync(rootNodes, "", System.Threading.CancellationToken.None, deepNode);
            sw.Stop();
            log.LogSuccess($"Search Clear (Prioritizing active path): {sw.ElapsedMilliseconds}ms (Task returned to UI)");

            log.Log("Stress test completed.");
            log.Log("=================================");
        }

        private static void GenerateDummyNodes(FileSystemNodeModel parent, int total, Random rnd, int depth)
        {
            if (total <= 0 || depth > 20) return;

            // Simular carpetas masivas (como profile-icons) en niveles profundos
            int childrenCount = (depth == 6) ? Math.Min(total, 5000) : Math.Min(total, rnd.Next(5, 30));
            int remaining = total - childrenCount;

            for (int i = 0; i < childrenCount; i++)
            {
                var type = (depth < 6 && rnd.Next(0, 2) == 0) ? NodeType.VirtualDirectory : NodeType.VirtualFile;
                var node = new FileSystemNodeModel($"Item_{depth}_{i}_{rnd.Next(1000, 9999)}.jpg", type == NodeType.VirtualDirectory, $"path/to/item_{i}", "source.wad")
                {
                    Parent = parent
                };
                parent.Children.Add(node);

                if (remaining > 0 && type == NodeType.VirtualDirectory)
                {
                    int subTotal = remaining / (childrenCount - i);
                    GenerateDummyNodes(node, subTotal, rnd, depth + 1);
                    remaining -= subTotal;
                }
            }
        }
    }
}