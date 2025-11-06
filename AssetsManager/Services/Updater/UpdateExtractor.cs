// AssetsManager/Utils/UpdateExtractor.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using SharpCompress.Archives;
using SharpCompress.Common;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using System.Reflection;

namespace AssetsManager.Services.Updater
{
    public class UpdateExtractor
    {
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly CustomMessageBoxService _customMessageBoxService;

        public UpdateExtractor(LogService logService, DirectoriesCreator directoriesCreator, CustomMessageBoxService customMessageBoxService)
        {
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));
            _directoriesCreator = directoriesCreator ?? throw new ArgumentNullException(nameof(directoriesCreator));
            _customMessageBoxService = customMessageBoxService ?? throw new ArgumentNullException(nameof(customMessageBoxService));
        }

        public void ExtractAndRestart(string zipPath, bool preserveConfig)
        {
            try
            {
                _logService.Log("Starting update extraction process...");

                _directoriesCreator.GenerateUpdateFilePaths();
                string executablePath = Process.GetCurrentProcess().MainModule.FileName;
                string appDirectory = Path.GetDirectoryName(executablePath)!;
                string updaterCachePath = _directoriesCreator.UpdaterDirectoryPath;
                string updaterExePath = _directoriesCreator.UpdaterExePath;

                var assembly = Assembly.GetExecutingAssembly();
                var resourcePrefix = "AssetsManager.Resources.Updater.";

                foreach (var resource in assembly.GetManifestResourceNames())
                {
                    if (resource.StartsWith(resourcePrefix))
                    {
                        var relativePath = resource.Substring(resourcePrefix.Length);
                        var destinationPath = Path.Combine(updaterCachePath, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                        using (var stream = assembly.GetManifestResourceStream(resource))
                        {
                            if (stream == null)
                            {
                                _logService.LogError(null, $"Resource stream for {resource} is null!");
                                _customMessageBoxService.ShowError("Update Error", $"Could not find the updater resource: {resource}!");
                                return;
                            }

                            using (var fileStream = new FileStream(destinationPath, FileMode.Create))
                            {
                                stream.CopyTo(fileStream);
                            }
                        }
                        _logService.Log($"Extracted {resource} to {destinationPath}");
                    }
                }
                _logService.Log("Updater files extracted successfully to cache.");

                var processId = Process.GetCurrentProcess().Id;
                var arguments = new string[]
                {
                    processId.ToString(),
                    zipPath,
                    appDirectory,
                    executablePath,
                    _directoriesCreator.UpdateLogFilePath,
                    updaterCachePath,
                    preserveConfig.ToString()
                };

                var startInfo = new ProcessStartInfo(updaterExePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                foreach (var arg in arguments)
                {
                    startInfo.ArgumentList.Add(arg);
                }

                Process.Start(startInfo);

                _logService.Log("Update process started. Application will now shut down.");
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An error occurred during the update extraction process.");
                _customMessageBoxService.ShowError("Update Error", $"Error during update: {ex.Message}");
            }
        }
    }
}
