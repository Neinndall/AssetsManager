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
    public sealed class VfxEmitterScrollGpuTests
    {
        public static IEnumerable<object[]> ScrollCases()
        {
            foreach (VfxPrimitiveKind primitive in new[]
                     {
                         VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.ArbitraryQuad, VfxPrimitiveKind.Ray,
                         VfxPrimitiveKind.Beam, VfxPrimitiveKind.CameraSegmentBeam,
                         VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail,
                         VfxPrimitiveKind.Mesh, VfxPrimitiveKind.AttachedMesh
                     })
            for (int flips = 0; flips < 4; flips++)
            foreach (bool mult in new[] { false, true })
            foreach (bool fallback in new[] { false, true })
                yield return new object[] { primitive, flips, mult, fallback };
        }

        [Theory]
        [MemberData(nameof(ScrollCases))]
        public void EmitterScrollRunsAfterFlipsAndDoesNotMoveMeshTextures(
            VfxPrimitiveKind primitive, int flips, bool mult, bool fallback)
        {
            string install = InstalledSkins.FindInstall();
            if (install == null) return;
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, fallback ? null : InstalledSkins.Settings(install));

            uint target = Texture(gl, 64, 64, new byte[64 * 64 * 4]);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            uint white = Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
            var gradientPixels = new byte[64 * 64 * 4];
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int at = (y * 64 + x) * 4;
                gradientPixels[at] = (byte)(4 * x + 2);
                gradientPixels[at + 1] = (byte)(4 * y + 2);
                gradientPixels[at + 2] = 128;
                gradientPixels[at + 3] = 255;
            }
            uint gradient = Texture(gl, 64, 64, gradientPixels);
            try
            {
                bool mesh = primitive is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
                bool trail = primitive is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
                var scroll = new Vector2(0.125f, 0.1875f);
                var definition = new VfxEmitterDefinition("scroll", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "base.tex", Vector2.One, 1, false, mesh, PrimitiveKind: primitive,
                    RenderState: VfxEmitterRenderState.Default with
                    {
                        DisableBackfaceCull = true, TextureAddressMode = 2,
                        FlipU = !mult && (flips & 1) != 0, FlipV = !mult && (flips & 2) != 0
                    },
                    EmitterUvScrollRate: mult ? Vector2.Zero : scroll,
                    TextureMultPath: mult ? "mult.tex" : null,
                    TextureMultAddressMode: 2,
                    TextureMultFlipU: mult && (flips & 1) != 0,
                    TextureMultFlipV: mult && (flips & 2) != 0,
                    TextureMultEmitterUvScrollRate: mult ? scroll : Vector2.Zero,
                    Beam: new VfxBeamDefinition(0, 0, 1, VfxCurve3.Const(Vector3.One),
                        VfxCurve4.Const(Vector4.One), false, Vector3.Zero, Vector3.Zero),
                    Trail: new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0));

                int count = trail ? 2 : 1;
                var instances = new float[count * VfxPlaybackRuntime.InstanceStride];
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = mult ? white : gradient, TextureMult = mult ? gradient : 0,
                    SystemOrigin = new Vector3(-0.75f, 0, 0.2f), SystemTarget = new Vector3(0.75f, 0, 0.2f),
                    SystemOrientation = Matrix4x4.Identity, InstanceCount = count
                };
                for (int index = 0; index < count; index++)
                {
                    int at = index * VfxPlaybackRuntime.InstanceStride;
                    instances[at] = trail ? (index == 0 ? -0.75f : 0.75f) : 0;
                    instances[at + 2] = 0.2f;
                    instances[at + 3] = 0.3f;
                    instances[at + 4] = instances[at + 18] =
                        primitive is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam ? 0 : 0.5f;
                    instances[at + 5] = instances[at + 6] = instances[at + 7] = instances[at + 8] = 1;
                    instances[at + 21] = instances[at + 22] = instances[at + 31] = instances[at + 32] = 1;
                    instances[at + 36] = instances[at + 40] = instances[at + 44] = 1;
                    if (primitive == VfxPrimitiveKind.Ray)
                    {
                        // Keep the ray's longitudinal axis visible under the identity camera.
                        instances[at + 40] = instances[at + 44] = 0;
                        instances[at + 41] = -1;
                        instances[at + 43] = 1;
                    }
                    emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                    {
                        Serial = (uint)index, Life = 1, BirthFrame = Matrix4x4.Identity,
                        TrailTiling = trail ? new Vector3(2, 1, 0) : Vector3.Zero,
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
                byte[] Draw(float time)
                {
                    emitter.RenderTime = time;
                    gl.ClearColor(0, 0, 0, 0);
                    gl.Clear(ClearBufferMask.ColorBufferBit);
                    renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
                    var pixels = new byte[64 * 64 * 4];
                    gl.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                    return pixels;
                }

                byte[] before = Draw(0);
                byte[] after = Draw(1);
                Assert.Equal(fallback
                    ? "ShaderCache.dx11.wad.client was not found in the configured game installs." : null,
                    renderer.GameParticleFallback(emitter, mesh));
                // An interior linear texture encodes UV directly. A positive pan must add the
                // same channel increments under every flip; beam packing transposes both axes.
                bool transpose = primitive is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam;
                int deltaR = mesh ? 0 : transpose ? 48 : 32;
                int deltaG = mesh ? 0 : transpose ? 32 : 48;
                int[] interior = Enumerable.Range(0, 64 * 64).Where(at =>
                    before[at * 4] is > 32 and < 190 && before[at * 4 + 1] is > 32 and < 190 &&
                    before[at * 4 + 3] > 240).ToArray();
                Assert.NotEmpty(interior);
                foreach (int at in interior)
                {
                    Assert.True(Math.Abs(after[at * 4] - before[at * 4] - deltaR) <= 2 &&
                                Math.Abs(after[at * 4 + 1] - before[at * 4 + 1] - deltaG) <= 2,
                        $"{primitive}, flips={flips}, mult={mult}, fallback={fallback}, pixel={at}: " +
                        $"expected delta ({deltaR},{deltaG}), got ({after[at * 4] - before[at * 4]}," +
                        $"{after[at * 4 + 1] - before[at * 4 + 1]}).");
                    Assert.Equal(before[at * 4 + 2], after[at * 4 + 2]);
                    Assert.Equal(before[at * 4 + 3], after[at * 4 + 3]);
                }
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                foreach (uint texture in new[] { target, white, gradient }) gl.DeleteTexture(texture);
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
