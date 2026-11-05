using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxDistortionGpuTests
{
    public static IEnumerable<object[]> WarpCases()
    {
        foreach (bool mesh in new[] { false, true })
        foreach (byte alpha in new byte[] { 0, 128, 255 })
        foreach (bool vertical in new[] { false, true })
        foreach (bool occluded in new[] { false, true })
            yield return new object[] { mesh, alpha, vertical, occluded };
    }

    [Theory]
    [MemberData(nameof(WarpCases))]
    public void StockWarpMatchesInstalledShaderAcrossMasksAspectAndConsecutiveFrames(bool mesh, byte alpha, bool vertical, bool occluded)
        => CompareWarp(mesh, alpha, vertical, occluded);

    public static IEnumerable<object[]> LayerCases()
    {
        foreach (bool mesh in new[] { false, true })
        for (int ramp = 0; ramp < 3; ramp++)
        for (int erosion = 0; erosion < 3; erosion++)
        for (int mult = 0; mult < 3; mult++)
            yield return new object[] { mesh, ramp, erosion, mult };
    }

    [Theory]
    [MemberData(nameof(LayerCases))]
    public void DistortionRampAndUnsupportedLayersMatchInstalledShader(bool mesh, int ramp, int erosion, int mult)
        => CompareWarp(mesh, 128, false, false, ramp, erosion, mult);

    private static void CompareWarp(bool mesh, byte alpha, bool vertical, bool occluded, int rampMode = 0, int erosionMode = 0, int multMode = 0)
    {
        string install = InstalledSkins.FindInstall();
        Assert.NotNull(install);
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var native = new VfxOpenGlRenderer();
        using var stock = new VfxOpenGlRenderer();
        native.Initialize(gl, InstalledSkins.Settings(install)); stock.Initialize(gl);
        uint color = gl.GenTexture(), depth = gl.GenRenderbuffer(), framebuffer = gl.GenFramebuffer();
        uint white = Texture(1, 1, new byte[] { 255, 255, 255, 255 });
        uint normal = Texture(1, 1, new byte[] { vertical ? (byte)128 : (byte)255, vertical ? (byte)255 : (byte)128, 255, alpha });
        uint rampTexture = Texture(1, 1, rampMode == 2 ? new byte[] { 128, 64, 192, 128 } : new byte[] { 255, 255, 255, 128 });
        uint erosionTexture = Texture(1, 1, new byte[] { 0, 0, 0, 128 });
        uint multTexture = Texture(1, 1, new byte[] { 128, 128, 128, 128 });
        var definition = new VfxEmitterDefinition("warp", VfxCurveF.Const(1), VfxCurveF.Const(1),
            null, 0, 0, false, false, 1, VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One),
            null, null, null, null, VfxCurve3.Const(Vector3.Zero), "white.tex", Vector2.One, 1, false, mesh,
            PrimitiveKind: mesh ? VfxPrimitiveKind.Mesh : VfxPrimitiveKind.CameraQuad,
            RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true, AlphaReference = 0 },
            Distortion: new VfxDistortionDefinition(.25f, 1, "normal.tex"),
            ParticleColorTexturePath: rampMode != 0 ? "ramp.tex" : null,
            TextureMultPath: multMode == 2 ? "mult.tex" : null,
            AuthoredFeatures: multMode == 1 ? new VfxEmitterAuthoredFeatures(HasTextureMultLayer: true) : null,
            AlphaErosion: erosionMode != 0
                ? new VfxAlphaErosionDefinition("erosion.tex", VfxCurveF.Const(erosionMode == 1 ? .2f : .9f),
                    .1f, .1f, 2, VfxCurve4.Const(Vector4.UnitW), SliceWidth: 1.5f) : null);
        var nativeEmitter = State(); var stockEmitter = State();
        if (mesh)
            foreach (var pair in new[] { (native, nativeEmitter), (stock, stockEmitter) })
                pair.Item1.UploadEmitterMesh(pair.Item2,
                    new float[] { -1, -1, 0, 1, -1, 0, -1, 1, 0, 1, 1, 0 },
                    new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    new float[] { 0, 0, 1, 0, 0, 1, 1, 1 }, null, new uint[] { 0, 1, 2, 2, 1, 3 });
        try
        {
            foreach (var size in new[] { (64, 64), (96, 48), (48, 96), (64, 64) })
            foreach (float particleAlpha in new[] { 1f, .2f })
            for (int frame = 0; frame < 2; frame++)
            {
                int width = size.Item1, height = size.Item2;
                byte[] background = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int at = (y * width + x) * 4;
                    background[at] = (byte)(x * 255 / (width - 1));
                    background[at + 1] = (byte)(y * 255 / (height - 1));
                    background[at + 2] = frame == 0 ? (byte)30 : (byte)190;
                    background[at + 3] = 153;
                }
                foreach (bool missingMap in new[] { false, true, false })
                {
                    nativeEmitter.DistortionTexture = stockEmitter.DistortionTexture = missingMap ? 0 : normal;
                    nativeEmitter.Instances[8] = stockEmitter.Instances[8] = particleAlpha;
                    byte[] expected = Draw(native, nativeEmitter), actual = Draw(stock, stockEmitter);
                    Assert.Null(native.GameParticleFallback(nativeEmitter, mesh));
                    int center = ((height / 2) * width + width / 2) * 4;
                    if (alpha != 0 && !occluded && !missingMap)
                        Assert.True(Math.Abs(expected[center + (vertical ? 1 : 0)] - background[center + (vertical ? 1 : 0)]) > (rampMode == 0 ? 8 : 1),
                            "The installed shader must visibly warp the gradient for a non-zero normal mask.");
                    if (rampMode != 2) Assert.InRange(Math.Abs(expected[center + 2] - background[center + 2]), 0, 1);
                    for (int channel = 0; channel < 4; channel++)
                    {
                        Assert.True(Math.Abs(actual[center + channel] - expected[center + channel]) <= 2,
                            $"mesh={mesh}, ramp={rampMode}, erosion={erosionMode}, mult={multMode}, normalAlpha={alpha}, vertical={vertical}, occluded={occluded}, missing={missingMap}, size={width}x{height}, particleAlpha={particleAlpha}, frame={frame}, channel={channel}: game={expected[center + channel]}, stock={actual[center + channel]}.");
                        if (occluded || missingMap || alpha == 0)
                            Assert.InRange(Math.Abs(expected[center + channel] - background[center + channel]), 0, 1);
                    }
                    Assert.Equal(GLEnum.NoError, gl.GetError());
                }

                byte[] Draw(VfxOpenGlRenderer renderer, VfxPlaybackRuntime.EmitterState emitter)
                {
                    gl.ActiveTexture(TextureUnit.Texture0); gl.BindSampler(0, 0);
                    gl.BindTexture(TextureTarget.Texture2D, color);
                    gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                        PixelFormat.Rgba, PixelType.UnsignedByte, background.AsSpan());
                    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                    gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                        TextureTarget.Texture2D, color, 0);
                    gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
                    gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)width, (uint)height);
                    gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                        RenderbufferTarget.Renderbuffer, depth);
                    Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
                    gl.Viewport(0, 0, (uint)width, (uint)height);
                    gl.DepthMask(true); gl.ClearDepth(occluded ? .1 : 1); gl.Clear(ClearBufferMask.DepthBufferBit);
                    renderer.CaptureScene((uint)width, (uint)height, true, true);
                    renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
                    Assert.Equal((1, 1), renderer.LastParticleDrawCounts);
                    var storedDepth = new float[1];
                    gl.ReadPixels(width / 2, height / 2, 1, 1, PixelFormat.DepthComponent, PixelType.Float, storedDepth.AsSpan());
                    Assert.InRange(Math.Abs(storedDepth[0] - (occluded ? .1f : 1f)), 0, .00001f);
                    byte[] result = new byte[background.Length];
                    gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, result.AsSpan());
                    return result;
                }
            }
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer); gl.DeleteTexture(color); gl.DeleteRenderbuffer(depth);
            gl.DeleteTexture(white); gl.DeleteTexture(normal);
            gl.DeleteTexture(rampTexture); gl.DeleteTexture(erosionTexture); gl.DeleteTexture(multTexture);
        }
        uint Texture(uint width, uint height, byte[] pixels)
        {
            uint texture = gl.GenTexture(); gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            return texture;
        }
        VfxPlaybackRuntime.EmitterState State()
        {
            var row = new float[VfxPlaybackRuntime.InstanceStride];
            row[2] = .2f; row[3] = row[4] = row[18] = .8f;
            row[24] = erosionMode == 1 ? .2f : .9f;
            row[28] = 1;
            row[5] = row[6] = row[7] = row[8] = 1;
            row[21] = row[22] = row[31] = row[32] = row[36] = row[40] = row[44] = 1;
            return new VfxPlaybackRuntime.EmitterState
            {
                Def = definition, Texture = white, DistortionTexture = normal, TextureWidth = 1, TextureHeight = 1,
                ColorGradientTexture = rampMode != 0 ? rampTexture : 0,
                ErosionTexture = erosionMode != 0 ? erosionTexture : 0,
                TextureMult = multMode == 2 ? multTexture : 0,
                Instances = row, InstanceCount = 1
            };
        }
    }
}
