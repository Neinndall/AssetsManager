using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering;

public sealed partial class VfxOpenGlRenderer
{
    private uint _meshInstanceVbo;
    private float[] _meshRows = Array.Empty<float>();
    private Matrix4x4[] _meshBatchBones = Array.Empty<Matrix4x4>();

    private void RenderMeshInstances(VfxPlaybackRuntime.EmitterState emitter, ReadOnlySpan<float> instances,
        int count, Vector3 eye, Vector3 up, bool native, bool animated, bool attached)
    {
        int required = Math.Min(count, MeshesPerEmitter) * VfxMeshInstanceShaderSource.Stride;
        if (_meshRows.Length < required)
            Array.Resize(ref _meshRows, Math.Min(MeshesPerEmitter * VfxMeshInstanceShaderSource.Stride,
                Math.Max(required, Math.Max(VfxMeshInstanceShaderSource.Stride * 4, _meshRows.Length * 2))));
        if (animated && _meshBatchBones.Length == 0) _meshBatchBones = new Matrix4x4[GpuSkinningData.MaxBones];
        if (_meshInstanceVbo == 0) _meshInstanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshInstanceVbo);
        for (uint attribute = 0; attribute < 10; attribute++)
        {
            _gl.EnableVertexAttribArray(6 + attribute);
            _gl.VertexAttribPointer(6 + attribute, 4, VertexAttribPointerType.Float, false,
                VfxMeshInstanceShaderSource.Stride * sizeof(float), new IntPtr(attribute * 4 * sizeof(float)));
            _gl.VertexAttribDivisor(6 + attribute, 1);
        }

        int held = 0, bones = 0;
        for (int index = 0; index < count; index++)
        {
            var source = _activeSources != null ? _instanceOwners[index] : emitter;
            int particle = _activeSources != null ? _sourceParticles[index] : index;
            ReadOnlySpan<Matrix4x4> palette = animated && particle < source.Particles.Count
                ? source.MeshAnimation.EvaluatePalette(source.Particles[particle].Age) : default;
            // Each particle retains its own pose. Split only at the existing bone palette limit;
            // a shared emitter must never freeze all instances on its first particle's pose.
            if (held > 0 && (bones + palette.Length > _meshBatchBones.Length || held == MeshesPerEmitter)) Flush();
            Span<float> row = _meshRows.AsSpan(held * VfxMeshInstanceShaderSource.Stride, VfxMeshInstanceShaderSource.Stride);
            if (!WriteMeshInstance(source, instances.Slice(index * Stride, Stride), particle, eye, up, attached, row)) continue;
            row[38] = bones;
            palette.CopyTo(_meshBatchBones.AsSpan(bones));
            bones += palette.Length;
            held++;
        }
        if (held > 0) Flush();

        void Flush()
        {
            if (bones > 0) UploadMeshBonePalette(_meshBatchBones.AsSpan(0, bones));
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshInstanceVbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<float>(_meshRows, 0, held * VfxMeshInstanceShaderSource.Stride), BufferUsageARB.StreamDraw);
            if (emitter.MeshIndexCount > 0)
            {
                if (attached && emitter.MeshRanges is { Length: > 0 })
                {
                    bool narrowed = HasAttachedDrawMatch(emitter.MeshRanges, emitter.Def.SubmeshesToDraw);
                    foreach (var range in emitter.MeshRanges)
                        if (ShouldDrawAttachedRange(range.Hash, narrowed, emitter.Def.SubmeshesToDraw,
                                emitter.Def.SubmeshesToDrawAlways, _ownerHiddenSubmeshes))
                            DrawRange(range.IndexCount, range.StartIndex);
                }
                else DrawRange(emitter.MeshIndexCount, 0);
            }
            else
            {
                CountParticleDraw(held);
                if (native) _gameShaders.DrawBoundArrays(PrimitiveType.Triangles, emitter.MeshVertexCount, (uint)held);
                else _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, (uint)emitter.MeshVertexCount, (uint)held);
            }
            held = bones = 0;
        }

        unsafe void DrawRange(int indices, int first)
        {
            CountParticleDraw(held);
            if (native) _gameShaders.DrawBoundInstancedElements(indices, new IntPtr(first * sizeof(uint)), (uint)held);
            else _gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)indices, DrawElementsType.UnsignedInt,
                (void*)(first * sizeof(uint)), (uint)held);
        }
    }

    internal static bool WriteMeshInstance(VfxPlaybackRuntime.EmitterState source, ReadOnlySpan<float> input,
        int particle, Vector3 eye, Vector3 up, bool attached, Span<float> row)
    {
        row.Clear();
        var definition = source.Def;
        Vector3 position = new(input[0], input[1], input[2]);
        float ownerScale = attached && float.IsFinite(source.MeshOwnerScale) && source.MeshOwnerScale > 0f
            ? source.MeshOwnerScale : 1f;
        Vector3 scale = new(ClampScale(input[3]) * ownerScale, ClampScale(input[4]) * ownerScale, ClampScale(input[18]) * ownerScale);
        Vector3 right = new(input[36], input[37], input[38]);
        Vector3 lift = new(input[39], input[40], input[41]);
        Vector3 forward = new(input[42], input[43], input[44]);
        Vector4 tint = new(input[5], input[6], input[7], input[8]);
        if (definition.DrawsAsBeamMesh)
        {
            if (!VfxBeamGeometry.TryMeshPlacement(source, out position, out Matrix4x4 basis, out float stretch)) return false;
            right = new(basis.M11, basis.M12, basis.M13);
            lift = new(basis.M21, basis.M22, basis.M23);
            forward = new(basis.M31, basis.M32, basis.M33);
            scale.Z = stretch;
            tint *= VfxBeamGeometry.DistanceColor(source);
        }
        else if (!attached && !definition.IsDirectionOriented && (definition.MeshAlignPitchToCamera || definition.MeshAlignYawToCamera))
        {
            Vector3 facing = new(definition.MeshAlignYawToCamera ? eye.X - position.X : 0f,
                definition.MeshAlignPitchToCamera ? eye.Y - position.Y : 0f, eye.Z - position.Z);
            Vector3 aside = Vector3.Cross(up, facing);
            if (facing.LengthSquared() > 0f && aside.LengthSquared() > 0f)
            {
                facing = Vector3.Normalize(facing);
                aside = Vector3.Normalize(aside);
                Vector3 cameraLift = Vector3.Cross(facing, aside);
                if (!definition.MeshIsSkinned) { aside = -aside; facing = -facing; }
                Vector3 orbit = particle < source.Particles.Count
                    ? source.Particles[particle].BirthOrbitalVelocity * source.Particles[particle].Age : Vector3.Zero;
                Matrix4x4 orbital = Euler(orbit);
                aside = Vector3.TransformNormal(aside, orbital);
                cameraLift = Vector3.TransformNormal(cameraLift, orbital);
                facing = Vector3.TransformNormal(facing, orbital);
                Matrix4x4 carrier = new(aside.X, aside.Y, aside.Z, 0, cameraLift.X, cameraLift.Y, cameraLift.Z, 0,
                    facing.X, facing.Y, facing.Z, 0, 0, 0, 0, 1);
                Matrix4x4 standing = Euler(new Vector3(input[15], input[16], input[17])) * carrier;
                right = new(standing.M11, standing.M12, standing.M13);
                lift = new(standing.M21, standing.M22, standing.M23);
                forward = new(standing.M31, standing.M32, standing.M33);
            }
        }
        Write3(row, 0, position); Write3(row, 3, scale);
        row[6] = tint.X; row[7] = tint.Y; row[8] = tint.Z; row[9] = tint.W;
        row[10] = input[10];
        Write3(row, 11, new Vector3(input[11], new Vector3(input[12], input[13], input[14]).Length(), input[34]));
        Write3(row, 14, right); Write3(row, 17, lift); Write3(row, 20, forward);
        input.Slice(19, 5).CopyTo(row[23..]);
        input.Slice(29, 5).CopyTo(row[28..]);
        row[33] = input[24];
        input.Slice(25, 4).CopyTo(row[34..]);
        return true;

    }

    private static void Write3(Span<float> row, int at, Vector3 value)
    { row[at] = value.X; row[at + 1] = value.Y; row[at + 2] = value.Z; }

    private static Matrix4x4 Euler(Vector3 angles) =>
        Matrix4x4.CreateRotationZ(angles.Z) * Matrix4x4.CreateRotationX(angles.X) * Matrix4x4.CreateRotationY(angles.Y);
}
