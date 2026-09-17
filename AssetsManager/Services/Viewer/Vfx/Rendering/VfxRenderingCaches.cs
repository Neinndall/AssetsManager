using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    internal sealed class VfxTextureResourceCache : IDisposable
    {
        private readonly GL _gl;
        private readonly List<uint> _ownedTextures = new();

        internal VfxTextureResourceCache(GL gl)
        {
            _gl = gl;
            FallbackTransparentTexture = CreateFallbackTexture();
            _ownedTextures.Add(FallbackTransparentTexture);
        }

        internal uint FallbackTransparentTexture { get; }

        internal uint Upload(byte[] bgra, int width, int height)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(bgra));
            // League's particle samplers do not use mipmaps. Atlas mip levels blend neighbouring
            // cells and show up as rectangular halos around otherwise transparent particles.
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _ownedTextures.Add(texture);
            return texture;
        }

        internal uint UploadCube(VfxCubeMapData cube)
        {
            if (cube?.IsValid != true) return 0;

            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.TextureCubeMap, texture);
            for (int face = 0; face < 6; face++)
            {
                TextureTarget target = (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face);
                _gl.TexImage2D(
                    target,
                    0,
                    InternalFormat.Rgba8,
                    (uint)cube.Width,
                    (uint)cube.Height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    new ReadOnlySpan<byte>(cube.Faces[face]));
            }

            // LTK uploads the six DDS faces in file order, without mipmaps or a face flip.
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
            _ownedTextures.Add(texture);
            return texture;
        }

        internal void Clear()
        {
            foreach (uint texture in _ownedTextures)
            {
                if (texture != FallbackTransparentTexture)
                    _gl.DeleteTexture(texture);
            }

            _ownedTextures.Clear();
            _ownedTextures.Add(FallbackTransparentTexture);
        }

        public void Dispose()
        {
            foreach (uint texture in _ownedTextures)
                _gl.DeleteTexture(texture);
            _ownedTextures.Clear();
        }

        private uint CreateFallbackTexture()
        {
            byte[] transparentPixel = { 0, 0, 0, 0 };
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(transparentPixel));
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }
    }

    internal sealed class VfxMeshResourceCache : IDisposable
    {
        internal const int VertexStride = 20;

        private const int UvOffset = 3;
        private const int ColorOffset = 5;
        private const int NormalOffset = 9;
        private const int BoneIndexOffset = 12;
        private const int BoneWeightOffset = 16;

        private readonly GL _gl;
        private readonly Dictionary<float[], MeshGpuResource> _meshes =
            new(ReferenceEqualityComparer.Instance);

        internal VfxMeshResourceCache(GL gl)
        {
            _gl = gl;
        }

        internal void Upload(
            VfxPlaybackRuntime.EmitterState emitter,
            float[] positions,
            float[] normals,
            float[] uvs,
            float[] colors,
            uint[] indices,
            float[] boneIndices = null,
            float[] boneWeights = null)
        {
            if (_meshes.TryGetValue(positions, out MeshGpuResource cached))
            {
                Assign(emitter, cached);
                return;
            }

            int vertexCount = positions.Length / 3;
            bool hasSkinning =
                boneIndices is { Length: > 0 } &&
                boneWeights is { Length: > 0 } &&
                boneIndices.Length == vertexCount * 4 &&
                boneWeights.Length == vertexCount * 4;
            float[] interleaved = BuildInterleaved(positions, normals, uvs, colors, boneIndices, boneWeights);
            uint vao = _gl.GenVertexArray();
            uint vbo = _gl.GenBuffer();
            _gl.BindVertexArray(vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(interleaved), BufferUsageARB.DynamicDraw);

            ConfigureAttribute(0, 3, IntPtr.Zero);
            ConfigureAttribute(1, 2, new IntPtr(UvOffset * sizeof(float)));
            ConfigureAttribute(2, 4, new IntPtr(ColorOffset * sizeof(float)));
            ConfigureAttribute(3, 3, new IntPtr(NormalOffset * sizeof(float)));
            ConfigureAttribute(4, 4, new IntPtr(BoneIndexOffset * sizeof(float)));
            ConfigureAttribute(5, 4, new IntPtr(BoneWeightOffset * sizeof(float)));

            uint ebo = 0;
            if (indices is { Length: > 0 })
            {
                ebo = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, new ReadOnlySpan<uint>(indices), BufferUsageARB.StaticDraw);
            }

            _gl.BindVertexArray(0);
            MeshGpuResource resource = new(
                vao,
                vbo,
                ebo,
                vertexCount,
                indices?.Length ?? 0,
                interleaved,
                hasSkinning);
            _meshes[positions] = resource;
            Assign(emitter, resource);
        }

        internal void UpdatePositions(VfxPlaybackRuntime.EmitterState emitter, float[] positions)
        {
            if (emitter.MeshVbo == 0 || emitter.MeshInterleaved is not { } interleaved)
                return;

            int vertexCount = Math.Min(emitter.MeshVertexCount, positions.Length / 3);
            for (int i = 0; i < vertexCount; i++)
            {
                interleaved[i * VertexStride] = positions[i * 3];
                interleaved[i * VertexStride + 1] = positions[i * 3 + 1];
                interleaved[i * VertexStride + 2] = positions[i * 3 + 2];
            }

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, emitter.MeshVbo);
            _gl.BufferSubData(
                BufferTargetARB.ArrayBuffer,
                0,
                new ReadOnlySpan<float>(interleaved, 0, vertexCount * VertexStride));
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }

        internal void Clear()
        {
            foreach (MeshGpuResource mesh in _meshes.Values)
            {
                _gl.DeleteVertexArray(mesh.Vao);
                _gl.DeleteBuffer(mesh.Vbo);
                if (mesh.Ebo != 0)
                    _gl.DeleteBuffer(mesh.Ebo);
            }

            _meshes.Clear();
        }

        internal static float[] BuildInterleaved(
            float[] positions,
            float[] normals,
            float[] uvs,
            float[] colors,
            float[] boneIndices = null,
            float[] boneWeights = null)
        {
            ArgumentNullException.ThrowIfNull(positions);
            normals ??= Array.Empty<float>();
            uvs ??= Array.Empty<float>();
            colors ??= Array.Empty<float>();
            boneIndices ??= Array.Empty<float>();
            boneWeights ??= Array.Empty<float>();

            int vertexCount = positions.Length / 3;
            float[] interleaved = new float[vertexCount * VertexStride];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                int target = vertex * VertexStride;
                int position = vertex * 3;
                int normal = vertex * 3;
                int uv = vertex * 2;
                int color = vertex * 4;
                interleaved[target] = positions[position];
                interleaved[target + 1] = positions[position + 1];
                interleaved[target + 2] = positions[position + 2];
                interleaved[target + UvOffset] = uv < uvs.Length ? uvs[uv] : 0f;
                interleaved[target + UvOffset + 1] = uv + 1 < uvs.Length ? uvs[uv + 1] : 0f;
                for (int channel = 0; channel < 4; channel++)
                {
                    interleaved[target + ColorOffset + channel] =
                        color + channel < colors.Length ? colors[color + channel] : 1f;
                }
                interleaved[target + NormalOffset] = normal < normals.Length ? normals[normal] : 0f;
                interleaved[target + NormalOffset + 1] = normal + 1 < normals.Length ? normals[normal + 1] : 0f;
                interleaved[target + NormalOffset + 2] = normal + 2 < normals.Length ? normals[normal + 2] : 1f;
                int skin = vertex * 4;
                for (int influence = 0; influence < 4; influence++)
                {
                    interleaved[target + BoneIndexOffset + influence] =
                        skin + influence < boneIndices.Length ? boneIndices[skin + influence] : 0f;
                    interleaved[target + BoneWeightOffset + influence] =
                        skin + influence < boneWeights.Length ? boneWeights[skin + influence] : 0f;
                }
            }

            return interleaved;
        }

        public void Dispose() => Clear();

        private void Assign(VfxPlaybackRuntime.EmitterState emitter, MeshGpuResource resource)
        {
            emitter.MeshVao = resource.Vao;
            emitter.MeshVbo = resource.Vbo;
            emitter.MeshEbo = resource.Ebo;
            emitter.MeshVertexCount = resource.VertexCount;
            emitter.MeshIndexCount = resource.IndexCount;
            emitter.MeshInterleaved = resource.Interleaved;
            emitter.MeshHasSkinning = resource.HasSkinning;
        }

        private void ConfigureAttribute(uint location, int componentCount, IntPtr offset)
        {
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(
                location,
                componentCount,
                VertexAttribPointerType.Float,
                false,
                VertexStride * sizeof(float),
                offset);
        }

        private sealed record MeshGpuResource(
            uint Vao,
            uint Vbo,
            uint Ebo,
            int VertexCount,
            int IndexCount,
            float[] Interleaved,
            bool HasSkinning);
    }

    internal sealed class VfxSceneCapture : IDisposable
    {
        private readonly GL _gl;
        private int _colorWidth;
        private int _colorHeight;
        private int _depthWidth;
        private int _depthHeight;

        internal VfxSceneCapture(GL gl)
        {
            _gl = gl;
        }

        internal uint ColorTexture { get; private set; }
        internal uint DepthTexture { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }

        internal void Capture(uint width, uint height, bool captureColor, bool captureDepth)
        {
            if (width == 0 || height == 0 || (!captureColor && !captureDepth))
                return;

            _gl.GetInteger(GLEnum.ActiveTexture, out int activeTexture);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.GetInteger(GLEnum.TextureBinding2D, out int textureBinding);

            if (captureColor)
            {
                bool created = ColorTexture == 0;
                if (created)
                {
                    ColorTexture = _gl.GenTexture();
                    _gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
                    SetTextureParameters(TextureMinFilter.Linear, TextureMagFilter.Linear);
                }
                else
                {
                    _gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
                }

                if (created || _colorWidth != (int)width || _colorHeight != (int)height)
                {
                    _gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.Rgba8,
                        width,
                        height,
                        0,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        ReadOnlySpan<byte>.Empty);
                }

                _gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, width, height);
                _colorWidth = (int)width;
                _colorHeight = (int)height;
            }

            if (captureDepth)
            {
                bool created = DepthTexture == 0;
                if (created)
                {
                    DepthTexture = _gl.GenTexture();
                    _gl.BindTexture(TextureTarget.Texture2D, DepthTexture);
                    SetTextureParameters(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
                }
                else
                {
                    _gl.BindTexture(TextureTarget.Texture2D, DepthTexture);
                }

                if (created || _depthWidth != (int)width || _depthHeight != (int)height)
                {
                    _gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.DepthComponent24,
                        width,
                        height,
                        0,
                        PixelFormat.DepthComponent,
                        PixelType.Float,
                        ReadOnlySpan<byte>.Empty);
                }

                _gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, width, height);
                _depthWidth = (int)width;
                _depthHeight = (int)height;
            }

            _gl.BindTexture(TextureTarget.Texture2D, (uint)textureBinding);
            _gl.ActiveTexture((TextureUnit)activeTexture);
            Width = (int)width;
            Height = (int)height;
        }

        public void Dispose()
        {
            if (ColorTexture != 0)
                _gl.DeleteTexture(ColorTexture);
            if (DepthTexture != 0)
                _gl.DeleteTexture(DepthTexture);

            ColorTexture = 0;
            DepthTexture = 0;
            Width = 0;
            Height = 0;
        }

        private void SetTextureParameters(TextureMinFilter minFilter, TextureMagFilter magFilter)
        {
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }
    }
}
