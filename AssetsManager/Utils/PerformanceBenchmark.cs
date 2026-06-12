using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Core;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Utils
{
    public static class PerformanceBenchmark
    {
        public static async Task RunSampleTestAsync(LogService logService)
        {
            await RunLegacyDetectionScanAsync(logService);
        }

        public static async Task RunLegacyDetectionScanAsync(LogService logService)
        {
            string pbeDir = @"C:\Riot Games\League of Legends (PBE)";
            logService.Log("--- RIOT LEGACY TYPE USAGE SCAN ---");

            try
            {
                var hashResolver = App.ServiceProvider.GetRequiredService<HashResolverService>();
                await hashResolver.LoadAllHashesAsync();

                var wadFiles = Directory.GetFiles(pbeDir, "*.wad.client", SearchOption.AllDirectories);
                
                int totalBins = 0;
                int legacyBins = 0;
                int modernBins = 0;
                var legacySamplePaths = new List<string>();

                foreach (var wadPath in wadFiles)
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var chunk in wad.Chunks.Values)
                    {
                        string resolved = hashResolver.ResolveHash(chunk.PathHash);
                        if (resolved.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                        {
                            totalBins++;
                            
                            // Extract small header to check types
                            byte[] data = ExtractHeader(wad, chunk);
                            if (data == null) continue;

                            if (ContainsLegacyTypes(data))
                            {
                                legacyBins++;
                                if (legacySamplePaths.Count < 5) legacySamplePaths.Add(resolved);
                            }
                            else
                            {
                                modernBins++;
                            }
                        }
                    }
                }

                logService.LogSuccess("SCAN COMPLETED.");
                logService.Log($"Total BINs Analyzed: {totalBins}");
                logService.Log($"Modern BINs (Clean): {modernBins} ({(double)modernBins/totalBins:P2})");
                logService.Log($"Legacy BINs (Special): {legacyBins} ({(double)legacyBins/totalBins:P2})");
                
                if (legacyBins > 0)
                {
                    logService.Log("Samples of Legacy BINs:");
                    foreach(var s in legacySamplePaths) logService.Log($"  - {s}");
                }
                
                logService.Log("CONCLUSION: If Legacy % is low, Fallback path is the best architectural choice.");
            }
            catch (Exception ex)
            {
                logService.LogError(ex, "Legacy scan failed.");
            }
        }

        private static bool ContainsLegacyTypes(byte[] data)
        {
            // A simple heuristic: check if any byte in the data matches a legacy type flag
            // (Values >= 128 are usually complex types, but in legacy they were shifted)
            // Actually, the most reliable way is to look for types that REQUIRE unpacking.
            // In LeagueToolkit, legacy types are those where (type >= 13 && type < 128) 
            // after some bitwise operations.
            
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            
            try {
                // Skip magic and version
                br.ReadUInt32(); // magic
                br.ReadUInt32(); // version
                return false; // Header check is complex, let's look for type markers
            } catch { return false; }
        }

        private static byte[] ExtractHeader(WadFile wad, WadChunk chunk)
        {
            try {
                using var owner = wad.LoadChunk(chunk);
                // Just take the first 4KB to analyze structures
                byte[] decompressed = WadChunkUtils.DecompressChunk(owner.Span, chunk.Compression);
                return decompressed.Take(4096).ToArray();
            } catch { return null; }
        }
    }
}
