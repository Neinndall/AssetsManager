using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using AssetsManager.Views.Models.Monitor;
using ZstdSharp;

namespace AssetsManager.Services.Monitor;

public sealed class RmanService
{
    public const int MaxManifestFileSize = 256 * 1024 * 1024;     // 256 MB max file on disk
    public const int MaxCompressedBodySize = 256 * 1024 * 1024;   // 256 MB max compressed payload
    public const int MaxUncompressedBodySize = 512 * 1024 * 1024; // 512 MB max uncompressed body

    private const int HeaderSize = 28;
    private const int SignatureSize = 256;
    private const byte SupportedMajorVersion = 2;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public RmanManifest Parse(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("RMAN manifest file was not found.", filePath);

        if (fileInfo.Length > MaxManifestFileSize)
            throw new InvalidDataException($"RMAN manifest file size ({fileInfo.Length} bytes) exceeds maximum permitted limit ({MaxManifestFileSize} bytes).");

        byte[] rawData = File.ReadAllBytes(filePath);

        cancellationToken.ThrowIfCancellationRequested();
        return Parse(rawData, cancellationToken);
    }

    public RmanManifest Parse(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        if (data.Length < HeaderSize)
            throw new InvalidDataException($"Invalid RMAN header: expected at least {HeaderSize} bytes, got {data.Length}.");

        ReadOnlySpan<byte> header = data.AsSpan(0, HeaderSize);
        if (!header[..4].SequenceEqual("RMAN"u8))
            throw new InvalidDataException("Invalid RMAN signature.");
        if (header[4] != SupportedMajorVersion)
            throw new InvalidDataException($"Unsupported RMAN major version: {header[4]}.");
        if (header[6] != 0)
            throw new InvalidDataException($"Unsupported RMAN compression marker: {header[6]}.");

        byte signatureType = header[7];
        uint contentOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        uint compressedSizeValue = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
        ulong manifestId = BinaryPrimitives.ReadUInt64LittleEndian(header[16..24]);
        uint uncompressedSizeValue = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);

        int contentOffset = GetPositiveInt32(contentOffsetValue, "content offset");
        int compressedSize = GetBoundedInt32(compressedSizeValue, MaxCompressedBodySize, "compressed body size");
        int uncompressedSize = GetBoundedInt32(uncompressedSizeValue, MaxUncompressedBodySize, "uncompressed body size");
        if (contentOffset < HeaderSize)
            throw new InvalidDataException($"Invalid RMAN content offset: {contentOffset}.");

        long contentEnd = (long)contentOffset + compressedSize;
        if (contentEnd > data.Length)
            throw new InvalidDataException("RMAN compressed body extends beyond the supplied data.");
        if (signatureType != 0 && data.Length - contentEnd < SignatureSize)
            throw new InvalidDataException("RMAN declares a signature but its signature payload is truncated.");

        byte[] uncompressedBody = ArrayPool<byte>.Shared.Rent(uncompressedSize);
        try
        {
            int decompressedBytes;
            try
            {
                using var decompressor = new Decompressor();
                decompressedBytes = decompressor.Unwrap(
                    data.AsSpan(contentOffset, compressedSize),
                    uncompressedBody.AsSpan(0, uncompressedSize));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException("RMAN body could not be decompressed.", ex);
            }

            if (decompressedBytes != uncompressedSize)
                throw new InvalidDataException($"RMAN body size mismatch: expected {uncompressedSize}, got {decompressedBytes}.");

            cancellationToken.ThrowIfCancellationRequested();
            return new RmanParser(uncompressedBody, uncompressedSize, manifestId, cancellationToken).Parse();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(uncompressedBody);
        }
    }

    private static int GetBoundedInt32(uint value, int maxValue, string fieldName)
    {
        if (value == 0 || value > (uint)maxValue)
            throw new InvalidDataException($"Invalid RMAN {fieldName}: {value} (max allowed: {maxValue}).");
        return (int)value;
    }

    private static int GetPositiveInt32(uint value, string fieldName)
    {
        if (value == 0 || value > int.MaxValue)
            throw new InvalidDataException($"Invalid RMAN {fieldName}: {value}.");
        return (int)value;
    }

    private sealed class RmanParser
    {
        private readonly byte[] _data;
        private readonly int _dataLength;
        private readonly ulong _manifestId;
        private readonly CancellationToken _cancellationToken;

        public RmanParser(byte[] data, int dataLength, ulong manifestId, CancellationToken cancellationToken)
        {
            _data = data;
            _dataLength = dataLength;
            _manifestId = manifestId;
            _cancellationToken = cancellationToken;
        }

        public RmanManifest Parse()
        {
            uint rootRelativeOffset = ReadUInt32(0, "root offset");
            int rootOffset = GetAbsoluteOffset(0, rootRelativeOffset, "root object");
            FlatBufferObject root = GetObject(rootOffset, "root object");

            VectorLayout bundles = GetVector(GetFieldOffset(root, 0), sizeof(uint), "bundles");
            VectorLayout languages = GetVector(GetFieldOffset(root, 1), sizeof(uint), "languages");
            VectorLayout files = GetVector(GetFieldOffset(root, 2), sizeof(uint), "files");
            VectorLayout directories = GetVector(GetFieldOffset(root, 3), sizeof(uint), "directories");
            VectorLayout parameters = GetVector(GetFieldOffset(root, 5), sizeof(uint), "chunking parameters");

            var manifest = new RmanManifest
            {
                ManifestId = _manifestId,
                Bundles = new List<RmanBundle>(bundles.Count),
                Languages = new List<RmanLanguage>(languages.Count),
                Files = new List<RmanFile>(files.Count),
                Directories = new List<RmanDirectory>(directories.Count)
            };

            ParseBundles(manifest, bundles);
            manifest.BuildChunkLookup();
            ParseLanguages(manifest, languages);
            ParseDirectories(manifest, directories);
            HashType[] hashTypes = ParseHashTypes(parameters);
            ParseFiles(manifest, files, hashTypes);
            ResolveFullPaths(manifest);
            return manifest;
        }

        private void ParseBundles(RmanManifest manifest, VectorLayout bundles)
        {
            for (int bundleIndex = 0; bundleIndex < bundles.Count; bundleIndex++)
            {
                CheckCancellation(bundleIndex);
                FlatBufferObject bundleObject = GetVectorObject(bundles, bundleIndex, "bundle");
                ulong bundleId = GetUInt64(GetFieldOffset(bundleObject, 0), "bundle id");
                VectorLayout chunks = GetVector(GetFieldOffset(bundleObject, 1), sizeof(uint), "bundle chunks");
                var bundle = new RmanBundle
                {
                    BundleId = bundleId,
                    Chunks = new List<RmanChunk>(chunks.Count)
                };

                uint bundleOffset = 0;
                for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
                {
                    CheckCancellation(chunkIndex);
                    FlatBufferObject chunkObject = GetVectorObject(chunks, chunkIndex, "bundle chunk");
                    ulong chunkId = GetUInt64(GetFieldOffset(chunkObject, 0), "chunk id");
                    uint compressedSize = GetUInt32(GetFieldOffset(chunkObject, 1), "compressed chunk size");
                    uint uncompressedSize = GetUInt32(GetFieldOffset(chunkObject, 2), "uncompressed chunk size");
                    if (chunkId == 0 || compressedSize == 0 || uncompressedSize == 0)
                        throw new InvalidDataException($"Bundle {bundleId:X16} contains an invalid chunk descriptor.");

                    bundle.Chunks.Add(new RmanChunk
                    {
                        ChunkId = chunkId,
                        CompressedSize = compressedSize,
                        UncompressedSize = uncompressedSize,
                        BundleId = bundleId,
                        BundleOffset = bundleOffset
                    });

                    try
                    {
                        bundleOffset = checked(bundleOffset + compressedSize);
                    }
                    catch (OverflowException ex)
                    {
                        throw new InvalidDataException($"Bundle {bundleId:X16} exceeds the supported 32-bit offset range.", ex);
                    }
                }

                manifest.Bundles.Add(bundle);
            }
        }

        private void ParseLanguages(RmanManifest manifest, VectorLayout languages)
        {
            for (int index = 0; index < languages.Count; index++)
            {
                CheckCancellation(index);
                FlatBufferObject languageObject = GetVectorObject(languages, index, "language");
                manifest.Languages.Add(new RmanLanguage
                {
                    LanguageId = GetByte(GetFieldOffset(languageObject, 0)),
                    Name = GetString(GetFieldOffset(languageObject, 1), "language name")
                });
            }
        }

        private void ParseDirectories(RmanManifest manifest, VectorLayout directories)
        {
            for (int index = 0; index < directories.Count; index++)
            {
                CheckCancellation(index);
                FlatBufferObject directoryObject = GetVectorObject(directories, index, "directory");
                manifest.Directories.Add(new RmanDirectory
                {
                    DirectoryId = GetUInt64(GetFieldOffset(directoryObject, 0), "directory id"),
                    ParentId = GetUInt64(GetFieldOffset(directoryObject, 1), "directory parent id"),
                    Name = GetString(GetFieldOffset(directoryObject, 2), "directory name")
                });
            }
        }

        private HashType[] ParseHashTypes(VectorLayout parameters)
        {
            var hashTypes = new HashType[parameters.Count];
            for (int index = 0; index < parameters.Count; index++)
            {
                FlatBufferObject parameterObject = GetVectorObject(parameters, index, "chunking parameter");
                HashType hashType = (HashType)GetByte(GetFieldOffset(parameterObject, 1));
                if (!Enum.IsDefined(hashType))
                    throw new InvalidDataException($"RMAN declares unsupported hash type {(byte)hashType}.");
                hashTypes[index] = hashType;
            }
            return hashTypes;
        }

        private void ParseFiles(RmanManifest manifest, VectorLayout files, HashType[] hashTypes)
        {
            for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                CheckCancellation(fileIndex);
                FlatBufferObject fileObject = GetVectorObject(files, fileIndex, "file");
                byte parameterIndex = GetByte(GetFieldOffset(fileObject, 11));
                if (hashTypes.Length > 0 && parameterIndex >= hashTypes.Length)
                    throw new InvalidDataException($"RMAN file references missing chunking parameter {parameterIndex}.");

                ulong languageMask = GetUInt64(GetFieldOffset(fileObject, 4), "file language flags");
                VectorLayout chunkIds = GetVector(GetFieldOffset(fileObject, 7), sizeof(ulong), "file chunk ids");
                var file = new RmanFile
                {
                    FileId = GetUInt64(GetFieldOffset(fileObject, 0), "file id"),
                    DirectoryId = GetUInt64(GetFieldOffset(fileObject, 1), "file directory id"),
                    FileSize = GetUInt64(GetFieldOffset(fileObject, 2), "file size"),
                    Name = GetString(GetFieldOffset(fileObject, 3), "file name"),
                    HashType = hashTypes.Length == 0 ? HashType.Sha256 : hashTypes[parameterIndex],
                    LanguageIds = new List<byte>(BitOperations.PopCount(languageMask)),
                    ChunkIds = new List<ulong>(chunkIds.Count)
                };

                if (string.IsNullOrEmpty(file.Name))
                    throw new InvalidDataException($"RMAN file {file.FileId:X16} has an empty name.");

                for (int bit = 0; bit < 64; bit++)
                    if ((languageMask & (1UL << bit)) != 0) file.LanguageIds.Add((byte)(bit + 1));

                ulong calculatedSize = 0;
                for (int chunkIndex = 0; chunkIndex < chunkIds.Count; chunkIndex++)
                {
                    CheckCancellation(chunkIndex);
                    ulong chunkId = GetVectorUInt64(chunkIds, chunkIndex, "file chunk id");
                    RmanChunk chunk = manifest.GetChunk(chunkId)
                        ?? throw new InvalidDataException($"RMAN file '{file.Name}' references missing chunk {chunkId:X16}.");
                    file.ChunkIds.Add(chunkId);
                    try
                    {
                        calculatedSize = checked(calculatedSize + chunk.UncompressedSize);
                    }
                    catch (OverflowException ex)
                    {
                        throw new InvalidDataException($"RMAN file '{file.Name}' exceeds the supported size range.", ex);
                    }
                }

                if (calculatedSize != file.FileSize)
                    throw new InvalidDataException($"RMAN file '{file.Name}' size mismatch: expected {file.FileSize}, chunks describe {calculatedSize}.");

                manifest.Files.Add(file);
            }
        }

        private void ResolveFullPaths(RmanManifest manifest)
        {
            var directoryMap = new Dictionary<ulong, RmanDirectory>(manifest.Directories.Count);
            foreach (RmanDirectory directory in manifest.Directories)
            {
                if (directory.DirectoryId == 0) continue;
                if (!directoryMap.TryAdd(directory.DirectoryId, directory))
                    throw new InvalidDataException($"RMAN contains duplicate directory id {directory.DirectoryId:X16}.");
            }

            var resolvedPaths = new Dictionary<ulong, string>(directoryMap.Count);
            var states = new Dictionary<ulong, byte>(directoryMap.Count);
            var chain = new List<RmanDirectory>();
            foreach (ulong directoryId in directoryMap.Keys)
            {
                if (resolvedPaths.ContainsKey(directoryId)) continue;
                chain.Clear();
                ulong currentId = directoryId;
                while (currentId != 0 && !resolvedPaths.ContainsKey(currentId))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (states.TryGetValue(currentId, out byte state) && state == 1)
                        throw new InvalidDataException($"RMAN directory graph contains a cycle at {currentId:X16}.");
                    if (!directoryMap.TryGetValue(currentId, out RmanDirectory directory))
                        throw new InvalidDataException($"RMAN directory references missing parent {currentId:X16}.");

                    states[currentId] = 1;
                    chain.Add(directory);
                    currentId = directory.ParentId;
                }

                string path = currentId == 0 ? string.Empty : resolvedPaths[currentId];
                for (int index = chain.Count - 1; index >= 0; index--)
                {
                    RmanDirectory directory = chain[index];
                    path = string.IsNullOrEmpty(path)
                        ? directory.Name
                        : string.IsNullOrEmpty(directory.Name) ? path : $"{path}/{directory.Name}";
                    resolvedPaths[directory.DirectoryId] = path;
                    states[directory.DirectoryId] = 2;
                }
            }

            foreach (RmanFile file in manifest.Files)
            {
                if (file.DirectoryId == 0) continue;
                if (!resolvedPaths.TryGetValue(file.DirectoryId, out string directoryPath))
                    throw new InvalidDataException($"RMAN file '{file.Name}' references missing directory {file.DirectoryId:X16}.");
                if (!string.IsNullOrEmpty(directoryPath)) file.Name = $"{directoryPath}/{file.Name}";
            }
        }

        private FlatBufferObject GetObject(int offset, string context)
        {
            EnsureRange(offset, sizeof(int), context);
            int vtableDistance = ReadInt32(offset, context);
            if (vtableDistance == 0)
                throw new InvalidDataException($"Invalid {context} vtable distance: {vtableDistance}.");

            long vtableOffsetValue = (long)offset - vtableDistance;
            if (vtableOffsetValue < 0 || vtableOffsetValue > int.MaxValue)
                throw new InvalidDataException($"Invalid {context} vtable position.");
            int vtableOffset = (int)vtableOffsetValue;
            EnsureRange(vtableOffset, 4, context);
            ushort vtableSize = ReadUInt16(vtableOffset, context);
            ushort objectSize = ReadUInt16(vtableOffset + 2, context);
            if (vtableSize < 4 || objectSize < 4)
                throw new InvalidDataException($"Invalid {context} table dimensions.");
            EnsureRange(vtableOffset, vtableSize, context);
            EnsureRange(offset, objectSize, context);
            return new FlatBufferObject(offset, vtableOffset, vtableSize, objectSize);
        }

        private int GetFieldOffset(FlatBufferObject obj, int index)
        {
            int entryOffset = 4 + checked(index * sizeof(ushort));
            if (entryOffset + sizeof(ushort) > obj.VTableSize) return 0;
            ushort fieldOffset = ReadUInt16(obj.VTableOffset + entryOffset, "field offset");
            if (fieldOffset == 0) return 0;
            if (fieldOffset >= obj.ObjectSize)
                throw new InvalidDataException("FlatBuffer field points outside its object.");
            return obj.Offset + fieldOffset;
        }

        private VectorLayout GetVector(int fieldOffset, int elementSize, string context)
        {
            if (fieldOffset == 0) return default;
            uint relativeOffset = ReadUInt32(fieldOffset, context);
            int vectorOffset = GetAbsoluteOffset(fieldOffset, relativeOffset, context);
            uint countValue = ReadUInt32(vectorOffset, context);
            if (countValue > int.MaxValue)
                throw new InvalidDataException($"{context} contains too many elements: {countValue}.");
            int count = (int)countValue;
            int dataOffset = vectorOffset + sizeof(uint);
            long byteLength = (long)count * elementSize;
            if (byteLength > int.MaxValue)
                throw new InvalidDataException($"{context} byte length exceeds the supported range.");
            EnsureRange(dataOffset, (int)byteLength, context);
            return new VectorLayout(dataOffset, count, elementSize);
        }

        private FlatBufferObject GetVectorObject(VectorLayout vector, int index, string context)
        {
            int itemOffset = vector.GetElementOffset(index);
            uint relativeOffset = ReadUInt32(itemOffset, context);
            return GetObject(GetAbsoluteOffset(itemOffset, relativeOffset, context), context);
        }

        private ulong GetVectorUInt64(VectorLayout vector, int index, string context)
            => ReadUInt64(vector.GetElementOffset(index), context);

        private string GetString(int fieldOffset, string context)
        {
            if (fieldOffset == 0) return string.Empty;
            uint relativeOffset = ReadUInt32(fieldOffset, context);
            int stringOffset = GetAbsoluteOffset(fieldOffset, relativeOffset, context);
            uint lengthValue = ReadUInt32(stringOffset, context);
            if (lengthValue > int.MaxValue)
                throw new InvalidDataException($"{context} is too long: {lengthValue} bytes.");
            int length = (int)lengthValue;
            int dataOffset = stringOffset + sizeof(uint);
            EnsureRange(dataOffset, length, context);
            try
            {
                return StrictUtf8.GetString(_data, dataOffset, length);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException($"{context} is not valid UTF-8.", ex);
            }
        }

        private byte GetByte(int offset) => offset == 0 ? (byte)0 : ReadByte(offset, "byte field");
        private uint GetUInt32(int offset, string context) => offset == 0 ? 0 : ReadUInt32(offset, context);
        private ulong GetUInt64(int offset, string context) => offset == 0 ? 0 : ReadUInt64(offset, context);

        private byte ReadByte(int offset, string context)
        {
            EnsureRange(offset, sizeof(byte), context);
            return _data[offset];
        }

        private ushort ReadUInt16(int offset, string context)
        {
            EnsureRange(offset, sizeof(ushort), context);
            return BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset, sizeof(ushort)));
        }

        private int ReadInt32(int offset, string context)
        {
            EnsureRange(offset, sizeof(int), context);
            return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset, sizeof(int)));
        }

        private uint ReadUInt32(int offset, string context)
        {
            EnsureRange(offset, sizeof(uint), context);
            return BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset, sizeof(uint)));
        }

        private ulong ReadUInt64(int offset, string context)
        {
            EnsureRange(offset, sizeof(ulong), context);
            return BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(offset, sizeof(ulong)));
        }

        private int GetAbsoluteOffset(int origin, uint relativeOffset, string context)
        {
            long absoluteOffset = (long)origin + relativeOffset;
            if (relativeOffset == 0 || absoluteOffset < 0 || absoluteOffset > int.MaxValue)
                throw new InvalidDataException($"Invalid {context} offset.");
            return (int)absoluteOffset;
        }

        private void EnsureRange(int offset, int length, string context)
        {
            if (offset < 0 || length < 0 || (long)offset + length > _dataLength)
                throw new InvalidDataException($"{context} extends beyond the RMAN body.");
        }

        private void CheckCancellation(int index)
        {
            if ((index & 0x3FF) == 0) _cancellationToken.ThrowIfCancellationRequested();
        }

        private readonly record struct FlatBufferObject(
            int Offset,
            int VTableOffset,
            ushort VTableSize,
            ushort ObjectSize);

        private readonly record struct VectorLayout(int DataOffset, int Count, int ElementSize)
        {
            public int GetElementOffset(int index)
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                return DataOffset + checked(index * ElementSize);
            }
        }
    }
}
