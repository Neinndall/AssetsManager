using System;
using System.IO;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using Silk.NET.OpenGL;
using Xunit;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

public sealed class GroundAppearanceTests
{
    [Fact]
    public void SmallLogoAveragesFineDetailWithoutChangingItsSource() => RunSta(() =>
    {
        BitmapSource logo = StripedBitmap();
        BitmapSource composed = SceneElements.ComposeGroundTexture(SolidBitmap(0, 0, 0), logo, 0.25, 1);
        for (int x = 950; x < 1100; x += 7)
            Assert.InRange(Pixel(composed, x, 1024)[0], (byte)72, (byte)98);

        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(logo, 0, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(logo, 1, 0));
    });

    [Fact]
    public void CompositionPreservesLogoOrientationAspectAlphaAndScale() => RunSta(() =>
    {
        BitmapSource ground = SolidBitmap(0, 0, 255);
        byte[] pixels =
        {
            0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255,
            0, 255, 0, 128, 0, 255, 0, 128, 0, 255, 0, 128, 0, 255, 0, 128
        };
        BitmapSource logo = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        BitmapSource composed = SceneElements.ComposeGroundTexture(ground, logo, 1, 0.5);

        Assert.True(composed.IsFrozen);
        byte[] upper = Pixel(composed, 1024, 874);
        Assert.InRange(upper[1], (byte)62, (byte)66);
        Assert.InRange(upper[0], (byte)189, (byte)193);
        byte[] lower = Pixel(composed, 1024, 1174);
        Assert.InRange(lower[2], (byte)126, (byte)130);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(composed, 1024, 1400));

        BitmapSource small = SceneElements.ComposeGroundTexture(ground, logo, 0, 1);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(small, 1174, 1024));
        BitmapSource large = SceneElements.ComposeGroundTexture(ground, logo, 2, 1);
        Assert.Equal((byte)255, Pixel(large, 1600, 1200)[2]);
    });

    [Fact]
    public void SharedGroundRefreshesSettingsAndReplacedLogoWithoutRetainingOldPaths() => RunSta(() =>
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = Path.Combine(bridge.RootPath, "logo.png");
        SceneElements.ClearGroundCache();
        try
        {
            SavePng(path, SolidBitmap(255, 0, 0));
            var settings = new AppSettings { CustomGroundLogoPath = path };
            BitmapSource first = SceneElements.LoadGroundTexture(settings, bridge.LogService);
            Assert.NotNull(first);
            Assert.Same(first, SceneElements.LoadGroundTexture(settings, bridge.LogService));
            Assert.Equal((byte)255, Pixel(first, 1024, 1024)[2]);

            DateTime modified = File.GetLastWriteTimeUtc(path);
            SavePng(path, SolidBitmap(0, 255, 0));
            File.SetLastWriteTimeUtc(path, modified.AddSeconds(2));
            BitmapSource replaced = SceneElements.LoadGroundTexture(settings, bridge.LogService);
            Assert.NotSame(first, replaced);
            Assert.Equal((byte)255, Pixel(replaced, 1024, 1024)[1]);

            settings.GroundLogoScale = 0.25;
            Assert.NotSame(replaced, SceneElements.LoadGroundTexture(settings, bridge.LogService));
            BitmapSource plain = SceneElements.LoadGroundTexture(new AppSettings(), bridge.LogService);
            settings.GroundLogoOpacity = 0;
            Assert.Same(plain, SceneElements.LoadGroundTexture(settings, bridge.LogService));
            settings.GroundLogoOpacity = 1;
            settings.CustomGroundLogoPath = Path.Combine(bridge.RootPath, "missing.png");
            Assert.Same(plain, SceneElements.LoadGroundTexture(settings, bridge.LogService));
        }
        finally
        {
            SceneElements.ClearGroundCache();
        }
    });

    [Theory]
    [InlineData(3200f, -0.5f, 0f)]
    [InlineData(2000f, 1000f, 1000f)]
    public void PreviewGroundPreservesLayoutVisibilityDepthAndTextureUpdates(
        float size, float height, float gridHeight) => RunSta(() =>
    {
        using var context = new HiddenWglContext();
        using var gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new PreviewSurfaceRenderer();
        BitmapSource ground = SolidBitmap(0, 0, 255);
        BitmapSource composed = SceneElements.ComposeGroundTexture(ground, SolidBitmap(255, 0, 0), 1, 1);
        renderer.Initialize(gl, composed, size, height, gridHeight);

        uint target = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, target);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 32, 32, 0,
            GlPixelFormat.Rgba, PixelType.UnsignedByte, new ReadOnlySpan<byte>(new byte[32 * 32 * 4]));
        uint framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, target, 0);
        uint depth = gl.GenRenderbuffer();
        uint sampler = gl.GenSampler();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, 32, 32);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, depth);
        Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        gl.Viewport(0, 0, 32, 32);
        Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(new Vector3(0, height + 3000, 0),
            new Vector3(0, height, 0), -Vector3.UnitZ)
            * Matrix4x4.CreateOrthographic(size * 2, size * 2, 1, 5000);
        try
        {
            byte[] Draw(bool visible)
            {
                gl.ClearColor(0, 1, 0, 1);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                renderer.Render(viewProjection, false, visible, false);
                byte[] pixel = new byte[4];
                gl.ReadPixels(16, 16, 1, 1, GlPixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
                return pixel;
            }

            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Draw(true));
            byte[] outside = new byte[4];
            gl.ReadPixels(25, 16, 1, 1, GlPixelFormat.Rgba, PixelType.UnsignedByte, outside.AsSpan());
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, outside);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, Draw(false));
            renderer.SetGroundTexture(ground);
            gl.GetInteger(GLEnum.TextureBinding2D, out int binding);
            Assert.Equal(target, (uint)binding);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Draw(true));
            renderer.SetGroundTexture(SolidBitmap(128, 96, 64));
            byte[] color = Draw(true);
            Assert.InRange(color[0], (byte)127, (byte)129);
            Assert.InRange(color[1], (byte)95, (byte)97);
            Assert.InRange(color[2], (byte)63, (byte)65);

            // A previous material's nearest sampler must not alias fine ground detail.
            gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.Nearest);
            gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Nearest);
            gl.BindSampler(0, sampler);
            renderer.SetGroundTexture(StripedBitmap());
            Draw(true);
            for (int x = 11; x < 22; x++)
            {
                byte[] filtered = new byte[4];
                gl.ReadPixels(x, 16, 1, 1, GlPixelFormat.Rgba, PixelType.UnsignedByte, filtered.AsSpan());
                Assert.InRange(filtered[0], (byte)145, (byte)168);
            }
            gl.GetInteger(GLEnum.SamplerBinding, out int restoredSampler);
            Assert.Equal(sampler, (uint)restoredSampler);
            gl.BindSampler(0, 0);

            // A nearer surface must occlude the ground instead of being painted over.
            renderer.SetGroundTexture(SolidBitmap(255, 0, 0));
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.Render(Matrix4x4.CreateTranslation(0, 10, 0) * viewProjection, false, true, false);
            renderer.SetGroundTexture(ground);
            renderer.Render(viewProjection, false, true, false);
            byte[] occluded = new byte[4];
            gl.ReadPixels(16, 16, 1, 1, GlPixelFormat.Rgba, PixelType.UnsignedByte, occluded.AsSpan());
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, occluded);

            renderer.SetGroundTexture(null);
            byte[] fallback = Draw(true);
            Assert.InRange(fallback[0], (byte)34, (byte)36);

            gl.Disable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Greater);
            gl.DepthMask(false);
            gl.Enable(EnableCap.Blend);
            renderer.Render(viewProjection, true, true, height < 0);
            Assert.False(gl.IsEnabled(EnableCap.DepthTest));
            Assert.True(gl.IsEnabled(EnableCap.Blend));
            gl.GetInteger(GLEnum.DepthFunc, out int function);
            Assert.Equal((int)DepthFunction.Greater, function);
            gl.GetInteger(GLEnum.DepthWritemask, out int depthWrite);
            Assert.Equal(0, depthWrite);
            Assert.Equal(GLEnum.NoError, gl.GetError());
            renderer.Dispose();
            renderer.Dispose();
        }
        finally
        {
            gl.BindSampler(0, 0);
            gl.DeleteSampler(sampler);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteRenderbuffer(depth);
            gl.DeleteTexture(target);
        }
    });

    private static BitmapSource SolidBitmap(byte red, byte green, byte blue) =>
        BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { blue, green, red, 255 }, 4);

    private static BitmapSource StripedBitmap()
    {
        const int size = 513;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int offset = (y * size + x) * 4;
            byte color = x % 3 == 0 ? (byte)255 : (byte)0;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = color;
            pixels[offset + 3] = 255;
        }
        return BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
    }

    private static byte[] Pixel(BitmapSource bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static void SavePng(string path, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void RunSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Ground appearance test timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
