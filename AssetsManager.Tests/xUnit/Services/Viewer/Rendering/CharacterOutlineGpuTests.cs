using System;
using System.Numerics;
using System.Threading;
using System.Linq;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

public sealed class CharacterOutlineGpuTests
{
    [Fact]
    public void ShellExtrusionKeepsTheBodyVisibleAndFollowsTheSameSkinnedVertices()
    {
        using var scene = new Shell();
        var none = scene.Draw(3, 0);
        var near = scene.Draw(3, .3f);
        var far = scene.Draw(6, .3f);
        // Independently captured with the exact 213852c2 stock material in Edge/Three 0.185.1.
        Assert.InRange(Math.Abs(Count(none, green: false) - 324), 0, 4);
        Assert.InRange(Math.Abs(Count(near, green: true) - 352), 0, 4);
        Assert.InRange(Math.Abs(Count(far, green: true) - 224), 0, 4);
        Assert.InRange(Math.Abs(Count(far, green: false) - 100), 0, 4);
        Assert.True(Count(near, green: true) > 0);
        Assert.Equal(Count(none, green: false), Count(near, green: false));
        Assert.True(Count(far, green: false) < Count(near, green: false));
        Assert.Equal(scene.Draw(3, .3f, translatedWorld: true), scene.Draw(3, .3f, skinned: true));
        Assert.Equal(GLEnum.NoError, scene.Gl.GetError());
    }

    private static int Count(byte[] image, bool green) => Enumerable.Range(0, image.Length / 4).Count(at =>
        green ? image[at * 4 + 1] > image[at * 4] && image[at * 4 + 1] > image[at * 4 + 2]
            : image[at * 4] > 250 && image[at * 4 + 1] > 250 && image[at * 4 + 2] > 250);

    private sealed class Shell : IDisposable
    {
        private readonly HiddenWglContext _context = new();
        internal readonly GL Gl;
        private readonly CharacterOutlineRenderer _renderer;
        private readonly uint _vao, _vertices, _bones, _target, _depth, _frame;
        internal Shell()
        {
            Gl = GL.GetApi(_context.GetProcAddress);
            _renderer = new CharacterOutlineRenderer(Gl, false);
            _vao = Gl.GenVertexArray(); Gl.BindVertexArray(_vao);
            _vertices = Gl.GenBuffer(); Gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertices);
            float n = MathF.Sqrt(.5f);
            float[] vertices = { -.5f,-.5f,0,-n,-n,0,0,0, .5f,-.5f,0,n,-n,0,1,0,
                -.5f,.5f,0,-n,n,0,0,1, .5f,.5f,0,n,n,0,1,1 };
            Gl.BufferData(BufferTargetARB.ArrayBuffer, vertices.AsSpan(), BufferUsageARB.StaticDraw);
            for (uint attribute = 0; attribute < 3; attribute++)
            {
                Gl.EnableVertexAttribArray(attribute);
                Gl.VertexAttribPointer(attribute, attribute == 2 ? 2 : 3, VertexAttribPointerType.Float, false, 32,
                    new IntPtr(attribute == 0 ? 0 : attribute == 1 ? 12 : 24));
            }
            Gl.VertexAttrib4(5, 0f, 0f, 0f, 0f); Gl.VertexAttrib4(6, 1f, 0f, 0f, 0f);
            _bones = Gl.GenBuffer(); Gl.BindBuffer(BufferTargetARB.UniformBuffer, _bones);
            Gl.BufferData(BufferTargetARB.UniformBuffer, new float[512 * 16].AsSpan(), BufferUsageARB.DynamicDraw);
            Gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _bones);
            _target = Gl.GenTexture(); Gl.BindTexture(TextureTarget.Texture2D, _target);
            Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 64, 64, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            _depth = Gl.GenRenderbuffer(); Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, 64, 64);
            _frame = Gl.GenFramebuffer(); Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _frame);
            Gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _target, 0);
            Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depth);
            Assert.Equal(GLEnum.FramebufferComplete, Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        }
        internal byte[] Draw(float distance, float width, bool translatedWorld = false, bool skinned = false)
        {
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _frame); Gl.Viewport(0, 0, 64, 64);
            Gl.DepthMask(true); Gl.ClearColor(0, 0, 0, 0); Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            var eye = new Vector3(0, 0, distance);
            var viewProjection = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY)
                * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1, .1f, 100);
            var translation = Matrix4x4.CreateTranslation(.2f, 0, 0);
            var world = translatedWorld ? translation : Matrix4x4.Identity;
            var palette = new[] { skinned ? translation : Matrix4x4.Identity };
            Gl.BindBuffer(BufferTargetARB.UniformBuffer, _bones);
            Gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, palette.AsSpan());
            Gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _bones);
            Gl.BindVertexArray(_vao);
            var outline = new CharacterOutlineDefinition(1, Vector3.One, 0, 3, 0, 1, null,
                new Vector4(1, 0, 0, 0), ModelMaterialDefinition.Missing);
            _renderer.Bind(outline, world, viewProjection, eye, skinned, _ => null);
            Gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            _renderer.Bind(outline with { Color = new(.1f, .8f, .2f), Width = width, DepthOffset = -.05f },
                world, viewProjection, eye, skinned, _ => null);
            Gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            var pixels = new byte[64 * 64 * 4];
            Gl.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            return pixels;
        }
        public void Dispose()
        {
            _renderer.Dispose(); Gl.DeleteFramebuffer(_frame); Gl.DeleteRenderbuffer(_depth);
            Gl.DeleteTexture(_target); Gl.DeleteBuffer(_vertices); Gl.DeleteBuffer(_bones); Gl.DeleteVertexArray(_vao);
            Gl.Dispose(); _context.Dispose();
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealHostsDrawStockShellAndMaskAndRespectViewModesAndHiddenSubmeshes(bool map)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var draw = new CharacterPbrGpuTests.Draw(map);
                var outline = new CharacterOutlineDefinition(1, new Vector3(.1f, .8f, .2f), 0, 600, 0,
                    1, null, new Vector4(1, 0, 0, 0), ModelMaterialDefinition.Missing);
                byte[] Pixel(StudioViewMode mode, CharacterOutlineDefinition shell = null, bool hidden = false) =>
                    draw.HostPixel(map, 0, mode, pbr: false, outline: shell, hidden: hidden);
                var full = Pixel(StudioViewMode.Lit, outline);
                Check(full, 26, 204, 51);
                var baseline = Pixel(StudioViewMode.Lit);
                Assert.Equal(baseline, Pixel(StudioViewMode.Lit, outline with { Alpha = 0 }));
                Assert.Equal(baseline, Pixel(StudioViewMode.Lit, outline with { MaskPath = "mask" }));
                var half = Pixel(StudioViewMode.Lit, outline with { MaskPath = "mask", MaskChannels = new(0, 1, 0, 0) });
                for (int channel = 0; channel < 3; channel++)
                    Assert.InRange(Math.Abs(half[channel] - (full[channel] * (128f / 255) + baseline[channel] * (127f / 255))), 0, 2);
                Assert.Equal(full, Pixel(StudioViewMode.Lit, outline with { MaskPath = "absent" }));
                Assert.Equal(Pixel(StudioViewMode.Unshaded), Pixel(StudioViewMode.Unshaded, outline));
                Assert.Equal(Pixel(StudioViewMode.Untextured), Pixel(StudioViewMode.Untextured, outline));
                Assert.Equal(Pixel(StudioViewMode.Lit, hidden: true), Pixel(StudioViewMode.Lit, outline, hidden: true));
                Assert.Equal(GLEnum.NoError, draw.Gl.GetError());
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Check(byte[] pixel, params byte[] rgb)
    {
        for (int channel = 0; channel < 3; channel++) Assert.InRange(Math.Abs(pixel[channel] - rgb[channel]), 0, 1);
    }
}
