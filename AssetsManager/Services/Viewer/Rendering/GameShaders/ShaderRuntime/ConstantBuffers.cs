using System;
using System.Collections.Generic;
using AssetsManager.Shaders;
using System.Numerics;
using System.Runtime.InteropServices;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    internal sealed partial class GameShaderRuntime
    {
        private void UpdateBlocks(
            ProgramRuntime runtime,
            PassGlobals globals,
            MapGeometryMeshData mesh,
            in Frame frame,
            CharacterDraw? character, VfxEmitterDefinition particle = null,
            IReadOnlyDictionary<string, Vector4> overrides = null)
        {
            bool skinned = character.HasValue || particle != null;
            foreach (BlockRuntime block in runtime.Blocks)
            {
                Array.Clear(block.Data, 0, block.Data.Length);
                switch (block.Block.Name)
                {
                    case Globals:
                        WriteGlobals(block.Data, block.Block, globals, mesh, overrides);
                        break;
                    case "PerFrameVertexCB":
                        WritePerFrameVertex(block.Data, frame, skinned);
                        break;
                    case "PerFramePixelCB":
                        WritePerFramePixel(block.Data, frame, skinned);
                        break;
                    case "CharacterPerDrawVertexCB" when character.HasValue || particle != null:
                        WriteCharacterPerDrawVertex(block.Data, frame, character?.World ?? Matrix4x4.Identity);
                        break;
                    case "CharacterPerDrawPS" when character.HasValue || particle != null:
                        WriteCharacterPerDrawPixel(block.Data, character ?? new CharacterDraw(Matrix4x4.Identity, Array.Empty<Matrix4x4>()), frame.LightGrid);
                        break;
                    case "VFXDynamicPerParticleInstanceCBVS" when particle != null:
                        WriteVector4(block.Data, 4, 4, Vector4.One);
                        Set(block.Data, 10, particle.DepthPushPull);
                        break;
                    case "VFXDynamicPerParticleInstanceCBPS":
                        // PARTICLE_COLOR_FACTOR scales colour and alpha (VFX_Uber_StaticMesh_Unlit with
                        // PARTICLE_COLOR_ACTIVE); left at zero it hides the draw. Neutral, like the vertex stage's.
                        WriteVector4(block.Data, 0, 4, Vector4.One);
                        break;
                    case "IBL_CUBEMAP_SCALES_BUFFER":
                        // Scale of cube 0, the only cube the preview binds (IBL_CUBEMAP_INDEX stays 0).
                        Set(block.Data, 0, 1f);
                        break;
                    case "EnvironmentTransitionVertexCB":
                    case "EnvironmentTransitionPixelCB":
                        // TransitionFactorAndDirection: the shaders read the factor from z; x marks an entering state.
                        WriteVector3(block.Data, 0, new Vector3(1f, 0f, frame.Environment.TransitionFactor));
                        break;
                    case "BonesCB" when character.HasValue:
                        WriteBones(block.Data, character.Value);
                        break;
                }

                if (!block.HasUploadedData || !BlockDataMatches(block.Data, block.UploadedData))
                {
                    _gl.BindBuffer(BufferTargetARB.UniformBuffer, block.Buffer);
                    // Replacing the store lets in-flight draws retain their data without a GPU wait.
                    _gl.BufferData(BufferTargetARB.UniformBuffer, new ReadOnlySpan<float>(block.Data), BufferUsageARB.StreamDraw);
                    block.Data.CopyTo(block.UploadedData, 0);
                    block.HasUploadedData = true;
                }
                if (ParticleDrawBindings != null)
                    ParticleDrawBindings.BindUniformBuffer(block.Binding, block.Buffer);
                else
                    _gl.BindBufferBase(BufferTargetARB.UniformBuffer, block.Binding, block.Buffer);
            }
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        }

        internal static bool BlockDataMatches(ReadOnlySpan<float> current, ReadOnlySpan<float> uploaded) =>
            MemoryMarshal.AsBytes(current).SequenceEqual(MemoryMarshal.AsBytes(uploaded));

        private static void WriteGlobals(
            float[] data,
            GameShaderTranslator.UniformBlock block,
            PassGlobals globals,
            MapGeometryMeshData mesh, IReadOnlyDictionary<string, Vector4> overrides = null)
        {
            IReadOnlyDictionary<string, Vector4> parameters = globals?.Parameters;
            IReadOnlyDictionary<string, bool> switches = globals?.RuntimeSwitches;

            foreach (GameShaderTranslator.BlockMember member in block.Members)
            {
                int at = checked((int)(member.Offset / sizeof(float)));
                int count = checked((int)(member.Size / sizeof(float)));
                if (member.Name is "WORLD_MATRIX" or "WORLD_MATRIX_INV")
                {
                    WriteIdentityRows(data, at, count);
                    continue;
                }

                // Per-mesh wind phase and grass-distortion origin of VertexDeform: the bounds centre in the
                // baked world space, so each bush sways on its own phase instead of all in step.
                if (member.Name == MeshCenter && mesh != null)
                {
                    WriteVector3(data, at, (mesh.Min + mesh.Max) * 0.5f);
                    continue;
                }

                if (member.Name == BakedLightTransform)
                {
                    WriteLightTransform(data, at, mesh?.BakedLight);
                    continue;
                }
                if (member.Name == StationaryLightTransform)
                {
                    WriteLightTransform(data, at, mesh?.StationaryLight);
                    continue;
                }

                if (overrides != null && overrides.TryGetValue(member.Name, out Vector4 dynamicValue))
                {
                    WriteVector4(data, at, count, dynamicValue);
                    continue;
                }
                if (parameters != null && parameters.TryGetValue(member.Name, out Vector4 value))
                {
                    WriteVector4(data, at, count, value);
                    continue;
                }

                if (member.Name.StartsWith("switch_", StringComparison.Ordinal) &&
                    switches != null && switches.TryGetValue(member.Name[7..], out bool enabled) && at < data.Length)
                {
                    data[at] = enabled ? 1f : 0f;
                }
            }
        }

        private static void WriteLightTransform(float[] data, int at, MapGeometryLightChannelData channel)
        {
            Set(data, at, channel?.Scale.X ?? 1f);
            Set(data, at + 1, channel?.Scale.Y ?? 1f);
            Set(data, at + 2, channel?.Bias.X ?? 0f);
            Set(data, at + 3, channel?.Bias.Y ?? 0f);
        }

        private static void WritePerFrameVertex(float[] data, in Frame frame, bool skinned)
        {
            Matrix4x4 mirror = Matrix4x4.CreateScale(-1f, 1f, 1f);
            Matrix4x4 clip = skinned
                ? frame.View * frame.Projection
                : mirror * frame.View * frame.Projection;
            Matrix4x4.Invert(frame.View, out Matrix4x4 cameraWorld);
            Vector3 eye = skinned
                ? frame.Eye
                : new Vector3(-frame.Eye.X, frame.Eye.Y, frame.Eye.Z);
            Vector3 direction = ResolveSunDirection(frame.Sun, skinned);

            WriteClipRows(data, 0, clip);
            WriteVector3(data, 16, eye);
            Set(data, 20, frame.TimeSeconds);
            WriteVector4(data, 24, 4, frame.Environment.TerrainTransform);
            WriteClipRows(data, 28, clip);
            WriteMatrixRows(data, 96, frame.View);
            WriteMatrixRows(data, 112, cameraWorld);
            WriteVector3(data, 132, direction);
        }

        private static void WritePerFramePixel(float[] data, in Frame frame, bool skinned)
        {
            ResolveSun(frame.Sun, out Vector3 color, out float intensity, out Vector3 sky, out float skyScale,
                out Vector3 direction, out float lightMapScale, out bool fogEnabled, out Vector3 fog,
                out Vector3 fogAlternate, out Vector2 fogStartEnd, out float fogEmissive);
            Matrix4x4.Invert(frame.View, out Matrix4x4 cameraWorld);
            Vector3 eye = skinned
                ? frame.Eye
                : new Vector3(-frame.Eye.X, frame.Eye.Y, frame.Eye.Z);
            if (skinned)
                direction.X = -direction.X;
            Vector3 sun = color * intensity;
            Vector3 shadow = sky * skyScale;
            Vector3 complement = Vector3.Max(sun - shadow, Vector3.Zero);

            WriteVector3(data, 0, eye);
            Set(data, 4, frame.TimeSeconds);
            WriteVector4(data, 8, 4, frame.Environment.TerrainTransform);
            WriteVector3(data, 12, shadow);
            Set(data, 15, 1f);
            WriteVector3(data, 16, complement);
            Set(data, 19, 1f);
            Vector2 depth = DepthConversion(frame.Projection);
            Set(data, 20, depth.X);
            Set(data, 21, depth.Y);
            WriteVector3(data, 24, sun);
            Set(data, 27, 1f);
            WriteVector3(data, 29, direction);
            Set(data, 32, lightMapScale);
            if (fogEnabled)
            {
                WriteVector3(data, 33, fog);
                WriteVector3(data, 36, fogAlternate);
                Set(data, 40, fogStartEnd.X);
                Set(data, 41, fogStartEnd.Y);
                Set(data, 42, 1f);
                Set(data, 43, fogEmissive);
            }
            else
            {
                WriteVector3(data, 33, sky);
                WriteVector3(data, 36, sky);
                Set(data, 40, FogStart);
                Set(data, 41, FogEnd);
            }
            WriteMatrixRows(data, 68, frame.View);
            // m[16]: LIGHT_GRID_TEXTURE_SCALE (x, PBR characters) and GRASS_INTERP (y, VertexDeform).
            // LIGHT_GRID_WORLD_TO_GRID (60) stays zero, so the one-texel grid texture is read at its origin.
            Set(data, 64, frame.LightGrid?.RmaIntensityScale ?? MapLightGridData.DefaultRmaIntensityScale);
            Set(data, 65, frame.Environment.GrassInterp);
            // HDR_ENV_DIFFUSE_SCALE (88): weight of the IBL diffuse term PBR shaders add to the light grid.
            Set(data, 88, 1f);
            WriteMatrixRows(data, 104, cameraWorld);
        }

        internal static Vector2 DepthConversion(Matrix4x4 projection)
        {
            if (MathF.Abs(projection.M34) > 0f && projection.M43 != 0f)
                return new Vector2((projection.M33 - 1f) / projection.M43, 2f / projection.M43);
            float span = projection.M33 == 0f ? 1f : MathF.Abs(2f / projection.M33);
            const float slope = 1e-3f;
            return new Vector2(slope / span, -slope * slope / span);
        }

        /// <summary>
        /// mWorld (0) and mWorldInv (44) are the character placement. The bone palette already carries it, and
        /// shaders subtract mWorld-transformed authored points from the skinned position to work in object space.
        /// </summary>
        internal static void WriteCharacterPerDrawVertex(float[] data, in Frame frame, Matrix4x4 world)
        {
            WriteWorldRows(data, 0, 44, world);
            Span<Vector3> cube = stackalloc Vector3[6];
            ResolveAmbientCube(frame, cube);
            for (int face = 0; face < 6; face++)
            {
                WriteVector3(data, 16 + face * 4, cube[face]);
                Set(data, 19 + face * 4, 1f);
            }
        }

        private static void WriteWorldRows(float[] data, int worldAt, int inverseAt, Matrix4x4 world)
        {
            WriteMatrixRows(data, worldAt, world);
            WriteMatrixRows(data, inverseAt, Matrix4x4.Invert(world, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity);
        }

        /// <summary>
        /// The ambient light cube (+X, -X, +Y, -Y, +Z, -Z) around the character: the map light grid at its
        /// position, or the preview sun and sky when no grid is loaded.
        /// </summary>
        internal static void ResolveAmbientCube(in Frame frame, Span<Vector3> cube)
        {
            if (frame.LightGrid != null)
            {
                frame.LightGrid.SampleSceneCube(frame.CharacterPosition, cube);
                return;
            }
            ResolveSun(
                frame.Sun,
                out Vector3 color,
                out float intensity,
                out Vector3 sky,
                out float skyScale,
                out Vector3 direction,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _);
            direction.X = -direction.X;
            Vector3 ground = frame.Sun == null
                ? sky
                : new Vector3(frame.Sun.GroundColor.X, frame.Sun.GroundColor.Y, frame.Sun.GroundColor.Z);
            Vector3 horizon = frame.Sun == null
                ? sky
                : new Vector3(frame.Sun.HorizonColor.X, frame.Sun.HorizonColor.Y, frame.Sun.HorizonColor.Z);
            Vector3 sun = color * intensity;
            Vector3[] faces =
            {
                Vector3.UnitX,
                -Vector3.UnitX,
                Vector3.UnitY,
                -Vector3.UnitY,
                Vector3.UnitZ,
                -Vector3.UnitZ
            };

            for (int face = 0; face < faces.Length; face++)
            {
                Vector3 axis = faces[face];
                Vector3 basis = axis.Y > 0f ? sky : axis.Y < 0f ? ground : horizon;
                float facing = MathF.Max(Vector3.Dot(axis, direction), 0f);
                cube[face] = basis * skyScale + sun * facing;
            }
        }

        private static void WriteCharacterPerDrawPixel(float[] data, in CharacterDraw character, MapLightGridData lightGrid)
        {
            Set(data, 0, character.SelfIllumination);
            Set(data, 1, character.SelfIllumination);
            Set(data, 2, character.SelfIllumination);
            Set(data, 7, 1f);
            Set(data, 8, 1f);
            Set(data, 9, lightGrid?.FullBright ?? 1f);
            // mWorld (16) and mWorldInv (32).
            WriteWorldRows(data, 16, 32, character.World);
        }

        private static void WriteBones(float[] data, in CharacterDraw character)
        {
            IReadOnlyList<Matrix4x4> bones = character.Bones;
            if (bones == null || bones.Count == 0)
                return;

            int count = Math.Min(256, Math.Min(bones.Count, data.Length / 12));
            for (int bone = 0; bone < count; bone++)
            {
                Matrix4x4 matrix = bones[bone] * character.World;
                int at = bone * 12;
                Set(data, at + 0, matrix.M11); Set(data, at + 1, matrix.M21); Set(data, at + 2, matrix.M31); Set(data, at + 3, matrix.M41);
                Set(data, at + 4, matrix.M12); Set(data, at + 5, matrix.M22); Set(data, at + 6, matrix.M32); Set(data, at + 7, matrix.M42);
                Set(data, at + 8, matrix.M13); Set(data, at + 9, matrix.M23); Set(data, at + 10, matrix.M33); Set(data, at + 11, matrix.M43);
            }
        }

        private static void ResolveSun(
            MapSunData sun,
            out Vector3 color,
            out float intensity,
            out Vector3 sky,
            out float skyScale,
            out Vector3 direction,
            out float lightMapScale,
            out bool fogEnabled,
            out Vector3 fog,
            out Vector3 fogAlternate,
            out Vector2 fogStartEnd,
            out float fogEmissive)
        {
            if (sun == null)
            {
                color = Vector3.One;
                intensity = 0.8f;
                sky = Vector3.One;
                skyScale = 1.2f;
                direction = Vector3.Normalize(new Vector3(-0.25f, 0.75f, -0.05f));
                lightMapScale = 1f;
                fogEnabled = false;
                fog = sky;
                fogAlternate = sky;
                fogStartEnd = new Vector2(FogStart, FogEnd);
                fogEmissive = 0f;
                return;
            }

            color = new Vector3(sun.Color.X, sun.Color.Y, sun.Color.Z);
            intensity = MathF.Max(sun.Intensity, 0f);
            sky = new Vector3(sun.SkyColor.X, sun.SkyColor.Y, sun.SkyColor.Z);
            skyScale = MathF.Max(sun.SkyScale, 0f);
            direction = ResolveSunDirection(sun);
            lightMapScale = MathF.Max(sun.LightMapColorScale, 0f);
            fogEnabled = sun.FogEnabled;
            fog = new Vector3(sun.FogColor.X, sun.FogColor.Y, sun.FogColor.Z);
            fogAlternate = new Vector3(sun.FogAlternateColor.X, sun.FogAlternateColor.Y, sun.FogAlternateColor.Z);
            fogStartEnd = sun.FogStartEnd;
            fogEmissive = sun.FogEmissiveRemap;
        }

        private static Vector3 ResolveSunDirection(MapSunData sun, bool skinned = false)
        {
            Vector3 direction = sun?.Direction ?? new Vector3(-0.25f, 0.75f, -0.05f);
            direction = float.IsFinite(direction.X) && float.IsFinite(direction.Y) && float.IsFinite(direction.Z) &&
                        direction.LengthSquared() > 1e-12f
                ? Vector3.Normalize(direction)
                : Vector3.Normalize(new Vector3(-0.25f, 0.75f, -0.05f));
            if (skinned)
                direction.X = -direction.X;
            return direction;
        }

        private static void WriteVector4(float[] data, int at, int count, Vector4 value)
        {
            if (count > 0) Set(data, at, value.X);
            if (count > 1) Set(data, at + 1, value.Y);
            if (count > 2) Set(data, at + 2, value.Z);
            if (count > 3) Set(data, at + 3, value.W);
        }

        private static void WriteVector3(float[] data, int at, Vector3 value)
        {
            Set(data, at, value.X);
            Set(data, at + 1, value.Y);
            Set(data, at + 2, value.Z);
        }

        private static void WriteIdentityRows(float[] data, int at, int floats)
        {
            int rows = Math.Min(4, floats / 4);
            for (int row = 0; row < rows; row++)
                Set(data, at + row * 4 + row, 1f);
        }

        private static void WriteClipRows(float[] data, int at, Matrix4x4 matrix)
        {
            WriteMatrixRows(data, at, matrix);
            for (int column = 0; column < 4; column++)
            {
                int z = at + 8 + column;
                int w = at + 12 + column;
                if ((uint)z < (uint)data.Length && (uint)w < (uint)data.Length)
                    data[z] = (data[z] + data[w]) * 0.5f;
            }
        }

        /// <summary>
        /// System.Numerics matrices are row-vector matrices. The game cbuffer stores the rows of
        /// the equivalent column-vector matrix, so each written row is one CPU matrix column.
        /// </summary>
        private static void WriteMatrixRows(float[] data, int at, Matrix4x4 matrix)
        {
            Set(data, at + 0, matrix.M11); Set(data, at + 1, matrix.M21); Set(data, at + 2, matrix.M31); Set(data, at + 3, matrix.M41);
            Set(data, at + 4, matrix.M12); Set(data, at + 5, matrix.M22); Set(data, at + 6, matrix.M32); Set(data, at + 7, matrix.M42);
            Set(data, at + 8, matrix.M13); Set(data, at + 9, matrix.M23); Set(data, at + 10, matrix.M33); Set(data, at + 11, matrix.M43);
            Set(data, at + 12, matrix.M14); Set(data, at + 13, matrix.M24); Set(data, at + 14, matrix.M34); Set(data, at + 15, matrix.M44);
        }

        private static void Set(float[] data, int at, float value)
        {
            if (at >= 0 && at < data.Length)
                data[at] = value;
        }
    }
}
