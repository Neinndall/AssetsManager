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
            => StoodForFlags(characters, 1 << layer);

        public static IReadOnlyList<MapCharacterData> StoodForFlags(
            IEnumerable<MapCharacterData> characters,
            int flags)
            => StoodFor(characters, null, MapVisibilityState.FromFlags(flags));

        /// <summary>
        /// Structures the previewed game state stands: mask and controller graph like the engine,
        /// excluding the neutral team placeholders LTK also skips.
        /// </summary>
        public static IReadOnlyList<MapCharacterData> StoodFor(
            IEnumerable<MapCharacterData> characters,
            MapSceneVisibility visibility,
            MapVisibilityState state)
        {
            if (characters == null)
                return Array.Empty<MapCharacterData>();

            return characters
                .Where(character =>
                    character != null &&
                    character.Team != NeutralTeam &&
                    MapVisibilitySemantics.IsVisible(
                        visibility,
                        state,
                        character.Placeable.Visibility,
                        character.VisibilityController))
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

        internal static (Vector3 Center, float Radius) PoseReach(Vector3[] positions)
        {
            if (positions == null || positions.Length == 0) return (Vector3.Zero, float.PositiveInfinity);
            Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            foreach (Vector3 position in positions)
            {
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
            return ((min + max) * 0.5f, Vector3.Distance(min, max));
        }

        internal static bool PoseInView(Vector3 center, float radius, Matrix4x4 world, Matrix4x4 viewProjection)
        {
            // Twice the bind-pose radius allows for animation, matching the map preview's pose slack.
            center = Vector3.Transform(center, world);
            float scale = MathF.Sqrt(MathF.Max(
                world.M11 * world.M11 + world.M12 * world.M12 + world.M13 * world.M13,
                MathF.Max(world.M21 * world.M21 + world.M22 * world.M22 + world.M23 * world.M23,
                    world.M31 * world.M31 + world.M32 * world.M32 + world.M33 * world.M33)));
            radius *= scale;
            if (!float.IsFinite(radius)) return true;
            var m = viewProjection;
            Span<Vector4> planes = stackalloc Vector4[6]
            {
                new(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41),
                new(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41),
                new(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42),
                new(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42),
                new(m.M14 + m.M13, m.M24 + m.M23, m.M34 + m.M33, m.M44 + m.M43),
                new(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43)
            };
            foreach (Vector4 plane in planes)
            {
                Vector3 normal = new(plane.X, plane.Y, plane.Z);
                if (Vector3.Dot(normal, center) + plane.W < -radius * normal.Length()) return false;
            }
            return true;
        }
    }
}
