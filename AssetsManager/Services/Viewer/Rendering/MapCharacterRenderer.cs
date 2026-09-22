using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Dedicated renderer for structures and level props placed by a MAP scene.
    /// One GPU resource set is kept per skin while every placement only contributes a world transform.
    /// </summary>
    internal sealed class MapCharacterRenderer : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);

        private sealed class SkinResources
        {
            internal uint Vao;
            internal uint PositionVbo;
            internal uint NormalVbo;
            internal uint UvVbo;
            internal uint SkinIndexVbo;
            internal uint SkinWeightVbo;
            internal uint Ebo;
            internal bool HasSkin;
            internal int[] InfluenceJointSlots = Array.Empty<int>();
            internal IReadOnlyList<BoundRange> Ranges = Array.Empty<BoundRange>();
            internal readonly Dictionary<string, Matrix4x4[]> Palettes = new(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed record BoundRange(
            int StartIndex,
            int IndexCount,
            string Name,
            ModelMaterialDefinition Material,
            BitmapSource Texture,
            bool Lit,
            bool Transparent,
            ModelMaterialWrapMode WrapU,
            ModelMaterialWrapMode WrapV);

        private readonly record struct DrawCommand(
            SkinResources Resources,
            BoundRange Range,
            Matrix4x4 World,
            Matrix4x4[] Palette,
            float DistanceSquared,
            float TimeSeconds);

        private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.25f, 0.75f, -0.05f));
        private static readonly Vector3 DefaultUntextured = new(0.5f);
        private static readonly Vector3 DefaultErrored = new(1f, 0.08f, 0.16f);

        private readonly Dictionary<MapCharacterAssetData, SkinResources> _skins =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<BitmapSource, uint> _textures =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(ModelMaterialWrapMode U, ModelMaterialWrapMode V), uint> _samplers = new();
        private readonly List<DrawCommand> _opaque = new();
        private readonly List<DrawCommand> _transparent = new();

        private GL _gl;
        private DrawElementsDelegate _drawElements;
        private uint _program;
        private uint _boneBuffer;
        private uint _whiteTexture;
        private int _uViewProjection;
        private int _uWorld;
        private int _uUseSkinning;
        private int _uBaseTexture;
        private int _uHasTexture;
        private int _uColor;
        private int _uOpacity;
        private int _uAlphaTest;
        private int _uUvRepeat;
        private int _uUvOffset;
        private int _uLit;
        private int _uPremultipliedAlpha;
        private int _uLightDirection;
        private bool _ready;

        internal void Initialize(GL gl)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready) return;

            _gl = gl;
            IntPtr drawElements = gl.Context.GetProcAddress("glDrawElements");
            if (drawElements == IntPtr.Zero)
                throw new InvalidOperationException("OpenGL glDrawElements is unavailable for MAP character rendering.");
            _drawElements = Marshal.GetDelegateForFunctionPointer<DrawElementsDelegate>(drawElements);

            _program = GlShaderCompiler.CreateProgram(
                gl,
                GlShaderCompiler.UsesEmbeddedProfile(gl),
                MapCharacterShaderSource.Vertex,
                MapCharacterShaderSource.Fragment);
            _uViewProjection = gl.GetUniformLocation(_program, "uViewProjection");
            _uWorld = gl.GetUniformLocation(_program, "uWorld");
            _uUseSkinning = gl.GetUniformLocation(_program, "uUseSkinning");
            _uBaseTexture = gl.GetUniformLocation(_program, "uBaseTexture");
            _uHasTexture = gl.GetUniformLocation(_program, "uHasTexture");
            _uColor = gl.GetUniformLocation(_program, "uColor");
            _uOpacity = gl.GetUniformLocation(_program, "uOpacity");
            _uAlphaTest = gl.GetUniformLocation(_program, "uAlphaTest");
            _uUvRepeat = gl.GetUniformLocation(_program, "uUvRepeat");
            _uUvOffset = gl.GetUniformLocation(_program, "uUvOffset");
            _uLit = gl.GetUniformLocation(_program, "uLit");
            _uPremultipliedAlpha = gl.GetUniformLocation(_program, "uPremultipliedAlpha");
            _uLightDirection = gl.GetUniformLocation(_program, "uLightDirection");

            uint boneBlock = gl.GetUniformBlockIndex(_program, "BoneTransforms");
            if (boneBlock != uint.MaxValue)
                gl.UniformBlockBinding(_program, boneBlock, 0);
            _boneBuffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.UniformBuffer, _boneBuffer);
            gl.BufferData(
                BufferTargetARB.UniformBuffer,
                new ReadOnlySpan<float>(new float[MapCharacterShaderSource.MaximumBones * 16]),
                BufferUsageARB.DynamicDraw);
            gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _boneBuffer);
            gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);

            gl.UseProgram(_program);
            gl.Uniform1(_uBaseTexture, 0);
            gl.UseProgram(0);
            _whiteTexture = CreateWhiteTexture();
            _ready = true;
        }

        internal void Render(
            IReadOnlyList<MapCharacterRuntimeGroup> groups,
            Matrix4x4 viewProjection,
            Vector3 cameraPosition,
            float timeSeconds,
            IReadOnlySet<string> hidden = null,
            Vector3? untexturedLinear = null,
            Vector3? erroredLinear = null)
        {
            if (!_ready || groups == null || groups.Count == 0)
                return;

            Vector3 untextured = untexturedLinear ?? DefaultUntextured;
            Vector3 errored = erroredLinear ?? DefaultErrored;
            BuildQueues(groups, cameraPosition, timeSeconds, hidden);
            if (_opaque.Count == 0 && _transparent.Count == 0)
                return;

            _transparent.Sort((left, right) => right.DistanceSquared.CompareTo(left.DistanceSquared));

            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProjection, 1, false, in viewProjection.M11);
            _gl.Uniform3(_uLightDirection, SunDirection.X, SunDirection.Y, SunDirection.Z);
            _gl.DepthFunc(DepthFunction.Lequal);

            Matrix4x4[] activePalette = null;
            uint activeVao = 0;
            try
            {
                DrawQueue(_opaque, untextured, errored, ref activePalette, ref activeVao);
                DrawQueue(_transparent, untextured, errored, ref activePalette, ref activeVao);
            }
            finally
            {
                _gl.FrontFace(FrontFaceDirection.Ccw);
                _gl.Disable(EnableCap.Blend);
                _gl.Disable(EnableCap.CullFace);
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthMask(true);
                _gl.BindSampler(0, 0);
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.BindVertexArray(0);
                _gl.UseProgram(0);
            }
        }

        private void BuildQueues(
            IReadOnlyList<MapCharacterRuntimeGroup> groups,
            Vector3 cameraPosition,
            float timeSeconds,
            IReadOnlySet<string> hidden = null)
        {
            _opaque.Clear();
            _transparent.Clear();

            foreach (MapCharacterRuntimeGroup group in groups)
            {
                if (group?.Asset == null || group.Animation == null || group.Placements == null)
                    continue;
                SkinResources resources = EnsureResources(group.Asset);
                if (resources?.Ranges == null || resources.Ranges.Count == 0)
                    continue;

                foreach (MapCharacterAnimationPlacementGroup animationGroup in group.AnimationGroups)
                {
                    Matrix4x4[] joints = group.Animation.Evaluate(group.Asset, animationGroup.Animation, timeSeconds);
                    Matrix4x4[] palette = GetPalette(resources, animationGroup.Animation);
                    FillInfluencePalette(palette, joints, resources.InfluenceJointSlots);

                    foreach (MapCharacterRuntimePlacement runtimePlacement in animationGroup.Placements)
                    {
                        MapCharacterData placement = runtimePlacement.Placement;
                        if (MapOutlineSemantics.IsHidden(hidden, placement.ChunkHash, placement.KeyHash))
                            continue;

                        Matrix4x4 world = runtimePlacement.World;
                        float distance = Vector3.DistanceSquared(runtimePlacement.Position, cameraPosition);
                        foreach (BoundRange range in resources.Ranges)
                        {
                            var command = new DrawCommand(resources, range, world, palette, distance, timeSeconds);
                            if (range.Transparent) _transparent.Add(command);
                            else _opaque.Add(command);
                        }
                    }
                }
            }
        }

        private void DrawQueue(
            IReadOnlyList<DrawCommand> queue,
            Vector3 untextured,
            Vector3 errored,
            ref Matrix4x4[] activePalette,
            ref uint activeVao)
        {
            foreach (DrawCommand command in queue)
            {
                if (command.Resources.Vao != activeVao)
                {
                    activeVao = command.Resources.Vao;
                    _gl.BindVertexArray(activeVao);
                }

                if (!ReferenceEquals(activePalette, command.Palette))
                {
                    activePalette = command.Palette;
                    UploadPalette(activePalette);
                }

                Matrix4x4 world = command.World;
                _gl.UniformMatrix4(_uWorld, 1, false, in world.M11);
                _gl.Uniform1(_uUseSkinning, command.Resources.HasSkin ? 1 : 0);
                _gl.FrontFace(world.GetDeterminant() < 0f
                    ? FrontFaceDirection.CW
                    : FrontFaceDirection.Ccw);
                ApplyMaterial(command.Range, command.TimeSeconds, untextured, errored);

                _drawElements(
                    (uint)PrimitiveType.Triangles,
                    command.Range.IndexCount,
                    (uint)DrawElementsType.UnsignedInt,
                    new IntPtr(checked(command.Range.StartIndex * sizeof(uint))));
            }
        }

        private SkinResources EnsureResources(MapCharacterAssetData asset)
        {
            if (_skins.TryGetValue(asset, out SkinResources existing))
                return existing;

            MapCharacterMeshData mesh = asset.Mesh;
            if (mesh?.Positions == null || mesh.Positions.Length == 0 || mesh.Indices == null || mesh.Indices.Length == 0)
                return null;

            Vector3[] normals = mesh.Normals != null && mesh.Normals.Length == mesh.VertexCount
                ? mesh.Normals
                : ComputeNormals(mesh.Positions, mesh.Indices);
            Vector2[] uv = mesh.Uv != null && mesh.Uv.Length == mesh.VertexCount
                ? mesh.Uv
                : new Vector2[mesh.VertexCount];

            var resources = new SkinResources
            {
                Vao = _gl.GenVertexArray(),
                HasSkin = mesh.HasSkin,
                InfluenceJointSlots = BuildInfluenceJointSlots(asset.Skeleton)
            };
            _gl.BindVertexArray(resources.Vao);
            resources.PositionVbo = UploadVector3Attribute(0, mesh.Positions);
            resources.NormalVbo = UploadVector3Attribute(1, normals);
            resources.UvVbo = UploadVector2Attribute(2, uv);
            if (mesh.HasSkin)
            {
                resources.SkinIndexVbo = UploadByte4Attribute(3, mesh.SkinIndices);
                resources.SkinWeightVbo = UploadFloat4Attribute(4, mesh.SkinWeights);
            }

            resources.Ebo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, resources.Ebo);
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                new ReadOnlySpan<uint>(mesh.Indices),
                BufferUsageARB.StaticDraw);
            resources.Ranges = BindRanges(asset);

            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _skins.Add(asset, resources);
            return resources;
        }

        private IReadOnlyList<BoundRange> BindRanges(MapCharacterAssetData asset)
        {
            MapCharacterMeshData mesh = asset.Mesh;
            IEnumerable<MapCharacterMeshRange> ranges = mesh.Ranges != null && mesh.Ranges.Count > 0
                ? mesh.Ranges
                : new[] { new MapCharacterMeshRange(string.Empty, 0, mesh.Indices.Length) };
            var hidden = new HashSet<string>(
                asset.Skin.HiddenSubmeshes ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var result = new List<BoundRange>();

            foreach (MapCharacterMeshRange range in ranges)
            {
                if (range.IndexCount <= 0 || hidden.Contains(range.Name ?? string.Empty))
                    continue;

                ModelMaterialDefinition material = asset.Materials?.ResolveMaterialDefinition(range.Name) ??
                                                   ModelMaterialDefinition.TextureOnly(null);
                BitmapSource texture = null;
                if (material.BindingKind != ModelMaterialBindingKind.Missing &&
                    !string.IsNullOrWhiteSpace(material.BaseTextureName))
                {
                    asset.Textures?.TryGetValue(material.BaseTextureName, out texture);
                }

                bool transparent = material.RenderState.Blending != ModelMaterialBlendMode.Opaque &&
                                   !material.RenderState.Cutout;
                bool forceRepeat = material.UvRepeat != Vector2.One;
                result.Add(new BoundRange(
                    range.StartIndex,
                    range.IndexCount,
                    range.Name ?? string.Empty,
                    material,
                    texture,
                    material.IsLit,
                    transparent,
                    forceRepeat ? ModelMaterialWrapMode.Repeat : material.WrapU,
                    forceRepeat ? ModelMaterialWrapMode.Repeat : material.WrapV));
            }

            return result;
        }

        private void ApplyMaterial(
            BoundRange range,
            float timeSeconds,
            Vector3 untextured,
            Vector3 errored)
        {
            ModelMaterialDefinition material = range.Material;
            bool hasTexture = range.Texture != null;
            uint textureId = hasTexture ? AcquireTexture(range.Texture) : _whiteTexture;
            Vector3 color;
            if (material.BindingKind == ModelMaterialBindingKind.Missing)
                color = errored;
            else if (!hasTexture && material.BindingKind == ModelMaterialBindingKind.Authored && !material.HasAuthoredTint)
                color = untextured;
            else if (!hasTexture && material.BindingKind == ModelMaterialBindingKind.TextureOnly)
                color = untextured;
            else
                color = SrgbToLinear(new Vector3(material.Color.X, material.Color.Y, material.Color.Z));

            Vector2 scroll = new(
                ScrollAt(material.UvScroll.X, timeSeconds),
                ScrollAt(material.UvScroll.Y, timeSeconds));

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, textureId);
            _gl.BindSampler(0, ResolveSampler(range.WrapU, range.WrapV));
            _gl.Uniform1(_uHasTexture, hasTexture ? 1 : 0);
            _gl.Uniform3(_uColor, color.X, color.Y, color.Z);
            _gl.Uniform1(_uOpacity, material.BindingKind == ModelMaterialBindingKind.Missing ? 1f : material.Color.W);
            _gl.Uniform1(_uAlphaTest, material.BindingKind == ModelMaterialBindingKind.Missing ? 0f : material.AlphaCutoff);
            _gl.Uniform2(_uUvRepeat, material.UvRepeat.X, material.UvRepeat.Y);
            _gl.Uniform2(_uUvOffset, scroll.X, scroll.Y);
            _gl.Uniform1(_uLit, range.Lit ? 1 : 0);
            _gl.Uniform1(_uPremultipliedAlpha, material.RenderState.PremultipliedAlpha ? 1 : 0);
            ApplyRenderState(material.RenderState, range.Transparent);
        }

        private void ApplyRenderState(ModelMaterialRenderState state, bool transparent)
        {
            if (transparent)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquation(GLEnum.FuncAdd);
                switch (state.Blending)
                {
                    case ModelMaterialBlendMode.Additive:
                        _gl.BlendFunc(
                            state.PremultipliedAlpha ? BlendingFactor.One : BlendingFactor.SrcAlpha,
                            BlendingFactor.One);
                        break;
                    case ModelMaterialBlendMode.Modulate:
                        _gl.BlendFunc(BlendingFactor.OneMinusSrcColor, BlendingFactor.Zero);
                        break;
                    default:
                        _gl.BlendFuncSeparate(
                            state.PremultipliedAlpha ? BlendingFactor.One : BlendingFactor.SrcAlpha,
                            BlendingFactor.OneMinusSrcAlpha,
                            BlendingFactor.One,
                            BlendingFactor.OneMinusSrcAlpha);
                        break;
                }
            }
            else
            {
                _gl.Disable(EnableCap.Blend);
            }

            if (state.DepthTest) _gl.Enable(EnableCap.DepthTest);
            else _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(state.DepthWrite);

            if (state.DoubleSided)
            {
                _gl.Disable(EnableCap.CullFace);
            }
            else
            {
                _gl.Enable(EnableCap.CullFace);
                _gl.CullFace(state.Inverted ? TriangleFace.Front : TriangleFace.Back);
            }
        }

        internal static Matrix4x4 CreateWorldMatrix(Matrix4x4 authoredTransform, float skinScale) =>
            MapCharacterSemantics.WorldTransform(authoredTransform, skinScale);

        internal static float ScrollAt(float rate, float timeSeconds)
        {
            float turns = rate * timeSeconds;
            if (!float.IsFinite(turns)) return 0f;
            return turns - MathF.Floor(turns);
        }

        internal static int[] BuildInfluenceJointSlots(RigResource skeleton)
        {
            if (skeleton == null || skeleton.Influences == null)
                return Array.Empty<int>();
            var byId = new Dictionary<short, int>();
            for (int index = 0; index < skeleton.Joints.Count; index++)
                byId.TryAdd(skeleton.Joints[index].Id, index);

            int count = Math.Min(skeleton.Influences.Count, MapCharacterShaderSource.MaximumBones);
            var slots = new int[count];
            for (int index = 0; index < count; index++)
                slots[index] = byId.TryGetValue(skeleton.Influences[index], out int slot) ? slot : -1;
            return slots;
        }

        internal static void FillInfluencePalette(
            Matrix4x4[] palette,
            IReadOnlyList<Matrix4x4> jointTransforms,
            IReadOnlyList<int> influenceSlots)
        {
            if (palette == null) return;
            Array.Fill(palette, Matrix4x4.Identity);
            if (jointTransforms == null || influenceSlots == null) return;

            int count = Math.Min(palette.Length, influenceSlots.Count);
            for (int index = 0; index < count; index++)
            {
                int slot = influenceSlots[index];
                if ((uint)slot < (uint)jointTransforms.Count)
                    palette[index] = jointTransforms[slot];
            }
        }

        internal static Vector3[] ComputeNormals(Vector3[] positions, uint[] indices)
        {
            var normals = new Vector3[positions?.Length ?? 0];
            if (positions == null || indices == null) return normals;

            for (int index = 0; index + 2 < indices.Length; index += 3)
            {
                int a = checked((int)indices[index]);
                int b = checked((int)indices[index + 1]);
                int c = checked((int)indices[index + 2]);
                if ((uint)a >= (uint)positions.Length ||
                    (uint)b >= (uint)positions.Length ||
                    (uint)c >= (uint)positions.Length)
                    continue;
                Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if (normal.LengthSquared() <= 1e-12f) continue;
                normals[a] += normal;
                normals[b] += normal;
                normals[c] += normal;
            }

            for (int index = 0; index < normals.Length; index++)
                normals[index] = normals[index].LengthSquared() > 1e-12f
                    ? Vector3.Normalize(normals[index])
                    : Vector3.UnitY;
            return normals;
        }

        private Matrix4x4[] GetPalette(SkinResources resources, string animation)
        {
            string key = animation ?? string.Empty;
            if (resources.Palettes.TryGetValue(key, out Matrix4x4[] palette))
                return palette;
            palette = new Matrix4x4[MapCharacterShaderSource.MaximumBones];
            Array.Fill(palette, Matrix4x4.Identity);
            resources.Palettes[key] = palette;
            return palette;
        }

        private void UploadPalette(Matrix4x4[] palette)
        {
            if (_boneBuffer == 0 || palette == null) return;
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, _boneBuffer);
            _gl.BufferSubData(
                BufferTargetARB.UniformBuffer,
                0,
                new ReadOnlySpan<Matrix4x4>(palette, 0, Math.Min(palette.Length, MapCharacterShaderSource.MaximumBones)));
            _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _boneBuffer);
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        }

        private uint UploadVector3Attribute(uint location, Vector3[] values)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<Vector3>(values), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 3, VertexAttribPointerType.Float, false, (uint)Marshal.SizeOf<Vector3>(), IntPtr.Zero);
            return buffer;
        }

        private uint UploadVector2Attribute(uint location, Vector2[] values)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<Vector2>(values), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 2, VertexAttribPointerType.Float, false, (uint)Marshal.SizeOf<Vector2>(), IntPtr.Zero);
            return buffer;
        }

        private uint UploadByte4Attribute(uint location, byte[] values)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<byte>(values), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 4, VertexAttribPointerType.UnsignedByte, false, 4, IntPtr.Zero);
            return buffer;
        }

        private uint UploadFloat4Attribute(uint location, float[] values)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(values), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 4, VertexAttribPointerType.Float, false, 16, IntPtr.Zero);
            return buffer;
        }

        private uint AcquireTexture(BitmapSource source)
        {
            if (_textures.TryGetValue(source, out uint existing)) return existing;

            BitmapSource bitmap = source;
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = bitmap;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                converted.Freeze();
                bitmap = converted;
            }

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = checked(width * 4);
            byte[] pixels = new byte[checked(stride * height)];
            bitmap.CopyPixels(pixels, stride, 0);

            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Srgb8Alpha8,
                (uint)width,
                (uint)height,
                0,
                Silk.NET.OpenGL.PixelFormat.Bgra,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixels));
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _textures[source] = texture;
            return texture;
        }

        private uint ResolveSampler(ModelMaterialWrapMode u, ModelMaterialWrapMode v)
        {
            var key = (u, v);
            if (_samplers.TryGetValue(key, out uint sampler)) return sampler;
            sampler = _gl.GenSampler();
            _gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)ToWrap(u));
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)ToWrap(v));
            _samplers[key] = sampler;
            return sampler;
        }

        private uint CreateWhiteTexture()
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            byte[] white = { 255, 255, 255, 255 };
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Srgb8Alpha8,
                1,
                1,
                0,
                Silk.NET.OpenGL.PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(white));
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        private static TextureWrapMode ToWrap(ModelMaterialWrapMode wrap) => wrap switch
        {
            ModelMaterialWrapMode.Clamp => TextureWrapMode.ClampToEdge,
            ModelMaterialWrapMode.Mirror => TextureWrapMode.MirroredRepeat,
            ModelMaterialWrapMode.Border => TextureWrapMode.ClampToEdge,
            _ => TextureWrapMode.Repeat
        };

        private static Vector3 SrgbToLinear(Vector3 value) => new(
            SrgbChannelToLinear(value.X),
            SrgbChannelToLinear(value.Y),
            SrgbChannelToLinear(value.Z));

        private static float SrgbChannelToLinear(float value) =>
            value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

        internal void Clear()
        {
            if (!_ready) return;

            foreach (SkinResources resources in _skins.Values)
                ReleaseResources(resources);
            _skins.Clear();
            foreach (uint texture in _textures.Values)
                if (texture != 0) _gl.DeleteTexture(texture);
            _textures.Clear();
            _opaque.Clear();
            _transparent.Clear();
        }

        private void ReleaseResources(SkinResources resources)
        {
            DeleteBuffer(resources.PositionVbo);
            DeleteBuffer(resources.NormalVbo);
            DeleteBuffer(resources.UvVbo);
            DeleteBuffer(resources.SkinIndexVbo);
            DeleteBuffer(resources.SkinWeightVbo);
            DeleteBuffer(resources.Ebo);
            if (resources.Vao != 0) _gl.DeleteVertexArray(resources.Vao);
        }

        private void DeleteBuffer(uint buffer)
        {
            if (buffer != 0) _gl.DeleteBuffer(buffer);
        }

        public void Dispose()
        {
            if (!_ready) return;
            try
            {
                foreach (SkinResources resources in _skins.Values)
                    ReleaseResources(resources);
                _skins.Clear();
                foreach (uint texture in _textures.Values)
                    if (texture != 0) _gl.DeleteTexture(texture);
                _textures.Clear();
                foreach (uint sampler in _samplers.Values)
                    if (sampler != 0) _gl.DeleteSampler(sampler);
                _samplers.Clear();
                if (_boneBuffer != 0) _gl.DeleteBuffer(_boneBuffer);
                if (_whiteTexture != 0) _gl.DeleteTexture(_whiteTexture);
                if (_program != 0) _gl.DeleteProgram(_program);
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _skins.Clear();
                _textures.Clear();
                _samplers.Clear();
                _boneBuffer = 0;
                _whiteTexture = 0;
                _program = 0;
                _drawElements = null;
                _gl = null;
                _ready = false;
            }
        }
    }
}
