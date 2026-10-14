using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using Microsoft.Win32;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Renders lossless UHD snapshots through a temporary OpenGL framebuffer.
    /// The caller owns scene rendering and must invoke Capture while its GL context is active.
    /// </summary>
    public sealed class OpenGlSnapshotService
    {
        private const int UhdWidth = 3840;
        private const int UhdHeight = 2160;

        internal sealed record SnapshotRequest(string FilePath, int Width, int Height);

        internal static SnapshotRequest RequestUhdSnapshot(
            bool viewportReady, int sourceWidth, int sourceHeight, string name, LogService log)
        {
            if (!viewportReady || sourceWidth <= 0 || sourceHeight <= 0)
            {
                log?.LogWarning("The OpenGL viewport is not ready for high-definition capture.");
                return null;
            }
            string fileName = string.Join("_", (string.IsNullOrWhiteSpace(name) ? "Viewport" : name)
                .Split(Path.GetInvalidFileNameChars()));
            var dialog = new SaveFileDialog
            {
                FileName = $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                Filter = "PNG Image (*.png)|*.png|All Files (*.*)|*.*",
                Title = "Save Viewport Snapshot",
                DefaultExt = "png"
            };
            if (dialog.ShowDialog() != true) return null;
            (int width, int height) = CalculateUhdSize(sourceWidth, sourceHeight);
            ImageExportUtils.ValidateDimensions(width, height);
            return new SnapshotRequest(dialog.FileName, width, height);
        }

        internal void ProcessPendingSnapshot(
            ref SnapshotRequest pending, GL gl, int restoreWidth, int restoreHeight,
            Action<int, int> renderScene, LogService log)
        {
            SnapshotRequest request = pending;
            if (request == null) return;
            pending = null;
            try
            {
                BitmapSource snapshot = Capture(gl, request.Width, request.Height, restoreWidth, restoreHeight,
                    () => renderScene(request.Width, request.Height));
                _ = SaveWithFeedbackAsync(snapshot, request.FilePath, log);
            }
            catch (Exception ex)
            {
                log?.LogError(ex, $"Failed to render high-definition snapshot to {request.FilePath}");
            }
        }

        private async Task SaveWithFeedbackAsync(BitmapSource snapshot, string filePath, LogService log)
        {
            try
            {
                await SaveAsync(snapshot, filePath);
                log?.LogInteractiveSuccess($"Snapshot saved ({snapshot.PixelWidth}x{snapshot.PixelHeight})",
                    filePath, Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                log?.LogError(ex, $"Failed to save high-definition snapshot to {filePath}");
            }
        }

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
