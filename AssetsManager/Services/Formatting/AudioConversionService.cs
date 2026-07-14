
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading; // Added to resolve CancellationToken
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Utils;

namespace AssetsManager.Services.Formatting
{
    public class AudioConversionService
    {
        private readonly LogService _logService;
        private readonly string _vgmstreamExePath;
        private readonly string _tempConversionPath;
        private static bool _isExtracted = false;
        private static readonly object _lock = new object();

        public AudioConversionService(LogService logService)
        {
            _logService = logService;
            string tempBasePath = Path.Combine(Path.GetTempPath(), "AssetsManager");
            _vgmstreamExePath = Path.Combine(tempBasePath, "Vgmstream", "vgmstream-cli.exe");
            _tempConversionPath = Path.Combine(tempBasePath, "WemPreview");

            EnsureVgmstreamIsExtracted();

            if (!Directory.Exists(_tempConversionPath))
            {
                Directory.CreateDirectory(_tempConversionPath);
            }
        }

        private void EnsureVgmstreamIsExtracted()
        {
            lock (_lock)
            {
                if (_isExtracted) return;

                var assembly = Assembly.GetExecutingAssembly();
                var resourceNames = assembly.GetManifestResourceNames().Where(n => n.StartsWith("AssetsManager.Resources.Vgmstream."));

                if (!resourceNames.Any())
                {
                    _logService.LogError(new FileNotFoundException(), "Vgmstream embedded resources not found.");
                    return;
                }

                var vgmstreamDir = Path.GetDirectoryName(_vgmstreamExePath);
                if (!Directory.Exists(vgmstreamDir))
                {
                    Directory.CreateDirectory(vgmstreamDir);
                }

                foreach (var resourceName in resourceNames)
                {
                    // We need to get the actual file name
                    var fileName = string.Join(".", resourceName.Split('.').Skip(3));

                    var filePath = Path.Combine(vgmstreamDir, fileName);

                    try
                    {
                        using (var stream = assembly.GetManifestResourceStream(resourceName))
                        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            stream.CopyTo(fileStream);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, $"Failed to extract embedded resource: {resourceName}");
                        // If extraction fails, we can't proceed.
                        return;
                    }
                }

                _isExtracted = true;
            }
        }

        public Task<byte[]> ConvertAudioToFormatAsync(byte[] audioData, string inputExtension, AudioExportFormat format, CancellationToken cancellationToken = default)
        {
            return ConvertAudioToFormatInternalAsync(audioData, inputExtension, format, cancellationToken);
        }

        private async Task<byte[]> ConvertAudioToFormatInternalAsync(byte[] audioData, string inputExtension, AudioExportFormat format, CancellationToken cancellationToken = default)
        {
            if (!_isExtracted || !File.Exists(_vgmstreamExePath))
            {
                _logService.LogError(new FileNotFoundException(), $"vgmstream-cli.exe not found or not extracted. Path: {_vgmstreamExePath}");
                return null;
            }

            string outputExtension = format switch
            {
                AudioExportFormat.Wav => ".wav",
                AudioExportFormat.Mp3 => ".mp3",
                _ => ".ogg"
            };

            // Use the correct input extension so vgmstream knows how to decode it
            var tempInputFile = Path.Combine(_tempConversionPath, $"{Guid.NewGuid()}{inputExtension}");
            var tempOutFile = Path.Combine(_tempConversionPath, $"{Guid.NewGuid()}{outputExtension}");

            try
            {
                await File.WriteAllBytesAsync(tempInputFile, audioData, cancellationToken);

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = _vgmstreamExePath,
                    Arguments = $"-o \"{tempOutFile}\" \"{tempInputFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_vgmstreamExePath)
                };

                using (var process = new Process { StartInfo = processStartInfo })
                {
                    process.Start();
                    await process.WaitForExitAsync(cancellationToken);

                    if (process.ExitCode != 0)
                    {
                        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                        _logService.LogError(new Exception(error), $"Vgmstream process failed with exit code {process.ExitCode}");
                        return null;
                    }
                }

                if (File.Exists(tempOutFile))
                {
                    return await File.ReadAllBytesAsync(tempOutFile, cancellationToken);
                }

                _logService.LogWarning($"Vgmstream process succeeded but the output {outputExtension} file was not found.");
                return null;
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning($"Audio conversion to {outputExtension} was cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"An error occurred during audio conversion to {outputExtension}.");
                return null;
            }
            finally
            {
                if (File.Exists(tempInputFile)) File.Delete(tempInputFile);
                if (File.Exists(tempOutFile)) File.Delete(tempOutFile);
            }
        }
    }
}
