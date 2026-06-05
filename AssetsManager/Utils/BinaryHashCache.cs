using System;
using System.Buffers;
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
        private MemoryMappedViewAccessor _hashAccessor;
        private MemoryMappedViewAccessor _offsetAccessor;
        private MemoryMappedViewAccessor _dataAccessor;
        private int _count;
        private bool _isLoaded;

        public BinaryHashCache(string txtPath, LogService logService)
        {
            _txtPath = txtPath;
            _binPath = Path.ChangeExtension(txtPath, ".bin");
            _logService = logService;
        }

        public string BinPath => _binPath;
        public int Count => _count;

        public void Load()
        {
            if (_isLoaded) return;

            try
            {
                if (!File.Exists(_binPath) || File.GetLastWriteTime(_binPath) < File.GetLastWriteTime(_txtPath))
                {
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

        private struct HashFileEntry
        {
            public ulong Hash;
            public long Offset;
            public int Length;
        }

        private static ulong ParseHex(ReadOnlySpan<byte> span)
        {
            ulong result = 0;
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                int val = b switch
                {
                    >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                    >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                    >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                    _ => -1
                };
                if (val == -1) break;
                result = (result << 4) | (uint)val;
            }
            return result;
        }

        private void GenerateCache()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // Usamos una capacidad inicial razonable para hashes.game.txt (~1.8M entradas)
            var entries = new List<HashFileEntry>(2000000); 

            using (var fs = new FileStream(_txtPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024))
            {
                byte[] buffer = new byte[512 * 1024];
                int bytesRead;
                long bufferStartPos = 0;
                int leftover = 0;

                while ((bytesRead = fs.Read(buffer, leftover, buffer.Length - leftover)) > 0 || leftover > 0)
                {
                    int totalInBuffer = bytesRead + leftover;
                    int scanStart = 0;
                    int lineEnd;

                    // Buscar saltos de línea (\n)
                    while ((lineEnd = Array.IndexOf(buffer, (byte)'\n', scanStart, totalInBuffer - scanStart)) != -1)
                    {
                        int lineStart = scanStart;
                        int currentLineEnd = lineEnd;
                        
                        // Quitar \r si existe (compatibilidad Windows/Linux)
                        if (currentLineEnd > lineStart && buffer[currentLineEnd - 1] == (byte)'\r')
                        {
                            currentLineEnd--;
                        }

                        ProcessLine(buffer, lineStart, currentLineEnd, bufferStartPos, entries);
                        scanStart = lineEnd + 1;
                    }

                    // Mover el residuo (línea incompleta) al principio para la siguiente lectura
                    leftover = totalInBuffer - scanStart;
                    if (leftover > 0)
                    {
                        Array.Copy(buffer, scanStart, buffer, 0, leftover);
                    }
                    
                    bufferStartPos += scanStart;

                    // Si hemos llegado al final del archivo y queda algo, es la última línea sin \n
                    if (bytesRead == 0 && leftover > 0)
                    {
                        ProcessLine(buffer, 0, leftover, bufferStartPos, entries);
                        leftover = 0;
                    }
                    
                    if (bytesRead == 0) break;
                }
            }

            if (entries.Count == 0) return;

            // Ordenar la lista compacta (ocupa solo ~40MB para 2M de entradas)
            // Esto es mucho más eficiente que ordenar objetos string.
            entries.Sort((a, b) => a.Hash.CompareTo(b.Hash));

            // Escritura del binario por streaming (Segundo pase)
            // Optimizamos usando MMF también para el origen para evitar latencia de SEEKING
            using (var mmfSource = MemoryMappedFile.CreateFromFile(_txtPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
            using (var accessorSource = mmfSource.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            using (var fsDest = new FileStream(_binPath, FileMode.Create, FileAccess.Write, FileShare.None, 512 * 1024))
            using (var writer = new BinaryWriter(fsDest))
            {
                int count = entries.Count;
                writer.Write(count);

                // 1. Escribir todos los Hashes primero
                for (int i = 0; i < count; i++) writer.Write(entries[i].Hash);

                // 2. Calcular y escribir offsets de los strings
                int currentDataOffset = 0;
                for (int i = 0; i < count; i++)
                {
                    writer.Write(currentDataOffset);
                    currentDataOffset += 4 + entries[i].Length;
                }

                // 3. Escribir los strings reales mapeando el origen
                byte[] pathBuffer = new byte[8192];
                for (int i = 0; i < count; i++)
                {
                    accessorSource.ReadArray(entries[i].Offset, pathBuffer, 0, entries[i].Length);
                    
                    writer.Write(entries[i].Length);
                    writer.Write(pathBuffer, 0, entries[i].Length);
                }
                
                writer.Flush();
                fsDest.Flush(true);
            }

            entries.Clear();
            entries = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private void ProcessLine(byte[] buffer, int start, int end, long bufferGlobalPos, List<HashFileEntry> entries)
        {
            int len = end - start;
            if (len <= 0) return;

            // Buscamos el espacio que separa el Hash de la Ruta
            int spaceIdx = -1;
            for (int i = start; i < end; i++)
            {
                if (buffer[i] == (byte)' ')
                {
                    spaceIdx = i - start;
                    break;
                }
            }

            if (spaceIdx > 0)
            {
                // Parseamos el Hash directamente desde el span de bytes (Zero-Allocation)
                ReadOnlySpan<byte> hashSpan = new ReadOnlySpan<byte>(buffer, start, spaceIdx);
                ulong hash = ParseHex(hashSpan);
                
                // Guardamos el offset físico del path dentro del archivo original
                long pathStart = bufferGlobalPos + start + spaceIdx + 1;
                int pathLen = len - spaceIdx - 1;

                if (pathLen > 0)
                {
                    entries.Add(new HashFileEntry { Hash = hash, Offset = pathStart, Length = pathLen });
                }
            }
        }

        private void InitializeFromCache()
        {
            _mmf = MemoryMappedFile.CreateFromFile(_binPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            
            // Leemos el contador inicial
            using (var accessor = _mmf.CreateViewAccessor(0, 4, MemoryMappedFileAccess.Read))
            {
                _count = accessor.ReadInt32(0);
            }

            // Creamos los accessors para las tres secciones del archivo
            _hashAccessor = _mmf.CreateViewAccessor(4, _count * 8, MemoryMappedFileAccess.Read);
            _offsetAccessor = _mmf.CreateViewAccessor(4 + (_count * 8), _count * 4, MemoryMappedFileAccess.Read);
            
            long dataStartOffset = 4 + (_count * 8) + (_count * 4);
            _dataAccessor = _mmf.CreateViewAccessor(dataStartOffset, 0, MemoryMappedFileAccess.Read);
        }

        public string Resolve(ulong hash)
        {
            if (!_isLoaded || _hashAccessor == null) return null;

            // Búsqueda binaria directamente sobre el MemoryMappedViewAccessor (Zero-RAM)
            int low = 0;
            int high = _count - 1;

            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                ulong midHash = _hashAccessor.ReadUInt64(mid * 8);

                if (midHash == hash)
                {
                    return ResolveByIndex(mid);
                }
                
                if (midHash < hash)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return null;
        }

        public string ResolveByIndex(int index)
        {
            if (index < 0 || index >= _count) return null;

            int offset = _offsetAccessor.ReadInt32(index * 4);
            int length = _dataAccessor.ReadInt32(offset);
            
            byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                _dataAccessor.ReadArray(offset + 4, bytes, 0, length);
                return Encoding.UTF8.GetString(bytes, 0, length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        public ulong GetHash(int index)
        {
            if (index < 0 || index >= _count) return 0;
            return _hashAccessor.ReadUInt64(index * 8);
        }

        public void Dispose()
        {
            _hashAccessor?.Dispose();
            _offsetAccessor?.Dispose();
            _dataAccessor?.Dispose();
            _mmf?.Dispose();
        }
    }
}
