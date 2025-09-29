using System;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services;
using AssetsManager.Services.Core;

namespace AssetsManager.Utils
{
    public class DirectoriesCreator
    {
        private readonly LogService _logService;

        public string ResourcesPath { get; private set; }
        public string HashesNewPath { get; private set; }
        public string HashesOldsPaths { get; private set; }
        public string JsonCacheNewPath { get; private set; }
        public string JsonCacheOldPath { get; private set; }
        public string JsonCacheHistoryPath { get; private set; }
        public string SubAssetsDownloadedPath { get; private set; }
        public string BackUpOldHashesPath { get; private set; }
        public string WadComparisonSavePath { get; private set; }
        public string VersionsPath { get; private set; } // Add this

        public string AppDirectory { get; private set; }
        public string CurrentConfigFilePath { get; private set; }
        public string UpdateCachePath { get; private set; }
        public string UpdateTempExtractionPath { get; private set; }
        public string UpdateBatchFilePath { get; private set; }
        public string UpdateLogFilePath { get; private set; }
        public string UpdateTempBackupConfigFilePath { get; private set; }

        public string WadNewAssetsPath { get; private set; }
        public string WadModifiedAssetsPath { get; private set; }
        public string WadRenamedAssetsPath { get; private set; }
        public string WadRemovedAssetsPath { get; private set; }

        public string WebView2DataPath { get; private set; }
        public string TempPreviewPath { get; private set; }
        
        public string WadComparisonDirName { get; private set; }
        public string WadComparisonFullPath { get; private set; }
        public string OldChunksPath { get; private set; }
        public string NewChunksPath { get; private set; }

        public DirectoriesCreator(LogService logService)
        {
            _logService = logService;

            HashesNewPath = Path.Combine("hashes", "new");
            HashesOldsPaths = Path.Combine("hashes", "olds");

            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolderPath = Path.Combine(appDataPath, "AssetsManager");

            JsonCacheNewPath = Path.Combine(appFolderPath, "json_cache", "new");
            JsonCacheOldPath = Path.Combine(appFolderPath, "json_cache", "old");
            JsonCacheHistoryPath = Path.Combine(appFolderPath, "json_cache", "history");

            AppDirectory = AppDomain.CurrentDomain.BaseDirectory;
            CurrentConfigFilePath = Path.Combine(AppDirectory, "config.json");
            UpdateCachePath = Path.Combine(appFolderPath, "update_cache");
            UpdateTempExtractionPath = Path.Combine(UpdateCachePath, "extracted");
            UpdateBatchFilePath = Path.Combine(UpdateCachePath, "update_script.bat");
            UpdateLogFilePath = Path.Combine(UpdateCachePath, "update_log.txt");
            UpdateTempBackupConfigFilePath = Path.Combine(UpdateCachePath, "config.backup.json");

            WebView2DataPath = Path.Combine(appFolderPath, "WebView2Data");
            TempPreviewPath = Path.Combine(WebView2DataPath, "TempPreview");
            
            WadComparisonSavePath = Path.Combine(appFolderPath, "WadComparison");
            VersionsPath = Path.Combine(appFolderPath, "Versions");
        }

        public void GenerateNewResourcesPath()
        {
            string date = DateTime.Now.ToString("ddMMyyyy_HHmmss");
            ResourcesPath = Path.Combine("Resources", date);
            CreateDirectoryInternal(ResourcesPath, false);
        }

        public void GenerateNewSubAssetsDownloadedPath()
        {
            string date = DateTime.Now.ToString("ddMMyyyy_HHmmss");
            SubAssetsDownloadedPath = Path.Combine("AssetsDownloaded", date);
            CreateDirectoryInternal(SubAssetsDownloadedPath, false);
        }

        public void GenerateNewBackUpOldHashesPath()
        {
            string date = DateTime.Now.ToString("ddMMyyyy_HHmmss");
            BackUpOldHashesPath = Path.Combine("hashes", "olds", "BackUp", date);
            CreateDirectoryInternal(BackUpOldHashesPath, false);
        }

        public void GenerateNewWadComparisonPaths()
        {
            string date = DateTime.Now.ToString("ddMMyyyy_HHmmss");
            WadComparisonDirName = $"Comparison_{date}";
            WadComparisonFullPath = Path.Combine(WadComparisonSavePath, WadComparisonDirName);
            OldChunksPath = Path.Combine(WadComparisonFullPath, "wad_chunks", "old");
            NewChunksPath = Path.Combine(WadComparisonFullPath, "wad_chunks", "new");

            CreateDirectoryInternal(OldChunksPath, false);
            CreateDirectoryInternal(NewChunksPath, false);
        }
                                                                                                                                                                                                                          
        public Task CreateDirJsonCacheNewAsync() => CreateDirectoryInternal(JsonCacheNewPath, false);                                                                                 
        public Task CreateDirJsonCacheOldAsync() => CreateDirectoryInternal(JsonCacheOldPath, false);                                                                                 
        public Task CreateDirVersionsAsync() => CreateDirectoryInternal(VersionsPath, false); 
        public Task CreateDirTempPreviewAsync() => CreateDirectoryInternal(TempPreviewPath, false);                                                                                 
        public Task CreateDirWebView2DataAsync() => CreateDirectoryInternal(WebView2DataPath, false);                                                                                 
 
        public string CreateAssetDirectoryPath(string url, string downloadDirectory) 
        {
            string path = new Uri(url).AbsolutePath;

            if (path.StartsWith("/pbe/"))
            {
                path = path.Substring(5); // Eliminar "/pbe/"
            }

            // Reemplazar "rcp-be-lol-game-data/global/" por "rcp-be-lol-game-data/"
            string patternToReplace = "rcp-be-lol-game-data/global/default/";
            if (path.Contains(patternToReplace))
            {
                path = path.Replace(patternToReplace, "rcp-be-lol-game-data/");
            }

            // Mantener la estructura de carpetas en Windows
            string safePath = path.Replace("/", "\\");

            foreach (char invalidChar in Path.GetInvalidPathChars())
            {
                safePath = safePath.Replace(invalidChar.ToString(), "_");
            }

            string fullDirectoryPath = Path.Combine(downloadDirectory, safePath);
            string directory = Path.GetDirectoryName(fullDirectoryPath);

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                // Opcional: _logService.LogDebug($"Created directory for asset: {directory}");
            }

            return directory;
        }

        private Task CreateDirectoryInternal(string path, bool withLogging)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    if (withLogging)
                    {
                        _logService.LogInteractiveInfo($"Directory created at: {path}", path);
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error during directory creation for path: {path}.");
            }

            return Task.CompletedTask;
        }
    }
}