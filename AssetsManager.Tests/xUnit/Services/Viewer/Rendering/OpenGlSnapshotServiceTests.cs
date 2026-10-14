using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Tests.Support;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class OpenGlSnapshotServiceTests
    {
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

        [Fact]
        public void FailedCaptureClearsTheRequestAndReleasesItsFramebuffer()
        {
            using var context = new HiddenWglContext();
            using var gl = GL.GetApi(context.GetProcAddress);
            var service = new OpenGlSnapshotService();
            var pending = new OpenGlSnapshotService.SnapshotRequest(Path.Combine(Path.GetTempPath(), "unused-snapshot.png"), 16, 8);
            uint previousFramebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
            int captureFramebuffer = 0;
            try
            {
                service.ProcessPendingSnapshot(ref pending, gl, 64, 32, (width, height) =>
                {
                    Assert.Equal(16, width);
                    Assert.Equal(8, height);
                    gl.GetInteger(GLEnum.FramebufferBinding, out captureFramebuffer);
                    throw new InvalidOperationException("Render failure");
                }, null);

                Assert.Null(pending);
                Assert.NotEqual(0, captureFramebuffer);
                Assert.False(gl.IsFramebuffer((uint)captureFramebuffer));
                AssertRestored(gl, previousFramebuffer);
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(previousFramebuffer);
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
