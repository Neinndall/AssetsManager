using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxGroundCompositionGpuTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GroundAttachedMeshKeepsCharacterVisibleAndDefaultAttachedMeshStillCoversIt(bool native, bool ground)
    {
        using var frame = new Frame();
        using var session = new VfxRenderSession(null);
        string install = InstalledSkins.FindInstall();
        if (native) Assert.NotNull(install);
        session.Initialize(frame.Gl, native ? InstalledSkins.Settings(install) : null);
        var emitter = new VfxEmitterDefinition("attached", VfxCurveF.Const(1), VfxCurveF.Const(1),
            null, 0, 0, true, false, 1, VfxCurve3.Const(Vector3.One), null,
            VfxCurve4.Const(new Vector4(1, .25f, 0, 1)), null, null, null, null,
            VfxCurve3.Const(Vector3.Zero), "", Vector2.One, 1, false, true,
            PrimitiveKind: VfxPrimitiveKind.AttachedMesh, IsGroundLayer: ground, MiscRenderFlags: 1,
            RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true });
        var system = new VfxSystemDefinition(1, "ground", "ground", new[] { emitter });
        session.SetSystem(new VfxSystemModel { Definition = system, TotalDuration = 1 });
        session.SetViewportSize(64, 64);
        session.Seek(.1);
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 10), Vector3.Zero, Vector3.UnitY);
        var projection = view * Matrix4x4.CreateOrthographic(2, 2, .1f, 100);
        Assert.True(session.PrepareRenderFrame(projection, view));
        var state = session.Graphs.Single().Root.Emitters.Single();
        Assert.Equal(1, state.InstanceCount);
        var renderer = (VfxOpenGlRenderer)typeof(VfxRenderSession)
            .GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        renderer.UploadEmitterMesh(state, new float[] { -.8f, -.8f, 0, .8f, -.8f, 0, 0, .8f, 0 },
            new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, new float[] { 0, 0, 1, 0, .5f, 1 }, null);
        uint white = frame.Gl.GenTexture();
        try
        {
            frame.Gl.BindTexture(TextureTarget.Texture2D, white);
            frame.Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 }.AsSpan());
            frame.Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            frame.Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            state.Texture = white;
            state.TextureWidth = state.TextureHeight = 1;
            PreparedParticlePasses.Render(new IPreparedParticlePass[] { session }, new List<IDisposable>(),
                () => frame.Character());

            byte[] center = frame.Pixel(32, 32);
            Assert.Equal(ground ? 255 : 0, center[2]);
            Assert.True(frame.Pixel(32, 12)[0] > 200, "The attached effect must remain outside the character.");
            if (native) Assert.Null(session.GameParticleFallback(state, true));
            Assert.Equal(GLEnum.NoError, frame.Gl.GetError());
        }
        finally
        {
            // The session owns uploaded emitter textures after the test assigns them.
            state.Texture = 0;
            frame.Gl.DeleteTexture(white);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneDepthPrepassResizesAndPreservesBothDepthFormats(bool floatingDepth)
    {
        using var frame = new Frame(floatingDepth);
        using var prepass = new GlSceneDepthPass(frame.Gl);
        int target = frame.Gl.GetInteger(GLEnum.DrawFramebufferBinding);
        foreach (uint size in new uint[] { 64, 32, 64 })
        {
            prepass.Render(size, size, () =>
            {
                frame.Gl.ClearColor(1, 0, 0, 1);
                frame.Gl.Clear(ClearBufferMask.ColorBufferBit);
            }, () =>
            {
                Assert.Equal(255, frame.Pixel(0, 0)[0]);
                Assert.InRange(frame.Depth(0, 0), .79f, .81f);
            });
            Assert.Equal(target, frame.Gl.GetInteger(GLEnum.DrawFramebufferBinding));
            Assert.Equal(0, frame.Pixel(0, 0)[0]);
            Assert.Equal(GLEnum.NoError, frame.Gl.GetError());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneDepthPrepassPreservesVisibleColorDepthAndFramebufferOnSuccessAndFailure(bool fail)
    {
        using var frame = new Frame();
        using var prepass = new GlSceneDepthPass(frame.Gl);
        using var captured = new GlSceneCapture(frame.Gl);
        int framebuffer = frame.Gl.GetInteger(GLEnum.DrawFramebufferBinding);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            Action render = () => prepass.Render(64, 64, () => frame.Character(), () =>
            {
                captured.Capture(64, 64, false, true);
                Assert.Equal(255, frame.Pixel(32, 32)[2]);
                Assert.InRange(frame.Depth(32, 32), .19f, .21f);
                if (fail) throw new InvalidOperationException("capture");
            });
            if (fail) Assert.Throws<InvalidOperationException>(render);
            else render();

            Assert.Equal(framebuffer, frame.Gl.GetInteger(GLEnum.DrawFramebufferBinding));
            Assert.Equal(framebuffer, frame.Gl.GetInteger(GLEnum.ReadFramebufferBinding));
            Assert.Equal(0, frame.Pixel(32, 32)[2]);
            Assert.InRange(frame.Depth(32, 32), .79f, .81f);
            Assert.NotEqual(0u, captured.DepthTexture);
            Assert.Equal(GLEnum.NoError, frame.Gl.GetError());
        }
    }

    private sealed class Frame : IDisposable
    {
        private readonly HiddenWglContext _context = new();
        internal readonly GL Gl;
        private readonly uint _framebuffer;
        private readonly uint _color;
        private readonly uint _depth;

        internal Frame(bool floatingDepth = false)
        {
            Gl = GL.GetApi(_context.GetProcAddress);
            _framebuffer = Gl.GenFramebuffer();
            _color = Gl.GenRenderbuffer();
            _depth = Gl.GenRenderbuffer();
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _color);
            Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, 64, 64);
            Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer, _color);
            Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                floatingDepth ? InternalFormat.Depth32fStencil8 : InternalFormat.Depth24Stencil8, 64, 64);
            Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                RenderbufferTarget.Renderbuffer, _depth);
            Assert.Equal(GLEnum.FramebufferComplete, Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            Gl.Viewport(0, 0, 64, 64);
            Gl.DepthMask(true);
            Gl.ClearColor(0, 0, 0, 1);
            Gl.ClearDepth(.8);
            Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        }

        internal void Character()
        {
            Gl.Enable(EnableCap.ScissorTest);
            Gl.Scissor(24, 24, 16, 16);
            Gl.ColorMask(true, true, true, true);
            Gl.DepthMask(true);
            Gl.ClearColor(0, 0, 1, 1);
            Gl.ClearDepth(.2);
            Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            Gl.Disable(EnableCap.ScissorTest);
        }

        internal byte[] Pixel(int x, int y)
        {
            var pixel = new byte[4];
            Gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
            return pixel;
        }

        internal float Depth(int x, int y)
        {
            var depth = new float[1];
            Gl.ReadPixels(x, y, 1, 1, PixelFormat.DepthComponent, PixelType.Float, depth.AsSpan());
            return depth[0];
        }

        public void Dispose()
        {
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            Gl.DeleteFramebuffer(_framebuffer);
            Gl.DeleteRenderbuffer(_color);
            Gl.DeleteRenderbuffer(_depth);
            Gl.Dispose();
            _context.Dispose();
        }
    }
}
