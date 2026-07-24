using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Silk.NET.OpenGL;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Draws effect billboards and mesh primitives from prepared playback state.
    /// </summary>
    public sealed class VfxOpenGlRenderer : IDisposable
    {
        private GL _gl = null!;
        private uint _program, _vao, _quadVbo, _instVbo, _fallbackWhiteTexture;
        private int _uViewProj, _uCamRight, _uCamUp, _uTexDiv, _uTexSize, _uTex, _uUvScrollRate, _uEmitterUvOffset;
        private int _uTexMult, _uHasTexMult, _uTexDivMult, _uUvScrollRateMult, _uFlipVMult;
        private int _uIsDistortion, _uDistortionTex, _uSceneTex, _uViewportSize, _uDistortionStrength;
        private int _uDirectionOriented, _uArbitraryQuad;
        private int _uPrimitiveKind;
        private int _uAlphaCutoff, _uFlipU, _uFlipV, _uClampUv;
        private int _uErosionTex, _uHasErosion, _uErosionFeatherIn, _uErosionFeatherOut;
        private int _uPlacementRight, _uPlacementUp, _uPlacementForward, _uIsGroundLayer;
        private int _instCapFloats;
        private bool _ready;
        private readonly List<uint> _ownedTextures = new();
        private uint _sceneTexture;
        private int _sceneWidth, _sceneHeight;

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);
        private DrawElementsDelegate _drawElements = null!;

        private const int Stride = VfxPlaybackRuntime.InstanceStride;
        private bool _gles;

        public void Initialize(GL gl)
        {
            _gl = gl;
            var proc = gl.Context.GetProcAddress("glDrawElements");
            if (proc != IntPtr.Zero)
            {
                _drawElements = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DrawElementsDelegate>(proc);
            }
            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _gles = gles;
            _program = GlShaderCompiler.CreateProgram(gl, gles, Vert, Frag);
            _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
            _uCamRight = gl.GetUniformLocation(_program, "uCamRight");
            _uCamUp = gl.GetUniformLocation(_program, "uCamUp");
            _uTexDiv = gl.GetUniformLocation(_program, "uTexDiv");
            _uTexSize = gl.GetUniformLocation(_program, "uTexSize");
            _uTex = gl.GetUniformLocation(_program, "uTex");
            _uTexMult = gl.GetUniformLocation(_program, "uTexMult");
            _uHasTexMult = gl.GetUniformLocation(_program, "uHasTexMult");
            _uTexDivMult = gl.GetUniformLocation(_program, "uTexDivMult");
            _uUvScrollRateMult = gl.GetUniformLocation(_program, "uUvScrollRateMult");
            _uFlipVMult = gl.GetUniformLocation(_program, "uFlipVMult");
            _uUvScrollRate = gl.GetUniformLocation(_program, "uUvScrollRate");
            _uEmitterUvOffset = gl.GetUniformLocation(_program, "uEmitterUvOffset");
            _uIsDistortion = gl.GetUniformLocation(_program, "uIsDistortion");
            _uDistortionTex = gl.GetUniformLocation(_program, "uDistortionTex");
            _uSceneTex = gl.GetUniformLocation(_program, "uSceneTex");
            _uViewportSize = gl.GetUniformLocation(_program, "uViewportSize");
            _uDistortionStrength = gl.GetUniformLocation(_program, "uDistortionStrength");
            _uDirectionOriented = gl.GetUniformLocation(_program, "uDirectionOriented");
            _uArbitraryQuad = gl.GetUniformLocation(_program, "uArbitraryQuad");
            _uPrimitiveKind = gl.GetUniformLocation(_program, "uPrimitiveKind");
            _uAlphaCutoff = gl.GetUniformLocation(_program, "uAlphaCutoff");
            _uFlipU = gl.GetUniformLocation(_program, "uFlipU");
            _uFlipV = gl.GetUniformLocation(_program, "uFlipV");
            _uClampUv = gl.GetUniformLocation(_program, "uClampUv");
            _uErosionTex = gl.GetUniformLocation(_program, "uErosionTex");
            _uHasErosion = gl.GetUniformLocation(_program, "uHasErosion");
            _uErosionFeatherIn = gl.GetUniformLocation(_program, "uErosionFeatherIn");
            _uErosionFeatherOut = gl.GetUniformLocation(_program, "uErosionFeatherOut");
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
            gl.EnableVertexAttribArray(7); gl.VertexAttribPointer(7, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(19 * sizeof(float)));
            gl.EnableVertexAttribArray(8); gl.VertexAttribPointer(8, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(21 * sizeof(float)));
            gl.EnableVertexAttribArray(9); gl.VertexAttribPointer(9, 1, VertexAttribPointerType.Float, false, bstride, new IntPtr(23 * sizeof(float)));
            _uIsDistortion = gl.GetUniformLocation(_program, "uIsDistortion");
            _uDistortionTex = gl.GetUniformLocation(_program, "uDistortionTex");
            _uSceneTex = gl.GetUniformLocation(_program, "uSceneTex");
            _uViewportSize = gl.GetUniformLocation(_program, "uViewportSize");
            _uDistortionStrength = gl.GetUniformLocation(_program, "uDistortionStrength");
            _uDirectionOriented = gl.GetUniformLocation(_program, "uDirectionOriented");
            _uArbitraryQuad = gl.GetUniformLocation(_program, "uArbitraryQuad");
            _uPrimitiveKind = gl.GetUniformLocation(_program, "uPrimitiveKind");
            _uAlphaCutoff = gl.GetUniformLocation(_program, "uAlphaCutoff");
            _uFlipU = gl.GetUniformLocation(_program, "uFlipU");
            _uFlipV = gl.GetUniformLocation(_program, "uFlipV");
            _uClampUv = gl.GetUniformLocation(_program, "uClampUv");
            _uErosionTex = gl.GetUniformLocation(_program, "uErosionTex");
            _uHasErosion = gl.GetUniformLocation(_program, "uHasErosion");
            _uErosionFeatherIn = gl.GetUniformLocation(_program, "uErosionFeatherIn");
            _uErosionFeatherOut = gl.GetUniformLocation(_program, "uErosionFeatherOut");
            _uPlacementRight = gl.GetUniformLocation(_program, "uPlacementRight");
            _uPlacementUp = gl.GetUniformLocation(_program, "uPlacementUp");
            _uPlacementForward = gl.GetUniformLocation(_program, "uPlacementForward");
            _uIsGroundLayer = gl.GetUniformLocation(_program, "uIsGroundLayer");
            gl.EnableVertexAttribArray(10); gl.VertexAttribPointer(10, 1, VertexAttribPointerType.Float, false, bstride, new IntPtr(24 * sizeof(float)));
            gl.EnableVertexAttribArray(11); gl.VertexAttribPointer(11, 4, VertexAttribPointerType.Float, false, bstride, new IntPtr(25 * sizeof(float)));
            gl.EnableVertexAttribArray(12); gl.VertexAttribPointer(12, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(29 * sizeof(float)));
            gl.EnableVertexAttribArray(13); gl.VertexAttribPointer(13, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(31 * sizeof(float)));
            gl.EnableVertexAttribArray(14); gl.VertexAttribPointer(14, 1, VertexAttribPointerType.Float, false, bstride, new IntPtr(33 * sizeof(float)));

            gl.VertexAttribDivisor(1, 1);
            gl.VertexAttribDivisor(2, 1);
            gl.VertexAttribDivisor(3, 1);
            gl.VertexAttribDivisor(4, 1);
            gl.VertexAttribDivisor(5, 1);
            gl.VertexAttribDivisor(6, 1);
            gl.VertexAttribDivisor(7, 1);
            gl.VertexAttribDivisor(8, 1);
            gl.VertexAttribDivisor(9, 1);
            gl.VertexAttribDivisor(10, 1);
            gl.VertexAttribDivisor(11, 1);
            gl.VertexAttribDivisor(12, 1);
            gl.VertexAttribDivisor(13, 1);
            gl.VertexAttribDivisor(14, 1);

            gl.BindVertexArray(0);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

            byte[] whitePixel = { 255, 255, 255, 255 };
            _fallbackWhiteTexture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _fallbackWhiteTexture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, new ReadOnlySpan<byte>(whitePixel));
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.BindTexture(TextureTarget.Texture2D, 0);
            _ownedTextures.Add(_fallbackWhiteTexture);

            _ready = true;
        }

        private readonly Dictionary<uint, bool> _texHasAlpha = new();

        public uint UploadTexture(byte[] rgba, int width, int height)
        {
            int n = width * height, varied = 0;
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] < 250) varied++;
            bool hasAlpha = varied > n / 100;

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
            _texHasAlpha[tex] = hasAlpha;
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

        public void Render(VfxPlaybackRuntime sim, Matrix4x4 viewProj, Matrix4x4 view)
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
            _gl.Uniform1(_uErosionTex, 4);
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
                if (es.Texture == 0 && !es.Def.IsMeshPrimitive) continue;
                if (es.MeshVao != 0) { RenderMeshEmitter(es, viewProj); continue; }
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
                else if (IsAdditiveFor(es.Def.BlendMode, es.Texture)) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                else if (es.Def.BlendMode == 2) _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

                _gl.Uniform2(_uTexDiv, es.Def.TexDiv.X <= 0 ? 1f : es.Def.TexDiv.X, es.Def.TexDiv.Y <= 0 ? 1f : es.Def.TexDiv.Y);
                _gl.Uniform2(_uTexSize, Math.Max(1f, es.TextureWidth), Math.Max(1f, es.TextureHeight));
                _gl.Uniform2(_uUvScrollRate, 0f, 0f);
                Vector2 emitterUvOffset = es.Def.EmitterUvScrollRate * es.EmitterAge;
                _gl.Uniform2(_uEmitterUvOffset, emitterUvOffset.X, emitterUvOffset.Y);
                _gl.Uniform1(_uHasTexMult, es.TextureMult != 0 ? 1 : 0);
                var multDiv = es.Def.TextureMultTexDiv;
                _gl.Uniform2(_uTexDivMult, multDiv.X <= 0 ? 1f : multDiv.X, multDiv.Y <= 0 ? 1f : multDiv.Y);
                _gl.Uniform2(_uUvScrollRateMult, 0f, 0f);
                _gl.Uniform1(_uFlipVMult, es.Def.TextureMultFlipV ? 1 : 0);
                bool directional = es.Def.IsDirectionOriented || es.Def.PrimitiveKind is
                    VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.Ray or VfxPrimitiveKind.Beam;
                bool arbitrary = es.Def.IsArbitraryQuad || es.Def.ParticleIsLocalOrientation || es.Def.PrimitiveKind is
                    VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.PlanarProjection;
                _gl.Uniform1(_uDirectionOriented, directional ? 1 : 0);
                _gl.Uniform1(_uArbitraryQuad, arbitrary ? 1 : 0);
                _gl.Uniform1(_uIsGroundLayer, (es.Def.IsGroundLayer || es.Def.IsFollowingTerrain || es.Def.PrimitiveKind == VfxPrimitiveKind.PlanarProjection) ? 1 : 0);
                _gl.Uniform1(_uPrimitiveKind, (int)es.Def.PrimitiveKind);
                var renderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
                _gl.Uniform1(_uAlphaCutoff, renderState.AlphaCutoff);
                _gl.Uniform1(_uFlipU, renderState.FlipU ? 1 : 0);
                _gl.Uniform1(_uFlipV, renderState.FlipV ? 1 : 0);
                _gl.Uniform1(_uClampUv, renderState.ClampUvScroll ? 1 : 0);
                _gl.Uniform1(_uIsDistortion, isDistortion ? 1 : 0);
                _gl.Uniform1(_uDistortionStrength, es.Def.Distortion?.Strength ?? 0f);
                _gl.Uniform1(_uHasErosion, es.ErosionTexture != 0 ? 1 : 0);
                _gl.Uniform1(_uErosionFeatherIn, es.Def.AlphaErosion?.FeatherIn ?? 0f);
                _gl.Uniform1(_uErosionFeatherOut, es.Def.AlphaErosion?.FeatherOut ?? 0f);
                _gl.Uniform3(_uPlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
                _gl.Uniform3(_uPlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
                _gl.Uniform3(_uPlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _fallbackWhiteTexture);
                ApplyAddressMode(renderState.TextureAddressMode);
                if (es.TextureMult != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture1);
                    _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                    ApplyAddressMode(es.Def.TextureMultAddressMode);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                if (es.DistortionTexture != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture3);
                    _gl.BindTexture(TextureTarget.Texture2D, es.DistortionTexture);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                if (es.ErosionTexture != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture4);
                    _gl.BindTexture(TextureTarget.Texture2D, es.ErosionTexture);
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
            _gl.ActiveTexture(TextureUnit.Texture4);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private static bool IsAdditive(int blendMode) => blendMode is 1 or 3 or 4 or 5;

        private bool IsAdditiveFor(int blendMode, uint texture) => IsAdditive(blendMode);

        private void ApplyAddressMode(int addressMode)
        {
            var wrap = addressMode switch
            {
                1 => TextureWrapMode.ClampToEdge,
                2 => TextureWrapMode.MirroredRepeat,
                _ => TextureWrapMode.Repeat,
            };
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        }

        public void ClearTextures()
        {
            if (!_ready) return;
            foreach (var t in _ownedTextures) _gl.DeleteTexture(t);
            _ownedTextures.Clear();
            ReleaseMeshes();
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
            if (_meshProgram != 0) _gl.DeleteProgram(_meshProgram);
            _meshProgram = 0;
            ReleaseMeshes();
            if (_sceneTexture != 0) _gl.DeleteTexture(_sceneTexture);
            _sceneTexture = 0;
            _sceneWidth = _sceneHeight = 0;
            _ready = false;
        }

        private uint _meshProgram;
        private int _muViewProj, _muWorldPos, _muScale, _muRotation, _muColor, _muTex, _muUvOffset, _muEmitterUvOffset;
        private int _muTexMult, _muHasTexMult, _muTexDivMult, _muUvOffsetMult, _muUvScaleMult, _muUvRotationMult, _muFlipVMult;
        private int _muPlacementRight, _muPlacementUp, _muPlacementForward;
        private int _muAlphaCutoff, _muFlipU, _muFlipV;
        private int _muBirthUvOffset, _muUvScale, _muUvRotation;
        private int _muErosionTex, _muHasErosion, _muErosionDrive, _muErosionFeatherIn, _muErosionFeatherOut, _muErosionMixer;
        private uint _whiteTex;

        private void EnsureMeshProgram()
        {
            if (_meshProgram == 0)
            {
                _meshProgram = GlShaderCompiler.CreateProgram(_gl, _gles, MeshVert, MeshFrag);
                _muViewProj = _gl.GetUniformLocation(_meshProgram, "uViewProj");
                _muWorldPos = _gl.GetUniformLocation(_meshProgram, "uWorldPos");
                _muScale = _gl.GetUniformLocation(_meshProgram, "uScale");
                _muRotation = _gl.GetUniformLocation(_meshProgram, "uRotation");
                _muColor = _gl.GetUniformLocation(_meshProgram, "uColor");
                _muTex = _gl.GetUniformLocation(_meshProgram, "uTex");
                _muUvOffset = _gl.GetUniformLocation(_meshProgram, "uUvOffset");
                _muEmitterUvOffset = _gl.GetUniformLocation(_meshProgram, "uEmitterUvOffset");
                _muTexMult = _gl.GetUniformLocation(_meshProgram, "uTexMult");
                _muHasTexMult = _gl.GetUniformLocation(_meshProgram, "uHasTexMult");
                _muTexDivMult = _gl.GetUniformLocation(_meshProgram, "uTexDivMult");
                _muUvOffsetMult = _gl.GetUniformLocation(_meshProgram, "uUvOffsetMult");
                _muUvScaleMult = _gl.GetUniformLocation(_meshProgram, "uUvScaleMult");
                _muUvRotationMult = _gl.GetUniformLocation(_meshProgram, "uUvRotationMult");
                _muFlipVMult = _gl.GetUniformLocation(_meshProgram, "uFlipVMult");
                _muPlacementRight = _gl.GetUniformLocation(_meshProgram, "uPlacementRight");
                _muPlacementUp = _gl.GetUniformLocation(_meshProgram, "uPlacementUp");
                _muPlacementForward = _gl.GetUniformLocation(_meshProgram, "uPlacementForward");
                _muAlphaCutoff = _gl.GetUniformLocation(_meshProgram, "uAlphaCutoff");
                _muFlipU = _gl.GetUniformLocation(_meshProgram, "uFlipU");
                _muFlipV = _gl.GetUniformLocation(_meshProgram, "uFlipV");
                _muBirthUvOffset = _gl.GetUniformLocation(_meshProgram, "uBirthUvOffset");
                _muUvScale = _gl.GetUniformLocation(_meshProgram, "uUvScale");
                _muUvRotation = _gl.GetUniformLocation(_meshProgram, "uUvRotation");
                _muErosionTex = _gl.GetUniformLocation(_meshProgram, "uErosionTex");
                _muHasErosion = _gl.GetUniformLocation(_meshProgram, "uHasErosion");
                _muErosionDrive = _gl.GetUniformLocation(_meshProgram, "uErosionDrive");
                _muErosionFeatherIn = _gl.GetUniformLocation(_meshProgram, "uErosionFeatherIn");
                _muErosionFeatherOut = _gl.GetUniformLocation(_meshProgram, "uErosionFeatherOut");
                _muErosionMixer = _gl.GetUniformLocation(_meshProgram, "uErosionMixer");
            }
            if (_whiteTex == 0) _whiteTex = UploadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
        }

        public void UploadEmitterMesh(VfxPlaybackRuntime.EmitterState es, float[] positions, float[] uvs, uint[] indices = null)
        {
            if (!_ready) return;
            EnsureMeshProgram();
            if (_meshCache.TryGetValue(positions, out var cached))
            {
                es.MeshVao = cached.Vao;
                es.MeshVbo = cached.Vbo;
                es.MeshEbo = cached.Ebo;
                es.MeshVertexCount = cached.VertexCount;
                es.MeshIndexCount = cached.IndexCount;
                es.MeshInterleaved = cached.Interleaved;
                return;
            }
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
            _meshCache[positions] = new MeshGpuResource(
                vao,
                vbo,
                ebo,
                verts,
                indices?.Length ?? 0,
                inter);
        }
        private readonly Dictionary<float[], MeshGpuResource> _meshCache =
            new(ReferenceEqualityComparer.Instance);

        private sealed record MeshGpuResource(
            uint Vao,
            uint Vbo,
            uint Ebo,
            int VertexCount,
            int IndexCount,
            float[] Interleaved);

        private void ReleaseMeshes()
        {
            foreach (var mesh in _meshCache.Values)
            {
                _gl.DeleteVertexArray(mesh.Vao);
                _gl.DeleteBuffer(mesh.Vbo);
                if (mesh.Ebo != 0) _gl.DeleteBuffer(mesh.Ebo);
            }
            _meshCache.Clear();
        }

        public void UpdateEmitterMeshPositions(VfxPlaybackRuntime.EmitterState es, float[] positions)
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

        private void RenderMeshEmitter(VfxPlaybackRuntime.EmitterState es, Matrix4x4 viewProj)
        {
            EnsureMeshProgram();
            _gl.UseProgram(_meshProgram);
            _gl.BindVertexArray(es.MeshVao);
            _gl.UniformMatrix4(_muViewProj, 1, false, in viewProj.M11);
            _gl.Uniform1(_muTex, 0);
            _gl.Uniform1(_muTexMult, 1);
            _gl.Uniform1(_muErosionTex, 4);
            _gl.Uniform1(_muHasTexMult, es.TextureMult != 0 ? 1 : 0);
            Vector2 textureMultTexDiv = es.Def.TextureMultTexDiv;
            _gl.Uniform2(
                _muTexDivMult,
                textureMultTexDiv.X <= 0f ? 1f : textureMultTexDiv.X,
                textureMultTexDiv.Y <= 0f ? 1f : textureMultTexDiv.Y);
            _gl.Uniform1(_muHasErosion, es.ErosionTexture != 0 ? 1 : 0);
            _gl.Uniform1(_muErosionFeatherIn, es.Def.AlphaErosion?.FeatherIn ?? 0f);
            _gl.Uniform1(_muErosionFeatherOut, es.Def.AlphaErosion?.FeatherOut ?? 0f);
            _gl.Uniform3(_muPlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
            _gl.Uniform3(_muPlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
            _gl.Uniform3(_muPlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _whiteTex);
            var renderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
            ApplyAddressMode(renderState.TextureAddressMode);
            _gl.Uniform1(_muAlphaCutoff, renderState.AlphaCutoff);
            _gl.Uniform1(_muFlipU, renderState.FlipU ? 1 : 0);
            _gl.Uniform1(_muFlipV, renderState.FlipV ? 1 : 0);
            if (es.TextureMult != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                ApplyAddressMode(es.Def.TextureMultAddressMode);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (es.ErosionTexture != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture4);
                _gl.BindTexture(TextureTarget.Texture2D, es.ErosionTexture);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (renderState.DisableBackfaceCull) _gl.Disable(EnableCap.CullFace);
            else _gl.Enable(EnableCap.CullFace);
            if (IsAdditive(es.Def.BlendMode)) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            _gl.Uniform2(_muUvOffset, 0f, 0f);
            Vector2 emitterUvOffset = es.Def.EmitterUvScrollRate * es.EmitterAge;
            _gl.Uniform2(_muEmitterUvOffset, emitterUvOffset.X, emitterUvOffset.Y);
            _gl.Uniform1(_muFlipVMult, es.Def.TextureMultFlipV ? 1 : 0);
            for (int i = 0; i < es.InstanceCount; i++)
            {
                int o = i * Stride;
                _gl.Uniform3(_muWorldPos, es.Instances[o], es.Instances[o + 1], es.Instances[o + 2]);
                float scaleX = ClampScale(es.Instances[o + 3]);
                float scaleY = ClampScale(es.Instances[o + 4]);
                float scaleZ = ClampScale(es.Instances[o + 18]);
                _gl.Uniform3(_muScale, scaleX, scaleY, scaleZ);
                _gl.Uniform3(
                    _muRotation,
                    es.Instances[o + 15],
                    es.Instances[o + 16],
                    es.Instances[o + 17]);
                _gl.Uniform4(_muColor, es.Instances[o + 5], es.Instances[o + 6], es.Instances[o + 7], es.Instances[o + 8]);
                _gl.Uniform2(_muBirthUvOffset, es.Instances[o + 19], es.Instances[o + 20]);
                _gl.Uniform2(_muUvScale, es.Instances[o + 21], es.Instances[o + 22]);
                _gl.Uniform1(_muUvRotation, es.Instances[o + 23]);
                _gl.Uniform1(_muErosionDrive, es.Instances[o + 24]);
                _gl.Uniform4(_muErosionMixer, es.Instances[o + 25], es.Instances[o + 26], es.Instances[o + 27], es.Instances[o + 28]);
                _gl.Uniform2(_muUvOffsetMult, es.Instances[o + 29], es.Instances[o + 30]);
                _gl.Uniform2(_muUvScaleMult, es.Instances[o + 31], es.Instances[o + 32]);
                _gl.Uniform1(_muUvRotationMult, es.Instances[o + 33]);

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

        private static float ClampScale(float value)
            => MathF.Abs(value) < 0.01f ? MathF.CopySign(0.01f, value == 0f ? 1f : value) : value;

        private const string MeshVert = @"
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUv;
uniform mat4 uViewProj;
uniform vec3 uWorldPos;
uniform vec3 uScale;
uniform vec3 uRotation;
uniform vec2 uUvOffset;
uniform vec2 uEmitterUvOffset;
uniform vec2 uUvOffsetMult;
uniform vec2 uUvScaleMult;
uniform float uUvRotationMult;
uniform vec2 uTexDivMult;
uniform vec3 uPlacementRight;
uniform vec3 uPlacementUp;
uniform vec3 uPlacementForward;
uniform int uFlipU;
uniform int uFlipV;
uniform int uFlipVMult;
uniform vec2 uBirthUvOffset;
uniform vec2 uUvScale;
uniform float uUvRotation;
out vec2 vUv;
out vec2 vUvMult;
void main(){
    vec3 scaled = aPos * uScale;
    float sz = sin(uRotation.z); float cz = cos(uRotation.z);
    vec3 local = vec3(scaled.x * cz - scaled.y * sz, scaled.x * sz + scaled.y * cz, scaled.z);
    float sx = sin(uRotation.x); float cx = cos(uRotation.x);
    local = vec3(local.x, local.y * cx - local.z * sx, local.y * sx + local.z * cx);
    float sy = sin(uRotation.y); float cy = cos(uRotation.y);
    local = vec3(local.x * cy + local.z * sy, local.y, -local.x * sy + local.z * cy);
    vec3 p = uPlacementRight * local.x + uPlacementUp * local.y + uPlacementForward * local.z + uWorldPos;
    gl_Position = uViewProj * vec4(p, 1.0);
    vec2 baseUv = aUv;
    vec2 centeredUv = (baseUv - vec2(0.5)) * uUvScale;
    float uvSin = sin(uUvRotation); float uvCos = cos(uUvRotation);
    centeredUv = vec2(centeredUv.x * uvCos - centeredUv.y * uvSin,
                      centeredUv.x * uvSin + centeredUv.y * uvCos);
    baseUv = centeredUv + vec2(0.5) + uBirthUvOffset;
    if (uFlipU != 0) baseUv.x = 1.0 - baseUv.x;
    if (uFlipV != 0) baseUv.y = 1.0 - baseUv.y;
    vUv = baseUv + uUvOffset + uEmitterUvOffset;
    vec2 multUv = aUv;
    vec2 centeredMultUv = (multUv - vec2(0.5)) * uUvScaleMult;
    float multSin = sin(uUvRotationMult); float multCos = cos(uUvRotationMult);
    centeredMultUv = vec2(centeredMultUv.x * multCos - centeredMultUv.y * multSin,
                          centeredMultUv.x * multSin + centeredMultUv.y * multCos);
    multUv = centeredMultUv + vec2(0.5) + uUvOffsetMult;
    if (uFlipVMult != 0) multUv.y = 1.0 - multUv.y;
    vUvMult = multUv / max(uTexDivMult, vec2(1.0)) + uEmitterUvOffset;
}";

        private const string MeshFrag = @"
in vec2 vUv;
in vec2 vUvMult;
uniform sampler2D uTex;
uniform sampler2D uTexMult;
uniform int uHasTexMult;
uniform vec4 uColor;
uniform float uAlphaCutoff;
uniform sampler2D uErosionTex;
uniform int uHasErosion;
uniform float uErosionDrive;
uniform float uErosionFeatherIn;
uniform float uErosionFeatherOut;
uniform vec4 uErosionMixer;
out vec4 fragColor;
void main(){
    vec4 texel = texture(uTex, vUv);
    if (uHasTexMult != 0) {
        vec4 mult = texture(uTexMult, vUvMult);
        texel.rgb *= mult.rgb;
        float multAlpha = mult.a < 0.999 ? mult.a : max(mult.r, max(mult.g, mult.b));
        texel.a *= multAlpha;
    }
    if (uHasErosion != 0) {
        float erosion = dot(texture(uErosionTex, vUv), uErosionMixer);
        float feather = max(0.001, mix(uErosionFeatherIn, uErosionFeatherOut, clamp(uErosionDrive, 0.0, 1.0)));
        texel.a *= smoothstep(uErosionDrive - feather, uErosionDrive + feather, erosion);
    }
    if (texel.a * uColor.a <= uAlphaCutoff) discard;
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
layout(location=7) in vec2 aUvOffset;
layout(location=8) in vec2 aUvScale;
layout(location=9) in float aUvRotation;
layout(location=10) in float aErosionDrive;
layout(location=11) in vec4 aErosionMixer;
layout(location=12) in vec2 aUvOffsetMult;
layout(location=13) in vec2 aUvScaleMult;
layout(location=14) in float aUvRotationMult;
uniform mat4 uViewProj;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform vec2 uTexDiv;
uniform vec2 uTexSize;
uniform vec2 uUvScrollRate;
uniform vec2 uEmitterUvOffset;
uniform vec2 uTexDivMult;
uniform vec2 uUvScrollRateMult;
uniform int uDirectionOriented;
uniform int uArbitraryQuad;
uniform int uIsGroundLayer;
uniform int uPrimitiveKind;
uniform int uFlipU;
uniform int uFlipV;
uniform int uFlipVMult;
uniform int uClampUv;
uniform vec3 uPlacementRight;
uniform vec3 uPlacementUp;
uniform vec3 uPlacementForward;
out vec2 vUv;
out vec2 vUvMult;
out vec4 vColor;
out float vErosionDrive;
out vec4 vErosionMixer;
vec3 rotateEuler(vec3 p, vec3 r){
    float sz = sin(r.z); float cz = cos(r.z);
    p = vec3(p.x * cz - p.y * sz, p.x * sz + p.y * cz, p.z);
    float sx = sin(r.x); float cx = cos(r.x);
    p = vec3(p.x, p.y * cx - p.z * sx, p.y * sx + p.z * cx);
    float sy = sin(r.y); float cy = cos(r.y);
    return vec3(p.x * cy + p.z * sy, p.y, -p.x * sy + p.z * cy);
}
void main(){
    float rotation = uArbitraryQuad != 0 ? 0.0 : aRotFrame.x;
    vec3 localRight = rotateEuler(vec3(1.0, 0.0, 0.0), aRotation);
    vec3 localUp = rotateEuler(vec3(0.0, 1.0, 0.0), aRotation);
    vec3 placedRight = uPlacementRight * localRight.x + uPlacementUp * localRight.y + uPlacementForward * localRight.z;
    vec3 placedUp = uPlacementRight * localUp.x + uPlacementUp * localUp.y + uPlacementForward * localUp.z;
    vec3 right = uArbitraryQuad != 0 ? placedRight : uCamRight;
    vec3 up = uArbitraryQuad != 0 ? placedUp : uCamUp;

    if (uDirectionOriented != 0) {
        vec3 vel = aAgeVelX.yzw;
        float lenSq = dot(vel, vel);
        if (lenSq > 0.0001) {
            vec3 dir = vel * inversesqrt(lenSq);
            vec3 side = cross(dir, uCamRight);
            if (dot(side, side) < 0.0001) side = cross(dir, uCamUp);
            right = normalize(side);
            up = dir;
        } else {
            float vx = dot(vel, uCamRight);
            float vy = dot(vel, uCamUp);
            if (abs(vx) + abs(vy) > 0.0001) rotation = atan(-vx, vy);
        }
    }

    float s = sin(rotation);
    float c = cos(rotation);
    vec2 rc = vec2(aCorner.x * c - aCorner.y * s, aCorner.x * s + aCorner.y * c);
    vec3 world;
    if (uPrimitiveKind == 7 || uPrimitiveKind == 8) {
        float alongRay = aCorner.y + 0.5;
        world = aCenter + up * (alongRay * aSize.y) + right * (rc.x * aSize.x);
    } else {
        world = aCenter + right * (rc.x * aSize.x) + up * (rc.y * aSize.y);
    }
    gl_Position = uViewProj * vec4(world, 1.0);
    vec2 cell = aCorner + vec2(0.5, 0.5);
    float cols = max(uTexDiv.x, 1.0);
    float rows = max(uTexDiv.y, 1.0);
    float frame = floor(aRotFrame.y + 0.0001);
    float fx = mod(frame, cols);
    float fy = floor(frame / cols);
    vec2 localUv = vec2(cell.x, 1.0 - cell.y);
    vec2 centeredUv = (localUv - vec2(0.5)) * aUvScale;
    float uvSin = sin(aUvRotation); float uvCos = cos(aUvRotation);
    centeredUv = vec2(centeredUv.x * uvCos - centeredUv.y * uvSin,
                      centeredUv.x * uvSin + centeredUv.y * uvCos);
    localUv = centeredUv + vec2(0.5) + aUvOffset;
    if (uFlipU != 0) localUv.x = 1.0 - localUv.x;
    if (uFlipV != 0) localUv.y = 1.0 - localUv.y;
    vec2 halfTexel = 0.5 / max(uTexSize, vec2(1.0));
    vec2 atlasUv = (vec2(fx, fy) + localUv) / vec2(cols, rows);
    vec2 cellMin = vec2(fx, fy) / vec2(cols, rows) + halfTexel;
    vec2 cellMax = vec2(fx + 1.0, fy + 1.0) / vec2(cols, rows) - halfTexel;
    atlasUv = clamp(atlasUv, cellMin, cellMax);
    vec2 scroll = uUvScrollRate * aAgeVelX.x;
    if (uClampUv != 0) scroll = clamp(scroll, -localUv, vec2(1.0) - localUv);
    vUv = atlasUv + scroll + uEmitterUvOffset;
    vec2 multUv = vec2(cell.x, 1.0 - cell.y);
    vec2 centeredMultUv = (multUv - vec2(0.5)) * aUvScaleMult;
    float multSin = sin(aUvRotationMult); float multCos = cos(aUvRotationMult);
    centeredMultUv = vec2(centeredMultUv.x * multCos - centeredMultUv.y * multSin,
                          centeredMultUv.x * multSin + centeredMultUv.y * multCos);
    multUv = centeredMultUv + vec2(0.5) + aUvOffsetMult;
    if (uFlipVMult != 0) multUv.y = 1.0 - multUv.y;
    vUvMult = multUv / max(uTexDivMult, vec2(1.0)) + uUvScrollRateMult * aAgeVelX.x;
    vColor = aColor;
    vErosionDrive = aErosionDrive;
    vErosionMixer = aErosionMixer;
}";

        private const string Frag = @"
in vec2 vUv;
in vec2 vUvMult;
in vec4 vColor;
in float vErosionDrive;
in vec4 vErosionMixer;
uniform sampler2D uTex;
uniform sampler2D uTexMult;
uniform int uHasTexMult;
uniform int uIsDistortion;
uniform sampler2D uDistortionTex;
uniform sampler2D uSceneTex;
uniform vec2 uViewportSize;
uniform float uDistortionStrength;
uniform float uAlphaCutoff;
uniform sampler2D uErosionTex;
uniform int uHasErosion;
uniform float uErosionFeatherIn;
uniform float uErosionFeatherOut;
out vec4 fragColor;
void main(){
    vec4 t = texture(uTex, vUv);
    if (uHasTexMult != 0) {
        vec4 mult = texture(uTexMult, vUvMult);
        t.rgb *= mult.rgb;
        float multAlpha = mult.a < 0.999 ? mult.a : max(mult.r, max(mult.g, mult.b));
        t.a *= multAlpha;
    }
    if (uHasErosion != 0) {
        float erosion = dot(texture(uErosionTex, vUv), vErosionMixer);
        float feather = max(0.001, mix(uErosionFeatherIn, uErosionFeatherOut, clamp(vErosionDrive, 0.0, 1.0)));
        t.a *= smoothstep(vErosionDrive - feather, vErosionDrive + feather, erosion);
    }
    if (t.a * vColor.a <= uAlphaCutoff) discard;
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
