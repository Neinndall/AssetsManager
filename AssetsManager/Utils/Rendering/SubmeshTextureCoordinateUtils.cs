using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>
    /// Utility methods for evaluating texture coordinates, UV tiling, continuous UV scrolling,
    /// and address wrapping modes on submesh materials.
    /// </summary>
    public static class SubmeshTextureCoordinateUtils
    {
        /// <summary>
        /// Computes the transformed UV coordinates with repeat and time-based scroll animation.
        /// </summary>
        public static Vector2 ComputeAnimatedUv(
            Vector2 uv,
            Vector2 repeat,
            Vector2 scrollVelocity,
            float elapsedTimeSeconds)
        {
            return (uv * repeat) + (scrollVelocity * elapsedTimeSeconds);
        }

        /// <summary>
        /// Resolves the effective wrap mode for texture addressing, defaulting to Clamp if unassigned.
        /// </summary>
        public static ModelMaterialWrapMode NormalizeWrapMode(ModelMaterialWrapMode wrapMode)
        {
            return wrapMode switch
            {
                ModelMaterialWrapMode.Repeat => ModelMaterialWrapMode.Repeat,
                ModelMaterialWrapMode.Clamp => ModelMaterialWrapMode.Clamp,
                ModelMaterialWrapMode.Mirror => ModelMaterialWrapMode.Mirror,
                _ => ModelMaterialWrapMode.Clamp
            };
        }
    }
}
