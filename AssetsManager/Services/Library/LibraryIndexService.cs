using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Library;
using LeagueToolkit.Core.Wad;
using Newtonsoft.Json;

namespace AssetsManager.Services.Library
{
    public class LibraryIndexService
    {
        private readonly HashResolverService _hashResolverService;
        private readonly LogService _logService;
        private readonly AppSettings _settings;
        private readonly DirectoriesCreator _directoriesCreator;

        private LibraryIndex _currentIndex;
        private readonly string _indexPath;

        public event Action<int, int, string> IndexingProgressChanged;
        public event Action IndexingCompleted;

        private static readonly FieldInfo _checksumField = typeof(WadChunk).GetField("_checksum", BindingFlags.NonPublic | BindingFlags.Instance);

        public LibraryIndexService(
            HashResolverService hashResolverService, 
            LogService logService, 
            AppSettings settings, 
            DirectoriesCreator directoriesCreator)
        {
            _hashResolverService = hashResolverService;
            _logService = logService;
            _settings = settings;
            _directoriesCreator = directoriesCreator;
            
            _indexPath = Path.Combine(_directoriesCreator.LibraryPath, "asset_index.json");
            _directoriesCreator.CreateDirectory(_directoriesCreator.LibraryPath);
        }

        public async Task<LibraryIndex> GetOrLoadIndexAsync()
        {
            if (_currentIndex != null) return _currentIndex;

            if (File.Exists(_indexPath))
            {
                try
                {
                    _currentIndex = await Task.Run(() => 
                    {
                        using var stream = File.OpenRead(_indexPath);
                        using var reader = new StreamReader(stream);
                        using var jsonReader = new JsonTextReader(reader);
                        var serializer = new JsonSerializer();
                        return serializer.Deserialize<LibraryIndex>(jsonReader);
                    });
                    
                    _logService.LogSuccess($"Loaded global asset index with {_currentIndex.Assets.Count} items.");
                    return _currentIndex;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, "Failed to load asset index. It might be corrupted.");
                }
            }

            return new LibraryIndex();
        }

        public async Task RebuildIndexAsync(string gamePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
            {
                _logService.LogError("Cannot rebuild index: Invalid game path.");
                return;
            }

            _logService.Log($"Rebuilding global asset library index from: {gamePath}");
            
            var newIndex = new LibraryIndex
            {
                LastScan = DateTime.Now,
                GameVersion = "Auto-detected" // Could be improved by reading LeagueClient.exe
            };

            var wadFiles = Directory.GetFiles(gamePath, "*.wad.client", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(gamePath, "*.wad", SearchOption.AllDirectories))
                .ToList();

            int totalWads = wadFiles.Count;
            int processedWads = 0;

            foreach (var wadPath in wadFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedWads++;
                
                string wadName = Path.GetFileName(wadPath);
                IndexingProgressChanged?.Invoke(processedWads, totalWads, wadName);

                try
                {
                    await Task.Run(() => 
                    {
                        using var wad = new WadFile(wadPath);
                        foreach (var chunk in wad.Chunks.Values)
                        {
                            var path = _hashResolverService.ResolveHash(chunk.PathHash);
                            if (string.IsNullOrEmpty(path)) continue;

                            ulong checksum = 0;
                            if (_checksumField != null)
                            {
                                checksum = (ulong)_checksumField.GetValue(chunk);
                            }

                            var asset = new LibraryAsset
                            {
                                Path = path,
                                PathHash = chunk.PathHash,
                                Checksum = checksum,
                                WadSource = wadName,
                                Extension = Path.GetExtension(path).ToLowerInvariant(),
                                Size = chunk.CompressedSize, // Using compressed size for index efficiency
                                Category = DetermineCategory(path)
                            };

                            newIndex.Assets.Add(asset);
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logService.LogWarning($"Skipped WAD {wadName} due to error: {ex.Message}");
                }
            }

            _currentIndex = newIndex;
            await SaveIndexAsync();
            IndexingCompleted?.Invoke();
            
            _logService.LogSuccess($"Global library indexed! Discovered {newIndex.Assets.Count} assets in {processedWads} WAD files.");
        }

        private async Task SaveIndexAsync()
        {
            if (_currentIndex == null) return;

            try
            {
                await Task.Run(() => 
                {
                    using var stream = File.Create(_indexPath);
                    using var writer = new StreamWriter(stream);
                    using var jsonWriter = new JsonTextWriter(writer);
                    var serializer = new JsonSerializer { Formatting = Newtonsoft.Json.Formatting.Indented };
                    serializer.Serialize(jsonWriter, _currentIndex);
                });
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to save asset index.");
            }
        }

        private string DetermineCategory(string path)
        {
            string p = path.ToLowerInvariant();
            if (p.Contains("/characters/")) return "Champions";
            if (p.Contains("/skins/")) return "Skins";
            if (p.Contains("/ux/") || p.Contains("/ui/")) return "UI/HUD";
            if (p.Contains("/maps/")) return "Maps";
            if (p.Contains("/audio/") || p.Contains("/sounds/")) return "Audio";
            if (p.Contains("/vfx/")) return "VFX";
            if (p.Contains("/animations/")) return "Animations";
            
            return "General";
        }

        public async Task<List<LibraryAsset>> SearchAssetsAsync(string query, string category = null)
        {
            var index = await GetOrLoadIndexAsync();
            if (index == null || index.Assets == null) return new List<LibraryAsset>();

            return await Task.Run(() => 
            {
                var results = index.Assets.AsEnumerable();

                if (!string.IsNullOrEmpty(category) && category != "All")
                {
                    results = results.Where(a => a.Category == category);
                }

                if (!string.IsNullOrEmpty(query))
                {
                    string q = query.ToLowerInvariant();
                    results = results.Where(a => a.Path.ToLowerInvariant().Contains(q));
                }

                return results.ToList();
            });
        }
    }
}
