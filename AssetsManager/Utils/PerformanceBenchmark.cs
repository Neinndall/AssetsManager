using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DiffPlex.Chunkers;
using System.Text;

namespace AssetsManager.Utils
{
    public static class PerformanceBenchmark
    {
        public static async Task<string> RunBenchmarkAsync()
        {
            var sb = new StringBuilder();
            sb.AppendLine("DIFFPLEX PERFORMANCE BENCHMARK");
            sb.AppendLine("==============================");

            // Generate some data
            int lineCount = 50000;
            var oldBuilder = new StringBuilder();
            var newBuilder = new StringBuilder();
            for (int i = 0; i < lineCount; i++)
            {
                string line = $"This is line number {i} with some extra text to make it longer.\r\n";
                oldBuilder.Append(line);
                if (i % 10 == 0)
                {
                    newBuilder.Append($"This is line number {i} with SOME MODIFIED text to make it longer.\r\n");
                }
                else if (i % 15 != 0)
                {
                    newBuilder.Append(line);
                }
            }

            string oldText = oldBuilder.ToString();
            string newText = newBuilder.ToString();

            // Test 1: Standard SideBySideDiffBuilder
            var sw = Stopwatch.StartNew();
            var builder = new SideBySideDiffBuilder(new Differ());
            var model = builder.BuildDiffModel(oldText, newText, false);
            sw.Stop();
            sb.AppendLine($"SideBySideDiffBuilder (Standard): {sw.ElapsedMilliseconds}ms");

            // Test 2: InlineDiffBuilder
            sw.Restart();
            var inlineBuilder = new InlineDiffBuilder(new Differ());
            var inlineModel = inlineBuilder.BuildDiffModel(oldText, newText);
            sw.Stop();
            sb.AppendLine($"InlineDiffBuilder: {sw.ElapsedMilliseconds}ms");

            return sb.ToString();
        }
    }
}
