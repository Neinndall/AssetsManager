using System;
using System.IO;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
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
    [Theory]
    [InlineData(4, 2, 1, 0.425, 0.2125)]
    [InlineData(2, 4, 1, 0.2125, 0.425)]
    [InlineData(1024, 1024, 0.25, 0.10625, 0.10625)]
    [InlineData(4, 2, 0, 0.10625, 0.053125)]
    [InlineData(4, 2, 2, 0.6375, 0.31875)]
    public void LogoSizePreservesAspectAndClampsWithoutResampling(
        int width, int height, double scale, double expectedWidth, double expectedHeight) => RunSta(() =>
    {
        BitmapSource logo = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, new byte[width * height * 4], width * 4);
        var appearance = new GroundAppearance(null, logo, scale, 0.7);
        Assert.Same(logo, appearance.Logo);
        Assert.InRange(appearance.LogoUvSize.X - expectedWidth, -0.000001, 0.000001);
        Assert.InRange(appearance.LogoUvSize.Y - expectedHeight, -0.000001, 0.000001);
        Assert.Equal(0.7f, appearance.Opacity);
        Assert.Equal(width, appearance.Logo.PixelWidth);
        Assert.Equal(height, appearance.Logo.PixelHeight);
    });

    [Theory]
    [InlineData(double.NaN, double.PositiveInfinity, 0.425, 1)]
    [InlineData(double.NegativeInfinity, -1, 0.425, 0)]
    [InlineData(1, 2, 0.425, 1)]
    public void InvalidAppearanceValuesCannotReachShader(
        double scale, double opacity, double expectedSize, double expectedOpacity) => RunSta(() =>
    {
        var appearance = new GroundAppearance(null, SolidBitmap(0, 0, 0), scale, opacity);
        Assert.Equal(expectedSize, appearance.LogoUvSize.X, 5);
        Assert.Equal(expectedOpacity, appearance.Opacity, 5);
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
            GroundAppearance first = SceneElements.LoadGroundAppearance(settings, bridge.LogService);
            Assert.NotNull(first.Logo);
            Assert.NotNull(first.Texture);
            Assert.Equal(2048, first.Texture.PixelWidth);
            Assert.Equal(2048, first.Texture.PixelHeight);
            Assert.Same(first.Logo, SceneElements.LoadGroundAppearance(settings, bridge.LogService).Logo);
            Assert.Equal((byte)255, Pixel(first.Logo, 0, 0)[2]);

            DateTime modified = File.GetLastWriteTimeUtc(path);
            SavePng(path, SolidBitmap(0, 255, 0));
            File.SetLastWriteTimeUtc(path, modified.AddSeconds(2));
            GroundAppearance replaced = SceneElements.LoadGroundAppearance(settings, bridge.LogService);
            Assert.NotSame(first.Logo, replaced.Logo);
            Assert.Same(first.Texture, replaced.Texture);
            Assert.Equal((byte)255, Pixel(replaced.Logo, 0, 0)[1]);

            settings.GroundLogoScale = 0.25;
            settings.GroundLogoOpacity = 0.7;
            GroundAppearance smaller = SceneElements.LoadGroundAppearance(settings, bridge.LogService);
            Assert.Same(replaced.Logo, smaller.Logo);
            Assert.Same(replaced.Texture, smaller.Texture);
            Assert.Equal(0.10625f, smaller.LogoUvSize.X, 5);
            Assert.Equal(0.7f, smaller.Opacity);
            settings.GroundLogoOpacity = 0;
            Assert.Same(replaced.Logo, SceneElements.LoadGroundAppearance(settings, bridge.LogService).Logo);

            settings.CustomGroundLogoPath = Path.Combine(bridge.RootPath, "missing.png");
            GroundAppearance missing = SceneElements.LoadGroundAppearance(settings, bridge.LogService);
            Assert.Null(missing.Logo);
            Assert.Same(first.Texture, missing.Texture);
            settings.CustomGroundLogoPath = path;
            GroundAppearance restored = SceneElements.LoadGroundAppearance(settings, bridge.LogService);
            Assert.NotSame(replaced.Logo, restored.Logo);
            Assert.Equal((byte)255, Pixel(restored.Logo, 0, 0)[1]);
        }
        finally
        {
            SceneElements.ClearGroundCache();
        }
    });

    [Fact]
    public void GroundPreparedOnWorkerCanBeUsedOnViewportThread() => RunSta(() =>
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = Path.Combine(bridge.RootPath, "startup-logo.png");
        SavePng(path, SolidBitmap(255, 0, 0));
        SceneElements.ClearGroundCache();
        try
        {
            var settings = new AppSettings { CustomGroundLogoPath = path, GroundLogoScale = 0.5, GroundLogoOpacity = 0.7 };
            GroundAppearance appearance = Task.Run(() => SceneElements.LoadGroundAppearance(settings, null)).GetAwaiter().GetResult();
            Assert.NotNull(appearance.Texture);
            Assert.NotNull(appearance.Logo);
            Assert.True(appearance.Texture.IsFrozen);
            Assert.True(appearance.Logo.IsFrozen);
            Assert.Equal(2048, appearance.Texture.PixelWidth);
            Assert.Equal((byte)255, Pixel(appearance.Logo, 0, 0)[2]);
            Assert.Equal(0.2125f, appearance.LogoUvSize.X, 5);
            Assert.Equal(0.7f, appearance.Opacity);
        }
        finally { SceneElements.ClearGroundCache(); }
    });

    [Fact]
    public void LogoLargerThan2048RetainsSourcePixelsAcrossSettingsChanges() => RunSta(() =>
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = Path.Combine(bridge.RootPath, "wide-logo.png");
        const int width = 3072;
        const int height = 128;
        byte[] pixels = new byte[width * height * 4];
        pixels[3] = 255;
        pixels[^2] = 255;
        pixels[^1] = 255;
        SavePng(path, BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4));
        SceneElements.ClearGroundCache();
        try
        {
            var settings = new AppSettings { CustomGroundLogoPath = path, GroundLogoScale = 0.25, GroundLogoOpacity = 0.7 };
            GroundAppearance appearance = SceneElements.LoadGroundAppearance(settings, bridge.LogService);
            Assert.True(appearance.Logo.IsFrozen);
            Assert.Equal(width, appearance.Logo.PixelWidth);
            Assert.Equal(height, appearance.Logo.PixelHeight);
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(appearance.Logo, 0, 0));
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(appearance.Logo, width - 1, height - 1));
            settings.GroundLogoScale = 1.5;
            settings.GroundLogoOpacity = 1;
            Assert.Same(appearance.Logo, SceneElements.LoadGroundAppearance(settings, bridge.LogService).Logo);
        }
        finally { SceneElements.ClearGroundCache(); }
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
        renderer.Initialize(gl, new GroundAppearance(ground, SolidBitmap(255, 0, 0)), size, height, gridHeight);

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
            renderer.SetGroundAppearance(new GroundAppearance(ground));
            gl.GetInteger(GLEnum.TextureBinding2D, out int binding);
            Assert.Equal(target, (uint)binding);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Draw(true));
            renderer.SetGroundAppearance(new GroundAppearance(SolidBitmap(128, 96, 64)));
            byte[] color = Draw(true);
            Assert.InRange(color[0], (byte)127, (byte)129);
            Assert.InRange(color[1], (byte)95, (byte)97);
            Assert.InRange(color[2], (byte)63, (byte)65);

            // A previous material's nearest sampler must not alias fine ground detail.
            gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.Nearest);
            gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Nearest);
            gl.BindSampler(0, sampler);
            renderer.SetGroundAppearance(new GroundAppearance(StripedBitmap()));
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

            // Both texture units must ignore inherited material samplers and restore the caller's bindings.
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, target);
            gl.BindSampler(1, sampler);
            BitmapSource logo = TransparentStripedBitmap();
            BitmapSource black = SolidBitmap(0, 0, 0);
            renderer.SetGroundAppearance(new GroundAppearance(black, logo, 0.25, 0.7));
            byte[] transparent = Draw(true);
            // Half opaque red / half transparent cyan: only red may contribute after minification.
            Assert.InRange(transparent[0], (byte)158, (byte)162);
            Assert.Equal((byte)0, transparent[1]);
            Assert.Equal((byte)0, transparent[2]);
            gl.GetInteger(GLEnum.ActiveTexture, out int restoredUnit);
            Assert.Equal((int)TextureUnit.Texture1, restoredUnit);
            gl.GetInteger(GLEnum.TextureBinding2D, out int restoredTexture);
            Assert.Equal(target, (uint)restoredTexture);
            gl.GetInteger(GLEnum.SamplerBinding, out restoredSampler);
            Assert.Equal(sampler, (uint)restoredSampler);

            renderer.SetGroundAppearance(new GroundAppearance(black, logo, 0.25, 0));
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, Draw(true));
            renderer.SetGroundAppearance(new GroundAppearance(black, logo, 1.5, 1));
            Assert.InRange(Draw(true)[0], (byte)186, (byte)189);
            gl.BindSampler(1, 0);
            gl.BindTexture(TextureTarget.Texture2D, 0);
            gl.ActiveTexture(TextureUnit.Texture0);

            byte[] rows =
            {
                0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255,
                0, 255, 0, 128, 0, 255, 0, 128, 0, 255, 0, 128, 0, 255, 0, 128
            };
            BitmapSource twoRows = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, rows, 16);
            renderer.SetGroundAppearance(new GroundAppearance(ground, twoRows, 1, 0.5));
            Matrix4x4 closeViewProjection = Matrix4x4.CreateLookAt(new Vector3(0, height + 3000, 0),
                new Vector3(0, height, 0), -Vector3.UnitZ)
                * Matrix4x4.CreateOrthographic(size * 0.425f, size * 0.425f, 1, 5000);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.Render(closeViewProjection, false, true, false);
            byte[] upper = new byte[4];
            byte[] lower = new byte[4];
            gl.ReadPixels(16, 20, 1, 1, GlPixelFormat.Rgba, PixelType.UnsignedByte, upper.AsSpan());
            gl.ReadPixels(16, 11, 1, 1, GlPixelFormat.Rgba, PixelType.UnsignedByte, lower.AsSpan());
            Assert.InRange(upper[0], (byte)186, (byte)189);
            Assert.Equal((byte)0, upper[1]);
            Assert.InRange(upper[2], (byte)186, (byte)189);
            Assert.Equal((byte)0, lower[0]);
            Assert.InRange(lower[1], (byte)135, (byte)139);
            Assert.InRange(lower[2], (byte)223, (byte)226);

            // A nearer surface must occlude the ground instead of being painted over.
            renderer.SetGroundAppearance(new GroundAppearance(SolidBitmap(255, 0, 0)));
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.Render(Matrix4x4.CreateTranslation(0, 10, 0) * viewProjection, false, true, false);
            renderer.SetGroundAppearance(new GroundAppearance(ground));
            renderer.Render(viewProjection, false, true, false);
            byte[] occluded = new byte[4];
            gl.ReadPixels(16, 16, 1, 1, GlPixelFormat.Rgba, PixelType.UnsignedByte, occluded.AsSpan());
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, occluded);

            renderer.SetGroundAppearance(new GroundAppearance(null));
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
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindSampler(1, 0);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindSampler(0, 0);
            gl.DeleteSampler(sampler);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteRenderbuffer(depth);
            gl.DeleteTexture(target);
        }
    });

    [Fact]
    public void PreviewSurfacesAllocateOnlyWhenShownAndReleaseTheirGpuResources() => RunSta(() =>
    {
        using var context = new HiddenWglContext();
        using var gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new PreviewSurfaceRenderer();
        renderer.Initialize(gl, new GroundAppearance(SolidBitmap(0, 0, 255)));

        uint Resource(string field) => (uint)typeof(PreviewSurfaceRenderer)
            .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .GetValue(renderer);

        renderer.Render(Matrix4x4.Identity, false, false, false);
        Assert.Equal(0u, Resource("_groundProgram"));
        Assert.Equal(0u, Resource("_groundTexture"));
        Assert.Equal(0u, Resource("_stageProgram"));

        renderer.Prepare(true, false, false);
        renderer.SetGroundAppearance(new GroundAppearance(SolidBitmap(255, 0, 0)));
        Assert.Equal(0u, Resource("_groundProgram"));
        Assert.Equal(0u, Resource("_groundTexture"));
        Assert.Equal(0u, Resource("_stageProgram"));

        gl.ClearColor(0, 1, 0, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        renderer.Prepare(false, false, true);
        uint stage = Resource("_stageProgram");
        Assert.True(gl.IsProgram(stage));
        Assert.Equal(0u, Resource("_groundTexture"));

        renderer.Prepare(false, true, false);
        uint ground = Resource("_groundTexture");
        uint groundProgram = Resource("_groundProgram");
        Assert.True(gl.IsTexture(ground));
        byte[] pixel = new byte[4];
        gl.ReadPixels(0, 0, 1, 1, GlPixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixel);
        renderer.Dispose();
        Assert.False(gl.IsTexture(ground));
        Assert.False(gl.IsProgram(groundProgram));
        Assert.False(gl.IsProgram(stage));
        Assert.Equal(GLEnum.NoError, gl.GetError());
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

    private static BitmapSource TransparentStripedBitmap()
    {
        const int size = 1024;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int offset = (y * size + x) * 4;
            bool opaque = x % 2 == 0;
            pixels[offset] = pixels[offset + 1] = opaque ? (byte)0 : (byte)255;
            pixels[offset + 2] = pixels[offset + 3] = opaque ? (byte)255 : (byte)0;
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
