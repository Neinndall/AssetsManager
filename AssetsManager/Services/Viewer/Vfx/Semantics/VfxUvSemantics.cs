using System;
using System.Numerics;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    /// <summary>
    /// League's UV bookkeeping before the shader samples a texture cell. Birth ramps are
    /// clamped only when authored to clamp; otherwise they wrap inside one cell. Long-running
    /// wrap/mirror offsets are folded on the CPU so float attributes retain texel precision.
    /// </summary>
    internal static class VfxUvSemantics
    {
        internal static Vector2 BirthRamp(Vector2 value, bool clamped)
            => clamped
                ? Vector2.Clamp(value, new Vector2(-1f), Vector2.One)
                : new Vector2(Fract(value.X), Fract(value.Y));

        internal static Vector2 Periodic(Vector2 value, int addressMode)
        {
            float period = addressMode switch
            {
                0 => 1f, // wrap
                1 => 2f, // mirror
                _ => 0f  // clamp / border keep the authored displacement
            };
            return period > 0f
                ? new Vector2(PositiveModulo(value.X, period), PositiveModulo(value.Y, period))
                : value;
        }

        private static float Fract(float value) => value - MathF.Floor(value);

        private static float PositiveModulo(float value, float period)
        {
            float wrapped = value % period;
            return wrapped < 0f ? wrapped + period : wrapped;
        }
    }
}
