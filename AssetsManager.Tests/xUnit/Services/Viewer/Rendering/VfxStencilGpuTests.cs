using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Tests.Support;
using AssetsManager.Tests.xUnit.Services.Viewer.Vfx;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxStencilGpuTests
{
    public static IEnumerable<object[]> Primitives()
    {
        foreach (var kind in new[] { VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.Mesh,
            VfxPrimitiveKind.AttachedMesh, VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail,
            VfxPrimitiveKind.Beam, VfxPrimitiveKind.CameraSegmentBeam, VfxPrimitiveKind.PlanarProjection })
            foreach (bool native in new[] { false, true }) yield return new object[] { kind, native };
    }

    [Theory]
    [MemberData(nameof(Primitives))]
    public void EveryPrimitiveWritesAMaskThatClipsAnotherEmitter(VfxPrimitiveKind kind, bool native)
    {
        using var frame = new Frame(native, kind == VfxPrimitiveKind.PlanarProjection);
        var writer = frame.Emitter(1, 127, Vector4.UnitX + Vector4.UnitW, .4f, kind);
        var tester = frame.Emitter(2, 63, Vector4.UnitY + Vector4.UnitW, 2);
        // Attached meshes and ground projections are at different depths from the billboard.
        // This check isolates their stencil coverage; depth failure has its own regression below.
        tester.Def = tester.Def with { MiscRenderFlags = 1 };
        frame.Draw(writer, tester);
        Assert.Equal(255, frame.Stencil(32));
        Assert.Equal(255, frame.Pixel(32)[1]);
        Assert.Equal(0, frame.Pixel(2)[1]);
        Assert.Equal(192, frame.Stencil(2));
        if (native && kind != VfxPrimitiveKind.PlanarProjection)
            Assert.Null(frame.Renderer.GameParticleFallback(writer, writer.Def.IsMeshPrimitive));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void ComparisonControlsColorAndMode4WritesOnlyWhereDifferent(byte mode, bool inside)
    {
        using var frame = new Frame(false);
        var writer = frame.Emitter(1, 5, new(1, 0, 0, 1), .4f);
        var tester = frame.Emitter(mode, 5, new(0, 1, 0, 1), 2);
        frame.Draw(writer, tester);
        Assert.Equal(inside ? 255 : 0, frame.Pixel(32)[1]);
        Assert.Equal(inside ? 0 : 255, frame.Pixel(2)[1]);
        Assert.Equal(inside ? 0 : 255, frame.Pixel(32)[0]);
        Assert.Equal(mode == 4 ? 197 : 192, frame.Stencil(2));
        Assert.Equal(197, frame.Stencil(32));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedAlphaOrDepthDoesNotWriteStencil(bool native, bool depthFailure)
    {
        using var frame = new Frame(native);
        var writer = frame.Emitter(1, 5, new(1, 0, 0, depthFailure ? 1 : 0), .4f);
        writer.Def = writer.Def with { BlendMode = 1, RenderState = writer.Def.RenderState with { AlphaReference = 128 } };
        if (depthFailure) frame.Clear(depth: .1f);
        frame.Draw(writer);
        Assert.Equal(192, frame.Stencil(32));
        Assert.Equal(0, frame.Pixel(32)[0]);
        if (native) Assert.Null(frame.Renderer.GameParticleFallback(writer, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenOrRemovedWriterUsesPreviewFallbackButVisibleEmptyWriterStillCounts(bool hidden)
    {
        using var frame = new Frame(false);
        var writer = frame.Emitter(1, 5, new(1, 0, 0, 1), .4f);
        var tester = frame.Emitter(2, 5, new(0, 1, 0, 1), 2);
        writer.IsVisible = !hidden;
        writer.InstanceCount = 0;
        frame.Draw(writer, tester);
        Assert.Equal(hidden ? 255 : 0, frame.Pixel(32)[1]);
        frame.Clear();
        frame.Draw(tester);
        Assert.Equal(255, frame.Pixel(32)[1]);
    }

    [Fact]
    public void Mode4RejectsItsOwnSecondDrawAndPlainEmittersIgnorePreviousMasks()
    {
        using var frame = new Frame(false);
        var writer = frame.Emitter(4, 5, new(.25f, 0, 0, 1), .4f);
        writer.Def = writer.Def with { BlendMode = 0 };
        var scene = new VfxStencilScene();
        scene.Add(writer.Def, true);
        using (frame.Renderer.BeginRenderBatch(scene))
        {
            frame.Draw(writer);
            frame.Draw(writer);
            Assert.InRange(frame.Pixel(32)[0], 62, 65);
            var plain = frame.Emitter(0, 5, new(0, 1, 0, 1), 2);
            frame.Draw(plain);
            Assert.Equal(255, frame.Pixel(32)[1]);
            Assert.Equal(255, frame.Pixel(2)[1]);
        }
        Assert.Equal(197, frame.Stencil(32));
    }

    [Fact]
    public void SharedSceneReferencesAndMasksSurviveAcrossOwnersAndPhases()
    {
        using var frame = new Frame(false);
        using var other = new VfxOpenGlRenderer();
        other.Initialize(frame.Gl);
        var writer = frame.Emitter(1, 7, new(1, 0, 0, 1), .4f);
        writer.Def = writer.Def with { RenderState = writer.Def.RenderState with { StencilReferenceId = 0x1234 } };
        var tester = frame.Emitter(2, 9, new(0, 1, 0, 1), 2);
        tester.Def = tester.Def with { RenderState = tester.Def.RenderState with { StencilReferenceId = 0x1234 } };
        var passes = new IPreparedParticlePass[]
        {
            new ParticlePass(frame.Renderer, writer, false), new ParticlePass(other, tester, true)
        };
        PreparedParticlePasses.Render(passes, new List<IDisposable>());
        Assert.Equal(255, frame.Pixel(32)[1]);
        Assert.Equal(0, frame.Pixel(2)[1]);
        Assert.Equal(255, frame.Stencil(32));
        frame.Clear();
        PreparedParticlePasses.Render(new IPreparedParticlePass[] { passes[1] }, new List<IDisposable>());
        Assert.Equal(255, frame.Pixel(2)[1]);
    }

    [Fact]
    public void RestoresDistinctFrontAndBackStencilStateAtBatchBoundary()
    {
        using var frame = new Frame(false);
        var gl = frame.Gl;
        gl.Enable(EnableCap.StencilTest);
        gl.StencilFuncSeparate(TriangleFace.Front, StencilFunction.Less, 8, 0xa5);
        gl.StencilFuncSeparate(TriangleFace.Back, StencilFunction.Greater, 9, 0x5a);
        gl.StencilMaskSeparate(TriangleFace.Front, 0x11);
        gl.StencilMaskSeparate(TriangleFace.Back, 0x22);
        gl.StencilOpSeparate(TriangleFace.Front, StencilOp.Incr, StencilOp.Decr, StencilOp.Invert);
        gl.StencilOpSeparate(TriangleFace.Back, StencilOp.Zero, StencilOp.Replace, StencilOp.Keep);
        var names = new[] { GLEnum.StencilFunc, GLEnum.StencilRef, GLEnum.StencilValueMask, GLEnum.StencilWritemask,
            GLEnum.StencilFail, GLEnum.StencilPassDepthFail, GLEnum.StencilPassDepthPass,
            GLEnum.StencilBackFunc, GLEnum.StencilBackRef, GLEnum.StencilBackValueMask, GLEnum.StencilBackWritemask,
            GLEnum.StencilBackFail, GLEnum.StencilBackPassDepthFail, GLEnum.StencilBackPassDepthPass };
        int[] before = Array.ConvertAll(names, name => gl.GetInteger(name));
        using (frame.Renderer.BeginRenderBatch())
        {
            frame.Draw(frame.Emitter(1, 5, new(1, 0, 0, 1), .4f));
            Assert.Equal(0x3f, gl.GetInteger(GLEnum.StencilWritemask));
            Assert.Equal(0x3f, gl.GetInteger(GLEnum.StencilBackWritemask));
        }
        Assert.True(gl.IsEnabled(EnableCap.StencilTest));
        Assert.Equal(before, Array.ConvertAll(names, name => gl.GetInteger(name)));
    }

    [Fact]
    public void WireframeNeverWritesStencil()
    {
        using var frame = new Frame(false);
        var writer = frame.Emitter(1, 5, new(1, 0, 0, 1), 2);
        frame.Renderer.Render(new[] { new VfxRenderQueueEntry(writer, 0, 0) }, frame.ViewProjection, frame.View, true);
        for (int x = 0; x < 64; x++) Assert.Equal(192, frame.Stencil(x));
    }

    private sealed class ParticlePass(VfxOpenGlRenderer renderer, VfxPlaybackRuntime.EmitterState emitter,
        bool post) : IPreparedParticlePass
    {
        private VfxStencilScene _scene;
        public void PrepareStencilScene(VfxStencilScene scene) { scene.Add(emitter.Def, emitter.IsVisible); _scene = scene; }
        public IDisposable BeginPreparedRenderBatch() => renderer.BeginRenderBatch(_scene);
        private void Draw() => renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
        public void RenderPreparedColorPass() { if (!post) Draw(); }
        public void RenderPreparedPostColorPass() { if (post) Draw(); }
        public void CapturePreparedDistortionFrame() { }
        public void RenderPreparedDistortionPass() { }
    }

    private sealed class Frame : IDisposable
    {
        private readonly HiddenWglContext _context = new();
        internal readonly GL Gl;
        internal readonly VfxOpenGlRenderer Renderer = new();
        internal readonly Matrix4x4 View, ViewProjection;
        private readonly uint _target, _white, _frame, _depth;
        internal Frame(bool native, bool projection = false)
        {
            Gl = GL.GetApi(_context.GetProcAddress);
            string install = native ? InstalledSkins.FindInstall() : null;
            if (native) Assert.NotNull(install);
            Renderer.Initialize(Gl, native ? InstalledSkins.Settings(install) : null);
            _target = Texture(64, new byte[64 * 64 * 4]);
            _white = Texture(1, new byte[] { 255, 255, 255, 255 });
            _depth = Gl.GenRenderbuffer();
            Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, 64, 64);
            _frame = Gl.GenFramebuffer();
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _frame);
            Gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _target, 0);
            Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                RenderbufferTarget.Renderbuffer, _depth);
            Assert.Equal(GLEnum.FramebufferComplete, Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            View = projection ? Matrix4x4.CreateLookAt(new(0, 3, 0), Vector3.Zero, -Vector3.UnitZ) : Matrix4x4.Identity;
            ViewProjection = projection ? View * Matrix4x4.CreateOrthographic(2, 2, .1f, 10) : Matrix4x4.Identity;
            Gl.Viewport(0, 0, 64, 64);
            Clear();
        }

        internal void Clear(float depth = 1)
        {
            Gl.ColorMask(true, true, true, true);
            Gl.DepthMask(true);
            Gl.StencilMask(0xff);
            Gl.ClearColor(0, 0, 0, 0);
            Gl.ClearDepth(depth);
            // Keep the upper bits populated to check that particles own only the lower six.
            Gl.ClearStencil(192);
            Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        }

        internal VfxPlaybackRuntime.EmitterState Emitter(byte mode, byte reference, Vector4 color, float size,
            VfxPrimitiveKind kind = VfxPrimitiveKind.CameraQuad)
        {
            bool mesh = kind is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
            bool trail = kind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
            var def = VfxStencilSceneTests.Definition(mode, reference) with
            {
                PrimitiveKind = kind, IsMeshPrimitive = mesh,
                Trail = trail ? new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0) : null,
                Beam = kind is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam ?
                    new VfxBeamDefinition(0, 0, 1, VfxCurve3.Const(Vector3.One), VfxCurve4.Const(Vector4.One),
                        false, Vector3.Zero, Vector3.Zero) : null
            };
            int count = trail ? 2 : 1;
            var instances = new float[count * VfxPlaybackRuntime.InstanceStride];
            var emitter = new VfxPlaybackRuntime.EmitterState
            {
                Def = def, Texture = _white, TextureWidth = 1, TextureHeight = 1,
                SystemOrigin = new(-.75f, 0, .2f), SystemTarget = new(.75f, 0, .2f),
                SystemOrientation = Matrix4x4.Identity, InstanceCount = count
            };
            for (int index = 0; index < count; index++)
            {
                int at = index * VfxPlaybackRuntime.InstanceStride;
                instances[at] = trail ? (index == 0 ? -.75f : .75f) : 0;
                instances[at + 2] = .2f;
                instances[at + 3] = size;
                instances[at + 4] = instances[at + 18] = size;
                instances[at + 5] = color.X; instances[at + 6] = color.Y;
                instances[at + 7] = color.Z; instances[at + 8] = color.W;
                instances[at + 21] = instances[at + 22] = instances[at + 31] = instances[at + 32] = 1;
                instances[at + 36] = instances[at + 40] = instances[at + 44] = 1;
                emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                {
                    Serial = (uint)index, Life = 1, BirthFrame = Matrix4x4.Identity, TrailTiling = new(2, 1, 0),
                    BirthRotation = kind == VfxPrimitiveKind.ArbitraryTrail ? new(0, 0, MathF.PI / 2) : Vector3.Zero
                });
            }
            emitter.Instances = instances;
            if (mesh) Renderer.UploadEmitterMesh(emitter, new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, new float[] { 0, 0, 1, 0, .5f, 1 }, null);
            return emitter;
        }

        internal void Draw(params VfxPlaybackRuntime.EmitterState[] emitters)
        {
            var queue = new VfxRenderQueueEntry[emitters.Length];
            for (int index = 0; index < emitters.Length; index++) queue[index] = new(emitters[index], 0, index);
            Renderer.Render(queue, ViewProjection, View);
        }
        internal byte[] Pixel(int x)
        {
            byte[] pixel = new byte[4];
            Gl.ReadPixels(x, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
            return pixel;
        }
        internal byte Stencil(int x)
        {
            Span<byte> pixel = stackalloc byte[4];
            Gl.ReadPixels(x, 32, 1, 1, PixelFormat.StencilIndex, PixelType.UnsignedByte, pixel);
            return pixel[0];
        }
        private uint Texture(uint size, byte[] pixels)
        {
            uint texture = Gl.GenTexture();
            Gl.BindTexture(TextureTarget.Texture2D, texture);
            Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, size, size, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            return texture;
        }
        public void Dispose()
        {
            Assert.Equal(GLEnum.NoError, Gl.GetError());
            Renderer.Dispose();
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            Gl.DeleteFramebuffer(_frame); Gl.DeleteRenderbuffer(_depth);
            Gl.DeleteTexture(_target); Gl.DeleteTexture(_white);
            Gl.Dispose(); _context.Dispose();
        }
    }
}
