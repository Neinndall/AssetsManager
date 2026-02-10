using System;
using System.IO;
using System.Threading.Tasks;

namespace AssetsManager.Utils
{
    public class DirectoriesCreator
    {
        // Fixed root paths
        public string AppDirectory { get; }
        public string HashesPath { get; }
        public string JsonCacheNewPath { get; }
        public string JsonCacheOldPath { get; }
        public string JsonCacheHistoryPath { get; }
        public string AssetsDownloadedPath { get; }
        public string WadComparisonSavePath { get; }
        public string VersionsPath { get; }
        public string UpdateCachePath { get; }
        public string WebView2DataPath { get; }
        public string TempPreviewPath { get; }
        public string ApiCachePath { get; }

        public DirectoriesCreator()
        {
            AppDirectory = AppDomain.CurrentDomain.BaseDirectory;
            HashesPath = Path.Combine(AppDirectory, "hashes");

            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolderPath = Path.Combine(appDataPath, "AssetsManager");

            AssetsDownloadedPath = Path.Combine(AppDirectory, "AssetsDownloaded");
            
            JsonCacheNewPath = Path.Combine(appFolderPath, "json_cache", "new");
            JsonCacheOldPath = Path.Combine(appFolderPath, "json_cache", "old");
            JsonCacheHistoryPath = Path.Combine(appFolderPath, "json_cache", "history");

            UpdateCachePath = Path.Combine(appFolderPath, "update_cache");

            WebView2DataPath = Path.Combine(appFolderPath, "webview2data");
            TempPreviewPath = Path.Combine(WebView2DataPath, "TempPreview");
            ApiCachePath = Path.Combine(appFolderPath, "api_cache");

            WadComparisonSavePath = Path.Combine(appFolderPath, "wadcomparison");
            VersionsPath = Path.Combine(appFolderPath, "versions");
        }

        // Dynamic naming logic (Stateless & Safe)
        public string GetNewSubAssetsDownloadedPath()
        {
            string path = Path.Combine(AssetsDownloadedPath, DateTime.Now.ToString("ddMMyyyy_HHmmss"));
            CreateDirectoryInternal(path);
            return path;
        }

        public (string FolderName, string FullPath) GetNewWadComparisonFolderInfo()
        {
            string folderName = $"comparison_{DateTime.Now:ddMMyyyy_HHmmss}";
            string fullPath = Path.Combine(WadComparisonSavePath, folderName);
            return (folderName, fullPath);
        }

        public string GetNewJsonHistoryPath(string fileName)
        {
            string historyKey = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) 
                ? fileName.Substring(0, fileName.Length - 5) 
                : fileName;
            
            string path = Path.Combine(JsonCacheHistoryPath, historyKey, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            CreateDirectoryInternal(path);
            return path;
        }

        public (string DirectoryPath, string ExePath, string LogFilePath) GetUpdaterInfo()
        {
            string dirPath = Path.Combine(UpdateCachePath, "Updater");
            string exePath = Path.Combine(dirPath, "Updater.exe");
            string logPath = Path.Combine(UpdateCachePath, "update_log.log");
            
            CreateDirectoryInternal(dirPath);
            return (dirPath, exePath, logPath);
        }

        // Action methods
        public async Task PrepareComparisonDirectory(string fullPath)
        {
            await CreateDirectoryAsync(fullPath);
            await CreateDirectoryAsync(Path.Combine(fullPath, "wad_chunks", "old"));
            await CreateDirectoryAsync(Path.Combine(fullPath, "wad_chunks", "new"));
        }

        public Task CreateDirectoryAsync(string path)
        {
            CreateDirectoryInternal(path);
            return Task.CompletedTask;
        }

        public Task CreateHashesDirectories()
        {
            CreateDirectoryInternal(HashesPath);
            return Task.CompletedTask;
        }

        private void CreateDirectoryInternal(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch { /* Silent Fail */ }
        }
    }
}
