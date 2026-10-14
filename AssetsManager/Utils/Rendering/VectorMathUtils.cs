using System;
using System.Numerics;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>Vector, basis and colour-space helpers shared by the viewer and VFX renderers.</summary>
    internal static class VectorMathUtils
    {
        internal static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        /// <summary><paramref name="value"/> normalised, or <paramref name="fallback"/> when it is (nearly) zero.</summary>
        internal static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
            value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

        /// <summary>The turn of <paramref name="transform"/> with its scale and translation taken out, axis by axis.</summary>
        internal static Matrix4x4 OrientationOnly(Matrix4x4 transform)
        {
            Vector3 right = NormalizeOr(Vector3.TransformNormal(Vector3.UnitX, transform), Vector3.UnitX);
            Vector3 up = NormalizeOr(Vector3.TransformNormal(Vector3.UnitY, transform), Vector3.UnitY);
            Vector3 forward = NormalizeOr(Vector3.TransformNormal(Vector3.UnitZ, transform), Vector3.UnitZ);
            return new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                forward.X, forward.Y, forward.Z, 0f,
                0f, 0f, 0f, 1f);
        }

        internal static Vector3 SrgbToLinear(Vector3 value) => new(
            SrgbChannelToLinear(value.X),
            SrgbChannelToLinear(value.Y),
            SrgbChannelToLinear(value.Z));

        internal static float SrgbChannelToLinear(float value) =>
            value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }
}
