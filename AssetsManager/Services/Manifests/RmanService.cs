using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsManager.Views.Models.Monitor;
using ZstdSharp;

namespace AssetsManager.Services.Manifests;

public class RmanService
{
    private byte[] _data = Array.Empty<byte>();

    public RmanManifest Parse(string filePath)
    {
        byte[] rawData = File.ReadAllBytes(filePath);
        return Parse(rawData);
    }

    public RmanManifest Parse(byte[] data)
    {
        if (Encoding.ASCII.GetString(data, 0, 4) != "RMAN")
            throw new Exception("Invalid RMAN file: Missing magic bytes.");

        uint headerSize = BitConverter.ToUInt32(data, 8);
        uint compressedSize = BitConverter.ToUInt32(data, 12);
        ulong manifestId = BitConverter.ToUInt64(data, 16);
        uint uncompressedSize = BitConverter.ToUInt32(data, 24);

        byte[] compressedBody = new byte[compressedSize];
        Array.Copy(data, headerSize, compressedBody, 0, compressedSize);

        using (var decompressor = new Decompressor())
        {
            byte[] uncompressedBody = decompressor.Unwrap(compressedBody).ToArray();

            if (uncompressedBody.Length != uncompressedSize)
                throw new Exception("Decompression failed: size mismatch.");

            return ParseBody(uncompressedBody, manifestId);
        }
    }

    private RmanManifest ParseBody(byte[] body, ulong manifestId)
    {
        _data = body;
        int rootOffset = BitConverter.ToInt32(_data, 0);
        var root = GetObject(rootOffset);
        var manifest = new RmanManifest { ManifestId = manifestId };

        // 1. Bundles & Chunks
        var bundleOffsets = GetVector(GetFieldOffset(root, 0));
        foreach (var bundleOffset in bundleOffsets)
        {
            var bundleObj = GetObject(bundleOffset);
            var bundle = new RmanBundle
            {
                BundleId = GetUInt64(GetFieldOffset(bundleObj, 0))
            };

            var chunkOffsets = GetVector(GetFieldOffset(bundleObj, 1));
            uint currentBundleOffset = 0;
            foreach (var chunkOffset in chunkOffsets)
            {
                var chunkObj = GetObject(chunkOffset);
                var chunk = new RmanChunk
                {
                    ChunkId = GetUInt64(GetFieldOffset(chunkObj, 0)),
                    CompressedSize = GetUInt32(GetFieldOffset(chunkObj, 1)),
                    UncompressedSize = GetUInt32(GetFieldOffset(chunkObj, 2)),
                    BundleId = bundle.BundleId,
                    BundleOffset = currentBundleOffset
                };
                bundle.Chunks.Add(chunk);
                currentBundleOffset += chunk.CompressedSize;
            }
            manifest.Bundles.Add(bundle);
        }

        // 2. Languages
        var langOffsets = GetVector(GetFieldOffset(root, 1));
        foreach (var langOffset in langOffsets)
        {
            var langObj = GetObject(langOffset);
            manifest.Languages.Add(new RmanLanguage
            {
                LanguageId = GetByte(GetFieldOffset(langObj, 0)),
                Name = GetString(GetFieldOffset(langObj, 1))
            });
        }

        // 3. Directories
        var dirOffsets = GetVector(GetFieldOffset(root, 3));
        foreach (var dirOffset in dirOffsets)
        {
            var dirObj = GetObject(dirOffset);
            manifest.Directories.Add(new RmanDirectory
            {
                DirectoryId = GetUInt64(GetFieldOffset(dirObj, 0)),
                ParentId = GetUInt64(GetFieldOffset(dirObj, 1)),
                Name = GetString(GetFieldOffset(dirObj, 2))
            });
        }

        // 4. Files
        var fileOffsets = GetVector(GetFieldOffset(root, 2));
        var paramOffsets = GetVector(GetFieldOffset(root, 5));
        var hashTypes = paramOffsets.Select(p => (HashType)GetByte(GetFieldOffset(GetObject(p), 1))).ToList();

        foreach (var fileOffset in fileOffsets)
        {
            var fileObj = GetObject(fileOffset);
            var file = new RmanFile
            {
                FileId = GetUInt64(GetFieldOffset(fileObj, 0)),
                DirectoryId = GetUInt64(GetFieldOffset(fileObj, 1)),
                FileSize = GetUInt64(GetFieldOffset(fileObj, 2)),
                Name = GetString(GetFieldOffset(fileObj, 3)),
            };

            ulong langMask = GetUInt64(GetFieldOffset(fileObj, 4));
            if (langMask > 0)
            {
                for (int i = 0; i < 64; i++)
                {
                    if ((langMask & (1UL << i)) != 0) file.LanguageIds.Add((byte)(i + 1));
                }
            }

            byte paramIndex = GetByte(GetFieldOffset(fileObj, 11));
            file.HashType = paramIndex < hashTypes.Count ? hashTypes[paramIndex] : HashType.Sha256;

            var chunkIdVectorOffset = GetFieldOffset(fileObj, 7);
            if (chunkIdVectorOffset != 0) file.ChunkIds.AddRange(GetVectorULong(chunkIdVectorOffset));

            manifest.Files.Add(file);
        }

        ResolveFullPaths(manifest);
        return manifest;
    }

    private void ResolveFullPaths(RmanManifest manifest)
    {
        var dirMap = new Dictionary<ulong, RmanDirectory>();
        foreach (var dir in manifest.Directories)
        {
            if (dir.DirectoryId != 0) dirMap.TryAdd(dir.DirectoryId, dir);
        }

        foreach (var file in manifest.Files)
        {
            var pathParts = new List<string> { file.Name };
            ulong currentDirId = file.DirectoryId;

            while (currentDirId != 0 && dirMap.TryGetValue(currentDirId, out var dir))
            {
                if (!string.IsNullOrEmpty(dir.Name)) pathParts.Insert(0, dir.Name);
                currentDirId = dir.ParentId;
            }

            file.Name = string.Join("/", pathParts);
        }
    }

    #region FlatBuffer Helpers
    private struct FBObject { public int Offset; public int VTableOffset; }

    private FBObject GetObject(int offset)
    {
        if (offset < 0 || offset + 4 > _data.Length) return new FBObject { Offset = -1 };
        int vtableOffset = offset - BitConverter.ToInt32(_data, offset);
        if (vtableOffset < 0 || vtableOffset + 2 > _data.Length) return new FBObject { Offset = -1 };
        return new FBObject { Offset = offset, VTableOffset = vtableOffset };
    }

    private int GetFieldOffset(FBObject obj, int index)
    {
        if (obj.Offset == -1 || obj.VTableOffset < 0 || obj.VTableOffset + 2 > _data.Length) return 0;
        ushort vtableSize = BitConverter.ToUInt16(_data, obj.VTableOffset);
        int fieldOffsetInVTable = 4 + (index * 2);
        if (fieldOffsetInVTable + 2 > vtableSize || obj.VTableOffset + fieldOffsetInVTable + 2 > _data.Length) return 0;
        ushort offsetInObject = BitConverter.ToUInt16(_data, obj.VTableOffset + fieldOffsetInVTable);
        if (offsetInObject == 0) return 0;
        int finalOffset = obj.Offset + offsetInObject;
        return (finalOffset < 0 || finalOffset >= _data.Length) ? 0 : finalOffset;
    }

    private uint GetUInt32(int offset) => (offset <= 0 || offset + 4 > _data.Length) ? (uint)0 : BitConverter.ToUInt32(_data, offset);
    private ulong GetUInt64(int offset) => (offset <= 0 || offset + 8 > _data.Length) ? (ulong)0 : BitConverter.ToUInt64(_data, offset);
    private byte GetByte(int offset) => (offset <= 0 || offset >= _data.Length) ? (byte)0 : _data[offset];

    private string GetString(int offset)
    {
        if (offset <= 0 || offset + 4 > _data.Length) return string.Empty;
        int stringOffset = offset + BitConverter.ToInt32(_data, offset);
        if (stringOffset < 0 || stringOffset + 4 > _data.Length) return string.Empty;
        uint length = BitConverter.ToUInt32(_data, stringOffset);
        if (stringOffset + 4 + length > _data.Length) return string.Empty;
        return Encoding.UTF8.GetString(_data, stringOffset + 4, (int)length);
    }

    private List<int> GetVector(int offset)
    {
        var result = new List<int>();
        if (offset <= 0 || offset + 4 > _data.Length) return result;
        int vectorOffset = offset + BitConverter.ToInt32(_data, offset);
        if (vectorOffset < 0 || vectorOffset + 4 > _data.Length) return result;
        uint length = BitConverter.ToUInt32(_data, vectorOffset);
        if (length > 1000000) length = 1000000;
        for (int i = 0; i < (int)length; i++)
        {
            int itemPos = vectorOffset + 4 + (i * 4);
            if (itemPos + 4 <= _data.Length) result.Add(itemPos + BitConverter.ToInt32(_data, itemPos));
            else break;
        }
        return result;
    }

    private List<ulong> GetVectorULong(int offset)
    {
        var result = new List<ulong>();
        if (offset <= 0 || offset + 4 > _data.Length) return result;
        int vectorOffset = offset + BitConverter.ToInt32(_data, offset);
        if (vectorOffset < 0 || vectorOffset + 4 > _data.Length) return result;
        uint length = BitConverter.ToUInt32(_data, vectorOffset);
        if (length > 1000000) length = 1000000;
        for (int i = 0; i < (int)length; i++)
        {
            int itemOffset = vectorOffset + 4 + (i * 8);
            if (itemOffset >= 0 && itemOffset + 8 <= _data.Length) result.Add(BitConverter.ToUInt64(_data, itemOffset));
            else break;
        }
        return result;
    }
    #endregion
}
