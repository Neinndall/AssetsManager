using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Draws simulated VFX particles as camera-facing, textured billboards using hardware instancing.
    /// Re-implemented in Safe C# using Spans instead of unsafe pointer arithmetic.
    /// </summary>
    public sealed class VfxParticleRenderer : IDisposable
    {
        private GL _gl = null!;
        private uint _program, _vao, _quadVbo, _instVbo;
        private int _uViewProj, _uCamRight, _uCamUp, _uTexDiv, _uTex, _uUvScrollRate;
        private int _uTexMult, _uHasTexMult, _uTexDivMult, _uUvScrollRateMult;
        private int _uIsDistortion, _uDistortionTex, _uSceneTex, _uViewportSize, _uDistortionStrength;
        private int _uDirectionOriented, _uArbitraryQuad;
        private int _uPlacementRight, _uPlacementUp, _uPlacementForward;
        private int _instCapFloats;
        private bool _ready;
        private readonly List<uint> _ownedTextures = new();
        private uint _sceneTexture;
        private int _sceneWidth, _sceneHeight;

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);
        private DrawElementsDelegate _drawElements = null!;

        private const int Stride = 18;
        private bool _gles;

        public void Initialize(GL gl)
        {
            _gl = gl;
            var proc = gl.Context.GetProcAddress("glDrawElements");
            if (proc != IntPtr.Zero)
            {
                _drawElements = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DrawElementsDelegate>(proc);
            }
            bool gles = ShaderUtil.DetectGles(gl);
            _gles = gles;
            _program = ShaderUtil.CreateProgram(gl, gles, Vert, Frag);
            _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
            _uCamRight = gl.GetUniformLocation(_program, "uCamRight");
            _uCamUp = gl.GetUniformLocation(_program, "uCamUp");
            _uTexDiv = gl.GetUniformLocation(_program, "uTexDiv");
            _uTex = gl.GetUniformLocation(_program, "uTex");
            _uTexMult = gl.GetUniformLocation(_program, "uTexMult");
            _uHasTexMult = gl.GetUniformLocation(_program, "uHasTexMult");
            _uTexDivMult = gl.GetUniformLocation(_program, "uTexDivMult");
            _uUvScrollRateMult = gl.GetUniformLocation(_program, "uUvScrollRateMult");
            _uUvScrollRate = gl.GetUniformLocation(_program, "uUvScrollRate");
            _uIsDistortion = gl.GetUniformLocation(_program, "uIsDistortion");
            _uDistortionTex = gl.GetUniformLocation(_program, "uDistortionTex");
            _uSceneTex = gl.GetUniformLocation(_program, "uSceneTex");
            _uViewportSize = gl.GetUniformLocation(_program, "uViewportSize");
            _uDistortionStrength = gl.GetUniformLocation(_program, "uDistortionStrength");
            _uDirectionOriented = gl.GetUniformLocation(_program, "uDirectionOriented");
            _uArbitraryQuad = gl.GetUniformLocation(_program, "uArbitraryQuad");
            _uPlacementRight = gl.GetUniformLocation(_program, "uPlacementRight");
            _uPlacementUp = gl.GetUniformLocation(_program, "uPlacementUp");
            _uPlacementForward = gl.GetUniformLocation(_program, "uPlacementForward");

            _vao = gl.GenVertexArray();
            gl.BindVertexArray(_vao);

            // static base quad (4 corners, drawn as a triangle fan)
            float[] quad = { -0.5f, -0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f };
            _quadVbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(quad), BufferUsageARB.StaticDraw);
            
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), IntPtr.Zero);

            // per-instance buffer (filled per emitter each frame)
            _instVbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instVbo);
            uint bstride = Stride * sizeof(float);
            
            gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, bstride, new IntPtr(0));
            gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(3 * sizeof(float)));
            gl.EnableVertexAttribArray(3); gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, bstride, new IntPtr(5 * sizeof(float)));
            gl.EnableVertexAttribArray(4); gl.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(9 * sizeof(float)));
            gl.EnableVertexAttribArray(5); gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, bstride, new IntPtr(11 * sizeof(float)));
            gl.EnableVertexAttribArray(6); gl.VertexAttribPointer(6, 3, VertexAttribPointerType.Float, false, bstride, new IntPtr(15 * sizeof(float)));
            
            gl.VertexAttribDivisor(1, 1);
            gl.VertexAttribDivisor(2, 1);
            gl.VertexAttribDivisor(3, 1);
            gl.VertexAttribDivisor(4, 1);
            gl.VertexAttribDivisor(5, 1);
            gl.VertexAttribDivisor(6, 1);

            gl.BindVertexArray(0);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _ready = true;
        }

        public uint UploadTexture(byte[] rgba, int width, int height)
        {
            uint tex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, tex);
            
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Bgra, PixelType.UnsignedByte, new ReadOnlySpan<byte>(rgba));
            
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _ownedTextures.Add(tex);
            return tex;
        }

        public void CaptureScene(uint width, uint height)
        {
            if (!_ready || width == 0 || height == 0) return;
            if (_sceneTexture == 0)
            {
                _sceneTexture = _gl.GenTexture();
                _gl.BindTexture(TextureTarget.Texture2D, _sceneTexture);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            }
            else _gl.BindTexture(TextureTarget.Texture2D, _sceneTexture);

            if (_sceneWidth != (int)width || _sceneHeight != (int)height)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
                _sceneWidth = (int)width;
                _sceneHeight = (int)height;
            }
            _gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, width, height);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        public void Render(VfxParticleSimulator sim, Matrix4x4 viewProj, Matrix4x4 view)
        {
            if (!_ready || sim.LiveParticleCount == 0) return;

            Matrix4x4.Invert(view, out var inv);
            var camRight = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, inv));
            var camUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inv));

            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProj, 1, false, in viewProj.M11);
            _gl.Uniform3(_uCamRight, camRight.X, camRight.Y, camRight.Z);
            _gl.Uniform3(_uCamUp, camUp.X, camUp.Y, camUp.Z);
            _gl.Uniform1(_uTex, 0);
            _gl.Uniform1(_uTexMult, 1);
            _gl.Uniform1(_uSceneTex, 2);
            _gl.Uniform1(_uDistortionTex, 3);
            _gl.Uniform2(_uViewportSize, (float)_sceneWidth, (float)_sceneHeight);

            _gl.BindVertexArray(_vao);
            _gl.ActiveTexture(TextureUnit.Texture0);

            bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquation(GLEnum.FuncAdd);

            foreach (var es in sim.Emitters)
            {
                if (es.InstanceCount == 0) continue;
                if (es.MeshVao != 0) { RenderMeshEmitter(es, viewProj); continue; }
                if (es.Texture == 0) continue;
                bool isDistortion = es.Def.Distortion is not null;
                if (isDistortion && (es.DistortionTexture == 0 || _sceneTexture == 0)) continue;

                int floats = es.InstanceCount * Stride;
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instVbo);
                
                var instancesSpan = new ReadOnlySpan<float>(es.Instances, 0, floats);
                if (floats > _instCapFloats)
                {
                    _gl.BufferData(BufferTargetARB.ArrayBuffer, instancesSpan, BufferUsageARB.DynamicDraw);
                    _instCapFloats = floats;
                }
                else
                {
                    _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, instancesSpan);
                }

                if (isDistortion) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                else if (IsAdditive(es.Def.BlendMode)) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

                _gl.Uniform2(_uTexDiv, es.Def.TexDiv.X <= 0 ? 1f : es.Def.TexDiv.X, es.Def.TexDiv.Y <= 0 ? 1f : es.Def.TexDiv.Y);
                _gl.Uniform2(_uUvScrollRate, es.Def.UvScrollRate.X, es.Def.UvScrollRate.Y);
                _gl.Uniform1(_uHasTexMult, es.TextureMult != 0 ? 1 : 0);
                var multDiv = es.Def.TextureMultTexDiv;
                _gl.Uniform2(_uTexDivMult, multDiv.X <= 0 ? 1f : multDiv.X, multDiv.Y <= 0 ? 1f : multDiv.Y);
                _gl.Uniform2(_uUvScrollRateMult, es.Def.TextureMultUvScrollRate.X, es.Def.TextureMultUvScrollRate.Y);
                _gl.Uniform1(_uDirectionOriented, es.Def.IsDirectionOriented ? 1 : 0);
                _gl.Uniform1(_uArbitraryQuad, es.Def.IsArbitraryQuad ? 1 : 0);
                _gl.Uniform1(_uIsDistortion, isDistortion ? 1 : 0);
                _gl.Uniform1(_uDistortionStrength, es.Def.Distortion?.Strength ?? 0f);
                _gl.Uniform3(_uPlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
                _gl.Uniform3(_uPlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
                _gl.Uniform3(_uPlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, es.Texture);
                if (es.TextureMult != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture1);
                    _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                if (isDistortion)
                {
                    _gl.ActiveTexture(TextureUnit.Texture2);
                    _gl.BindTexture(TextureTarget.Texture2D, _sceneTexture);
                    _gl.ActiveTexture(TextureUnit.Texture3);
                    _gl.BindTexture(TextureTarget.Texture2D, es.DistortionTexture);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                _gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, (uint)es.InstanceCount);
            }

            _gl.DepthMask(true);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            if (!depthTest) _gl.Disable(EnableCap.DepthTest);
            _gl.BindVertexArray(0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture3);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private static bool IsAdditive(int blendMode) => blendMode is 0 or 1 or 4 or 5;

        public void ClearTextures()
        {
            if (!_ready) return;
            foreach (var t in _ownedTextures) _gl.DeleteTexture(t);
            _ownedTextures.Clear();
            foreach (var (vao, vbo, ebo) in _ownedMeshes)
            {
                _gl.DeleteVertexArray(vao);
                _gl.DeleteBuffer(vbo);
                if (ebo != 0) _gl.DeleteBuffer(ebo);
            }
            _ownedMeshes.Clear();
            _whiteTex = 0;
        }

        public void Dispose()
        {
            if (!_ready) return;
            foreach (var t in _ownedTextures) _gl.DeleteTexture(t);
            _ownedTextures.Clear();
            _gl.DeleteBuffer(_quadVbo);
            _gl.DeleteBuffer(_instVbo);
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteProgram(_program);
            if (_sceneTexture != 0) _gl.DeleteTexture(_sceneTexture);
            _sceneTexture = 0;
            _sceneWidth = _sceneHeight = 0;
            _ready = false;
        }

        private uint _meshProgram;
        private int _muViewProj, _muWorldPos, _muScale, _muRot, _muColor, _muTex, _muUvOffset;
        private int _muTexMult, _muHasTexMult, _muUvOffsetMult;
        private int _muPlacementRight, _muPlacementUp, _muPlacementForward;
        private uint _whiteTex;

        private void EnsureMeshProgram()
        {
            if (_meshProgram == 0)
            {
                _meshProgram = ShaderUtil.CreateProgram(_gl, _gles, MeshVert, MeshFrag);
                _muViewProj = _gl.GetUniformLocation(_meshProgram, "uViewProj");
                _muWorldPos = _gl.GetUniformLocation(_meshProgram, "uWorldPos");
                _muScale = _gl.GetUniformLocation(_meshProgram, "uScale");
                _muRot = _gl.GetUniformLocation(_meshProgram, "uRot");
                _muColor = _gl.GetUniformLocation(_meshProgram, "uColor");
                _muTex = _gl.GetUniformLocation(_meshProgram, "uTex");
                _muUvOffset = _gl.GetUniformLocation(_meshProgram, "uUvOffset");
                _muTexMult = _gl.GetUniformLocation(_meshProgram, "uTexMult");
                _muHasTexMult = _gl.GetUniformLocation(_meshProgram, "uHasTexMult");
                _muUvOffsetMult = _gl.GetUniformLocation(_meshProgram, "uUvOffsetMult");
                _muPlacementRight = _gl.GetUniformLocation(_meshProgram, "uPlacementRight");
                _muPlacementUp = _gl.GetUniformLocation(_meshProgram, "uPlacementUp");
                _muPlacementForward = _gl.GetUniformLocation(_meshProgram, "uPlacementForward");
            }
            if (_whiteTex == 0) _whiteTex = UploadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
        }

        public void UploadEmitterMesh(VfxParticleSimulator.EmitterState es, float[] positions, float[] uvs, uint[] indices = null)
        {
            if (!_ready) return;
            EnsureMeshProgram();
            int verts = positions.Length / 3;
            var inter = new float[verts * 5];
            for (int i = 0; i < verts; i++)
            {
                inter[i * 5 + 0] = positions[i * 3 + 0];
                inter[i * 5 + 1] = positions[i * 3 + 1];
                inter[i * 5 + 2] = positions[i * 3 + 2];
                inter[i * 5 + 3] = i * 2 + 0 < uvs.Length ? uvs[i * 2 + 0] : 0f;
                inter[i * 5 + 4] = i * 2 + 1 < uvs.Length ? uvs[i * 2 + 1] : 0f;
            }
            var vao = _gl.GenVertexArray();
            var vbo = _gl.GenBuffer();
            _gl.BindVertexArray(vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(inter), BufferUsageARB.DynamicDraw);
            
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), IntPtr.Zero);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), new IntPtr(3 * sizeof(float)));
            uint ebo = 0;
            if (indices is { Length: > 0 })
            {
                ebo = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, new ReadOnlySpan<uint>(indices), BufferUsageARB.StaticDraw);
            }
            _gl.BindVertexArray(0);
            es.MeshVao = vao; es.MeshVbo = vbo; es.MeshEbo = ebo;
            es.MeshVertexCount = verts;
            es.MeshIndexCount = indices?.Length ?? 0;
            es.MeshInterleaved = inter;
            _ownedMeshes.Add((vao, vbo, ebo));
        }
        private readonly List<(uint Vao, uint Vbo, uint Ebo)> _ownedMeshes = new();

        public void UpdateEmitterMeshPositions(VfxParticleSimulator.EmitterState es, float[] positions)
        {
            if (!_ready || es.MeshVbo == 0 || es.MeshInterleaved is not { } inter) return;
            int verts = Math.Min(es.MeshVertexCount, positions.Length / 3);
            for (int i = 0; i < verts; i++)
            {
                inter[i * 5 + 0] = positions[i * 3 + 0];
                inter[i * 5 + 1] = positions[i * 3 + 1];
                inter[i * 5 + 2] = positions[i * 3 + 2];
            }
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, es.MeshVbo);
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, new ReadOnlySpan<float>(inter, 0, verts * 5));
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }

        private void RenderMeshEmitter(VfxParticleSimulator.EmitterState es, Matrix4x4 viewProj)
        {
            EnsureMeshProgram();
            _gl.UseProgram(_meshProgram);
            _gl.BindVertexArray(es.MeshVao);
            _gl.UniformMatrix4(_muViewProj, 1, false, in viewProj.M11);
            _gl.Uniform1(_muTex, 0);
            _gl.Uniform1(_muTexMult, 1);
            _gl.Uniform1(_muHasTexMult, es.TextureMult != 0 ? 1 : 0);
            _gl.Uniform3(_muPlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
            _gl.Uniform3(_muPlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
            _gl.Uniform3(_muPlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _whiteTex);
            if (es.TextureMult != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (IsAdditive(es.Def.BlendMode)) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            var scroll = es.Def.UvScrollRate * es.Age;
            _gl.Uniform2(_muUvOffset, scroll.X, scroll.Y);
            var scrollMult = es.Def.TextureMultUvScrollRate * es.Age;
            _gl.Uniform2(_muUvOffsetMult, scrollMult.X, scrollMult.Y);
            for (int i = 0; i < es.InstanceCount; i++)
            {
                int o = i * Stride;
                _gl.Uniform3(_muWorldPos, es.Instances[o], es.Instances[o + 1], es.Instances[o + 2]);
                float rawScale = es.Instances[o + 3];
                float sc = MathF.Abs(rawScale) < 0.01f ? MathF.CopySign(0.01f, rawScale == 0f ? 1f : rawScale) : rawScale;
                _gl.Uniform1(_muScale, sc);
                _gl.Uniform1(_muRot, es.Instances[o + 9]);
                _gl.Uniform4(_muColor, es.Instances[o + 5], es.Instances[o + 6], es.Instances[o + 7], es.Instances[o + 8]);
                
                if (es.MeshIndexCount > 0)
                {
                    if (_drawElements != null)
                    {
                        _drawElements((uint)PrimitiveType.Triangles, es.MeshIndexCount, (uint)DrawElementsType.UnsignedInt, IntPtr.Zero);
                    }
                }
                else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)es.MeshVertexCount);
            }
            _gl.UseProgram(_program);
            _gl.BindVertexArray(_vao);
        }

        private const string MeshVert = @"
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUv;
uniform mat4 uViewProj;
uniform vec3 uWorldPos;
uniform float uScale;
uniform float uRot;
uniform vec2 uUvOffset;
uniform vec2 uUvOffsetMult;
uniform vec3 uPlacementRight;
uniform vec3 uPlacementUp;
uniform vec3 uPlacementForward;
out vec2 vUv;
out vec2 vUvMult;
void main(){
    float s = sin(uRot); float c = cos(uRot);
    vec3 local = vec3(aPos.x * c - aPos.z * s, aPos.y, aPos.x * s + aPos.z * c) * uScale;
    vec3 p = uPlacementRight * local.x + uPlacementUp * local.y + uPlacementForward * local.z + uWorldPos;
    gl_Position = uViewProj * vec4(p, 1.0);
    vUv = aUv + uUvOffset;
    vUvMult = aUv + uUvOffsetMult;
}";

        private const string MeshFrag = @"
in vec2 vUv;
in vec2 vUvMult;
uniform sampler2D uTex;
uniform sampler2D uTexMult;
uniform int uHasTexMult;
uniform vec4 uColor;
out vec4 fragColor;
void main(){
    vec4 texel = texture(uTex, vUv);
    if (uHasTexMult != 0) texel *= texture(uTexMult, vUvMult);
    fragColor = texel * uColor;
}";

        private const string Vert = @"
layout(location=0) in vec2 aCorner;
layout(location=1) in vec3 aCenter;
layout(location=2) in vec2 aSize;
layout(location=3) in vec4 aColor;
layout(location=4) in vec2 aRotFrame;
layout(location=5) in vec4 aAgeVelX;
layout(location=6) in vec3 aRotation;
uniform mat4 uViewProj;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform vec2 uTexDiv;
uniform vec2 uUvScrollRate;
uniform vec2 uTexDivMult;
uniform vec2 uUvScrollRateMult;
uniform int uDirectionOriented;
uniform int uArbitraryQuad;
uniform vec3 uPlacementRight;
uniform vec3 uPlacementUp;
uniform vec3 uPlacementForward;
out vec2 vUv;
out vec2 vUvMult;
out vec4 vColor;
vec3 rotateEuler(vec3 p, vec3 r){
    float sx = sin(r.x); float cx = cos(r.x);
    float sy = sin(r.y); float cy = cos(r.y);
    float sz = sin(r.z); float cz = cos(r.z);
    p = vec3(p.x, p.y * cx - p.z * sx, p.y * sx + p.z * cx);
    p = vec3(p.x * cy + p.z * sy, p.y, -p.x * sy + p.z * cy);
    return vec3(p.x * cz - p.y * sz, p.x * sz + p.y * cz, p.z);
}
void main(){
    float rotation = uArbitraryQuad != 0 ? 0.0 : aRotFrame.x;
    if (uDirectionOriented != 0) {
        float vx = dot(aAgeVelX.yzw, uCamRight);
        float vy = dot(aAgeVelX.yzw, uCamUp);
        if (abs(vx) + abs(vy) > 0.0001) rotation = atan(-vx, vy);
    }
    float s = sin(rotation);
    float c = cos(rotation);
    vec2 rc = vec2(aCorner.x * c - aCorner.y * s, aCorner.x * s + aCorner.y * c);
    vec3 localRight = rotateEuler(vec3(1.0, 0.0, 0.0), aRotation);
    vec3 localUp = rotateEuler(vec3(0.0, 1.0, 0.0), aRotation);
    vec3 placedRight = uPlacementRight * localRight.x + uPlacementUp * localRight.y + uPlacementForward * localRight.z;
    vec3 placedUp = uPlacementRight * localUp.x + uPlacementUp * localUp.y + uPlacementForward * localUp.z;
    vec3 right = uArbitraryQuad != 0 ? placedRight : uCamRight;
    vec3 up = uArbitraryQuad != 0 ? placedUp : uCamUp;
    vec3 world = aCenter + right * (rc.x * aSize.x) + up * (rc.y * aSize.y);
    gl_Position = uViewProj * vec4(world, 1.0);
    vec2 cell = aCorner + vec2(0.5, 0.5);
    float cols = max(uTexDiv.x, 1.0);
    float rows = max(uTexDiv.y, 1.0);
    float frame = floor(aRotFrame.y + 0.0001);
    float fx = mod(frame, cols);
    float fy = floor(frame / cols);
    vUv = (vec2(fx, fy) + vec2(cell.x, 1.0 - cell.y)) / vec2(cols, rows)
        + uUvScrollRate * aAgeVelX.x;
    vUvMult = vec2(cell.x, 1.0 - cell.y) / max(uTexDivMult, vec2(1.0))
        + uUvScrollRateMult * aAgeVelX.x;
    vColor = aColor;
}";

        private const string Frag = @"
in vec2 vUv;
in vec2 vUvMult;
in vec4 vColor;
uniform sampler2D uTex;
uniform sampler2D uTexMult;
uniform int uHasTexMult;
uniform int uIsDistortion;
uniform sampler2D uDistortionTex;
uniform sampler2D uSceneTex;
uniform vec2 uViewportSize;
uniform float uDistortionStrength;
out vec4 fragColor;
void main(){
    vec4 t = texture(uTex, vUv);
    if (uHasTexMult != 0) t *= texture(uTexMult, vUvMult);
    if (uIsDistortion != 0) {
        vec4 normalSample = texture(uDistortionTex, vUv);
        float mask = normalSample.a * t.a * vColor.a;
        vec2 normalOffset = normalSample.rg * 2.0 - vec2(1.0);
        vec2 sceneUv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        sceneUv = clamp(sceneUv + normalOffset * uDistortionStrength * mask, vec2(0.0), vec2(1.0));
        vec4 refracted = texture(uSceneTex, sceneUv);
        fragColor = vec4(refracted.rgb, mask);
        return;
    }
    fragColor = t * vColor;
}";
    }
}
