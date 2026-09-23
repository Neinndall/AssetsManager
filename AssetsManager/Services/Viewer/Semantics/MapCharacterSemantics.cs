using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>
    /// Scene-side rules for map characters, matching the current LTK Manager MAIN preview.
    /// </summary>
    internal static class MapCharacterSemantics
    {
        internal const uint NeutralTeam = 300;

        public static IReadOnlyList<MapCharacterData> StoodOnLayer(
            IEnumerable<MapCharacterData> characters,
            int layer)
        {
            if (characters == null)
                return Array.Empty<MapCharacterData>();

            return characters
                .Where(character =>
                    character != null &&
                    character.Placeable.IsVisibleOnLayer(layer) &&
                    !character.VisibilityController.HasValue &&
                    character.Team != NeutralTeam)
                .ToArray();
        }

        public static string SkinFile(string skin) =>
            string.IsNullOrEmpty(skin)
                ? string.Empty
                : $"data/{skin.ToLowerInvariant()}.bin";

        /// <summary>
        /// Conjugates the authored placeable transform by the viewport's X-axis mirror.
        /// This is the Matrix4x4 equivalent of LTK's sceneMatrix() over its column-major array.
        /// </summary>
        public static Matrix4x4 SceneTransform(Matrix4x4 transform)
        {
            const float x = -1f;
            const float y = 1f;
            const float z = 1f;
            const float w = 1f;

            return new Matrix4x4(
                transform.M11 * x * x, transform.M12 * x * y, transform.M13 * x * z, transform.M14 * x * w,
                transform.M21 * y * x, transform.M22 * y * y, transform.M23 * y * z, transform.M24 * y * w,
                transform.M31 * z * x, transform.M32 * z * y, transform.M33 * z * z, transform.M34 * z * w,
                transform.M41 * w * x, transform.M42 * w * y, transform.M43 * w * z, transform.M44 * w * w);
        }

        /// <summary>
        /// Complete Character world matrix used by LTK: character-local X mirror/skin scale
        /// followed by the scene-conjugated authored placement transform.
        /// </summary>
        public static Matrix4x4 WorldTransform(Matrix4x4 authoredTransform, float skinScale)
        {
            Matrix4x4 placement = SceneTransform(authoredTransform);
            Matrix4x4 characterMirrorScale = Matrix4x4.CreateScale(-skinScale, skinScale, skinScale);
            return characterMirrorScale * placement;
        }

        /// <summary>
        /// World frame used by character-attached VFX in the viewport. The character-local X mirror
        /// belongs here, while skin scale remains owned by VfxOwnerSceneContext so bone offsets and
        /// attached meshes are not scaled twice.
        /// </summary>
        internal static Matrix4x4 VfxWorldTransform(Matrix4x4 authoredTransform) =>
            Matrix4x4.CreateScale(-1f, 1f, 1f) * SceneTransform(authoredTransform);
    }
}
