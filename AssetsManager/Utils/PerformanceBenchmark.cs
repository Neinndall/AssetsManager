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
        public static async Task RunAdaptiveResetPerformanceTest(int nodeCount, LogService log)
        {
            log.Log("=== ADAPTIVE RESET PERFORMANCE VALIDATION ===");
            log.Log($"Simulating massive tree with {nodeCount} nodes...");

            var rootNodes = new AssetsManager.Utils.Framework.ObservableRangeCollection<FileSystemNodeModel>();
            var allNodes = new List<FileSystemNodeModel>();
            
            // 1. Setup Phase
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                var root = new FileSystemNodeModel($"Root_{i}", NodeType.RealDirectory);
                rootNodes.Add(root);
                GenerateDummyNodesForReset(root, nodeCount / 10, allNodes);
            }
            sw.Stop();
            log.Log($"- Tree structure built in {sw.ElapsedMilliseconds}ms");

            // Mock search state: make all nodes invisible/matched
            foreach (var node in allNodes) { node.IsVisible = false; node.HasMatch = true; }

            var searchService = new AssetsManager.Services.Explorer.WadSearchBoxService();

            // 2. Execution Phase: Reset using the 16ms strategy
            log.Log("Executing Adaptive Search Clear (16ms time-slicing)...");
            
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            
            // We use the real service method
            // Note: In a console benchmark, Task.Yield() returns immediately, 
            // but we can still measure the overhead and logic correctness.
            await searchService.FilterTreeAsync(rootNodes, string.Empty, System.Threading.CancellationToken.None);
            
            totalSw.Stop();

            log.LogSuccess("Reset Logic verification:");
            log.Log($"- Total Nodes Processed: {allNodes.Count}");
            log.Log($"- Total Time (Main Logic Return): {totalSw.ElapsedMilliseconds}ms");
            
            log.Log("Note: The background reset continues asynchronously. Let's verify a sample of nodes...");
            
            // Wait a bit for the background task to finish (it yields every 16ms)
            int waitTime = 0;
            while (allNodes.Any(n => !n.IsVisible) && waitTime < 5000)
            {
                await Task.Delay(100);
                waitTime += 100;
            }

            int visibleCount = allNodes.Count(n => n.IsVisible);
            log.Log($"- Background Reset Visibility: {visibleCount}/{allNodes.Count} nodes restored");
            log.Log($"- Estimated Background Finish Time: {waitTime}ms");

            if (visibleCount == allNodes.Count)
            {
                log.LogSuccess("VERDICT: PERFECT. The system prioritized visibility and finished the rest without blocking.");
            }
            else
            {
                log.LogWarning("VERDICT: BACKGROUND STILL WORKING. Increase wait time for massive datasets.");
            }
            log.Log("=================================");
        }

        private static void GenerateDummyNodesForReset(FileSystemNodeModel parent, int count, List<FileSystemNodeModel> allNodes)
        {
            for (int i = 0; i < count; i++)
            {
                var node = new FileSystemNodeModel($"Node_{i}", false, "path", "wad") { Parent = parent };
                parent.Children.Add(node);
                allNodes.Add(node);
            }
        }

        public static async Task RunFilteredExpansionTest(int childCount, LogService log)
        {
            log.Log("=== TARGETED FILTERED EXPANSION TEST ===");
            log.Log($"Simulating folder with {childCount} total items...");

            var rootNodes = new AssetsManager.Utils.Framework.ObservableRangeCollection<FileSystemNodeModel>();
            var folder = new FileSystemNodeModel("profile-icons", NodeType.VirtualDirectory);
            rootNodes.Add(folder);

            // Generate items (one match, the rest are others)
            for (int i = 0; i < childCount; i++)
            {
                string name = (i == childCount / 2) ? "7146.jpg" : $"other_icon_{i}.jpg";
                folder.Children.Add(new FileSystemNodeModel(name, false, $"path/{name}", "source.wad") { Parent = folder });
            }

            var searchService = new AssetsManager.Services.Explorer.WadSearchBoxService();
            
            // 1. Run Search Filter
            log.Log("Searching for '7146'...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await searchService.FilterTreeAsync(rootNodes, "7146", System.Threading.CancellationToken.None);
            sw.Stop();
            log.LogSuccess($"Filtering completed in {sw.Elapsed.TotalMilliseconds:F2}ms");

            // 2. Measure Expansion Workload (THE CRITICAL PART)
            // We simulate what the UI has to do when expanding the folder while filtered
            log.Log("Simulating folder expansion while filtered...");
            sw.Restart();
            
            // This represents the work WPF has to do to populate the ItemsControl
            // With my optimization, VisibleChildren should only contain 1 item.
            var itemsToProcess = folder.VisibleChildren;
            int count = itemsToProcess.Count;
            
            sw.Stop();
            
            log.Log($"- Items found in VisibleChildren: {count}");
            log.LogSuccess($"Expansion data ready in {sw.Elapsed.TotalMilliseconds:F4}ms");
            
            if (count == 1)
            {
                log.LogSuccess("VERDICT: Optimization is ACTIVE. Expansion will be INSTANT because only 1 item is processed.");
            }
            else
            {
                log.LogWarning($"VERDICT: Optimization INACTIVE. Expansion would process {count} items, causing lag.");
            }

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
            await searchService.FilterTreeAsync(rootNodes, "", System.Threading.CancellationToken.None, deepNode);
            sw.Stop();
            log.LogSuccess($"Search Clear (Prioritizing active path): {sw.ElapsedMilliseconds}ms (Task returned to UI)");

            log.Log("Stress test completed.");
            log.Log("=================================");
        }

        private static void GenerateDummyNodes(FileSystemNodeModel parent, int total, Random rnd, int depth)
        {
            if (total <= 0 || depth > 20) return;

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