using System;
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
        private readonly Dictionary<BitmapSource, uint> _textures =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VfxCubeMapData, uint> _cubeMaps =
            new(ReferenceEqualityComparer.Instance);

        internal void UploadPendingResources(
            IEnumerable<VfxPlaybackGraphRuntime> graphs,
            VfxOpenGlRenderer renderer)
        {
            ArgumentNullException.ThrowIfNull(renderer);
            if (graphs == null) return;

            foreach (VfxPlaybackGraphRuntime graph in graphs)
            {
                if (graph?.Runtimes == null) continue;
                foreach (VfxPlaybackRuntime runtime in graph.Runtimes)
                {
                    if (runtime?.Emitters == null) continue;
                    foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
                        UploadEmitter(emitter, renderer);
                }
            }
        }

        internal void Clear() =>
            ClearHandlesOnly();

        private void UploadEmitter(
            VfxPlaybackRuntime.EmitterState emitter,
            VfxOpenGlRenderer renderer)
        {
            if (emitter == null) return;

            if (emitter.PendingTexture is BitmapSource baseTexture)
            {
                emitter.TextureWidth = baseTexture.PixelWidth;
                emitter.TextureHeight = baseTexture.PixelHeight;
            }
            emitter.Texture = UploadTexture(ref emitter.PendingTexture, emitter.Texture, renderer);

            if (emitter.PendingTextureMult is BitmapSource multiplierTexture)
            {
                emitter.TextureMultWidth = multiplierTexture.PixelWidth;
                emitter.TextureMultHeight = multiplierTexture.PixelHeight;
            }
            emitter.TextureMult = UploadTexture(ref emitter.PendingTextureMult, emitter.TextureMult, renderer);
            emitter.DistortionTexture = UploadTexture(
                ref emitter.PendingDistortionTexture,
                emitter.DistortionTexture,
                renderer);
            emitter.ErosionTexture = UploadTexture(
                ref emitter.PendingErosionTexture,
                emitter.ErosionTexture,
                renderer);
            emitter.ReflectionTexture = UploadCubeMap(
                ref emitter.PendingReflectionTexture,
                emitter.ReflectionTexture,
                renderer);
            emitter.ColorGradientTexture = UploadTexture(
                ref emitter.PendingColorGradient,
                emitter.ColorGradientTexture,
                renderer);
            emitter.PaletteTexture = UploadTexture(
                ref emitter.PendingPaletteTexture,
                emitter.PaletteTexture,
                renderer);

            if (emitter.PendingMesh is { } mesh)
            {
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
                emitter.PendingMesh = null;
            }
        }

        private uint UploadTexture(
            ref object pending,
            uint currentTexture,
            VfxOpenGlRenderer renderer)
        {
            if (pending is not BitmapSource bitmap)
                return currentTexture;

            if (!_textures.TryGetValue(bitmap, out uint texture))
            {
                texture = UploadBitmap(bitmap, renderer);
                _textures[bitmap] = texture;
            }
            pending = null;
            return texture;
        }

        private uint UploadCubeMap(
            ref object pending,
            uint currentTexture,
            VfxOpenGlRenderer renderer)
        {
            if (pending is not VfxCubeMapData cube || !cube.IsValid)
                return currentTexture;

            if (!_cubeMaps.TryGetValue(cube, out uint texture))
            {
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
            byte[] pixels = new byte[checked(stride * height)];
            bitmap.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
            return renderer.UploadTexture(pixels, width, height);
        }

        private void ClearHandlesOnly()
        {
            _textures.Clear();
            _cubeMaps.Clear();
        }
    }
}
