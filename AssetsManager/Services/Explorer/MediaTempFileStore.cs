using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;

namespace AssetsManager.Services.Explorer
{
    public sealed class MediaTempFileStore
    {
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;
        private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _ownerId = Guid.NewGuid().ToString("N");
        private long _fileSequence;
        private string _activeFilePath;

        public MediaTempFileStore(DirectoriesCreator directoriesCreator, LogService logService)
        {
            _directoriesCreator = directoriesCreator;
            _logService = logService;
        }

        internal int PendingCount => _pendingFiles.Count;

        public async Task<string> CreateAsync(byte[] data, string extension, CancellationToken cancellationToken)
        {
            _directoriesCreator.CreateDirectory(_directoriesCreator.TempPreviewPath);
            long sequence = Interlocked.Increment(ref _fileSequence);
            string fileName = $"preview_{_ownerId}_{sequence}{extension}";
            string filePath = Path.Combine(_directoriesCreator.TempPreviewPath, fileName);
            try
            {
                await File.WriteAllBytesAsync(filePath, data, cancellationToken);
                return filePath;
            }
            catch
            {
                DeleteOrDefer(filePath);
                throw;
            }
        }

        public void Activate(string filePath)
        {
            RetireActive();
            _activeFilePath = filePath;
        }

        public void RetireActive()
        {
            if (!string.IsNullOrEmpty(_activeFilePath))
            {
                _pendingFiles.Add(_activeFilePath);
                _activeFilePath = null;
            }
        }

        public void DeleteOrDefer(string filePath)
        {
            if (!TryDelete(filePath, false) && !string.IsNullOrEmpty(filePath))
            {
                _pendingFiles.Add(filePath);
            }
        }

        public void RetryPending(bool logFailures = false)
        {
            foreach (string filePath in _pendingFiles.ToList())
            {
                if (TryDelete(filePath, logFailures))
                {
                    _pendingFiles.Remove(filePath);
                }
            }
        }

        public void Release()
        {
            RetireActive();
            RetryPending(true);
        }

        private bool TryDelete(string filePath, bool logFailure)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return true;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                if (logFailure)
                {
                    _logService.LogError(ex, $"Failed to remove media preview temp file '{filePath}'.");
                }

                return false;
            }
        }
    }
}
