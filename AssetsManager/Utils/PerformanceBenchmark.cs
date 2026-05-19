using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Utils
{
    public static class PerformanceBenchmark
    {
        // Add new benchmark methods here for technical diagnostics.
        // Follow the 'Cleanup Protocol': always clean up after testing.
        public static async Task RunWadLookupTest(string wadPath, string virtualPath)
        {
            if (!File.Exists(wadPath))
            {
                Console.WriteLine($"WAD not found: {wadPath}");
                return;
            }

            try
            {
                using var wadFile = new WadFile(wadPath);
                ulong hash = LeagueToolkit.Hashing.XxHash64Ext.Hash(virtualPath.ToLowerInvariant());

                Console.WriteLine($"--- WAD Lookup Test ---");
                Console.WriteLine($"Virtual Path: {virtualPath}");
                Console.WriteLine($"Calculated Hash: 0x{hash:X16}");
                Console.WriteLine($"Total Chunks in WAD: {wadFile.Chunks.Count}");

                // Test 1: Lookup by Hash (Current way)
                bool foundByHash = wadFile.Chunks.TryGetValue(hash, out _);
                Console.WriteLine($"Found by Hash (Manual): {foundByHash}");

                // Test 2: Does WadFile have a string indexer or search?
                // We'll check if the library supports it
                Console.WriteLine("-----------------------");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Test failed: {ex.Message}");
            }
        }
    }
}
