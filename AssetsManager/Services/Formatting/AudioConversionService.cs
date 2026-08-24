
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using NAudio.SoundFile;

namespace AssetsManager.Services.Formatting
{
    public class AudioConversionService
    {
        private readonly LogService _logService;
        private readonly string _vgmstreamExePath;
        private readonly string _libsndfileDirectory;
        private readonly string _tempConversionPath;
        private bool _runtimeReady;
        private IntPtr _nativeLibraryHandle;
        private readonly object _runtimeLock = new();

        public AudioConversionService(LogService logService, DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            string audioRuntimePath = directoriesCreator.AudioRuntimePath;
            _vgmstreamExePath = Path.Combine(audioRuntimePath, "Vgmstream", "vgmstream-cli.exe");
            _libsndfileDirectory = Path.Combine(audioRuntimePath, "Libsndfile");
            _tempConversionPath = Path.Combine(audioRuntimePath, "WemPreview");
        }

        public Task<byte[]> ConvertAudioToFormatAsync(
            byte[] audioData,
            string inputExtension,
            AudioExportFormat format,
            CancellationToken cancellationToken = default)
        {
            return ConvertAudioToFormatInternalAsync(audioData, inputExtension, format, cancellationToken);
        }

        private bool EnsureToolsExtracted()
        {
            lock (_runtimeLock)
            {
                string sndfilePath = Path.Combine(_libsndfileDirectory, "sndfile.dll");
                if (_runtimeReady && File.Exists(_vgmstreamExePath) && File.Exists(sndfilePath))
                    return true;

                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    bool vgmstreamReady = ExtractResources(
                        assembly,
                        "Vgmstream",
                        Path.GetDirectoryName(_vgmstreamExePath));
                    bool libsndfileReady = ExtractResources(
                        assembly,
                        "Libsndfile",
                        _libsndfileDirectory);

                    if (!vgmstreamReady || !libsndfileReady || !File.Exists(_vgmstreamExePath) || !File.Exists(sndfilePath))
                    {
                        _logService.LogError(new FileNotFoundException(), "Audio conversion runtime is incomplete.");
                        return false;
                    }

                    _nativeLibraryHandle = NativeLibrary.Load(sndfilePath);
                    Directory.CreateDirectory(_tempConversionPath);
                    _runtimeReady = true;
                    return true;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, "Failed to prepare audio conversion runtime.");
                    return false;
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
            if (!await Task.Run(EnsureToolsExtracted, cancellationToken))
                return null;

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

                bool encoded = await EncodeWithLibsndfileAsync(sourcePath, outputPath, format, cancellationToken);

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

        private async Task<bool> EncodeWithLibsndfileAsync(
            string sourcePath,
            string outputPath,
            AudioExportFormat format,
            CancellationToken cancellationToken)
        {
            try
            {
                SoundFileMajorFormat majorFormat = GetMajorFormat(format);
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!SoundFileCapabilities.IsFormatSupported(majorFormat))
                        throw new NotSupportedException($"libsndfile does not support {format.GetExtension()} output.");

                    using var source = new SoundFileReader(sourcePath);
                    SoundFileWriter.CreateSoundFile(
                        outputPath,
                        source,
                        majorFormat,
                        GetWriterOptions(format));
                }, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"libsndfile failed to encode {format.GetExtension()} audio.");
                return false;
            }
        }

        private static SoundFileMajorFormat GetMajorFormat(AudioExportFormat format)
            => format switch
            {
                AudioExportFormat.Ogg => SoundFileMajorFormat.OggVorbis,
                AudioExportFormat.Wav => SoundFileMajorFormat.Wav,
                AudioExportFormat.Mp3 => SoundFileMajorFormat.Mp3,
                AudioExportFormat.Flac => SoundFileMajorFormat.Flac,
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported audio export format.")
            };

        private static SoundFileWriterOptions GetWriterOptions(AudioExportFormat format)
            => format == AudioExportFormat.Wav
                ? new SoundFileWriterOptions { Subtype = SoundFileSubtype.Pcm16 }
                : null;

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
                    _logService.LogWarning($"Failed to terminate vgmstream process: {killException.Message}");
                }

                try
                {
                    await process.WaitForExitAsync();
                }
                catch (Exception waitException)
                {
                    _logService.LogWarning($"Failed to wait for vgmstream process termination: {waitException.Message}");
                }
                throw;
            }

            string error = (await errorTask).Trim();
            if (process.ExitCode == 0)
                return true;

            _logService.LogError(
                new InvalidOperationException(error),
                $"vgmstream failed with exit code {process.ExitCode}.");
            return false;
        }

        private async Task<byte[]> ReadValidatedOutputAsync(
            string outputPath,
            AudioExportFormat format,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(outputPath))
            {
                _logService.LogWarning($"Audio conversion completed without creating {format.GetExtension()} output.");
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
