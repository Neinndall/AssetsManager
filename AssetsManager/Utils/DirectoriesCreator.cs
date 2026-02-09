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

        public string HashesPath { get; private set; }
        public string JsonCacheNewPath { get; private set; }
        public string JsonCacheOldPath { get; private set; }
        public string JsonCacheHistoryPath { get; private set; }
        public string AssetsDownloadedPath { get; private set; }
        public string SubAssetsDownloadedPath { get; private set; }
        public string WadComparisonSavePath { get; private set; }
        public string VersionsPath { get; private set; }
        public string AppDirectory { get; private set; }

        public string UpdateCachePath { get; private set; }
        public string UpdateLogFilePath { get; private set; }
        public string UpdaterDirectoryPath { get; private set; }
        public string UpdaterExePath { get; private set; }

        public string WebView2DataPath { get; private set; }
        public string TempPreviewPath { get; private set; }
        public string ApiCachePath { get; private set; }

        public string HistoryCachePath { get; private set; }

        public string WadComparisonDirName { get; private set; }
        public string WadComparisonFullPath { get; private set; }
        public string OldChunksPath { get; private set; }
        public string NewChunksPath { get; private set; }

        public DirectoriesCreator(LogService logService)
        {
            _logService = logService;

            AppDirectory = AppDomain.CurrentDomain.BaseDirectory;

            HashesPath = Path.Combine(AppDirectory, "hashes");

            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolderPath = Path.Combine(appDataPath, "AssetsManager");

            AssetsDownloadedPath = "AssetsDownloaded";

            JsonCacheNewPath = Path.Combine(appFolderPath, "json_cache", "new");
            JsonCacheOldPath = Path.Combine(appFolderPath, "json_cache", "old");
            JsonCacheHistoryPath = Path.Combine(appFolderPath, "json_cache", "history");

            UpdateCachePath = Path.Combine(appFolderPath, "update_cache");
            UpdateLogFilePath = Path.Combine(UpdateCachePath, "update_log.log");

            WebView2DataPath = Path.Combine(appFolderPath, "webview2data");
            TempPreviewPath = Path.Combine(WebView2DataPath, "TempPreview");
            ApiCachePath = Path.Combine(appFolderPath, "api_cache");
            HistoryCachePath = Path.Combine(appFolderPath, "history_cache");

            WadComparisonSavePath = Path.Combine(appFolderPath, "wadcomparison");
            VersionsPath = Path.Combine(appFolderPath, "versions");
        }

        public void GenerateNewSubAssetsDownloadedPath()
        {
            string date = DateTime.Now.ToString("ddMMyyyy_HHmmss");
            SubAssetsDownloadedPath = Path.Combine(AssetsDownloadedPath, date);
            CreateDirectoryInternal(SubAssetsDownloadedPath, false);
        }

        public void GenerateNewWadComparisonPaths()
        {
            string date = DateTime.Now.ToString("ddMMyyyy_HHmmss");
            WadComparisonDirName = $"comparison_{date}";
            WadComparisonFullPath = Path.Combine(WadComparisonSavePath, WadComparisonDirName);
            OldChunksPath = Path.Combine(WadComparisonFullPath, "wad_chunks", "old");
            NewChunksPath = Path.Combine(WadComparisonFullPath, "wad_chunks", "new");

            CreateDirectoryInternal(OldChunksPath, false);
            CreateDirectoryInternal(NewChunksPath, false);
        }

        public void GenerateUpdateFilePaths()
        {
            UpdaterDirectoryPath = Path.Combine(UpdateCachePath, "Updater");
            UpdaterExePath = Path.Combine(UpdaterDirectoryPath, "Updater.exe");
            CreateDirectoryInternal(UpdaterDirectoryPath, false);
        }

        public Task CreateDirJsonCacheNewAsync() => CreateDirectoryInternal(JsonCacheNewPath, false);
        public Task CreateDirJsonCacheOldAsync() => CreateDirectoryInternal(JsonCacheOldPath, false);
        public Task CreateDirVersionsAsync() => CreateDirectoryInternal(VersionsPath, false);
        public Task CreateDirTempPreviewAsync() => CreateDirectoryInternal(TempPreviewPath, false);
        public Task CreateDirWebView2DataAsync() => CreateDirectoryInternal(WebView2DataPath, false);
        public Task CreateDirApiCacheAsync() => CreateDirectoryInternal(ApiCachePath, false);
        public Task CreateDirHistoryCacheAsync() => CreateDirectoryInternal(HistoryCachePath, false);

        public Task CreateHashesDirectories()
        {
            CreateDirectoryInternal(HashesPath, false);
            return Task.CompletedTask;
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
