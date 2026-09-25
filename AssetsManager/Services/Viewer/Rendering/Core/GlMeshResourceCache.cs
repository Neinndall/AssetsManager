using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Silk.NET.OpenGL;
using AssetsManager.Services.Viewer.Resolvers;
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
            internal bool LoadedBitmapSrgb;
            internal readonly Dictionary<string, uint> ProgramTextures =
                new(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, string> ProgramTextureKeyByPath =
                new(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, BitmapSource> LoadedProgramBitmaps =
                new(StringComparer.OrdinalIgnoreCase);
            internal uint TangentVbo;
            internal uint BoneIndexVbo;
            internal uint BoneWeightVbo;
            internal GpuSkinningData.PartData SkinningData;
            internal bool IsGpuSkinned;
            internal int VertexCount;
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
        private readonly Dictionary<BitmapSource, SharedTexture> _sharedSrgbTextures =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ModelPart> _pendingReleases = new();

        internal GlMeshResourceCache(GL gl)
        {
            _gl = gl;
            WhiteTexture = CreateWhiteTexture();
            BlackTexture = CreateBlackTexture();
        }

        internal uint WhiteTexture { get; }
        internal uint BlackTexture { get; }

        internal PartResources Ensure(SceneModel model, ModelPart part)
        {
            if (!_partResources.TryGetValue(part, out PartResources resources))
            {
                resources = new PartResources();
                _partResources.Add(part, resources);
                _liveResources.Add(resources);
            }

            EnsureBaseTexture(part, resources);
            resources = EnsureMeshBuffers(part, resources);
            EnsureSkinningBuffers(model, resources, part);
            return resources;
        }

        internal uint? ResolveProgramTexture(ModelPart part, PartResources resources, string authoredPath)
        {
            if (part?.AllTextures == null || resources == null || string.IsNullOrWhiteSpace(authoredPath))
                return null;

            if (!resources.ProgramTextureKeyByPath.TryGetValue(authoredPath, out string key))
            {
                key = SknMaterialTextureResolver.MatchTextureKey(
                    authoredPath,
                    part.AllTextures.Keys.ToArray());
                resources.ProgramTextureKeyByPath[authoredPath] = key;
            }
            if (string.IsNullOrWhiteSpace(key))
                return null;
            if (resources.ProgramTextures.TryGetValue(key, out uint cached))
                return cached;

            BitmapSource bitmap = TextureUtils.ResolveTexture(part.AllTextures, key);
            if (bitmap == null)
                return null;
            uint texture = AcquireTexture(_sharedTextures, bitmap, () => UploadTexture(bitmap));
            resources.LoadedProgramBitmaps[key] = bitmap;
            resources.ProgramTextures[key] = texture;
            return texture;
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
                    BufferUsageARB.StaticDraw);

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
            }

            return resources;
        }

        private void EnsureSkinningBuffers(SceneModel model, PartResources resources, ModelPart part)
        {
            if (resources.Vao == 0)
                return;

            GpuSkinningData.PartData skinningData = null;
            model?.GpuSkinningData?.TryGetPart(part, out skinningData);

            if (ReferenceEquals(resources.SkinningData, skinningData) &&
                (skinningData == null || resources.IsGpuSkinned))
            {
                return;
            }

            ReleaseSkinningBuffers(resources);
            resources.SkinningData = skinningData;
            if (skinningData == null || skinningData.VertexCount != resources.VertexCount)
                return;

            resources.BoneIndexVbo = _gl.GenBuffer();
            resources.BoneWeightVbo = _gl.GenBuffer();

            _gl.BindVertexArray(resources.Vao);
            System.Numerics.Vector4[] tangents = skinningData.Tangents ?? BuildRuntimeTangents(part);
            if (tangents != null && tangents.Length == resources.VertexCount)
            {
                resources.TangentVbo = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resources.TangentVbo);
                _gl.BufferData(
                    BufferTargetARB.ArrayBuffer,
                    new ReadOnlySpan<System.Numerics.Vector4>(tangents),
                    BufferUsageARB.StaticDraw);
                ConfigureVertexAttribute(3, 4, 4 * sizeof(float), IntPtr.Zero);
            }

            ushort[] boneIndices = skinningData.BoneIndices
                .Select(value => checked((ushort)Math.Clamp((int)MathF.Round(value), 0, ushort.MaxValue)))
                .ToArray();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resources.BoneIndexVbo);
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<ushort>(boneIndices),
                BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(5);
            _gl.VertexAttribPointer(
                5,
                4,
                VertexAttribPointerType.UnsignedShort,
                false,
                4 * sizeof(ushort),
                IntPtr.Zero);

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resources.BoneWeightVbo);
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<float>(skinningData.BoneWeights),
                BufferUsageARB.StaticDraw);
            ConfigureVertexAttribute(6, 4, 4 * sizeof(float), IntPtr.Zero);

            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            resources.IsGpuSkinned = true;
        }

        private static System.Numerics.Vector4[] BuildRuntimeTangents(ModelPart part)
        {
            if (part?.Geometry?.Geometry is not MeshGeometry3D mesh ||
                mesh.Positions == null || mesh.TextureCoordinates == null ||
                mesh.Positions.Count == 0 || mesh.TextureCoordinates.Count != mesh.Positions.Count ||
                mesh.TriangleIndices == null || mesh.TriangleIndices.Count == 0)
            {
                return null;
            }

            System.Numerics.Vector3[] positions = mesh.Positions
                .Select(point => new System.Numerics.Vector3((float)point.X, (float)point.Y, (float)point.Z))
                .ToArray();
            System.Numerics.Vector3[] normals = mesh.Normals != null && mesh.Normals.Count == mesh.Positions.Count
                ? mesh.Normals.Select(normal => new System.Numerics.Vector3((float)normal.X, (float)normal.Y, (float)normal.Z)).ToArray()
                : null;
            System.Numerics.Vector2[] uv = mesh.TextureCoordinates
                .Select(point => new System.Numerics.Vector2((float)point.X, (float)point.Y))
                .ToArray();
            uint[] indices = mesh.TriangleIndices
                .Where(index => index >= 0)
                .Select(index => checked((uint)index))
                .ToArray();
            return MeshTangentBuilder.Build(positions, normals, uv, indices);
        }

        private void ReleaseSkinningBuffers(PartResources resources)
        {
            DeleteHandle(resources.TangentVbo, _gl.DeleteBuffer);
            DeleteHandle(resources.BoneIndexVbo, _gl.DeleteBuffer);
            DeleteHandle(resources.BoneWeightVbo, _gl.DeleteBuffer);
            resources.TangentVbo = 0;
            resources.BoneIndexVbo = 0;
            resources.BoneWeightVbo = 0;
            resources.SkinningData = null;
            resources.IsGpuSkinned = false;
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

        private void EnsureBaseTexture(ModelPart part, PartResources resources)
        {
            string selectedTexture = part.SelectedTextureName;
            bool srgb = part.UsesSrgbBaseTexture;
            if (resources.TextureResolved &&
                resources.LoadedTextureKey == selectedTexture &&
                resources.LoadedBitmapSrgb == srgb)
            {
                return;
            }

            ReleaseBaseTexture(resources);
            resources.TextureResolved = true;
            resources.LoadedTextureKey = selectedTexture;
            resources.LoadedBitmapSrgb = srgb;
            resources.LoadedBitmap = TextureUtils.ResolveTexture(part.AllTextures, selectedTexture);
            if (resources.LoadedBitmap == null) return;

            Dictionary<BitmapSource, SharedTexture> cache = srgb ? _sharedSrgbTextures : _sharedTextures;
            resources.Texture = AcquireTexture(
                cache,
                resources.LoadedBitmap,
                () => UploadTexture(
                    resources.LoadedBitmap,
                    internalFormat: BaseTextureInternalFormat(srgb)));
        }

        internal static InternalFormat BaseTextureInternalFormat(bool srgb) =>
            srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8;

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
            ReleaseProgramTextures(resources);
            DeleteHandle(resources.Vao, _gl.DeleteVertexArray);
            DeleteHandle(resources.Vbo, _gl.DeleteBuffer);
            DeleteHandle(resources.Ebo, _gl.DeleteBuffer);
            DeleteHandle(resources.BoneIndexVbo, _gl.DeleteBuffer);
            DeleteHandle(resources.BoneWeightVbo, _gl.DeleteBuffer);
            _liveResources.Remove(resources);
            _partResources.Remove(part);
        }

        private static void DeleteHandle(uint handle, Action<uint> delete)
        {
            if (handle != 0) delete(handle);
        }

        private void ReleaseBaseTexture(PartResources resources)
        {
            Dictionary<BitmapSource, SharedTexture> cache =
                resources.LoadedBitmapSrgb ? _sharedSrgbTextures : _sharedTextures;
            ReleaseSharedTexture(cache, resources.LoadedBitmap);
            resources.Texture = 0;
            resources.LoadedBitmap = null;
            resources.LoadedBitmapSrgb = false;
        }

        private void ReleaseProgramTextures(PartResources resources)
        {
            foreach (BitmapSource bitmap in resources.LoadedProgramBitmaps.Values)
                ReleaseSharedTexture(_sharedTextures, bitmap);
            resources.ProgramTextures.Clear();
            resources.ProgramTextureKeyByPath.Clear();
            resources.LoadedProgramBitmaps.Clear();
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

        private uint CreateBlackTexture()
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            byte[] black = { 0, 0, 0, 255 };
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                0,
                Silk.NET.OpenGL.PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(black));
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        private uint UploadTexture(
            BitmapSource bitmap,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat,
            InternalFormat internalFormat = InternalFormat.Rgba8)
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

            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                internalFormat,
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

        public void Dispose()
        {
            try
            {
                foreach (SharedTexture texture in _sharedTextures.Values)
                    _gl.DeleteTexture(texture.Id);
                _sharedTextures.Clear();

                foreach (SharedTexture texture in _sharedSrgbTextures.Values)
                    _gl.DeleteTexture(texture.Id);
                _sharedSrgbTextures.Clear();

                foreach (PartResources resources in _liveResources)
                {
                    DeleteHandle(resources.Vao, _gl.DeleteVertexArray);
                    DeleteHandle(resources.Vbo, _gl.DeleteBuffer);
                    DeleteHandle(resources.Ebo, _gl.DeleteBuffer);
                    DeleteHandle(resources.BoneIndexVbo, _gl.DeleteBuffer);
                    DeleteHandle(resources.BoneWeightVbo, _gl.DeleteBuffer);
                }
                _liveResources.Clear();
                _pendingReleases.Clear();

                if (WhiteTexture != 0)
                    _gl.DeleteTexture(WhiteTexture);
                if (BlackTexture != 0)
                    _gl.DeleteTexture(BlackTexture);
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException)
            {
                // The OpenGL context owns these handles and reclaims them on teardown.
            }
            catch (Exception)
            {
            }
            finally
            {
                _sharedTextures.Clear();
                _sharedSrgbTextures.Clear();
                _liveResources.Clear();
                _pendingReleases.Clear();
            }
        }
    }
}
