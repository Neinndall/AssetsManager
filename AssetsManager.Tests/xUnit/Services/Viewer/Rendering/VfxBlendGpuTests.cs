using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    [Collection("Viewport native graphics")]
    public sealed class VfxBlendGpuTests
    {
        public static IEnumerable<object[]> BlendCases()
        {
            foreach (VfxPrimitiveKind primitive in new[]
                     {
                         VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.Mesh, VfxPrimitiveKind.AttachedMesh,
                         VfxPrimitiveKind.Beam, VfxPrimitiveKind.CameraSegmentBeam,
                         VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail
                     })
            foreach (bool fallback in new[] { false, true })
            {
                for (int mode = 0; mode <= 8; mode++)
                    yield return new object[] { primitive, mode, 0.25f, fallback };
                yield return new object[] { primitive, 5, 0f, fallback };
                yield return new object[] { primitive, 5, 1f, fallback };
            }
        }

        [Theory]
        [MemberData(nameof(BlendCases))]
        public void AuthoredBlendPreservesColorAndCoverageOverBackground(
            VfxPrimitiveKind primitive, int mode, float alpha, bool fallback)
        {
            string install = InstalledSkins.FindInstall();
            if (install == null) return;
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, InstalledSkins.Settings(install));
            uint target = Texture(gl, 64, 64, new byte[64 * 64 * 4]);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            uint white = Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
            try
            {
                bool mesh = primitive is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
                bool trail = primitive is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
                var definition = new VfxEmitterDefinition("blend", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, mode, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "white.tex", Vector2.One, 1, false, mesh, PrimitiveKind: primitive,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true },
                    PaletteDefinition: fallback ? new VfxPaletteDefinition(0, VfxCurve3.Const(Vector3.Zero)) : null,
                    Beam: new VfxBeamDefinition(0, 0, 1, VfxCurve3.Const(Vector3.One),
                        VfxCurve4.Const(Vector4.One), false, Vector3.Zero, Vector3.Zero),
                    Trail: new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0));
                Vector4 authored = new(0.6f * alpha, 0.4f * alpha, 0.2f * alpha, alpha);
                Vector4 prepared = VfxColorSemantics.PremultiplyForAddOrSubtract(authored, mode, false);
                int count = trail ? 2 : 1;
                var instances = new float[count * VfxPlaybackRuntime.InstanceStride];
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = white, TextureWidth = 1, TextureHeight = 1,
                    SystemOrigin = new Vector3(-0.75f, 0, 0.2f), SystemTarget = new Vector3(0.75f, 0, 0.2f),
                    SystemOrientation = Matrix4x4.Identity, InstanceCount = count
                };
                for (int index = 0; index < count; index++)
                {
                    int at = index * VfxPlaybackRuntime.InstanceStride;
                    instances[at] = trail ? (index == 0 ? -0.75f : 0.75f) : 0;
                    instances[at + 2] = 0.2f;
                    instances[at + 3] = 0.3f;
                    instances[at + 4] = instances[at + 18] = mesh || primitive == VfxPrimitiveKind.CameraQuad ? 0.5f : 0;
                    instances[at + 5] = prepared.X;
                    instances[at + 6] = prepared.Y;
                    instances[at + 7] = prepared.Z;
                    instances[at + 8] = prepared.W;
                    instances[at + 21] = instances[at + 22] = instances[at + 31] = instances[at + 32] = 1;
                    instances[at + 36] = instances[at + 40] = instances[at + 44] = 1;
                    emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                    {
                        Serial = (uint)index, Life = 1, BirthFrame = Matrix4x4.Identity,
                        TrailTiling = new Vector3(2, 1, 0),
                        BirthRotation = primitive == VfxPrimitiveKind.ArbitraryTrail
                            ? new Vector3(0, 0, MathF.PI / 2) : Vector3.Zero
                    });
                }
                emitter.Instances = instances;
                if (mesh)
                    renderer.UploadEmitterMesh(emitter,
                        new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                        new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                        new float[] { 0, 0, 1, 0, 0.5f, 1 }, null);

                Vector4 background = new(0.2f, 0.4f, 0.6f, 0.3f);
                gl.Viewport(0, 0, 64, 64);
                gl.ClearColor(background.X, background.Y, background.Z, background.W);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
                Assert.Equal(fallback ? "Palette with no rows." : null, renderer.GameParticleFallback(emitter, mesh));
                var pixel = new byte[4];
                gl.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
                Vector4 expected = Compose(mode, authored, background);
                for (int channel = 0; channel < 4; channel++)
                    Assert.InRange((int)pixel[channel], (int)MathF.Round(expected[channel] * 255) - 2,
                        (int)MathF.Round(expected[channel] * 255) + 2);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                gl.DeleteTexture(target);
                gl.DeleteTexture(white);
            }
        }

        private static Vector4 Compose(int mode, Vector4 color, Vector4 background)
        {
            Vector4 source = mode is 0 or 2
                ? new Vector4(color.X * color.W, color.Y * color.W, color.Z * color.W, 1)
                : color;
            Vector4 result = mode switch
            {
                0 => source + background,
                1 => source * source.W + background * (1 - source.W),
                2 => background * (Vector4.One - source),
                3 => source,
                4 => source * source.W + background,
                5 => source + background * (1 - source.W),
                6 => Vector4.Min(source, background),
                7 => Vector4.Max(source, background),
                8 => new Vector4(
                    source.X * (1 - background.W) + background.X * background.W,
                    source.Y * (1 - background.W) + background.Y * background.W,
                    source.Z * (1 - background.W) + background.Z * background.W,
                    source.W + background.W),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            return Vector4.Clamp(result, Vector4.Zero, Vector4.One);
        }

        private static uint Texture(GL gl, uint width, uint height, byte[] pixels)
        {
            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            return texture;
        }
    }
}
