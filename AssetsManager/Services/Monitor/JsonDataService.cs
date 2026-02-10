using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Monitor
{
    public class FileUpdateInfo
    {
        public string FileName { get; set; }
        public string FullUrl { get; set; }
        public string OldFilePath { get; set; }
        public string NewFilePath { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class JsonDataService
    {
        public event Action<FileUpdateInfo> FileUpdated;
        public event Action<string> FileCheckStarted;
        public event Action<string> FileCheckFailed;
        public event Action<string> FileCheckUpToDate;

        private readonly LogService _logService;
        private readonly AppSettings _appSettings;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly Requests _requests;
        private readonly IServiceProvider _serviceProvider;
        private readonly DiffViewService _diffViewService;
        private readonly HttpClient _httpClient;

        private readonly HashSet<string> _filesRequiringUniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "trans.json",
        };

        public JsonDataService(LogService logService, AppSettings appSettings, DirectoriesCreator directoriesCreator, Requests requests, IServiceProvider serviceProvider, DiffViewService diffViewService, HttpClient httpClient)
        {
            _logService = logService;
            _appSettings = appSettings;
            _directoriesCreator = directoriesCreator;
            _requests = requests;
            _serviceProvider = serviceProvider;
            _diffViewService = diffViewService;
            _httpClient = httpClient;
        }

        public async Task<List<(string Url, DateTime Timestamp)>> GetFileUrlsFromDirectoryAsync(string directoryUrl)
        {
            var fileUrls = new List<(string Url, DateTime Timestamp)>();
            if (string.IsNullOrWhiteSpace(directoryUrl))
            {
                return fileUrls;
            }

            try
            {
                string html = await _httpClient.GetStringAsync(directoryUrl);
                var regex = new Regex(
                    @"<a href=""(?<filename>[^""]+\.json)""[^>]*>.*?<\/a><\/td><td class=""size"">.*?<\/td><td class=""date"">(?<date>[^<]+)<\/td>",
                    RegexOptions.Singleline
                );

                foreach (Match match in regex.Matches(html))
                {
                    string filename = match.Groups["filename"].Value;
                    string dateStr = match.Groups["date"].Value.Trim();

                    if (!string.IsNullOrEmpty(filename) && ParseDate(dateStr, out DateTime parsedDate))
                    {
                        var baseUri = new Uri(directoryUrl.EndsWith("/") ? directoryUrl : directoryUrl + "/");
                        var fullUri = new Uri(baseUri, filename);
                        fileUrls.Add((fullUri.ToString(), parsedDate));
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error getting file list from directory URL: {directoryUrl}.");
            }

            return fileUrls;
        }

        public async Task<bool> CheckJsonDataUpdatesAsync(bool silent = false, Action<List<string>> onUpdateFound = null)
        {
            if (!_appSettings.CheckJsonDataUpdates || (_appSettings.MonitoredJsonFiles == null))
            {
                return false;
            }

            if (!silent) _logService.Log("Checking for JSON file updates...");
            var serverJsonDataEntries = new Dictionary<string, (DateTime Date, string FullUrl)>();
            bool anyUrlProcessed = false;

            // Process MonitoredJsonFiles
            if (_appSettings.MonitoredJsonFiles != null)
            {
                foreach (var url in _appSettings.MonitoredJsonFiles)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    string parentDirectoryUrl = string.Empty;
                    try
                    {
                        Uri fileUri = new Uri(url);
                        parentDirectoryUrl = new Uri(fileUri, ".").ToString();
                        string html = await _httpClient.GetStringAsync(parentDirectoryUrl);

                        var regex = new Regex(
                            @"<a href=""(?<filename>[^""]+)""[^>]*>.*?<\/a><\/td><td class=""size"">.*?<\/td><td class=""date"">(?<date>[^<]+)<\/td>",
                            RegexOptions.Singleline
                        );

                        bool foundInParent = false;
                        foreach (Match match in regex.Matches(html))
                        {
                            string filenameInParent = match.Groups["filename"].Value;
                            if (url.EndsWith(filenameInParent))
                            {
                                string dateStr = match.Groups["date"].Value.Trim();
                                if (ParseDate(dateStr, out DateTime parsedDate))
                                {
                                    string key = _filesRequiringUniquePaths.Contains(filenameInParent) ? PathUtils.GetUniqueLocalPathFromJsonUrl(url) : filenameInParent;
                                    serverJsonDataEntries[key] = (parsedDate, url);
                                    anyUrlProcessed = true;
                                    foundInParent = true;
                                    break;
                                }
                                else
                                {
                                    _logService.LogWarning($"Could not parse date for {url}: {dateStr}");
                                }
                            }
                        }
                        if (!foundInParent)
                        {
                            _logService.LogWarning($"Could not find {url} in its parent directory listing: {parentDirectoryUrl}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, $"Error fetching or parsing parent directory {parentDirectoryUrl} for file {url}.");
                    }
                }
            }

            if (!anyUrlProcessed)
            {
                _logService.LogWarning("No JSON files could be processed from the configured URLs.");
                return false;
            }

            // Use a list to track updated file names for the current run
            var updatedFiles = new List<string>();
            bool wasUpdated = false;

            foreach (var serverEntry in serverJsonDataEntries)
            {
                string key = serverEntry.Key;
                DateTime serverDate = serverEntry.Value.Date;
                string fullUrl = serverEntry.Value.FullUrl;

                // Find the corresponding entry in AppSettings
                _appSettings.JsonDataModificationDates.TryGetValue(fullUrl, out DateTime lastUpdated);

                // Notify that we are checking this file
                FileCheckStarted?.Invoke(fullUrl);

                if (lastUpdated != serverDate)
                {
                    _appSettings.JsonDataModificationDates[fullUrl] = serverDate; // Update the date in the AppSettings object
                    wasUpdated = true;
                    updatedFiles.Add(key);

                    string oldFilePath = Path.Combine(_directoriesCreator.JsonCacheOldPath, key);
                    string newFilePath = Path.Combine(_directoriesCreator.JsonCacheNewPath, key);

                    await _directoriesCreator.CreateDirectoryAsync(Path.GetDirectoryName(oldFilePath));
                    await _directoriesCreator.CreateDirectoryAsync(Path.GetDirectoryName(newFilePath));

                    try
                    {
                        bool isUpdate = File.Exists(newFilePath);
                        if (isUpdate)
                        {
                            File.Copy(newFilePath, oldFilePath, true);
                        }
                        else
                        {
                            // This is a new file, create an empty old file for diffing purposes
                            File.WriteAllText(oldFilePath, string.Empty);
                        }

                        byte[] fileBytes = await _requests.DownloadFileAsBytesAsync(fullUrl);

                        if (fileBytes != null && fileBytes.Length > 0)
                        {
                            await File.WriteAllBytesAsync(newFilePath, fileBytes);

                            if (_appSettings.SaveJsonHistory && isUpdate)
                            {
                                string changeInstancePath = _directoriesCreator.GetNewJsonHistoryPath(key);

                                string historyOldFilePath = Path.Combine(changeInstancePath, $"old_{Path.GetFileName(key)}");
                                string historyNewFilePath = Path.Combine(changeInstancePath, $"new_{Path.GetFileName(key)}");

                                File.Copy(newFilePath, historyNewFilePath, true);
                                File.Copy(oldFilePath, historyOldFilePath, true);

                                _appSettings.DiffHistory.Insert(0, new HistoryEntry
                                {
                                    FileName = key,
                                    OldFilePath = historyOldFilePath,
                                    NewFilePath = historyNewFilePath,
                                    Timestamp = DateTime.Now,
                                    Type = HistoryEntryType.FileDiff
                                });
                            }

                            // Notify listeners that a file has been updated.
                            FileUpdated?.Invoke(new FileUpdateInfo
                            {
                                FileName = key,
                                FullUrl = fullUrl,
                                OldFilePath = oldFilePath,
                                NewFilePath = newFilePath,
                                Timestamp = serverDate
                            });


                        }
                        else
                        {
                            _logService.LogError($"Failed to download and save JSON content for {fullUrl}. FileBytes were null or empty.");
                            wasUpdated = false;
                        }
                    }
                    catch (IOException ioEx)
                    {
                        _logService.LogError(ioEx, $"IO Error during file operation for {fullUrl}. Path: {newFilePath}");
                        wasUpdated = false;
                    }
                    catch (UnauthorizedAccessException uaEx)
                    {
                        _logService.LogError(uaEx, $"Permission denied during file operation for {fullUrl}. Path: {newFilePath}");
                        wasUpdated = false;
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, $"General error during file operation for {fullUrl}. Path: {newFilePath}");
                        wasUpdated = false;
                        FileCheckFailed?.Invoke(fullUrl);
                    }
                }
                else
                {
                    FileCheckUpToDate?.Invoke(fullUrl);
                }
            }

            if (wasUpdated)
            {
                // Invoke the callback with the list of updated files
                onUpdateFound?.Invoke(updatedFiles);

                // Save settings only if there was an update to persist the LastUpdated date
                AppSettings.SaveSettings(_appSettings);
                if (!silent) _logService.LogSuccess("JSON files are updated.");
            }
            else
            {
                if (!silent) _logService.Log("JSON files are up-to-date.");
            }
            return wasUpdated;
        }

        private bool ParseDate(string dateStr, out DateTime date)
        {
            return DateTime.TryParseExact(dateStr, "yyyy-MMM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
    }
}
