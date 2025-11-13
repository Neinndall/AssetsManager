using LeagueToolkit.Core.Wad;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Views.Models;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Comparator
{
    public enum BinType
    {
        Unknown,
        Champion,
        Map
    }

    public class WadPackagingService
    {
        private readonly LogService _logService;
        private readonly HashResolverService _hashResolverService;

        public WadPackagingService(LogService logService, HashResolverService hashResolverService)
        {
            _logService = logService;
            _hashResolverService = hashResolverService;
        }

        private record BinFileStrategy(string BinPath, string TargetWadName, BinType Type);

        private BinFileStrategy GetBinFileSearchStrategy(string fullPath, string sourceWadPath)
        {
            string sourceWadName = Path.GetFileName(sourceWadPath);

            if (fullPath.Contains("/characters/") && fullPath.Contains("/skins/"))
            {
                var pathParts = fullPath.Split('/');
                string championName = pathParts.FirstOrDefault(p => pathParts.ToList().IndexOf(p) > pathParts.ToList().IndexOf("characters") && pathParts.ToList().IndexOf(p) < pathParts.ToList().IndexOf("skins"));
                string skinFolder = pathParts.FirstOrDefault(p => pathParts.ToList().IndexOf(p) > pathParts.ToList().IndexOf("skins"));

                if (!string.IsNullOrEmpty(championName) && !string.IsNullOrEmpty(skinFolder))
                {
                    string skinName = (skinFolder == "base") ? "skin0" : $"skin{int.Parse(skinFolder.Replace("skin", ""))}";
                    string binPath = $"data/characters/{championName}/skins/{skinName}.bin";
                    string targetWadName = $"{championName.ToLower()}.wad.client";
                    return new BinFileStrategy(binPath, targetWadName, BinType.Champion);
                }
            }
            else if (sourceWadName.StartsWith("Map") || sourceWadName.StartsWith("Common"))
            {
                string[] mapWadNameParts = sourceWadName.Split('.');
                string mapName = mapWadNameParts[0];
                if (!string.IsNullOrEmpty(mapName))
                {
                    string binPath = $"data/maps/shipping/{mapName.ToLower()}/{mapName.ToLower()}.bin";
                    string targetWadName = $"{mapName.ToLower()}.wad.client";
                    return new BinFileStrategy(binPath, targetWadName, BinType.Map);
                }
            }

            return null;
        }

        public async Task CreateLeanWadPackageAsync(IEnumerable<SerializableChunkDiff> diffs, string oldPbePath, string newPbePath, string targetOldWadsPath, string targetNewWadsPath)
        {
            var augmentedDiffs = diffs.ToList();
            var existingHashes = new HashSet<ulong>(augmentedDiffs.Select(d => d.NewPathHash).Concat(augmentedDiffs.Select(d => d.OldPathHash)).Where(h => h != 0));
            var diffsToAdd = new List<SerializableChunkDiff>();

            foreach (var diff in augmentedDiffs)
            {
                string pathForStrategy = diff.NewPath ?? diff.OldPath;
                if (pathForStrategy.EndsWith(".bnk") || pathForStrategy.EndsWith(".wpk"))
                {
                    var strategy = GetBinFileSearchStrategy(pathForStrategy, diff.SourceWadFile);
                    if (strategy != null)
                    {
                        ulong binHash = XxHash64Ext.Hash(strategy.BinPath.ToLower());
                        if (!existingHashes.Contains(binHash))
                        {
                            string wadPath = Path.Combine(newPbePath, strategy.TargetWadName);
                            if (!File.Exists(wadPath))
                            {
                                wadPath = Path.Combine(oldPbePath, strategy.TargetWadName);
                            }

                            if (File.Exists(wadPath))
                            {
                                using var wad = new WadFile(wadPath);
                                if (wad.Chunks.TryGetValue(binHash, out var chunk))
                                {
                                    var newDiff = new SerializableChunkDiff
                                    {
                                        OldPath = strategy.BinPath,
                                        NewPath = strategy.BinPath,
                                        OldPathHash = binHash,
                                        NewPathHash = binHash,
                                        Type = ChunkDiffType.Modified,
                                        SourceWadFile = strategy.TargetWadName,
                                        OldUncompressedSize = (ulong)chunk.UncompressedSize,
                                        NewUncompressedSize = (ulong)chunk.UncompressedSize
                                    };
                                    diffsToAdd.Add(newDiff);
                                    existingHashes.Add(binHash);
                                }
                            }
                        }
                    }
                }
            }

            augmentedDiffs.AddRange(diffsToAdd);

            var diffsByWad = augmentedDiffs.GroupBy(d => d.SourceWadFile);

            foreach (var wadGroup in diffsByWad)
            {
                var wadFileRelativePath = wadGroup.Key;
                _logService.LogDebug($"Processing {wadFileRelativePath} for chunk packaging...");

                // --- Handle OLD chunks ---
                string sourceOldWadPath = Path.Combine(oldPbePath, wadFileRelativePath);
                if (File.Exists(sourceOldWadPath))
                {
                    var oldChunksToSave = wadGroup
                        .Where(d => d.Type == ChunkDiffType.Modified || d.Type == ChunkDiffType.Renamed || d.Type == ChunkDiffType.Removed)
                        .Select(d => d.OldPathHash)
                        .Where(h => h != 0)
                        .Distinct();
                    if (oldChunksToSave.Any())
                        await SaveChunksFromWadAsync(sourceOldWadPath, targetOldWadsPath, oldChunksToSave);
                }

                // --- Handle NEW chunks ---
                string sourceNewWadPath = Path.Combine(newPbePath, wadFileRelativePath);
                if (File.Exists(sourceNewWadPath))
                {
                    var newChunksToSave = wadGroup
                        .Where(d => d.Type == ChunkDiffType.Modified || d.Type == ChunkDiffType.Renamed || d.Type == ChunkDiffType.New)
                        .Select(d => d.NewPathHash)
                        .Where(h => h != 0)
                        .Distinct();
                    if (newChunksToSave.Any())
                        await SaveChunksFromWadAsync(sourceNewWadPath, targetNewWadsPath, newChunksToSave);
                }
            }
        }

        private async Task SaveChunksFromWadAsync(string sourceWadPath, string targetChunkPath, IEnumerable<ulong> chunkHashes)
        {
            try
            {
                using var sourceWad = new WadFile(sourceWadPath);
                await using var fs = new FileStream(sourceWadPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);

                foreach (var hash in chunkHashes)
                {
                    if (sourceWad.Chunks.TryGetValue(hash, out var chunk))
                    {
                        fs.Seek(chunk.DataOffset, SeekOrigin.Begin);
                        byte[] rawChunkData = new byte[chunk.CompressedSize];
                        await fs.ReadAsync(rawChunkData, 0, rawChunkData.Length);

                        string chunkFileName = $"{chunk.PathHash:X16}.chunk";
                        string destChunkPath = Path.Combine(targetChunkPath, chunkFileName);
                        
                        Directory.CreateDirectory(targetChunkPath);
                        await File.WriteAllBytesAsync(destChunkPath, rawChunkData);
                    }
                    else
                    {
                        _logService.LogWarning($"Could not find chunk with hash {hash:X16} in {sourceWadPath}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logService.LogError(ex, $"Failed to save chunks from {sourceWadPath}");
            }
        }
    }
}
