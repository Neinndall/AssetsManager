using System;
using System.Numerics;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>
    /// Utility functions for particle texture coordinate calculations, sprite-sheet cell placement,
    /// and UV transformations.
    /// </summary>
    public static class VfxTextureTransformUtils
    {
        private const float DefaultRampReach = 1.0f;

        /// <summary>
        /// Resolves the integer (col, row) coordinates in a sprite-sheet grid given a logical frame index and divisions.
        /// </summary>
        public static Vector2 ResolveGridCell(float logicalFrame, Vector2 divisions)
        {
            int columns = Math.Max(1, (int)MathF.Round(MathF.Max(1f, divisions.X)));
            int rows = Math.Max(1, (int)MathF.Round(MathF.Max(1f, divisions.Y)));
            int cells = checked(columns * rows);
            int frame = (int)MathF.Floor(logicalFrame + 0.0001f);
            int cell = ((frame % cells) + cells) % cells;
            return new Vector2(cell % columns, cell / columns);
        }

        /// <summary>
        /// Calculates the normalized width and height of an individual cell in a sprite sheet.
        /// </summary>
        public static Vector2 ResolveCellSize(Vector2 divisions)
        {
            float width = 1f / Math.Max(1, (int)MathF.Round(MathF.Max(1f, divisions.X)));
            float height = 1f / Math.Max(1, (int)MathF.Round(MathF.Max(1f, divisions.Y)));
            return new Vector2(width, height);
        }

        /// <summary>
        /// Wraps an offset value into [0, span), correctly handling negative values.
        /// </summary>
        public static float Wrap(float value, float span)
        {
            if (span <= 0f) return 0f;
            return ((value % span) + span) % span;
        }

        /// <summary>
        /// Applies birth scroll clamp or fractional wrap to an authored ramp value.
        /// </summary>
        public static float Ramp(float value, bool clamped, float maxReach = DefaultRampReach)
        {
            if (clamped) return Math.Clamp(value, -maxReach, maxReach);
            return value - MathF.Floor(value);
        }

        /// <summary>
        /// Constrains a continuously growing UV offset according to its texture addressing mode (0=Wrap, 1=Clamp, 2=Mirror).
        /// </summary>
        public static float Periodic(float offset, int addressMode)
        {
            return addressMode switch
            {
                0 => Wrap(offset, 1f), // Wrap
                2 => Wrap(offset, 2f), // Mirror
                _ => offset            // Clamp or Border
            };
        }

        /// <summary>
        /// Transforms texture coordinates about a center point with rotation, scale, offset, and axis flipping.
        /// </summary>
        public static Vector2 Transform(
            Vector2 raw,
            Vector2 center,
            Vector2 scale,
            float radians,
            Vector2 offset,
            bool flipU,
            bool flipV)
        {
            Vector2 placed = (raw - center) * scale;
            float c = MathF.Cos(radians);
            float s = MathF.Sin(radians);
            Vector2 result = new(
                placed.X * c - placed.Y * s,
                placed.X * s + placed.Y * c);
            result += center + offset;
            if (flipU) result.X = 1f - result.X;
            if (flipV) result.Y = 1f - result.Y;
            return result;
        }

        /// <summary>
        /// Transforms texture coordinates under fixed/locked alpha mode without center offset.
        /// </summary>
        public static Vector2 TransformLockedAlpha(
            Vector2 raw,
            Vector2 scale,
            float radians,
            bool flipU,
            bool flipV)
        {
            Vector2 placed = raw * scale;
            float c = MathF.Cos(radians);
            float s = MathF.Sin(radians);
            Vector2 result = new(
                placed.X * c - placed.Y * s,
                placed.X * s + placed.Y * c);
            if (flipU) result.X = 1f - result.X;
            if (flipV) result.Y = 1f - result.Y;
            return result;
        }
    }
}
