
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Formatting
{
    public class WemConversionService
    {
        private readonly LogService _logService;
        private readonly string _vgmstreamExePath;
        private readonly string _tempConversionPath;
        private static bool _isExtracted = false;
        private static readonly object _lock = new object();

        public WemConversionService(LogService logService)
        {
            _logService = logService;
            string tempBasePath = Path.Combine(Path.GetTempPath(), "AssetsManager");
            _vgmstreamExePath = Path.Combine(tempBasePath, "vgmstream", "vgmstream-cli.exe");
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
                    // Resource name is like "AssetsManager.Resources.vgmstream.vgmstream-cli.exe"
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
                    catch(Exception ex)
                    {
                        _logService.LogError(ex, $"Failed to extract embedded resource: {resourceName}");
                        // If extraction fails, we can't proceed.
                        return;
                    }
                }

                _isExtracted = true;
            }
        }

        public async Task<byte[]> ConvertWemToOggAsync(byte[] wemData)
        {
            if (!_isExtracted || !File.Exists(_vgmstreamExePath))
            {
                _logService.LogError(new FileNotFoundException(), $"vgmstream-cli.exe not found or not extracted. Path: {_vgmstreamExePath}");
                return null;
            }

            var tempWemFile = Path.Combine(_tempConversionPath, $"{Guid.NewGuid()}.wem");
            var tempOggFile = Path.Combine(_tempConversionPath, $"{Guid.NewGuid()}.ogg");

            try
            {
                await File.WriteAllBytesAsync(tempWemFile, wemData);

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = _vgmstreamExePath,
                    Arguments = $"-o \"{tempOggFile}\" \"{tempWemFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_vgmstreamExePath)
                };

                using (var process = new Process { StartInfo = processStartInfo })
                {
                    process.Start();
                    await process.WaitForExitAsync();

                    if (process.ExitCode != 0)
                    {
                        var error = await process.StandardError.ReadToEndAsync();
                        _logService.LogError(new Exception(error), $"Vgmstream process failed with exit code {process.ExitCode}");
                        return null;
                    }
                }

                if (File.Exists(tempOggFile))
                {
                    return await File.ReadAllBytesAsync(tempOggFile);
                }

                _logService.LogWarning("Vgmstream process succeeded but the output OGG file was not found.");
                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An error occurred during WEM to OGG conversion.");
                return null;
            }
            finally
            {
                if (File.Exists(tempWemFile))
                {
                    File.Delete(tempWemFile);
                }
                if (File.Exists(tempOggFile))
                {
                    File.Delete(tempOggFile);
                }
            }
        }
    }
}
