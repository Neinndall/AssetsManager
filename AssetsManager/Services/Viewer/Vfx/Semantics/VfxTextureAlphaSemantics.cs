using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    /// <summary>Resolves legacy League texture alpha semantics without changing authored mesh opacity.</summary>
    internal static class VfxTextureAlphaSemantics
    {
        // Some legacy League masks store opacity in RGB while the decoded alpha
        // channel is opaque. Keep this compatibility path narrow: real meshes
        // retain their authored alpha and only dark RGB mask coverage qualifies.
        private const byte OpaqueAlphaFloor = 224;
        private const int RequiredNearOpaqueCoveragePercent = 98;
        private const byte DarkRgbLuminanceThreshold = 32;
        private const int RequiredDarkRgbCoveragePercent = 2;

        public static bool ShouldDeriveAlphaFromRgb(
            BitmapSource texture,
            int blendMode,
            VfxPrimitiveKind primitiveKind)
            => texture != null &&
                IsRgbMaskPrimitive(primitiveKind) &&
                ShouldDeriveAlphaFromRgb(IsLegacyOpaqueRgbMask(texture), blendMode);

        public static bool ShouldDeriveAlphaFromRgb(bool hasLegacyOpaqueRgbMask, int blendMode)
            => hasLegacyOpaqueRgbMask && VfxBlendModes.Resolve(blendMode) is VfxBlendModeKind.Alpha or VfxBlendModeKind.Additive;

        public static bool ShouldDeriveAlphaFromRgb(
            bool hasLegacyOpaqueRgbMask,
            int blendMode,
            VfxPrimitiveKind primitiveKind)
            => IsRgbMaskPrimitive(primitiveKind) &&
                ShouldDeriveAlphaFromRgb(hasLegacyOpaqueRgbMask, blendMode);

        public static bool IsLegacyOpaqueRgbMask(BitmapSource texture)
            => HasEffectivelyOpaqueAlpha(texture) && HasDarkRgbCoverage(texture);

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
                if (value >= OpaqueAlphaFloor)
                    nearOpaqueCount++;
            }

            return pixelCount > 0 &&
                (long)nearOpaqueCount * 100 >= (long)pixelCount * RequiredNearOpaqueCoveragePercent;
        }

        private static bool HasDarkRgbCoverage(BitmapSource texture)
        {
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
            int darkCount = 0;

            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                byte luminance = (byte)Math.Clamp(
                    (int)Math.Round(
                        pixels[offset + 2] * 0.2126 +
                        pixels[offset + 1] * 0.7152 +
                        pixels[offset] * 0.0722),
                    0,
                    byte.MaxValue);
                if (luminance <= DarkRgbLuminanceThreshold)
                    darkCount++;
            }

            return pixelCount > 0 &&
                (long)darkCount * 100 >= (long)pixelCount * RequiredDarkRgbCoveragePercent;
        }

        private static bool IsRgbMaskPrimitive(VfxPrimitiveKind primitiveKind)
            => primitiveKind is VfxPrimitiveKind.CameraQuad
                or VfxPrimitiveKind.CameraUnitQuad
                or VfxPrimitiveKind.ArbitraryQuad
                or VfxPrimitiveKind.Mesh
                or VfxPrimitiveKind.AttachedMesh
                or VfxPrimitiveKind.CameraTrail
                or VfxPrimitiveKind.ArbitraryTrail
                or VfxPrimitiveKind.Ray
                or VfxPrimitiveKind.Beam
                or VfxPrimitiveKind.PlanarProjection;
    }
}
