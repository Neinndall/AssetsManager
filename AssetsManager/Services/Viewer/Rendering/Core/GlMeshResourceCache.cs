using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Silk.NET.OpenGL;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Rendering.Core
{
    /// <summary>
    /// Owns GPU resources for scene mesh parts while leaving rendering policy to the caller.
    /// </summary>
    internal sealed class GlMeshResourceCache : IDisposable
    {
        internal sealed class PartResources
        {
            internal uint Vao;
            internal uint Vbo;
            internal uint Ebo;
            internal int IndexCount;
            internal uint Texture;
            internal bool TextureResolved;
            internal string LoadedTextureKey;
            internal BitmapSource LoadedBitmap;
            internal uint LightmapTexture;
            internal bool LightmapTextureResolved;
            internal string LoadedLightmapTextureKey;
            internal BitmapSource LoadedLightmapBitmap;
            internal uint LightmapVbo;
            internal uint ColorVbo;
            internal Point3DCollection UploadedPositions;
            internal int VertexCount;
            internal GlMeshVertexData VertexData;
        }

        private sealed class SharedTexture
        {
            internal uint Id;
            internal int ReferenceCount;
        }

        private readonly GL _gl;
        private readonly ConditionalWeakTable<ModelPart, PartResources> _partResources = new();
        private readonly HashSet<PartResources> _liveResources = new();
        private readonly Dictionary<BitmapSource, SharedTexture> _sharedTextures =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<BitmapSource, SharedTexture> _sharedLightmapTextures =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ModelPart> _pendingReleases = new();

        internal GlMeshResourceCache(GL gl)
        {
            _gl = gl;
            WhiteTexture = CreateWhiteTexture();
        }

        internal uint WhiteTexture { get; }

        internal PartResources Ensure(ModelPart part)
        {
            if (!_partResources.TryGetValue(part, out PartResources resources))
            {
                resources = new PartResources();
                _partResources.Add(part, resources);
                _liveResources.Add(resources);
            }

            EnsureBaseTexture(part, resources);
            EnsureLightmapTexture(part, resources);
            resources = EnsureMeshBuffers(part, resources);
            EnsureLightmapVertexBuffer(part, resources);
            EnsureVertexColorBuffer(part, resources);
            return resources;
        }

        internal void QueueRelease(SceneModel model)
        {
            if (model?.Parts == null) return;
            foreach (ModelPart part in model.Parts)
                _pendingReleases.Add(part);
        }

        internal void ProcessPendingReleases()
        {
            foreach (ModelPart part in _pendingReleases)
                ReleasePart(part);
            _pendingReleases.Clear();
        }

        private PartResources EnsureMeshBuffers(ModelPart part, PartResources resources)
        {
            if (resources.Vao == 0 && part.Geometry?.Geometry is MeshGeometry3D mesh)
            {
                Point3DCollection positions = mesh.Positions;
                Int32Collection indices = mesh.TriangleIndices;
                if (positions == null || indices == null) return resources;

                int vertexCount = positions.Count;
                var vertexData = new GlMeshVertexData(vertexCount);
                vertexData.Update(mesh, updateTextureCoordinates: true);
                resources.VertexCount = vertexCount;
                resources.VertexData = part.SourceVertexIndices != null ? vertexData : null;
                resources.UploadedPositions = positions;

                uint[] indexData = new uint[indices.Count];
                for (int i = 0; i < indices.Count; i++)
                    indexData[i] = (uint)indices[i];

                resources.Vao = _gl.GenVertexArray();
                resources.Vbo = _gl.GenBuffer();
                resources.Ebo = _gl.GenBuffer();
                _gl.BindVertexArray(resources.Vao);

                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resources.Vbo);
                _gl.BufferData(
                    BufferTargetARB.ArrayBuffer,
                    new ReadOnlySpan<float>(vertexData.Data),
                    resources.VertexData != null ? BufferUsageARB.DynamicDraw : BufferUsageARB.StaticDraw);

                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, resources.Ebo);
                _gl.BufferData(
                    BufferTargetARB.ElementArrayBuffer,
                    new ReadOnlySpan<uint>(indexData),
                    BufferUsageARB.StaticDraw);

                const uint stride = 8 * sizeof(float);
                ConfigureVertexAttribute(0, 3, stride, IntPtr.Zero);
                ConfigureVertexAttribute(1, 3, stride, new IntPtr(3 * sizeof(float)));
                ConfigureVertexAttribute(2, 2, stride, new IntPtr(6 * sizeof(float)));

                _gl.BindVertexArray(0);
                resources.IndexCount = indices.Count;
                return resources;
            }

            if (resources.Vao == 0 ||
                resources.VertexData == null ||
                part.Geometry?.Geometry is not MeshGeometry3D animatedMesh ||
                ReferenceEquals(resources.UploadedPositions, animatedMesh.Positions))
            {
                return resources;
            }

            Point3DCollection animatedPositions = animatedMesh.Positions;
            if (animatedPositions == null) return resources;
            if (animatedPositions.Count != resources.VertexData.VertexCount)
            {
                ReleasePart(part);
                return Ensure(part);
            }

            resources.VertexData.Update(animatedMesh, updateTextureCoordinates: false);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resources.Vbo);
            _gl.BufferSubData(
                BufferTargetARB.ArrayBuffer,
                0,
                new ReadOnlySpan<float>(resources.VertexData.Data));
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            resources.UploadedPositions = animatedPositions;
            return resources;
        }

        private void ConfigureVertexAttribute(uint location, int componentCount, uint stride, IntPtr offset)
        {
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(
                location,
                componentCount,
                VertexAttribPointerType.Float,
                false,
                stride,
                offset);
        }

        private void EnsureVertexColorBuffer(ModelPart part, PartResources resources)
        {
            byte[] colors = part.VertexColors;
            if (resources.Vao == 0 || resources.ColorVbo != 0 ||
                colors == null || colors.Length != resources.VertexCount * 4)
            {
                return;
            }

            resources.ColorVbo = _gl.GenBuffer();
            _gl.BindVertexArray(resources.Vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resources.ColorVbo);
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<byte>(colors),
                BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(4);
            _gl.VertexAttribPointer(
                4,
                4,
                VertexAttribPointerType.UnsignedByte,
                true,
                4,
                IntPtr.Zero);
            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }

        private void EnsureLightmapVertexBuffer(ModelPart part, PartResources resources)
        {
            float[] coordinates = part.Lightmap?.UvCoordinates;
            if (resources.Vao == 0 || resources.LightmapVbo != 0 ||
                coordinates == null || coordinates.Length != resources.VertexCount * 2)
            {
                return;
            }

            resources.LightmapVbo = _gl.GenBuffer();
            _gl.BindVertexArray(resources.Vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resources.LightmapVbo);
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<float>(coordinates),
                BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(3);
            _gl.VertexAttribPointer(
                3,
                2,
                VertexAttribPointerType.Float,
                false,
                2 * sizeof(float),
                IntPtr.Zero);
            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }

        private void EnsureBaseTexture(ModelPart part, PartResources resources)
        {
            string selectedTexture = part.SelectedTextureName;
            if (resources.TextureResolved && resources.LoadedTextureKey == selectedTexture)
                return;

            ReleaseBaseTexture(resources);
            resources.TextureResolved = true;
            resources.LoadedTextureKey = selectedTexture;
            resources.LoadedBitmap = TextureUtils.ResolveTexture(part.AllTextures, selectedTexture);
            if (resources.LoadedBitmap == null) return;

            resources.Texture = AcquireTexture(
                _sharedTextures,
                resources.LoadedBitmap,
                () => UploadTexture(resources.LoadedBitmap));
        }

        private void EnsureLightmapTexture(ModelPart part, PartResources resources)
        {
            string selectedTexture = part.Lightmap?.TextureKey;
            if (resources.LightmapTextureResolved && resources.LoadedLightmapTextureKey == selectedTexture)
                return;

            ReleaseLightmapTexture(resources);
            resources.LightmapTextureResolved = true;
            resources.LoadedLightmapTextureKey = selectedTexture;
            resources.LoadedLightmapBitmap = TextureUtils.ResolveTexture(part.AllTextures, selectedTexture);
            if (resources.LoadedLightmapBitmap == null) return;

            resources.LightmapTexture = AcquireTexture(
                _sharedLightmapTextures,
                resources.LoadedLightmapBitmap,
                () => UploadTexture(
                    resources.LoadedLightmapBitmap,
                    premultiplyAlpha: false,
                    wrapMode: TextureWrapMode.ClampToEdge));
        }

        private static uint AcquireTexture(
            Dictionary<BitmapSource, SharedTexture> textures,
            BitmapSource bitmap,
            Func<uint> upload)
        {
            if (!textures.TryGetValue(bitmap, out SharedTexture sharedTexture))
            {
                sharedTexture = new SharedTexture { Id = upload() };
                textures.Add(bitmap, sharedTexture);
            }

            sharedTexture.ReferenceCount++;
            return sharedTexture.Id;
        }

        private void ReleasePart(ModelPart part)
        {
            if (!_partResources.TryGetValue(part, out PartResources resources)) return;

            ReleaseBaseTexture(resources);
            ReleaseLightmapTexture(resources);
            DeleteHandle(resources.Vao, _gl.DeleteVertexArray);
            DeleteHandle(resources.Vbo, _gl.DeleteBuffer);
            DeleteHandle(resources.Ebo, _gl.DeleteBuffer);
            DeleteHandle(resources.LightmapVbo, _gl.DeleteBuffer);
            DeleteHandle(resources.ColorVbo, _gl.DeleteBuffer);
            _liveResources.Remove(resources);
            _partResources.Remove(part);
        }

        private static void DeleteHandle(uint handle, Action<uint> delete)
        {
            if (handle != 0) delete(handle);
        }

        private void ReleaseBaseTexture(PartResources resources)
        {
            ReleaseSharedTexture(_sharedTextures, resources.LoadedBitmap);
            resources.Texture = 0;
            resources.LoadedBitmap = null;
        }

        private void ReleaseLightmapTexture(PartResources resources)
        {
            ReleaseSharedTexture(_sharedLightmapTextures, resources.LoadedLightmapBitmap);
            resources.LightmapTexture = 0;
            resources.LoadedLightmapBitmap = null;
        }

        private void ReleaseSharedTexture(
            Dictionary<BitmapSource, SharedTexture> textures,
            BitmapSource bitmap)
        {
            if (bitmap == null || !textures.TryGetValue(bitmap, out SharedTexture sharedTexture))
                return;

            sharedTexture.ReferenceCount--;
            if (sharedTexture.ReferenceCount > 0) return;

            _gl.DeleteTexture(sharedTexture.Id);
            textures.Remove(bitmap);
        }

        private uint CreateWhiteTexture()
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            byte[] white = { 255, 255, 255, 255 };
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                0,
                Silk.NET.OpenGL.PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(white));
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        private uint UploadTexture(
            BitmapSource bitmap,
            bool premultiplyAlpha = true,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat)
        {
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = bitmap;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                bitmap = converted;
            }

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            bitmap.CopyPixels(pixels, stride, 0);
            if (premultiplyAlpha)
                PremultiplyBgra(pixels);

            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                (uint)width,
                (uint)height,
                0,
                Silk.NET.OpenGL.PixelFormat.Bgra,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixels));
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrapMode);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrapMode);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        internal static void PremultiplyBgra(Span<byte> pixels)
        {
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];
                pixels[i] = (byte)((pixels[i] * alpha + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 127) / 255);
            }
        }

        public void Dispose()
        {
            foreach (SharedTexture texture in _sharedTextures.Values)
                _gl.DeleteTexture(texture.Id);
            _sharedTextures.Clear();

            foreach (SharedTexture texture in _sharedLightmapTextures.Values)
                _gl.DeleteTexture(texture.Id);
            _sharedLightmapTextures.Clear();

            foreach (PartResources resources in _liveResources)
            {
                DeleteHandle(resources.Vao, _gl.DeleteVertexArray);
                DeleteHandle(resources.Vbo, _gl.DeleteBuffer);
                DeleteHandle(resources.Ebo, _gl.DeleteBuffer);
                DeleteHandle(resources.LightmapVbo, _gl.DeleteBuffer);
                DeleteHandle(resources.ColorVbo, _gl.DeleteBuffer);
            }
            _liveResources.Clear();
            _pendingReleases.Clear();

            if (WhiteTexture != 0)
                _gl.DeleteTexture(WhiteTexture);
        }
    }
}
