using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class ViewerTextureReuseGpuTests
{
    [Fact]
    public void MapCharacterStateChangesReuseUploadsAndClearDeletesBothFormats()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new MapCharacterRenderer();
        renderer.Initialize(gl);
        var image = Image(20);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var acquire = typeof(MapCharacterRenderer).GetMethod("AcquireTexture", flags);
        var color = typeof(MapCharacterRenderer).GetField("_textures", flags).GetValue(renderer);
        var raw = typeof(MapCharacterRenderer).GetField("_rawProgramTextures", flags).GetValue(renderer);
        uint Upload(object cache, InternalFormat format) => (uint)acquire.Invoke(renderer, new object[] { image, cache, format });
        uint colorId = Upload(color, InternalFormat.Srgb8Alpha8);
        uint rawId = Upload(raw, InternalFormat.Rgba8);
        Assert.NotEqual(colorId, rawId);
        renderer.Retain(Array.Empty<MapCharacterAssetData>());
        Assert.True(gl.IsTexture(colorId));
        Assert.True(gl.IsTexture(rawId));
        Assert.Equal(colorId, Upload(color, InternalFormat.Srgb8Alpha8));
        Assert.Equal(rawId, Upload(raw, InternalFormat.Rgba8));
        renderer.Clear();
        Assert.False(gl.IsTexture(colorId));
        Assert.False(gl.IsTexture(rawId));
        Assert.Equal(GLEnum.NoError, gl.GetError());
    }

    [Fact]
    public void ModelReplacementRetainsUploadsAndSeparatesRawFromSrgb()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        var cache = new GlMeshResourceCache(gl);
        BitmapSource image = Image(10);
        var first = Part(image);
        uint raw = cache.Ensure(null, first).Texture;
        var model = new SceneModel();
        model.Parts.Add(first);
        cache.QueueRelease(model);
        cache.ProcessPendingReleases();
        Assert.True(gl.IsTexture(raw));
        var replacement = Part(image);
        Assert.Equal(raw, cache.Ensure(null, replacement).Texture);
        replacement.TreatBaseTextureAsSrgb = true;
        uint srgb = cache.Ensure(null, replacement).Texture;
        Assert.NotEqual(raw, srgb);
        gl.BindTexture(TextureTarget.Texture2D, srgb);
        Assert.Equal((int)InternalFormat.Srgb8Alpha8, gl.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat));
        cache.Dispose();
        Assert.False(gl.IsTexture(raw));
        Assert.False(gl.IsTexture(srgb));
        Assert.Equal(GLEnum.NoError, gl.GetError());
    }

    [Fact]
    public void ReplacingImageUnderSameNameRefreshesBaseAndProgramTexturesOnlyWhenChanged()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var cache = new GlMeshResourceCache(gl);
        var part = Part(Image(10));
        var resources = cache.Ensure(null, part);
        uint first = resources.Texture;
        Assert.Equal(first, cache.ResolveProgramTexture(part, resources, "same.tex"));
        Assert.Equal(first, cache.Ensure(null, part).Texture);
        part.AllTextures["same"] = Image(80);
        uint changed = cache.Ensure(null, part).Texture;
        Assert.NotEqual(first, changed);
        Assert.Equal(changed, cache.ResolveProgramTexture(part, resources, "same.tex"));
        Assert.True(gl.IsTexture(first));
        gl.BindTexture(TextureTarget.Texture2D, changed);
        byte[] pixels = new byte[4];
        gl.GetTexImage(TextureTarget.Texture2D, 0, Silk.NET.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, pixels.AsSpan());
        Assert.Equal(80, pixels[0]);
        Assert.Equal(GLEnum.NoError, gl.GetError());
    }

    private static ModelPart Part(BitmapSource image) => new()
    {
        AllTextures = new Dictionary<string, BitmapSource> { ["same"] = image },
        SelectedTextureName = "same"
    };

    private static BitmapSource Image(byte value)
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { value, 0, 0, 255 }, 4);
        bitmap.Freeze();
        return bitmap;
    }
}
