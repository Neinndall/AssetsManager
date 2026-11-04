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
            renderer.Initialize(gl, fallback ? null : InstalledSkins.Settings(install));
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
                    Beam: new VfxBeamDefinition(0, 0, 2, VfxCurve3.Const(Vector3.One),
                        VfxCurve4.Const(Vector4.One), false, Vector3.Zero, Vector3.Zero),
                    Trail: new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0));
                Vector4 authored = new(0.6f * alpha, 0.4f * alpha, 0.2f * alpha, alpha);
                Vector4 prepared = VfxColorSemantics.PremultiplyForAddOrSubtract(authored, mode, false);
                int count = trail || primitive == VfxPrimitiveKind.CameraSegmentBeam ? 2 : 1;
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
                    if (emitter.Def.PrimitiveKind == VfxPrimitiveKind.CameraSegmentBeam)
                    {
                        instances[at] = emitter.SystemOrigin.X;
                        instances[at + 1] = emitter.SystemOrigin.Y;
                        instances[at + 2] = emitter.SystemOrigin.Z;
                    }
                    emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                    {
                        Serial = (uint)index, Ordinal = (ushort)index, Life = 1, BirthFrame = Matrix4x4.Identity,
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
                Assert.Equal(fallback ? "ShaderCache.dx11.wad.client was not found in the configured game installs." : null, renderer.GameParticleFallback(emitter, mesh));
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EqualDistanceQuadsComposeInStableInputOrder(bool fallback)
        {
            string install = InstalledSkins.FindInstall();
            Assert.False(string.IsNullOrWhiteSpace(install), "An installed client is required for this GPU regression.");
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
            try
            {
                var definition = new VfxEmitterDefinition("stable alpha", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 1, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "white.tex", Vector2.One, 1, false, false, PrimitiveKind: VfxPrimitiveKind.CameraQuad,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true },
                    PaletteDefinition: fallback ? new VfxPaletteDefinition(0, VfxCurve3.Const(Vector3.Zero)) : null);
                const int stride = VfxPlaybackRuntime.InstanceStride;
                var instances = new float[2 * stride];
                Vector4 red = new(1, 0, 0, 0.5f), blue = new(0, 0, 1, 0.5f);
                for (int index = 0; index < 2; index++)
                {
                    int at = index * stride;
                    instances[at + 2] = 0.2f;
                    instances[at + 3] = instances[at + 4] = instances[at + 18] = 0.5f;
                    Vector4 color = index == 0 ? red : blue;
                    instances[at + 5] = color.X;
                    instances[at + 6] = color.Y;
                    instances[at + 7] = color.Z;
                    instances[at + 8] = color.W;
                    instances[at + 21] = instances[at + 22] = instances[at + 31] = instances[at + 32] = 1;
                    instances[at + 36] = instances[at + 40] = instances[at + 44] = 1;
                }
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = white, TextureWidth = 1, TextureHeight = 1,
                    Instances = instances, InstanceCount = 2
                };
                gl.Viewport(0, 0, 64, 64);
                gl.ClearColor(0, 0, 0, 0);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
                Assert.Equal(fallback ? "ShaderCache.dx11.wad.client was not found in the configured game installs." : null, renderer.GameParticleFallback(emitter, false));
                var pixel = new byte[4];
                gl.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
                // LTK's stable tie draws red first, then blue, with authored alpha blending.
                Vector4 expected = Compose(1, blue, Compose(1, red, Vector4.Zero));
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

        public static IEnumerable<object[]> EmitterOverlapCases()
        {
            foreach (int firstMode in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })
            foreach (bool fallback in new[] { false, true })
            // Two visible particles, one occluded, both occluded, and disabled depth testing.
            for (int depthCase = 0; depthCase < 4; depthCase++)
                yield return new object[] { firstMode, fallback, depthCase };
        }

        [Theory]
        [MemberData(nameof(EmitterOverlapCases))]
        public void DistinctEmittersRespectAuthoredPassesAndSceneDepth(int firstMode, bool fallback, int depthCase)
        {
            string install = InstalledSkins.FindInstall();
            Assert.NotNull(install);
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, fallback ? null : InstalledSkins.Settings(install));
            uint target = Texture(gl, 64, 64, new byte[64 * 64 * 4]);
            uint white = Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
            uint framebuffer = gl.GenFramebuffer();
            uint depth = gl.GenRenderbuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, 64, 64);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, depth);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            try
            {
                Vector4 background = new(.08f, .16f, .24f, .32f);
                Vector4 red = new(.6f, .1f, .05f, .4f), blue = new(.05f, .1f, .7f, .5f);
                float sceneDepth = depthCase switch { 0 => .9f, 1 => .5f, _ => .1f };
                foreach (bool sameGraph in new[] { false, true })
                foreach (bool reverseCreation in new[] { false, true })
                foreach (bool blueEarly in new[] { false, true })
                {
                    object graph = new();
                    var first = State("red", firstMode, red, -.2f, blueEarly ? 15 : 5, graph, 0);
                    var second = State("blue", 1, blue, .2f, blueEarly ? 5 : 15, sameGraph ? graph : new object(), 1);
                    IReadOnlyList<VfxPlaybackRuntime.EmitterState>[] sources = reverseCreation
                        ? new[] { new[] { second }, new[] { first } }
                        : new[] { new[] { first }, new[] { second } };
                    var queue = VfxRenderQueue.Build(sources);
                    gl.Viewport(0, 0, 64, 64);
                    gl.Disable(EnableCap.ScissorTest);
                    gl.ColorMask(true, true, true, true);
                    gl.DepthMask(true);
                    gl.ClearDepth(sceneDepth);
                    gl.ClearColor(background.X, background.Y, background.Z, background.W);
                    gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    renderer.Render(queue, Matrix4x4.Identity, Matrix4x4.Identity);

                    // The oracle uses explicit pass precedence, opaque partitioning and fragment tests.
                    // It never derives the expected image from the production queue's sorted output.
                    Vector4 expected = background;
                    float expectedDepth = sceneDepth;
                    bool firstBeforeSecond = firstMode == 3 || !blueEarly;
                    foreach (bool drawFirst in firstBeforeSecond ? new[] { true, false } : new[] { false, true })
                    {
                        float fragmentDepth = drawFirst ? .4f : .6f;
                        if (depthCase != 3 && fragmentDepth > expectedDepth) continue;
                        expected = Compose(drawFirst ? firstMode : 1, drawFirst ? red : blue, expected);
                        if (drawFirst && firstMode == 3 && depthCase != 3) expectedDepth = fragmentDepth;
                    }
                    var pixel = new byte[4];
                    gl.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
                    for (int channel = 0; channel < 4; channel++)
                        Assert.True(Math.Abs(pixel[channel] - MathF.Round(expected[channel] * 255)) <= 3,
                            $"mode={firstMode}, fallback={fallback}, depth={depthCase}, sharedGraph={sameGraph}, reverse={reverseCreation}, blueEarly={blueEarly}, channel={channel}: expected {expected[channel] * 255}, got {pixel[channel]}.");
                    var depthPixel = new float[1];
                    gl.ReadPixels(32, 32, 1, 1, PixelFormat.DepthComponent, PixelType.Float, depthPixel.AsSpan());
                    Assert.InRange(Math.Abs(depthPixel[0] - expectedDepth), 0, .00001f);
                    Assert.Equal(fallback ? "ShaderCache.dx11.wad.client was not found in the configured game installs." : null,
                        renderer.GameParticleFallback(first, false));
                    Assert.Equal(GLEnum.NoError, gl.GetError());
                }

                VfxPlaybackRuntime.EmitterState State(string name, int mode, Vector4 color, float z, int pass, object graph, int sourceOrder)
                {
                    var definition = new VfxEmitterDefinition(name, VfxCurveF.Const(1), VfxCurveF.Const(1),
                        null, 0, 0, false, false, mode, VfxCurve3.Const(Vector3.One), null,
                        VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                        "white.tex", Vector2.One, 1, false, false, PrimitiveKind: VfxPrimitiveKind.CameraQuad,
                        MiscRenderFlags: (byte)(depthCase == 3 ? 1 : 0),
                        RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true, RenderPass = pass },
                        PaletteDefinition: fallback ? new VfxPaletteDefinition(0, VfxCurve3.Const(Vector3.Zero)) : null);
                    var row = new float[VfxPlaybackRuntime.InstanceStride];
                    row[2] = z;
                    row[3] = row[4] = row[18] = .5f;
                    Vector4 prepared = mode is 0 or 2
                        ? new Vector4(color.X * color.W, color.Y * color.W, color.Z * color.W, 1) : color;
                    row[5] = prepared.X; row[6] = prepared.Y; row[7] = prepared.Z; row[8] = prepared.W;
                    row[21] = row[22] = row[31] = row[32] = 1;
                    row[36] = row[40] = row[44] = 1;
                    return new VfxPlaybackRuntime.EmitterState
                    {
                        Def = definition, Texture = white, TextureWidth = 1, TextureHeight = 1,
                        Instances = row, InstanceCount = 1, RenderGraphKey = graph,
                        SourceOrder = sourceOrder, RenderRank = sourceOrder
                    };
                }
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                gl.DeleteRenderbuffer(depth);
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
