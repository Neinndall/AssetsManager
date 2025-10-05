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

namespace AssetsManager.Services.Updater
{
    public class UpdateExtractor
    {
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;

        public UpdateExtractor(LogService logService, DirectoriesCreator directoriesCreator)
        {
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));
            _directoriesCreator = directoriesCreator ?? throw new ArgumentNullException(nameof(directoriesCreator));
        }

        public void ExtractAndRestart(string zipPath, bool preserveConfig)
        {
            try
            {
                _logService.Log("Starting update extraction process...");

                string executablePath = Process.GetCurrentProcess().MainModule.FileName;

                // Clean up temp directory
                if (Directory.Exists(_directoriesCreator.UpdateTempExtractionPath))
                {
                    Directory.Delete(_directoriesCreator.UpdateTempExtractionPath, true);
                }
                Directory.CreateDirectory(_directoriesCreator.UpdateTempExtractionPath);
                _logService.LogDebug($"Temp extraction directory created at: {_directoriesCreator.UpdateTempExtractionPath}");

                // Extract archive
                using (var archive = ArchiveFactory.Open(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.IsDirectory)
                        {
                            string destinationPath = Path.Combine(_directoriesCreator.UpdateTempExtractionPath, entry.Key);
                            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                            entry.WriteToFile(destinationPath, new ExtractionOptions() { Overwrite = true, ExtractFullPath = true });
                        }
                    }
                }
                _logService.Log("Update archive extracted successfully.");

                // Build batch script
                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine($"set LOGFILE=\"{_directoriesCreator.UpdateLogFilePath}\"");
                sb.AppendLine("echo --- Update Log --- > %LOGFILE%");
                sb.AppendLine("echo Waiting 2 seconds for the application to close... >> %LOGFILE%");
                sb.AppendLine("timeout /t 2 /nobreak > nul");
                sb.AppendLine($"taskkill /PID {Process.GetCurrentProcess().Id} /F >> %LOGFILE% 2>&1");

                if (preserveConfig && File.Exists(_directoriesCreator.CurrentConfigFilePath))
                {
                    sb.AppendLine("echo Moving config.json to backup... >> %LOGFILE%");
                    sb.AppendLine($"move \"{_directoriesCreator.CurrentConfigFilePath}\" \"{_directoriesCreator.UpdateTempBackupConfigFilePath}\" >> %LOGFILE% 2>&1");
                }
                else
                {
                    sb.AppendLine("echo Not preserving config.json (clean mode). >> %LOGFILE%");
                    sb.AppendLine($"if exist \"{_directoriesCreator.CurrentConfigFilePath}\" (");
                    sb.AppendLine("  echo Deleting config.json... >> %LOGFILE%");
                    sb.AppendLine($"  del \"{_directoriesCreator.CurrentConfigFilePath}\" >> %LOGFILE% 2>&1");
                    sb.AppendLine(")");
                }

                sb.AppendLine("echo Copying new files... >> %LOGFILE%");
                sb.AppendLine($"xcopy \"{_directoriesCreator.UpdateTempExtractionPath}\" \"{_directoriesCreator.AppDirectory}\" /E /Y /I >> %LOGFILE% 2>&1");

                if (preserveConfig)
                {
                    sb.AppendLine($"if exist \"{_directoriesCreator.UpdateTempBackupConfigFilePath}\" (");
                    sb.AppendLine("  echo Restoring config.json from backup... >> %LOGFILE%");
                    sb.AppendLine($"  move \"{_directoriesCreator.UpdateTempBackupConfigFilePath}\" \"{_directoriesCreator.CurrentConfigFilePath}\" >> %LOGFILE% 2>&1");
                    sb.AppendLine(" ) ");
                }

                sb.AppendLine("echo Cleaning up temporary folder... >> %LOGFILE%");
                sb.AppendLine($"rmdir /s /q \"{_directoriesCreator.UpdateTempExtractionPath}\" >> %LOGFILE% 2>&1");

                sb.AppendLine("echo Deleting downloaded zip file... >> %LOGFILE%");
                sb.AppendLine($"del \"{zipPath}\" >> %LOGFILE% 2>&1");

                sb.AppendLine("echo Restarting application... >> %LOGFILE%");
                sb.AppendLine($"start \"\" \"{executablePath}\"");

                sb.AppendLine("echo Deleting update script... >> %LOGFILE%");
                sb.AppendLine("del \"%~f0\"");

                File.WriteAllText(_directoriesCreator.UpdateBatchFilePath, sb.ToString());
                _logService.LogDebug($"Update batch script created at: {_directoriesCreator.UpdateBatchFilePath}");

                // Execute batch script
                Process.Start(new ProcessStartInfo(_directoriesCreator.UpdateBatchFilePath)
                {
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                _logService.Log("Update script executed. Application will now shut down.");
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An error occurred during the update extraction process.");
                MessageBox.Show($"Error during update: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
