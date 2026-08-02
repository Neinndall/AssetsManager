using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>Resolves whether decoded particle RGB must supply the missing opacity mask.</summary>
    internal static class VfxTextureAlphaSemantics
    {
        // BC3/BC7 textures with an unused alpha channel can decode a few opaque
        // samples below 255. Keep genuine transparent masks out of this path.
        private const byte OpaqueAlphaFloor = 224;
        private const byte NearOpaqueAlpha = 240;
        private const int RequiredNearOpaqueCoveragePercent = 98;

        public static bool ShouldDeriveAlphaFromRgb(BitmapSource texture, int blendMode)
            => texture != null && ShouldDeriveAlphaFromRgb(HasEffectivelyOpaqueAlpha(texture), blendMode);

        public static bool ShouldDeriveAlphaFromRgb(bool hasOpaqueAlpha, int blendMode)
            => hasOpaqueAlpha && VfxBlendModes.Resolve(blendMode) == VfxBlendModeKind.Alpha;

        public static bool HasEffectivelyOpaqueAlpha(BitmapSource texture)
        {
            if (texture == null)
                return false;

            BitmapSource source = texture;
            if (source.Format != PixelFormats.Bgra32)
            {
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                source.Freeze();
            }

            int stride = checked(source.PixelWidth * 4);
            var pixels = new byte[checked(stride * source.PixelHeight)];
            source.CopyPixels(pixels, stride, 0);
            int pixelCount = source.PixelWidth * source.PixelHeight;
            int nearOpaqueCount = 0;
            for (int alpha = 3; alpha < pixels.Length; alpha += 4)
            {
                byte value = pixels[alpha];
                if (value < OpaqueAlphaFloor)
                    return false;
                if (value >= NearOpaqueAlpha)
                    nearOpaqueCount++;
            }

            return pixelCount > 0 &&
                (long)nearOpaqueCount * 100 >= (long)pixelCount * RequiredNearOpaqueCoveragePercent;
        }
    }
}
