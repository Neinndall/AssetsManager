
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Services.Formatting
{
    public class AudioConversionService
    {
        private readonly LogService _logService;
        private readonly string _vgmstreamExePath;
        private readonly string _ffmpegExePath;
        private readonly string _tempConversionPath;
        private static bool _toolsExtracted;
        private static readonly object _extractionLock = new();

        public AudioConversionService(LogService logService)
        {
            _logService = logService;
            string tempBasePath = Path.Combine(Path.GetTempPath(), "AssetsManager");
            _vgmstreamExePath = Path.Combine(tempBasePath, "Vgmstream", "vgmstream-cli.exe");
            _ffmpegExePath = Path.Combine(tempBasePath, "Ffmpeg", "ffmpeg.exe");
            _tempConversionPath = Path.Combine(tempBasePath, "WemPreview");

            EnsureToolsExtracted();
            Directory.CreateDirectory(_tempConversionPath);
        }

        public Task<byte[]> ConvertAudioToFormatAsync(
            byte[] audioData,
            string inputExtension,
            AudioExportFormat format,
            CancellationToken cancellationToken = default)
        {
            return ConvertAudioToFormatInternalAsync(audioData, inputExtension, format, cancellationToken);
        }

        private void EnsureToolsExtracted()
        {
            lock (_extractionLock)
            {
                if (_toolsExtracted && File.Exists(_vgmstreamExePath) && File.Exists(_ffmpegExePath))
                    return;

                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    bool vgmstreamExtracted = ExtractResources(
                        assembly,
                        "Vgmstream",
                        Path.GetDirectoryName(_vgmstreamExePath));
                    bool ffmpegExtracted = ExtractResources(
                        assembly,
                        "Ffmpeg",
                        Path.GetDirectoryName(_ffmpegExePath));

                    _toolsExtracted = vgmstreamExtracted
                        && ffmpegExtracted
                        && File.Exists(_vgmstreamExePath)
                        && File.Exists(_ffmpegExePath);
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, "Failed to extract audio conversion tools.");
                }
            }
        }

        private bool ExtractResources(Assembly assembly, string resourceFolder, string targetDirectory)
        {
            string prefix = $"AssetsManager.Resources.{resourceFolder}.";
            string[] resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (resourceNames.Length == 0)
            {
                _logService.LogError(
                    new FileNotFoundException(),
                    $"Embedded audio resources not found for {resourceFolder}.");
                return false;
            }

            Directory.CreateDirectory(targetDirectory);

            foreach (string resourceName in resourceNames)
            {
                string fileName = resourceName[prefix.Length..];
                string filePath = Path.Combine(targetDirectory, fileName);

                using Stream resourceStream = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new FileNotFoundException($"Embedded resource stream not found: {resourceName}");
                using FileStream fileStream = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                resourceStream.CopyTo(fileStream);
            }

            return true;
        }

        private async Task<byte[]> ConvertAudioToFormatInternalAsync(
            byte[] audioData,
            string inputExtension,
            AudioExportFormat format,
            CancellationToken cancellationToken)
        {
            if (!_toolsExtracted)
            {
                _logService.LogError(new FileNotFoundException(), "Audio conversion tools are not available.");
                return null;
            }

            string normalizedInputExtension = NormalizeExtension(inputExtension);
            string inputPath = CreateTempPath(normalizedInputExtension);
            string decodedWavPath = CreateTempPath(".wav");
            string outputPath = CreateTempPath(format.GetExtension());

            try
            {
                await File.WriteAllBytesAsync(inputPath, audioData, cancellationToken);

                string sourcePath = inputPath;
                bool decodedByVgmstream = normalizedInputExtension == ".wem";

                if (decodedByVgmstream)
                {
                    bool decoded = await RunProcessAsync(
                        _vgmstreamExePath,
                        new[] { "-W", "1", "-o", decodedWavPath, inputPath },
                        cancellationToken);

                    if (!decoded)
                        return null;

                    sourcePath = decodedWavPath;
                }

                if (format == AudioExportFormat.Wav && decodedByVgmstream)
                    return await ReadValidatedOutputAsync(sourcePath, format, cancellationToken);

                bool encoded = await RunProcessAsync(
                    _ffmpegExePath,
                    BuildFfmpegArguments(sourcePath, outputPath, format),
                    cancellationToken);

                return encoded
                    ? await ReadValidatedOutputAsync(outputPath, format, cancellationToken)
                    : null;
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning($"Audio conversion to {format.GetExtension()} was cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Audio conversion to {format.GetExtension()} failed.");
                return null;
            }
            finally
            {
                TryDelete(inputPath);
                TryDelete(decodedWavPath);
                TryDelete(outputPath);
            }
        }

        private async Task<bool> RunProcessAsync(
            string executablePath,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception killException)
                {
                    _logService.LogWarning($"Failed to terminate audio tool process: {killException.Message}");
                }

                try { await process.WaitForExitAsync(); } catch { }
                throw;
            }

            string error = (await errorTask).Trim();
            if (process.ExitCode == 0)
                return true;

            _logService.LogError(
                new InvalidOperationException(error),
                $"Audio tool {Path.GetFileName(executablePath)} failed with exit code {process.ExitCode}.");
            return false;
        }

        private static string[] BuildFfmpegArguments(string sourcePath, string outputPath, AudioExportFormat format)
        {
            var arguments = new List<string>
            {
                "-hide_banner",
                "-loglevel", "error",
                "-nostdin",
                "-y",
                "-i", sourcePath,
                "-map", "0:a:0",
                "-vn"
            };

            arguments.AddRange(format switch
            {
                AudioExportFormat.Mp3 => new[] { "-c:a", "libmp3lame", "-b:a", "192k" },
                AudioExportFormat.Flac => new[] { "-c:a", "flac" },
                AudioExportFormat.Wav => new[] { "-c:a", "pcm_s16le" },
                AudioExportFormat.Ogg => new[] { "-c:a", "libvorbis", "-q:a", "5" },
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported audio export format.")
            });
            arguments.Add(outputPath);
            return arguments.ToArray();
        }

        private async Task<byte[]> ReadValidatedOutputAsync(
            string outputPath,
            AudioExportFormat format,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(outputPath))
            {
                _logService.LogWarning($"Audio tool completed without creating {format.GetExtension()} output.");
                return null;
            }

            byte[] output = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            if (IsValidAudioHeader(output, format))
                return output;

            _logService.LogWarning($"Audio conversion produced invalid {format.GetExtension()} data.");
            return null;
        }

        private static bool IsValidAudioHeader(byte[] data, AudioExportFormat format)
        {
            return format switch
            {
                AudioExportFormat.Wav => data.Length >= 12
                    && HasAscii(data, 0, "RIFF")
                    && HasAscii(data, 8, "WAVE"),
                AudioExportFormat.Ogg => data.Length >= 4 && HasAscii(data, 0, "OggS"),
                AudioExportFormat.Flac => data.Length >= 4 && HasAscii(data, 0, "fLaC"),
                AudioExportFormat.Mp3 => data.Length >= 3
                    && (HasAscii(data, 0, "ID3") || (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported audio export format.")
            };
        }

        private static bool HasAscii(byte[] data, int offset, string value)
        {
            if (offset < 0 || data.Length < offset + value.Length)
                return false;

            return Encoding.ASCII.GetString(data, offset, value.Length) == value;
        }

        private string CreateTempPath(string extension)
            => Path.Combine(_tempConversionPath, $"{Guid.NewGuid():N}{extension}");

        private static string NormalizeExtension(string inputExtension)
        {
            string extension = inputExtension?.Trim() ?? string.Empty;
            return extension.StartsWith(".", StringComparison.Ordinal)
                ? extension.ToLowerInvariant()
                : $".{extension.ToLowerInvariant()}";
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"Failed to remove temporary audio file '{path}': {ex.Message}");
            }
        }
    }
}
