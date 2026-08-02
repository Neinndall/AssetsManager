using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>Resolves whether decoded particle RGB must supply the missing opacity mask.</summary>
    internal static class VfxTextureAlphaSemantics
    {
        public static bool ShouldDeriveAlphaFromRgb(BitmapSource texture, int blendMode)
            => texture != null && ShouldDeriveAlphaFromRgb(HasOpaqueAlpha(texture), blendMode);

        public static bool ShouldDeriveAlphaFromRgb(bool hasOpaqueAlpha, int blendMode)
            => hasOpaqueAlpha && VfxBlendModes.Resolve(blendMode) == VfxBlendModeKind.Alpha;

        public static bool HasOpaqueAlpha(BitmapSource texture)
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
            for (int alpha = 3; alpha < pixels.Length; alpha += 4)
            {
                if (pixels[alpha] < byte.MaxValue)
                    return false;
            }

            return pixels.Length != 0;
        }
    }
}
