using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class OpenGlSnapshotServiceTests
    {
        [Theory]
        [InlineData("Off")]
        [InlineData("Fxaa")]
        [InlineData("Smaa")]
        public void CaptureKeepsViewportMultisamplingAndPostFiltersAndRestoresSplitBindings(string mode)
        {
            using var context = new HiddenWglContext();
            using var gl = GL.GetApi(context.GetProcAddress);
            const int size = 24;
            uint frame = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, frame);
            uint color = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, color);
            gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.Rgba8, size, size);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, color);
            uint depth = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.Depth24Stencil8, size, size);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, depth);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            int samples = gl.GetInteger(GLEnum.Samples);
            Assert.True(samples >= 4);
            uint program = GlShaderCompiler.CreateProgram(gl, false, @"
void main() {
    vec2 p[3] = vec2[3](vec2(-0.87, -0.79), vec2(0.73, -0.63), vec2(0.11, 0.81));
    gl_Position = vec4(p[gl_VertexID], 0.0, 1.0);
}", @"
out vec4 color;
void main() { color = vec4(1.0); }");
            uint vao = gl.GenVertexArray();
            using var fxaa = new FxaaPostEffectsRenderer();
            using var smaa = new SmaaPostEffectsRenderer();
            if (mode == "Fxaa") fxaa.Initialize(gl);
            if (mode == "Smaa") smaa.Initialize(gl);
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, frame);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            try
            {
                var snapshot = new OpenGlSnapshotService().Capture(gl, size, size, 64, 32, () =>
                {
                    Assert.Equal(samples, gl.GetInteger(GLEnum.Samples));
                    gl.Viewport(0, 0, size, size);
                    gl.Enable(EnableCap.Multisample);
                    gl.Disable(EnableCap.Blend);
                    gl.Disable(EnableCap.DepthTest);
                    gl.ClearColor(0, 0, 0, 0);
                    gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    gl.UseProgram(program);
                    gl.BindVertexArray(vao);
                    gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                    if (mode == "Fxaa") fxaa.Render(size, size);
                    if (mode == "Smaa") smaa.Render(size, size);
                    Assert.Equal(GLEnum.NoError, gl.GetError());
                    gl.Enable(EnableCap.ScissorTest);
                    gl.Scissor(0, 0, 1, 1);
                });
                var pixels = new byte[size * size * 4];
                snapshot.CopyPixels(pixels, size * 4, 0);
                Assert.Contains(Enumerable.Range(0, size * size), index => pixels[index * 4] is > 0 and < 255);
                Assert.Contains(Enumerable.Range(0, size * size), index => pixels[index * 4] == 255);
                if (mode == "Off")
                    Assert.Contains(Enumerable.Range(0, size * size), index => pixels[index * 4 + 3] is > 0 and < 255);
                Assert.Equal(0, gl.GetInteger(GLEnum.ReadFramebufferBinding));
                AssertRestored(gl, frame);
                Assert.Equal((int)depth, gl.GetInteger(GLEnum.RenderbufferBinding));
                Assert.True(gl.IsEnabled(EnableCap.ScissorTest));
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.Disable(EnableCap.ScissorTest);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(frame);
                gl.DeleteRenderbuffer(color);
                gl.DeleteRenderbuffer(depth);
                gl.DeleteVertexArray(vao);
                gl.DeleteProgram(program);
            }
        }

        [Fact]
        public void CaptureRendersAtRequestedSizeAndPreservesPixelOrientationAndAlpha()
        {
            using var context = new HiddenWglContext();
            using var gl = GL.GetApi(context.GetProcAddress);
            var service = new OpenGlSnapshotService();
            const int width = 16, height = 8;
            uint previousFramebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
            try
            {
                BitmapSource snapshot = service.Capture(gl, width, height, 64, 32, () =>
                {
                    gl.Viewport(0, 0, width, height);
                    gl.ClearColor(1f, 0f, 0f, .5f);
                    gl.Clear(ClearBufferMask.ColorBufferBit);
                    gl.Enable(EnableCap.ScissorTest);
                    gl.Scissor(0, height / 2, width, height / 2);
                    gl.ClearColor(0f, 1f, 0f, 1f);
                    gl.Clear(ClearBufferMask.ColorBufferBit);
                    gl.Disable(EnableCap.ScissorTest);
                });

                Assert.Equal(width, snapshot.PixelWidth);
                Assert.Equal(height, snapshot.PixelHeight);
                Assert.True(snapshot.IsFrozen);
                Assert.Equal(PixelFormats.Bgra32, snapshot.Format);
                var pixels = new byte[width * height * 4];
                snapshot.CopyPixels(pixels, width * 4, 0);
                Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels[..4]);
                int bottom = (height - 1) * width * 4;
                Assert.Equal(new byte[] { 0, 0, 255 }, pixels[bottom..(bottom + 3)]);
                Assert.InRange(pixels[bottom + 3], (byte)127, (byte)128);
                AssertRestored(gl, previousFramebuffer);
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(previousFramebuffer);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        public void FailedCaptureClearsTheRequestAndReleasesItsFramebuffer(int samples)
        {
            using var context = new HiddenWglContext();
            using var gl = GL.GetApi(context.GetProcAddress);
            var service = new OpenGlSnapshotService();
            var pending = new OpenGlSnapshotService.SnapshotRequest(Path.Combine(Path.GetTempPath(), "unused-snapshot.png"), 16, 8);
            uint previousFramebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
            uint sourceColor = 0;
            if (samples > 0)
            {
                sourceColor = gl.GenRenderbuffer();
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, sourceColor);
                gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.Rgba8, 16, 8);
                gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, sourceColor);
                Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            }
            int captureFramebuffer = 0;
            int captureColor = 0, captureDepth = 0;
            try
            {
                service.ProcessPendingSnapshot(ref pending, gl, 64, 32, (width, height) =>
                {
                    Assert.Equal(16, width);
                    Assert.Equal(8, height);
                    gl.GetInteger(GLEnum.FramebufferBinding, out captureFramebuffer);
                    gl.GetFramebufferAttachmentParameter(GLEnum.DrawFramebuffer, GLEnum.ColorAttachment0,
                        GLEnum.FramebufferAttachmentObjectName, out captureColor);
                    gl.GetFramebufferAttachmentParameter(GLEnum.DrawFramebuffer, GLEnum.DepthAttachment,
                        GLEnum.FramebufferAttachmentObjectName, out captureDepth);
                    throw new InvalidOperationException("Render failure");
                }, null);

                Assert.Null(pending);
                Assert.NotEqual(0, captureFramebuffer);
                Assert.False(gl.IsFramebuffer((uint)captureFramebuffer));
                Assert.False(gl.IsRenderbuffer((uint)captureColor));
                Assert.False(gl.IsRenderbuffer((uint)captureDepth));
                AssertRestored(gl, previousFramebuffer);
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(previousFramebuffer);
                if (sourceColor != 0) gl.DeleteRenderbuffer(sourceColor);
            }
        }

        private static void AssertRestored(GL gl, uint framebuffer)
        {
            gl.GetInteger(GLEnum.FramebufferBinding, out int bound);
            Assert.Equal(framebuffer, (uint)bound);
            var viewport = new int[4];
            gl.GetInteger(GLEnum.Viewport, viewport);
            Assert.Equal(new[] { 0, 0, 64, 32 }, viewport);
        }
    }
}
