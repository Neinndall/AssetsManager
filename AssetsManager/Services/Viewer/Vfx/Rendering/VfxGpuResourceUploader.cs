using System;
using System.Buffers;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>
    /// Uploads decoded VFX resources into one renderer and reuses GPU handles across every graph
    /// that shares the same decoded bitmap, cubemap or mesh arrays.
    /// </summary>
    internal sealed class VfxGpuResourceUploader
    {
        internal const int DefaultMaxNewUploadsPerFrame = 4;
        internal const long DefaultMaxUploadBytesPerFrame = 16L * 1024L * 1024L;

        private readonly Dictionary<BitmapSource, uint> _textures =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VfxCubeMapData, uint> _cubeMaps =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<(float[] Positions, bool Skinning)> _meshes = new();

        internal void UploadPendingResources(
            IEnumerable<VfxPlaybackGraphRuntime> graphs,
            VfxOpenGlRenderer renderer,
            int maxNewUploads = DefaultMaxNewUploadsPerFrame,
            long maxUploadBytes = DefaultMaxUploadBytesPerFrame)
        {
            ArgumentNullException.ThrowIfNull(renderer);
            if (graphs == null) return;

            var budget = new UploadBudget(
                Math.Max(1, maxNewUploads),
                Math.Max(1L, maxUploadBytes));
            foreach (VfxPlaybackGraphRuntime graph in graphs)
            {
                if (graph?.Runtimes == null) continue;
                foreach (VfxPlaybackRuntime runtime in graph.Runtimes)
                {
                    if (runtime?.Emitters == null) continue;
                    foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
                        UploadEmitter(emitter, renderer, ref budget);
                }
            }
        }

        internal void Clear() =>
            ClearHandlesOnly();

        private void UploadEmitter(
            VfxPlaybackRuntime.EmitterState emitter,
            VfxOpenGlRenderer renderer,
            ref UploadBudget budget)
        {
            if (emitter == null) return;

            if (emitter.PendingTexture is BitmapSource baseTexture)
            {
                emitter.TextureWidth = baseTexture.PixelWidth;
                emitter.TextureHeight = baseTexture.PixelHeight;
            }
            emitter.Texture = UploadTexture(ref emitter.PendingTexture, emitter.Texture, renderer, ref budget);

            if (emitter.PendingTextureMult is BitmapSource multiplierTexture)
            {
                emitter.TextureMultWidth = multiplierTexture.PixelWidth;
                emitter.TextureMultHeight = multiplierTexture.PixelHeight;
            }
            emitter.TextureMult = UploadTexture(ref emitter.PendingTextureMult, emitter.TextureMult, renderer, ref budget);
            emitter.DistortionTexture = UploadTexture(
                ref emitter.PendingDistortionTexture,
                emitter.DistortionTexture,
                renderer,
                ref budget);
            emitter.ErosionTexture = UploadTexture(
                ref emitter.PendingErosionTexture,
                emitter.ErosionTexture,
                renderer,
                ref budget);
            emitter.ReflectionTexture = UploadCubeMap(
                ref emitter.PendingReflectionTexture,
                emitter.ReflectionTexture,
                renderer,
                ref budget);
            emitter.ColorGradientTexture = UploadTexture(
                ref emitter.PendingColorGradient,
                emitter.ColorGradientTexture,
                renderer,
                ref budget);
            emitter.PaletteTexture = UploadTexture(
                ref emitter.PendingPaletteTexture,
                emitter.PaletteTexture,
                renderer,
                ref budget);

            if (emitter.PendingMesh is { } mesh)
            {
                bool skinning = mesh.BoneIndices is { Length: > 0 } && mesh.BoneWeights is { Length: > 0 };
                var meshKey = (mesh.Positions, skinning);
                bool cached = _meshes.Contains(meshKey);
                if (!cached && !budget.TryTake(EstimateMeshBytes(mesh)))
                    return;

                emitter.MeshOwnerScale = float.IsFinite(mesh.OwnerScale) && mesh.OwnerScale > 0f
                    ? mesh.OwnerScale
                    : 1f;
                emitter.MeshRanges = mesh.Ranges ?? Array.Empty<VfxMeshRangeData>();
                renderer.UploadEmitterMesh(
                    emitter,
                    mesh.Positions,
                    mesh.Normals,
                    mesh.Uvs,
                    mesh.Colors,
                    mesh.Indices,
                    mesh.BoneIndices,
                    mesh.BoneWeights);
                if (!cached)
                    _meshes.Add(meshKey);
                emitter.PendingMesh = null;
            }
        }

        private uint UploadTexture(
            ref object pending,
            uint currentTexture,
            VfxOpenGlRenderer renderer,
            ref UploadBudget budget)
        {
            if (pending is not BitmapSource bitmap)
                return currentTexture;

            if (!_textures.TryGetValue(bitmap, out uint texture))
            {
                if (!budget.TryTake(EstimateTextureBytes(bitmap)))
                    return currentTexture;
                texture = UploadBitmap(bitmap, renderer);
                _textures[bitmap] = texture;
            }
            pending = null;
            return texture;
        }

        private uint UploadCubeMap(
            ref object pending,
            uint currentTexture,
            VfxOpenGlRenderer renderer,
            ref UploadBudget budget)
        {
            if (pending is not VfxCubeMapData cube || !cube.IsValid)
                return currentTexture;

            if (!_cubeMaps.TryGetValue(cube, out uint texture))
            {
                long bytes = checked((long)cube.Width * cube.Height * 4L * 6L);
                if (!budget.TryTake(bytes))
                    return currentTexture;
                texture = renderer.UploadCubeMap(cube);
                _cubeMaps[cube] = texture;
            }
            pending = null;
            return texture;
        }

        private static uint UploadBitmap(BitmapSource source, VfxOpenGlRenderer renderer)
        {
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
            int byteCount = checked(stride * height);
            byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                bitmap.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                return renderer.UploadTexture(pixels.AsSpan(0, byteCount), width, height);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }

        private static long EstimateTextureBytes(BitmapSource bitmap) =>
            checked((long)bitmap.PixelWidth * bitmap.PixelHeight * 4L);

        private static long EstimateMeshBytes(VfxMeshData mesh)
        {
            long vertexCount = (mesh.Positions?.LongLength ?? 0L) / 3L;
            long vertexBytes = checked(vertexCount * VfxMeshResourceCache.VertexStride * sizeof(float));
            long indexBytes = checked((mesh.Indices?.LongLength ?? 0L) * sizeof(uint));
            return checked(vertexBytes + indexBytes);
        }

        private void ClearHandlesOnly()
        {
            _textures.Clear();
            _cubeMaps.Clear();
            _meshes.Clear();
        }

        internal struct UploadBudget
        {
            private readonly int _maxUploads;
            private readonly long _maxBytes;
            private int _uploads;
            private long _bytes;

            internal UploadBudget(int maxUploads, long maxBytes)
            {
                _maxUploads = maxUploads;
                _maxBytes = maxBytes;
                _uploads = 0;
                _bytes = 0;
            }

            internal bool TryTake(long bytes)
            {
                bytes = Math.Max(1L, bytes);
                if (_uploads >= _maxUploads)
                    return false;
                if (_uploads > 0 && _bytes + bytes > _maxBytes)
                    return false;

                _uploads++;
                _bytes = checked(_bytes + bytes);
                return true;
            }
        }
    }
}
