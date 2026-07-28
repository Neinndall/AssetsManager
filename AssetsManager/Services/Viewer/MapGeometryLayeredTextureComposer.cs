using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;

namespace AssetsManager.Services.Viewer
{
    internal static class MapGeometryLayeredTextureComposer
    {
        public static bool IsTerrainBlend(MapGeometryMaterialDefinition material) =>
            material != null &&
            TryGetSampler(material, "Mask_Texture", out _) &&
            TryGetSampler(material, "Bottom_Texture", out _) &&
            TryGetSampler(material, "Middle_Texture", out _) &&
            TryGetSampler(material, "Top_Texture", out _) &&
            TryGetSampler(material, "Extras_Texture", out _);

        public static BitmapSource Compose(
            MapGeometryMaterialDefinition material,
            MapGeometryUvWorldMapping mapping,
            IReadOnlyDictionary<string, BitmapSource> texturesByPath,
            CancellationToken cancellationToken)
        {
            if (!IsTerrainBlend(material) || !mapping.IsValid)
                return null;

            if (!TryGetTexture(material, "Mask_Texture", texturesByPath, out BitmapSource maskSource) ||
                !TryGetTexture(material, "Bottom_Texture", texturesByPath, out BitmapSource bottomSource) ||
                !TryGetTexture(material, "Middle_Texture", texturesByPath, out BitmapSource middleSource) ||
                !TryGetTexture(material, "Top_Texture", texturesByPath, out BitmapSource topSource) ||
                !TryGetTexture(material, "Extras_Texture", texturesByPath, out BitmapSource extrasSource))
            {
                return null;
            }

            var mask = PixelBuffer.From(maskSource);
            var bottom = PixelBuffer.From(bottomSource);
            var middle = PixelBuffer.From(middleSource);
            var top = PixelBuffer.From(topSource);
            var extras = PixelBuffer.From(extrasSource);
            int width = mask.Width;
            int height = mask.Height;
            int stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];

            Vector2 bottomTiling = ReadTiling(material, "Bottom_Tiling");
            Vector2 middleTiling = ReadTiling(material, "Mid_Tiling");
            Vector2 topTiling = ReadTiling(material, "Top_Tiling");
            Vector2 extrasTiling = ReadTiling(material, "Extra_Tiling");
            float worldScale = ReadScalar(material, "WS_Multiplier", 1f);
            var maskMultipliers = new Vector3(
                ReadScalar(material, "R_mask_multiplier", 1f),
                ReadScalar(material, "G_mask_multiplier", 1f),
                ReadScalar(material, "B_mask_multiplier", 1f));

            var options = new ParallelOptions { CancellationToken = cancellationToken };
            Parallel.For(0, height, options, y =>
            {
                float v = (y + 0.5f) / height;
                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) / width;
                    Vector2 world = mapping.Transform(u, v) * worldScale;
                    Vector4 weights = mask.SampleClamp(u, v);
                    Vector4 color = bottom.SampleRepeat(
                        world.X * bottomTiling.X,
                        world.Y * bottomTiling.Y);
                    color = Vector4.Lerp(
                        color,
                        middle.SampleRepeat(world.X * middleTiling.X, world.Y * middleTiling.Y),
                        Math.Clamp(weights.X * maskMultipliers.X, 0f, 1f));
                    color = Vector4.Lerp(
                        color,
                        top.SampleRepeat(world.X * topTiling.X, world.Y * topTiling.Y),
                        Math.Clamp(weights.Y * maskMultipliers.Y, 0f, 1f));
                    color = Vector4.Lerp(
                        color,
                        extras.SampleRepeat(world.X * extrasTiling.X, world.Y * extrasTiling.Y),
                        Math.Clamp(weights.Z * maskMultipliers.Z, 0f, 1f));

                    int offset = (y * width + x) * 4;
                    pixels[offset] = ToByte(color.Z);
                    pixels[offset + 1] = ToByte(color.Y);
                    pixels[offset + 2] = ToByte(color.X);
                    pixels[offset + 3] = byte.MaxValue;
                }
            });

            BitmapSource result = BitmapSource.Create(
                width,
                height,
                maskSource.DpiX,
                maskSource.DpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            if (result.CanFreeze) result.Freeze();
            return result;
        }

        private static bool TryGetTexture(
            MapGeometryMaterialDefinition material,
            string samplerName,
            IReadOnlyDictionary<string, BitmapSource> texturesByPath,
            out BitmapSource texture)
        {
            texture = null;
            return TryGetSampler(material, samplerName, out MapGeometryTextureSampler sampler) &&
                   texturesByPath.TryGetValue(PathUtils.ToVirtualPath(sampler.TexturePath), out texture);
        }

        private static bool TryGetSampler(
            MapGeometryMaterialDefinition material,
            string samplerName,
            out MapGeometryTextureSampler sampler)
        {
            foreach (MapGeometryTextureSampler candidate in material.Samplers)
            {
                string identity = string.IsNullOrWhiteSpace(candidate.TextureName)
                    ? candidate.SamplerName
                    : candidate.TextureName;
                if (samplerName.Equals(identity, StringComparison.OrdinalIgnoreCase))
                {
                    sampler = candidate;
                    return true;
                }
            }

            sampler = null;
            return false;
        }

        private static Vector2 ReadTiling(MapGeometryMaterialDefinition material, string name)
        {
            if (!material.Parameters.TryGetValue(name, out Vector4 value))
                return Vector2.One;

            float x = value.X == 0f ? 1f : value.X;
            float y = value.Y == 0f ? x : value.Y;
            return new Vector2(x, y);
        }

        private static float ReadScalar(
            MapGeometryMaterialDefinition material,
            string name,
            float fallback) =>
            material.Parameters.TryGetValue(name, out Vector4 value) && value.X != 0f
                ? value.X
                : fallback;

        private static byte ToByte(float value) =>
            (byte)Math.Round(Math.Clamp(value, 0f, 1f) * byte.MaxValue);

        private sealed class PixelBuffer
        {
            private readonly byte[] _pixels;

            private PixelBuffer(int width, int height, byte[] pixels)
            {
                Width = width;
                Height = height;
                _pixels = pixels;
            }

            public int Width { get; }
            public int Height { get; }

            public static PixelBuffer From(BitmapSource source)
            {
                BitmapSource converted = source.Format == PixelFormats.Bgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int stride = checked(converted.PixelWidth * 4);
                var pixels = new byte[checked(stride * converted.PixelHeight)];
                converted.CopyPixels(pixels, stride, 0);
                return new PixelBuffer(converted.PixelWidth, converted.PixelHeight, pixels);
            }

            public Vector4 SampleClamp(float u, float v) =>
                SampleBilinear(Math.Clamp(u, 0f, 1f), Math.Clamp(v, 0f, 1f), false);

            public Vector4 SampleRepeat(float u, float v) =>
                SampleBilinear(u - MathF.Floor(u), v - MathF.Floor(v), true);

            private Vector4 SampleBilinear(float u, float v, bool repeat)
            {
                float px = u * Width - 0.5f;
                float py = v * Height - 0.5f;
                int x0 = (int)MathF.Floor(px);
                int y0 = (int)MathF.Floor(py);
                float tx = px - x0;
                float ty = py - y0;

                Vector4 c00 = ReadPixel(x0, y0, repeat);
                Vector4 c10 = ReadPixel(x0 + 1, y0, repeat);
                Vector4 c01 = ReadPixel(x0, y0 + 1, repeat);
                Vector4 c11 = ReadPixel(x0 + 1, y0 + 1, repeat);
                return Vector4.Lerp(
                    Vector4.Lerp(c00, c10, tx),
                    Vector4.Lerp(c01, c11, tx),
                    ty);
            }

            private Vector4 ReadPixel(int x, int y, bool repeat)
            {
                if (repeat)
                {
                    x = ((x % Width) + Width) % Width;
                    y = ((y % Height) + Height) % Height;
                }
                else
                {
                    x = Math.Clamp(x, 0, Width - 1);
                    y = Math.Clamp(y, 0, Height - 1);
                }

                int offset = (y * Width + x) * 4;
                const float scale = 1f / byte.MaxValue;
                return new Vector4(
                    _pixels[offset + 2] * scale,
                    _pixels[offset + 1] * scale,
                    _pixels[offset] * scale,
                    _pixels[offset + 3] * scale);
            }
        }
    }

    internal sealed class MapGeometryUvWorldMappingBuilder
    {
        private long _count;
        private double _sumU;
        private double _sumV;
        private double _sumX;
        private double _sumZ;
        private double _sumUU;
        private double _sumVV;
        private double _sumUX;
        private double _sumVZ;

        public void Add(float u, float v, Vector3 position, Matrix4x4 transform)
        {
            Vector3 world = Vector3.Transform(position, transform);
            _count++;
            _sumU += u;
            _sumV += v;
            _sumX += world.X;
            _sumZ += world.Z;
            _sumUU += u * u;
            _sumVV += v * v;
            _sumUX += u * world.X;
            _sumVZ += v * world.Z;
        }

        public MapGeometryUvWorldMapping Build()
        {
            float xScale = SolveScale(_sumU, _sumX, _sumUU, _sumUX, _count);
            float zScale = SolveScale(_sumV, _sumZ, _sumVV, _sumVZ, _count);
            return new MapGeometryUvWorldMapping(
                xScale,
                SolveBias(_sumU, _sumX, xScale, _count),
                zScale,
                SolveBias(_sumV, _sumZ, zScale, _count));
        }

        private static float SolveScale(
            double sumInput,
            double sumOutput,
            double sumInputSquared,
            double sumInputOutput,
            long count)
        {
            double denominator = count * sumInputSquared - sumInput * sumInput;
            return count > 1 && Math.Abs(denominator) > double.Epsilon
                ? (float)((count * sumInputOutput - sumInput * sumOutput) / denominator)
                : float.NaN;
        }

        private static float SolveBias(double sumInput, double sumOutput, float scale, long count) =>
            count > 0 && float.IsFinite(scale)
                ? (float)((sumOutput - sumInput * scale) / count)
                : float.NaN;
    }

    internal readonly record struct MapGeometryUvWorldMapping(
        float XScale,
        float XOffset,
        float ZScale,
        float ZOffset)
    {
        public bool IsValid =>
            float.IsFinite(XScale) &&
            float.IsFinite(XOffset) &&
            float.IsFinite(ZScale) &&
            float.IsFinite(ZOffset);

        public Vector2 Transform(float u, float v) =>
            new(XScale * u + XOffset, ZScale * v + ZOffset);
    }
}
