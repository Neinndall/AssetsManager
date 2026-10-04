using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    [Collection("Viewport native graphics")]
    public sealed class VfxColorRampGpuTests
    {
        public static IEnumerable<object[]> LookupCases()
        {
            foreach (VfxPrimitiveKind primitive in new[]
                     {
                         VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.Mesh, VfxPrimitiveKind.AttachedMesh,
                         VfxPrimitiveKind.Beam, VfxPrimitiveKind.CameraSegmentBeam,
                         VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail
                     })
            {
                for (int driver = 0; driver <= 3; driver++)
                    yield return new object[] { primitive, driver, false, false };
                if (primitive is not (VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh))
                    yield return new object[] { primitive, 1, true, false };
                else
                    for (int driver = 0; driver <= 3; driver++)
                        yield return new object[] { primitive, driver, false, true };
            }
        }

        public static IEnumerable<object[]> LookupAxesCases()
        {
            foreach (object[] test in LookupCases())
                foreach (bool vertical in new[] { false, true })
                    yield return test.Concat(new object[] { vertical }).ToArray();
        }

        [Theory]
        [MemberData(nameof(LookupAxesCases))]
        public void RampSamplesTheAuthoredParticleDriver(VfxPrimitiveKind primitive, int driver, bool mult, bool fallback, bool vertical)
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
            uint ramp = Texture(gl, vertical ? 1u : 2u, vertical ? 2u : 1u, new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 });
            try
            {
                bool mesh = primitive is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
                bool trail = primitive is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
                var definition = new VfxEmitterDefinition("ramp", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 4, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "base.tex", Vector2.One, 1, false, mesh, PrimitiveKind: primitive,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true },
                    TextureMultPath: mult ? "mult.tex" : null,
                    PaletteDefinition: fallback ? new VfxPaletteDefinition(0, VfxCurve3.Const(Vector3.Zero)) : null,
                    ParticleColorTexturePath: "ramp.tex", ColorLookUpTypeX: vertical ? 0 : driver,
                    ColorLookUpTypeY: vertical ? driver : 0,
                    ColorLookUpScales: vertical ? new Vector2(0.5f, 1) : new Vector2(1, 0.5f),
                    ColorLookUpOffsets: vertical ? new Vector2(0.35f, 0.15f) : new Vector2(0.15f, 0.35f),
                    Beam: new VfxBeamDefinition(0, 0, 1, VfxCurve3.Const(Vector3.One),
                        VfxCurve4.Const(Vector4.One), false, Vector3.Zero, Vector3.Zero),
                    Trail: new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0));
                int count = trail ? 2 : 1;
                var instances = new float[count * VfxPlaybackRuntime.InstanceStride];
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = white, TextureMult = mult ? white : 0, ColorGradientTexture = ramp,
                    SystemOrigin = new Vector3(-0.75f, 0, 0.2f), SystemTarget = new Vector3(0.75f, 0, 0.2f),
                    SystemOrientation = Matrix4x4.Identity, InstanceCount = count
                };
                for (int index = 0; index < count; index++)
                {
                    int at = index * VfxPlaybackRuntime.InstanceStride;
                    instances[at] = trail ? (index == 0 ? -0.75f : 0.75f) : 0;
                    instances[at + 2] = 0.2f;
                    instances[at + 3] = 0.3f;
                    instances[at + 4] = mesh || primitive == VfxPrimitiveKind.CameraQuad ? 0.5f : 0;
                    instances[at + 18] = mesh || primitive == VfxPrimitiveKind.CameraQuad ? 0.5f : 0;
                    instances[at + 5] = instances[at + 6] = instances[at + 7] = instances[at + 8] = 1;
                    instances[at + 11] = fallback ? 0 : 1;
                    instances[at + 12] = fallback ? 1 : 0;
                    instances[at + 21] = instances[at + 22] = instances[at + 31] = instances[at + 32] = 1;
                    instances[at + 34] = 1;
                    instances[at + 36] = instances[at + 40] = instances[at + 44] = 1;
                    emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                    {
                        Serial = (uint)index, Life = 1, BirthFrame = Matrix4x4.Identity,
                        TrailTiling = trail ? new Vector3(2, vertical && mult ? 0 : 1, 0) : Vector3.Zero,
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

                gl.Viewport(0, 0, 64, 64);
                gl.ClearColor(0, 0, 0, 1);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
                Assert.Equal(fallback ? "Palette with no rows." : null, renderer.GameParticleFallback(emitter, mesh));
                var pixels = new byte[64 * 64 * 4];
                gl.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                int[] lit = Enumerable.Range(0, 64 * 64)
                    .Where(at => pixels[at * 4] > 32 || pixels[at * 4 + 2] > 32).ToArray();
                Assert.NotEmpty(lit);
                if (mult)
                {
                    // MULT_PASS routes the quad/ribbon ramp through the varying mult UV, not particle age.
                    Assert.Contains(lit, at => pixels[at * 4] > 240 && pixels[at * 4 + 2] < 8);
                    Assert.Contains(lit, at => pixels[at * 4 + 2] > 240 && pixels[at * 4] < 8);
                }
                else
                {
                    // Fallback controls reverse age/speed to exercise non-default drivers; random is one in both paths.
                    int channel = (driver == 2 && !fallback) || (driver == 1 && fallback) ? 0 : 2;
                    int wrong = lit.Count(at => pixels[at * 4 + channel] < 240 || pixels[at * 4 + (2 - channel)] > 8);
                    int first = lit[0] * 4;
                    Assert.True(wrong == 0,
                        $"{primitive} driver={driver}: {wrong}/{lit.Length} pixels have the wrong ramp color; " +
                        $"expected channel {channel}, first pixel=({pixels[first]}, {pixels[first + 2]}).");
                }
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                gl.DeleteTexture(target);
                gl.DeleteTexture(white);
                gl.DeleteTexture(ramp);
            }
        }

        private static uint Texture(GL gl, uint width, uint height, byte[] pixels)
        {
            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            return texture;
        }
    }
}
