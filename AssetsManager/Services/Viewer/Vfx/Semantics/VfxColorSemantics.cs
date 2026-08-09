using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    /// <summary>Normalizes and composes authored BIN colors before they reach the renderer.</summary>
    internal static class VfxColorSemantics
    {
        public static Vector4 Normalize(Vector4 value)
        {
            if (!IsFinite(value)) return Vector4.One;

            float max = MathF.Max(MathF.Max(value.X, value.Y), MathF.Max(value.Z, value.W));
            if (max > 1f) value /= 255f;

            return new Vector4(
                Math.Clamp(value.X, 0f, 1f),
                Math.Clamp(value.Y, 0f, 1f),
                Math.Clamp(value.Z, 0f, 1f),
                Math.Clamp(value.W, 0f, 1f));
        }

        public static Vector4 Multiply(Vector4 left, Vector4 right)
            => new(left.X * right.X, left.Y * right.Y, left.Z * right.Z, left.W * right.W);

        public static Vector4 ResolveBirth(VfxCurve4 birthColor, float emitterTime, Random random)
            => Normalize(birthColor.SampleBirth(emitterTime, random));

        public static Vector4 ResolveParticle(Vector4 birthColor, VfxCurve4? colorOverLife, float normalizedAge)
        {
            Vector4 color = colorOverLife is { } curve
                ? Normalize(curve.Sample(normalizedAge))
                : Vector4.One;
            return Multiply(Normalize(birthColor), color);
        }

        private static bool IsFinite(Vector4 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}
