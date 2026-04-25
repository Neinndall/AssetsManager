using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using AssetsManager.Services.Core;

namespace AssetsManager.Utils
{
    public class BinaryHashCache : IDisposable
    {
        private readonly string _txtPath;
        private readonly string _binPath;
        private readonly LogService _logService;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _dataAccessor;
        private ulong[] _hashes;
        private int[] _offsets;
        private long _dataStartOffset;
        private bool _isLoaded;

        public BinaryHashCache(string txtPath, LogService logService)
        {
            _txtPath = txtPath;
            _binPath = Path.ChangeExtension(txtPath, ".bin");
            _logService = logService;
        }

        public string BinPath => _binPath;

        public void Load()
        {
            if (_isLoaded) return;

            try
            {
                if (!File.Exists(_binPath) || File.GetLastWriteTime(_binPath) < File.GetLastWriteTime(_txtPath))
                {
                    _logService.Log($"Generating binary hash cache for {Path.GetFileName(_txtPath)}...");
                    GenerateCache();
                }

                InitializeFromCache();
                _isLoaded = true;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to load binary cache for {Path.GetFileName(_txtPath)}");
            }
        }

        private void GenerateCache()
        {
            var entries = new List<(ulong Hash, string Path)>();
            using (var reader = new StreamReader(_txtPath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int spaceIndex = line.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        var hashStr = line.Substring(0, spaceIndex);
                        var path = line.Substring(spaceIndex + 1);
                        if (ulong.TryParse(hashStr, System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                        {
                            entries.Add((hash, path));
                        }
                    }
                }
            }

            var sortedEntries = entries.OrderBy(e => e.Hash).ToList();

            using (var fs = new FileStream(_binPath, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                writer.Write(sortedEntries.Count);
                long hashesPos = fs.Position;
                for (int i = 0; i < sortedEntries.Count; i++) writer.Write(0UL);
                long offsetsPos = fs.Position;
                for (int i = 0; i < sortedEntries.Count; i++) writer.Write(0);

                long stringsStartPos = fs.Position;
                int[] offsets = new int[sortedEntries.Count];
                for (int i = 0; i < sortedEntries.Count; i++)
                {
                    offsets[i] = (int)(fs.Position - stringsStartPos);
                    var bytes = Encoding.UTF8.GetBytes(sortedEntries[i].Path);
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                }

                fs.Position = hashesPos;
                foreach (var entry in sortedEntries) writer.Write(entry.Hash);
                fs.Position = offsetsPos;
                foreach (var offset in offsets) writer.Write(offset);
            }
        }

        private void InitializeFromCache()
        {
            _mmf = MemoryMappedFile.CreateFromFile(_binPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using (var accessor = _mmf.CreateViewAccessor(0, 4, MemoryMappedFileAccess.Read))
            {
                int count = accessor.ReadInt32(0);
                _hashes = new ulong[count];
                _offsets = new int[count];

                using (var hashAccessor = _mmf.CreateViewAccessor(4, count * 8, MemoryMappedFileAccess.Read))
                {
                    hashAccessor.ReadArray(0, _hashes, 0, count);
                }

                using (var offsetAccessor = _mmf.CreateViewAccessor(4 + (count * 8), count * 4, MemoryMappedFileAccess.Read))
                {
                    offsetAccessor.ReadArray(0, _offsets, 0, count);
                }

                _dataStartOffset = 4 + (count * 8) + (count * 4);
                // Abrir un accessor persistente para los datos de los strings
                _dataAccessor = _mmf.CreateViewAccessor(_dataStartOffset, 0, MemoryMappedFileAccess.Read);
            }
        }

        public IReadOnlyList<ulong> Hashes => _hashes;

        public string Resolve(ulong hash)
        {
            if (!_isLoaded || _hashes == null) return null;

            int index = Array.BinarySearch(_hashes, hash);
            if (index < 0) return null;

            return ResolveByIndex(index);
        }

        public string ResolveByIndex(int index)
        {
            if (index < 0 || index >= _hashes.Length) return null;

            long offset = _offsets[index];
            int length = _dataAccessor.ReadInt32(offset);
            byte[] bytes = new byte[length];
            _dataAccessor.ReadArray(offset + 4, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        public void Dispose()
        {
            _dataAccessor?.Dispose();
            _mmf?.Dispose();
        }
    }
}
