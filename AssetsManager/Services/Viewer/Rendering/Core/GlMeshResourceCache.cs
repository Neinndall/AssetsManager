using System;
using System.Collections.Generic;
using System.Linq;
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
            internal string AuxiliaryTextureSignature;
            internal readonly Dictionary<string, uint> AuxiliaryTextures =
                new(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, BitmapSource> LoadedAuxiliaryBitmaps =
                new(StringComparer.OrdinalIgnoreCase);
            internal uint LightmapTexture;
            internal bool LightmapTextureResolved;
            internal string LoadedLightmapTextureKey;
            internal BitmapSource LoadedLightmapBitmap;
            internal uint LightmapVbo;
            internal uint ColorVbo;
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
        private readonly Dictionary<BitmapSource, SharedTexture> _sharedLightmapTextures =
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
            EnsureAuxiliaryTextures(part, resources);
            EnsureLightmapTexture(part, resources);
            resources = EnsureMeshBuffers(part, resources);
            EnsureSkinningBuffers(model, resources, part);
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

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resources.BoneIndexVbo);
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<float>(skinningData.BoneIndices),
                BufferUsageARB.StaticDraw);
            ConfigureVertexAttribute(5, 4, 4 * sizeof(float), IntPtr.Zero);

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

        private void ReleaseSkinningBuffers(PartResources resources)
        {
            DeleteHandle(resources.BoneIndexVbo, _gl.DeleteBuffer);
            DeleteHandle(resources.BoneWeightVbo, _gl.DeleteBuffer);
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
                    wrapMode: TextureWrapMode.ClampToEdge));
        }

        private void EnsureAuxiliaryTextures(ModelPart part, PartResources resources)
        {
            ModelMaterialEffectDefinition effect = ResolveMaterialEffect(part);
            string[] textureKeys = effect
                .EnumerateTextureNames()
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string signature = string.Join("\n", textureKeys);
            if (string.Equals(resources.AuxiliaryTextureSignature, signature, StringComparison.Ordinal))
                return;

            ReleaseAuxiliaryTextures(resources);
            resources.AuxiliaryTextureSignature = signature;
            foreach (string textureKey in textureKeys)
            {
                BitmapSource bitmap = TextureUtils.ResolveTexture(part.AllTextures, textureKey);
                if (bitmap == null)
                    continue;

                resources.LoadedAuxiliaryBitmaps[textureKey] = bitmap;
                resources.AuxiliaryTextures[textureKey] = AcquireTexture(
                    _sharedTextures,
                    bitmap,
                    () => UploadTexture(bitmap));
            }
        }

        private static ModelMaterialEffectDefinition ResolveMaterialEffect(ModelPart part)
        {
            // Specialized SKN layers are owned by the authored material definition.
            return part.MaterialDefinition?.Effect ?? ModelMaterialEffectDefinition.None;
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
            ReleaseAuxiliaryTextures(resources);
            ReleaseLightmapTexture(resources);
            DeleteHandle(resources.Vao, _gl.DeleteVertexArray);
            DeleteHandle(resources.Vbo, _gl.DeleteBuffer);
            DeleteHandle(resources.Ebo, _gl.DeleteBuffer);
            DeleteHandle(resources.LightmapVbo, _gl.DeleteBuffer);
            DeleteHandle(resources.ColorVbo, _gl.DeleteBuffer);
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

        private void ReleaseAuxiliaryTextures(PartResources resources)
        {
            foreach (BitmapSource bitmap in resources.LoadedAuxiliaryBitmaps.Values)
                ReleaseSharedTexture(_sharedTextures, bitmap);

            resources.AuxiliaryTextures.Clear();
            resources.LoadedAuxiliaryBitmaps.Clear();
            resources.AuxiliaryTextureSignature = null;
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

        public void Dispose()
        {
            try
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
                _sharedLightmapTextures.Clear();
                _liveResources.Clear();
                _pendingReleases.Clear();
            }
        }
    }
}
