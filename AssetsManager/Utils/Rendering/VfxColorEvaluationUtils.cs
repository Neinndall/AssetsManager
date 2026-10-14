using System;
using System.Numerics;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>
    /// Utility functions for color ramp lookup coordinate evaluation and blend-mode premultiplication.
    /// </summary>
    public static class VfxColorEvaluationUtils
    {
        /// <summary>
        /// Evaluates one axis of a particle color lookup ramp based on its driver kind.
        /// </summary>
        public static float ResolveLookupAxis(
            int kind,
            float scale,
            float offset,
            float age01,
            float speed,
            float birthRandom)
        {
            return kind switch
            {
                1 => scale * age01 + offset,       // Lifetime
                2 => scale * speed + offset,       // Velocity
                3 => scale * birthRandom + offset, // Birth random roll
                _ => scale                         // Constant
            };
        }

        /// <summary>
        /// Evaluates a 2D color lookup coordinate pair for an emitter particle.
        /// </summary>
        public static Vector2 EvaluateColorLookup(
            int kindX,
            int kindY,
            Vector2 scales,
            Vector2 offsets,
            float age01,
            float speed,
            float birthRandom)
        {
            float u = ResolveLookupAxis(kindX, scales.X, offsets.X, age01, speed, birthRandom);
            float v = ResolveLookupAxis(kindY, scales.Y, offsets.Y, age01, speed, birthRandom);
            return new Vector2(u, v);
        }

        /// <summary>
        /// Premultiplies vertex color RGB by alpha for Add and Subtract blend modes when appropriate.
        /// Distortion particles and custom-material passes are excluded.
        /// </summary>
        public static Vector4 PremultiplyVertexColor(
            Vector4 color,
            int blendMode,
            bool isDistortion,
            bool hasCustomMaterial)
        {
            if (hasCustomMaterial || isDistortion) return color;
            if (blendMode != 0 && blendMode != 2) return color; // Only Add (0) and Subtract (2)

            return new Vector4(
                color.X * color.W,
                color.Y * color.W,
                color.Z * color.W,
                1.0f);
        }
    }
}
