using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer
{
    /// <summary>
    /// Renders lossless UHD snapshots through a temporary OpenGL framebuffer.
    /// The caller owns scene rendering and must invoke Capture while its GL context is active.
    /// </summary>
    public sealed class OpenGlSnapshotService
    {
        private const int UhdWidth = 3840;
        private const int UhdHeight = 2160;

        public static (int Width, int Height) CalculateUhdSize(int sourceWidth, int sourceHeight)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Snapshot source dimensions must be positive.");

            double scale = Math.Min((double)UhdWidth / sourceWidth, (double)UhdHeight / sourceHeight);
            return (
                Math.Max(1, (int)Math.Round(sourceWidth * scale)),
                Math.Max(1, (int)Math.Round(sourceHeight * scale)));
        }

        public BitmapSource Capture(
            GL gl,
            int width,
            int height,
            int restoreWidth,
            int restoreHeight,
            Action renderScene)
        {
            ArgumentNullException.ThrowIfNull(gl);
            ArgumentNullException.ThrowIfNull(renderScene);
            ImageExportUtils.ValidateDimensions(width, height);

            gl.GetInteger(GLEnum.FramebufferBinding, out int previousFramebuffer);
            uint framebuffer = 0;
            uint colorRenderbuffer = 0;
            uint depthRenderbuffer = 0;

            try
            {
                framebuffer = gl.GenFramebuffer();
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                colorRenderbuffer = CreateAttachment(
                    gl,
                    InternalFormat.Rgba8,
                    FramebufferAttachment.ColorAttachment0,
                    width,
                    height);
                depthRenderbuffer = CreateAttachment(
                    gl,
                    InternalFormat.Depth24Stencil8,
                    FramebufferAttachment.DepthStencilAttachment,
                    width,
                    height);

                GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (status != GLEnum.FramebufferComplete)
                    throw new InvalidOperationException($"OpenGL snapshot framebuffer is incomplete: {status}.");

                renderScene();

                byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
                gl.ReadPixels<byte>(
                    0,
                    0,
                    (uint)width,
                    (uint)height,
                    GLEnum.Bgra,
                    GLEnum.UnsignedByte,
                    out pixels[0]);
                FlipRows(pixels, width, height);

                BitmapSource bitmap = BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    checked(width * 4));
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFramebuffer);
                if (depthRenderbuffer != 0)
                    gl.DeleteRenderbuffer(depthRenderbuffer);
                if (colorRenderbuffer != 0)
                    gl.DeleteRenderbuffer(colorRenderbuffer);
                if (framebuffer != 0)
                    gl.DeleteFramebuffer(framebuffer);

                gl.Viewport(
                    0,
                    0,
                    (uint)Math.Max(1, restoreWidth),
                    (uint)Math.Max(1, restoreHeight));
            }
        }

        public Task SaveAsync(
            BitmapSource snapshot,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            return ImageExportUtils.SaveBitmapAsPngAsync(snapshot, filePath, cancellationToken);
        }

        private static uint CreateAttachment(
            GL gl,
            InternalFormat format,
            FramebufferAttachment attachment,
            int width,
            int height)
        {
            uint renderbuffer = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, renderbuffer);
            gl.RenderbufferStorage(
                RenderbufferTarget.Renderbuffer,
                format,
                (uint)width,
                (uint)height);
            gl.FramebufferRenderbuffer(
                FramebufferTarget.Framebuffer,
                attachment,
                RenderbufferTarget.Renderbuffer,
                renderbuffer);
            return renderbuffer;
        }

        private static void FlipRows(byte[] pixels, int width, int height)
        {
            int stride = checked(width * 4);
            byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(stride);
            try
            {
                for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
                {
                    int topOffset = top * stride;
                    int bottomOffset = bottom * stride;
                    System.Buffer.BlockCopy(pixels, topOffset, rowBuffer, 0, stride);
                    System.Buffer.BlockCopy(pixels, bottomOffset, pixels, topOffset, stride);
                    System.Buffer.BlockCopy(rowBuffer, 0, pixels, bottomOffset, stride);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rowBuffer);
            }
        }
    }
}
