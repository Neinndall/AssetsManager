using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetRipper.TextureDecoder.Etc;
using AssetRipper.TextureDecoder.Rgb.Formats;
using BCnEncoder.Shared;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Toolkit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Utils
{
    public static class TextureUtils
    {
        public static BitmapSource ResolveTexture(Dictionary<string, BitmapSource> allTextures, string selectedTextureName)
        {
            if (allTextures == null || string.IsNullOrEmpty(selectedTextureName))
                return null;

            allTextures.TryGetValue(selectedTextureName, out BitmapSource texture);
            return texture;
        }

        public static BitmapSource LoadTexture(byte[] data, string extension, int? maxWidth = null, int? maxHeight = null)
        {
            if (data == null || data.Length == 0) return null;
            using (var ms = new MemoryStream(data))
            {
                return LoadTexture(ms, extension, maxWidth, maxHeight);
            }
        }

        public static BitmapSource LoadViewerTexture(Stream textureStream, string extension, int? maxWidth = null, int? maxHeight = null)
        {
            return LoadTextureCore(textureStream, extension, maxWidth, maxHeight, null, null);
        }

        public static BitmapSource LoadViewerTexture(Stream textureStream, string extension, LogService logService, string source)
        {
            return LoadTextureCore(textureStream, extension, null, null, logService, source);
        }

        /// <summary>
        /// Decodes the texture's authored mip chain from the smallest level at least
        /// <paramref name="minWidth"/> pixels wide down to the file's last level.
        /// This mirrors the current LTK MAIN map viewport path and preserves alpha-test coverage.
        /// </summary>
        public static IReadOnlyList<BitmapSource> LoadViewerTextureMipChain(
            Stream textureStream,
            string extension,
            int minWidth)
        {
            if (textureStream == null)
                return Array.Empty<BitmapSource>();
            if (minWidth <= 0)
                throw new ArgumentOutOfRangeException(nameof(minWidth));

            try
            {
                if (extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
                    return LoadTexMipChain(textureStream, minWidth);

                if (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    Texture texture = Texture.LoadDds(textureStream);
                    return ConvertTextureMipChain(texture, minWidth);
                }

                BitmapSource image = LoadTextureCore(
                    textureStream,
                    extension,
                    minWidth,
                    minWidth,
                    null,
                    null);
                return image == null ? Array.Empty<BitmapSource>() : new[] { image };
            }
            catch
            {
                return Array.Empty<BitmapSource>();
            }
        }

        public static BitmapSource LoadTexture(Stream textureStream, string extension, int? maxWidth = null, int? maxHeight = null)
        {
            return LoadTextureCore(textureStream, extension, maxWidth, maxHeight, null, null);
        }

        private static BitmapSource LoadTextureCore(
            Stream textureStream,
            string extension,
            int? maxWidth,
            int? maxHeight,
            LogService logService,
            string source)
        {
            try
            {
                if (textureStream == null) { return null; }

                if (extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
                {
                    return LoadTexBitmapSource(textureStream, maxWidth, maxHeight);
                }
                else if (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    Texture texture = Texture.LoadDds(textureStream);
                    if (texture.Mips.Length > 0)
                    {
                        using Image<Rgba32> image = texture.Mips[0].ToImage();
                        return ConvertImageToBitmapSource(image, maxWidth, maxHeight);
                    }
                    return null;
                }
                else if (extension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    using Image<Rgba32> image = Image.Load<Rgba32>(textureStream);
                    return ConvertImageToBitmapSource(image, maxWidth, maxHeight);
                }
                else
                {
                    BitmapImage bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = textureStream;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    if (maxWidth.HasValue)
                    {
                        bitmapImage.DecodePixelWidth = maxWidth.Value;
                    }
                    if (maxHeight.HasValue)
                    {
                        bitmapImage.DecodePixelHeight = maxHeight.Value;
                    }
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                logService?.LogError(ex, $"Failed to decode viewer texture: {source ?? extension ?? "unknown source"}");
                return null;
            }
        }

        private static BitmapSource LoadTexBitmapSource(Stream textureStream, int? maxWidth, int? maxHeight)
        {
            MemoryStream copy = null;
            Stream stream = textureStream;
            if (!stream.CanSeek)
            {
                copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                stream = copy;
            }

            try
            {
                long start = stream.Position;
                TexHeader header = ReadTexHeader(stream);
                stream.Position = start;

                if (RequiresCompatibleTexDecode(header.Format))
                    return DecodeCompatibleTex(stream, start, header, maxWidth, maxHeight);

                Texture texture = maxWidth.HasValue && maxHeight.HasValue
                    ? Texture.LoadTex(stream, maxWidth.Value, maxHeight.Value)
                    : Texture.LoadTex(stream);
                return texture.Mips.Length > 0 ? ConvertTextureMipToBitmapSource(texture) : null;
            }
            finally
            {
                copy?.Dispose();
            }
        }

        private static bool RequiresCompatibleTexDecode(byte format) =>
            format is 1 or 2 or 3 or 14 or 21 or 22;

        private static TexHeader ReadTexHeader(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            uint magic = reader.ReadUInt32();
            if (magic != 0x00584554)
                throw new InvalidDataException($"Invalid TEX magic: {magic:x8}");

            ushort width = reader.ReadUInt16();
            ushort height = reader.ReadUInt16();
            byte depth = reader.ReadByte();
            byte format = reader.ReadByte();
            byte resourceType = reader.ReadByte();
            byte flags = reader.ReadByte();
            return new TexHeader(width, height, depth, format, resourceType, flags);
        }

        private static BitmapSource DecodeCompatibleTex(
            Stream stream,
            long start,
            TexHeader header,
            int? maxWidth,
            int? maxHeight)
        {
            int mipCount = GetTexMipCount(header);
            int level = 0;
            if (maxWidth.HasValue && maxHeight.HasValue)
            {
                for (int i = 0; i < mipCount; i++)
                {
                    int width = Math.Max(header.Width >> i, 1);
                    int height = Math.Max(header.Height >> i, 1);
                    if ((width <= maxWidth.Value && height <= maxHeight.Value) || i == mipCount - 1)
                    {
                        level = i;
                        break;
                    }
                }
            }

            return DecodeCompatibleTexLevel(stream, start, header, mipCount, level);
        }

        private static IReadOnlyList<BitmapSource> LoadTexMipChain(Stream textureStream, int minWidth)
        {
            MemoryStream copy = null;
            Stream stream = textureStream;
            if (!stream.CanSeek)
            {
                copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                stream = copy;
            }

            try
            {
                long start = stream.Position;
                TexHeader header = ReadTexHeader(stream);
                int mipCount = GetTexMipCount(header);
                int firstLevel = SelectFirstMipLevel(header.Width, mipCount, minWidth);
                var levels = new List<BitmapSource>(mipCount - firstLevel);

                if (RequiresCompatibleTexDecode(header.Format))
                {
                    for (int level = firstLevel; level < mipCount; level++)
                        levels.Add(DecodeCompatibleTexLevel(stream, start, header, mipCount, level));
                    return levels;
                }

                for (int level = firstLevel; level < mipCount; level++)
                {
                    stream.Position = start;
                    int width = Math.Max(header.Width >> level, 1);
                    int height = Math.Max(header.Height >> level, 1);
                    Texture texture = Texture.LoadTex(stream, width, height);
                    if (texture.Mips.Length == 0)
                        break;
                    levels.Add(ConvertTextureMipToBitmapSource(texture));
                }

                return levels;
            }
            finally
            {
                copy?.Dispose();
            }
        }

        private static IReadOnlyList<BitmapSource> ConvertTextureMipChain(Texture texture, int minWidth)
        {
            if (texture?.Mips is not { Length: > 0 })
                return Array.Empty<BitmapSource>();

            int firstLevel = SelectFirstMipLevel(texture.Mips[0].Width, texture.Mips.Length, minWidth);
            var levels = new List<BitmapSource>(texture.Mips.Length - firstLevel);
            for (int level = firstLevel; level < texture.Mips.Length; level++)
                levels.Add(ConvertTextureMipToBitmapSource(texture, level));
            return levels;
        }

        private static int GetTexMipCount(TexHeader header) =>
            (header.Flags & 1) != 0
                ? (int)Math.Floor(Math.Log2(Math.Max(Math.Max(header.Width, header.Height), Math.Max(header.Depth, (byte)1)))) + 1
                : 1;

        private static int SelectFirstMipLevel(int width, int mipCount, int minWidth)
        {
            for (int level = mipCount - 1; level >= 0; level--)
            {
                int mipWidth = level >= 31 ? 1 : Math.Max(width >> level, 1);
                if (mipWidth >= minWidth)
                    return level;
            }
            return 0;
        }

        private static BitmapSource DecodeCompatibleTexLevel(
            Stream stream,
            long start,
            TexHeader header,
            int mipCount,
            int level)
        {
            int mipWidth = Math.Max(header.Width >> level, 1);
            int mipHeight = Math.Max(header.Height >> level, 1);
            long offset = 0;
            for (int i = mipCount - 1; i > level; i--)
                offset = checked(offset + GetTexMipByteCount(header, i));

            int sliceBytes = GetTexSliceByteCount(header.Format, mipWidth, mipHeight);
            long dataOffset = checked(start + 12 + offset);
            if (dataOffset < 0 || sliceBytes < 0 || dataOffset + sliceBytes > stream.Length)
                throw new EndOfStreamException("TEX mip data is truncated.");

            stream.Position = dataOffset;
            byte[] encoded = new byte[sliceBytes];
            stream.ReadExactly(encoded);
            byte[] rgba = DecodeCompatibleTexPixels(header.Format, encoded, mipWidth, mipHeight);
            return CreateBgra32BitmapSource(rgba, mipWidth, mipHeight);
        }

        private static long GetTexMipByteCount(TexHeader header, int level)
        {
            int width = Math.Max(header.Width >> level, 1);
            int height = Math.Max(header.Height >> level, 1);
            int depth = Math.Max(header.Depth >> level, 1);
            return checked((long)GetTexSliceByteCount(header.Format, width, height) * depth);
        }

        private static int GetTexSliceByteCount(byte format, int width, int height)
        {
            (int blockWidth, int blockHeight, int bytesPerBlock) = format switch
            {
                1 => (4, 4, 8),
                2 or 3 or 14 => (4, 4, 16),
                21 => (1, 1, 8),
                22 => (1, 1, 16),
                _ => throw new NotSupportedException($"Unsupported compatible TEX format: {format}")
            };
            int blocksX = checked((width + blockWidth - 1) / blockWidth);
            int blocksY = checked((height + blockHeight - 1) / blockHeight);
            return checked(blocksX * blocksY * bytesPerBlock);
        }

        private static byte[] DecodeCompatibleTexPixels(byte format, byte[] encoded, int width, int height)
        {
            return format switch
            {
                1 => DecodeEtc(encoded, width, height, etc2: false),
                2 or 3 => DecodeEtc(encoded, width, height, etc2: true),
                14 => DecodeBc5Snorm(encoded, width, height),
                21 => DecodeFloatTexture(encoded, width, height, halfPrecision: true),
                22 => DecodeFloatTexture(encoded, width, height, halfPrecision: false),
                _ => throw new NotSupportedException($"Unsupported compatible TEX format: {format}")
            };
        }

        private static byte[] DecodeEtc(byte[] encoded, int width, int height, bool etc2)
        {
            byte[] rgba = new byte[checked(width * height * 4)];
            if (etc2)
                EtcDecoder.DecompressETC2A8<ColorRGBA<byte>, byte>(encoded, width, height, rgba);
            else
                EtcDecoder.DecompressETC<ColorRGBA<byte>, byte>(encoded, width, height, rgba);
            return rgba;
        }

        private static byte[] DecodeBc5Snorm(byte[] encoded, int width, int height)
        {
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            if (encoded.Length != checked(blocksX * blocksY * 16))
                throw new InvalidDataException("BC5_SNORM TEX payload has an invalid size.");

            byte[] rgba = new byte[checked(width * height * 4)];
            Span<sbyte> red = stackalloc sbyte[16];
            Span<sbyte> green = stackalloc sbyte[16];
            for (int blockIndex = 0; blockIndex < blocksX * blocksY; blockIndex++)
            {
                ReadOnlySpan<byte> block = encoded.AsSpan(blockIndex * 16, 16);
                DecodeBc4SnormBlock(block[..8], red);
                DecodeBc4SnormBlock(block[8..], green);
                int baseX = (blockIndex % blocksX) * 4;
                int baseY = (blockIndex / blocksX) * 4;
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int pixelX = baseX + x;
                        int pixelY = baseY + y;
                        if (pixelX >= width || pixelY >= height)
                            continue;

                        int source = y * 4 + x;
                        int target = (pixelY * width + pixelX) * 4;
                        rgba[target] = SnormToUnorm(red[source]);
                        rgba[target + 1] = SnormToUnorm(green[source]);
                        rgba[target + 2] = 0;
                        rgba[target + 3] = 255;
                    }
                }
            }
            return rgba;
        }

        private static void DecodeBc4SnormBlock(ReadOnlySpan<byte> block, Span<sbyte> output)
        {
            sbyte endpoint0 = unchecked((sbyte)block[0]);
            sbyte endpoint1 = unchecked((sbyte)block[1]);
            float value0 = Math.Max(endpoint0, (sbyte)-127);
            float value1 = Math.Max(endpoint1, (sbyte)-127);
            Span<float> palette = stackalloc float[8];
            palette[0] = value0;
            palette[1] = value1;
            if (endpoint0 > endpoint1)
            {
                for (int i = 2; i < 8; i++)
                    palette[i] = (value0 * (8 - i) + value1 * (i - 1)) / 7f;
            }
            else
            {
                for (int i = 2; i < 6; i++)
                    palette[i] = (value0 * (6 - i) + value1 * (i - 1)) / 5f;
                palette[6] = -127f;
                palette[7] = 127f;
            }

            ulong indices = BinaryPrimitives.ReadUInt64LittleEndian(block) >> 16;
            for (int i = 0; i < 16; i++)
            {
                int paletteIndex = (int)((indices >> (i * 3)) & 7);
                output[i] = (sbyte)MathF.Round(palette[paletteIndex], MidpointRounding.AwayFromZero);
            }
        }

        private static byte SnormToUnorm(sbyte value)
        {
            float normalized = Math.Max(value, (sbyte)-127) / 127f;
            return ToUnorm8(normalized * 0.5f + 0.5f);
        }

        private static byte[] DecodeFloatTexture(byte[] encoded, int width, int height, bool halfPrecision)
        {
            int bytesPerChannel = halfPrecision ? 2 : 4;
            int expected = checked(width * height * 4 * bytesPerChannel);
            if (encoded.Length != expected)
                throw new InvalidDataException("Floating-point TEX payload has an invalid size.");

            byte[] rgba = new byte[checked(width * height * 4)];
            for (int channel = 0; channel < rgba.Length; channel++)
            {
                int offset = channel * bytesPerChannel;
                float value = halfPrecision
                    ? (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(offset, 2)))
                    : BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(offset, 4)));
                rgba[channel] = ToUnorm8(value);
            }
            return rgba;
        }

        private static byte ToUnorm8(float value)
        {
            if (float.IsNaN(value))
                return 0;
            float clamped = Math.Clamp(value, 0f, 1f);
            return (byte)MathF.Round(clamped * 255f, MidpointRounding.AwayFromZero);
        }

        private readonly record struct TexHeader(
            ushort Width,
            ushort Height,
            byte Depth,
            byte Format,
            byte ResourceType,
            byte Flags);

        private static BitmapSource ConvertTextureMipToBitmapSource(Texture texture) =>
            ConvertTextureMipToBitmapSource(texture, 0);

        private static BitmapSource ConvertTextureMipToBitmapSource(Texture texture, int level)
        {
            var mip = texture.Mips[level];
            if (!mip.TryGetMemory(out Memory<ColorRgba32> colorMemory))
                throw new InvalidOperationException("Texture mip memory must be contiguous.");

            return CreateBgra32BitmapSource(
                MemoryMarshal.AsBytes(colorMemory.Span).ToArray(),
                mip.Width,
                mip.Height);
        }

        private static BitmapSource ConvertImageToBitmapSource(Image<Rgba32> image, int? maxWidth, int? maxHeight)
        {
            if ((maxWidth.HasValue && image.Width > maxWidth.Value) ||
                (maxHeight.HasValue && image.Height > maxHeight.Value))
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(maxWidth ?? image.Width, maxHeight ?? image.Height),
                    Mode = ResizeMode.Max
                }));
            }

            byte[] pixels = new byte[checked(image.Width * image.Height * 4)];
            image.CopyPixelDataTo(pixels);
            return CreateBgra32BitmapSource(pixels, image.Width, image.Height);
        }

        private static BitmapSource CreateBgra32BitmapSource(byte[] pixels, int width, int height)
        {
            for (int i = 0; i < pixels.Length; i += 4)
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);

            BitmapSource bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, checked(width * 4));
            bitmap.Freeze();
            return bitmap;
        }

        public static BitmapSource LoadTexture(Stream textureStream, string extension)
        {
            return LoadTexture(textureStream, extension, null, null);
        }

        public static BitmapSource LoadTextureFromFile(string filePath, int? maxWidth = null, int? maxHeight = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string extension = Path.GetExtension(filePath);
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                using Image<Rgba32> image = Image.Load<Rgba32>(fileStream);
                return ConvertImageToBitmapSource(image, maxWidth, maxHeight);
            }

            return LoadTexture(fileStream, extension, maxWidth, maxHeight);
        }

        public static async Task SaveBitmapSourceAsImageAsync(
            BitmapSource bitmapSource,
            string originalFileName,
            string destinationPath,
            ImageExportFormat format,
            Action<string> onFileSavedCallback,
            CancellationToken cancellationToken = default)
        {
            string extension = format == ImageExportFormat.Jpeg ? ".jpg" : ".png";
            string fileName = Path.ChangeExtension(originalFileName, extension);
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);
            await ImageExportUtils.SaveBitmapAsImageAsync(bitmapSource, filePath, format, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

    }
}
