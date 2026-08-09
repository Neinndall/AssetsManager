using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    internal sealed class VfxMeshResourceCache : IDisposable
    {
        internal const int VertexStride = 9;

        private const int UvOffset = 3;
        private const int ColorOffset = 5;

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
            float[] uvs,
            float[] colors,
            uint[] indices)
        {
            if (_meshes.TryGetValue(positions, out MeshGpuResource cached))
            {
                Assign(emitter, cached);
                return;
            }

            int vertexCount = positions.Length / 3;
            float[] interleaved = BuildInterleaved(positions, uvs, colors);
            uint vao = _gl.GenVertexArray();
            uint vbo = _gl.GenBuffer();
            _gl.BindVertexArray(vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(interleaved), BufferUsageARB.DynamicDraw);

            ConfigureAttribute(0, 3, IntPtr.Zero);
            ConfigureAttribute(1, 2, new IntPtr(UvOffset * sizeof(float)));
            ConfigureAttribute(2, 4, new IntPtr(ColorOffset * sizeof(float)));

            uint ebo = 0;
            if (indices is { Length: > 0 })
            {
                ebo = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, new ReadOnlySpan<uint>(indices), BufferUsageARB.StaticDraw);
            }

            _gl.BindVertexArray(0);
            MeshGpuResource resource = new(vao, vbo, ebo, vertexCount, indices?.Length ?? 0, interleaved);
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

        internal static float[] BuildInterleaved(float[] positions, float[] uvs, float[] colors)
        {
            ArgumentNullException.ThrowIfNull(positions);
            uvs ??= Array.Empty<float>();
            colors ??= Array.Empty<float>();

            int vertexCount = positions.Length / 3;
            float[] interleaved = new float[vertexCount * VertexStride];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                int target = vertex * VertexStride;
                int position = vertex * 3;
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
            float[] Interleaved);
    }
}
