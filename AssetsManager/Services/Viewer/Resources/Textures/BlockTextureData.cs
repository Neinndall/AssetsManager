using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;

namespace AssetsManager.Services.Viewer.Resources.Textures;

/// <summary>Authored BC1/BC3 TEX levels retained without decoding pixels.</summary>
internal sealed class BlockTextureData
{
    internal sealed record Level(int Width, int Height, byte[] Data);
    internal bool IsBc3 { get; }
    internal IReadOnlyList<Level> Levels { get; }

    private BlockTextureData(bool isBc3, IReadOnlyList<Level> levels)
    {
        IsBc3 = isBc3;
        Levels = levels;
    }

    /// <summary>Reads supported 2D TEX blocks, preserving the caller's stream position.</summary>
    internal static BlockTextureData TryReadTex(Stream stream, int minWidth)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minWidth);
        if (!stream.CanSeek || !stream.CanRead) return null;
        long start = stream.Position;
        try
        {
            if (stream.Length - start < 12) return null;
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x00584554) return null;
            int width = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            byte format = header[9];
            if (width == 0 || height == 0 || header[8] > 1 || header[10] != 0 || format is not (10 or 11 or 12))
                return null;
            bool bc3 = format == 12;
            int count = (header[11] & 1) != 0 ? 1 + (int)Math.Log2(Math.Max(width, height)) : 1;
            int first = 0;
            for (int level = count - 1; level >= 0; level--)
                if (Math.Max(width >> level, 1) >= minWidth) { first = level; break; }

            var levels = new Level[count - first];
            long offset = start + 12;
            for (int level = count - 1; level >= 0; level--)
            {
                int w = Math.Max(width >> level, 1), h = Math.Max(height >> level, 1);
                int size = checked(((w + 3) / 4) * ((h + 3) / 4) * (bc3 ? 16 : 8));
                if (offset + size > stream.Length) throw new EndOfStreamException("TEX block mip data is truncated.");
                if (level >= first)
                {
                    stream.Position = offset;
                    byte[] data = new byte[size];
                    stream.ReadExactly(data);
                    levels[level - first] = new Level(w, h, data);
                }
                offset += size;
            }
            // The existing decoder reads these formats backwards from EOF. Keep unusual containers on that path.
            if (offset != stream.Length) return null;
            return new BlockTextureData(bc3, levels);
        }
        finally { stream.Position = start; }
    }

    internal IReadOnlyList<BitmapSource> Decode()
    {
        // Rebuild only the retained levels for the hardware fallback, never the discarded large mips.
        using var stream = new MemoryStream(12 + Levels.Sum(level => level.Data.Length));
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x00584554u);
            writer.Write((ushort)Levels[0].Width);
            writer.Write((ushort)Levels[0].Height);
            writer.Write((byte)1);
            writer.Write((byte)(IsBc3 ? 12 : 10));
            writer.Write((byte)0);
            writer.Write((byte)(Levels.Count > 1 ? 1 : 0));
            for (int level = Levels.Count - 1; level >= 0; level--) writer.Write(Levels[level].Data);
        }
        stream.Position = 0;
        return TextureUtils.LoadViewerTextureMipChain(stream, ".tex", Levels[0].Width);
    }
}
