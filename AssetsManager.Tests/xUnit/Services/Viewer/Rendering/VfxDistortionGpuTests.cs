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

    public static IEnumerable<object[]> LookupCases()
    {
        foreach (bool mesh in new[] { false, true })
        for (int driver = 0; driver < 4; driver++)
        for (int mult = 0; mult < 3; mult++)
        foreach (bool clampEdges in new[] { false, true })
            yield return new object[] { mesh, driver, mult, clampEdges };
    }

    [Theory]
    [MemberData(nameof(LookupCases))]
    public void DistortionVariableLookupMatchesInstalledShaderAcrossDriversAndAnimatedUv(bool mesh, int driver, int mult, bool clampEdges)
        => CompareWarp(mesh, 128, false, false, 3, 1, mult, driver, clampEdges);

    public static IEnumerable<object[]> RibbonCases()
    {
        foreach (VfxPrimitiveKind primitive in new[] { VfxPrimitiveKind.Beam, VfxPrimitiveKind.CameraSegmentBeam,
                     VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail })
        for (int driver = 0; driver < 4; driver++)
        for (int mult = 0; mult < 3; mult++)
        foreach (byte uvMode in new byte[] { 0, 2 })
            yield return new object[] { primitive, driver, mult, uvMode };
    }

    [Theory]
    [MemberData(nameof(RibbonCases))]
    public void DistortionRibbonLookupMatchesInstalledShader(VfxPrimitiveKind primitive, int driver, int mult, byte uvMode)
        => CompareWarp(false, 128, false, false, 3, 1, mult, driver, false, primitive, uvMode);

    public static IEnumerable<object[]> MeshVariantCases()
    {
        foreach (VfxPrimitiveKind primitive in new[] { VfxPrimitiveKind.AttachedMesh, VfxPrimitiveKind.Beam })
        for (int driver = 0; driver < 4; driver++)
        for (int mult = 0; mult < 3; mult++)
        foreach (byte uvMode in new byte[] { 0, 1, 2 })
        foreach (bool patternedNormal in new[] { false, true })
            yield return new object[] { primitive, driver, mult, uvMode, patternedNormal };
    }

    [Theory]
    [MemberData(nameof(MeshVariantCases))]
    public void DistortionMeshVariantMatchesInstalledShaderWithAnimatedResources(VfxPrimitiveKind primitive, int driver, int mult, byte uvMode, bool patternedNormal)
        => CompareWarp(true, 128, false, false, 3, 1, mult, driver, false, null, uvMode, primitive, patternedNormal);

    private static void CompareWarp(bool mesh, byte alpha, bool vertical, bool occluded, int rampMode = 0, int erosionMode = 0, int multMode = 0, int lookupDriver = 0, bool clampEdges = false, VfxPrimitiveKind? ribbon = null, byte uvMode = 0, VfxPrimitiveKind? meshPrimitive = null, bool patternedNormal = false)
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
        byte[] normalPixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            int at = (y * 8 + x) * 4;
            normalPixels[at] = (byte)(150 + 12 * x);
            normalPixels[at + 1] = (byte)(132 + 8 * y);
            normalPixels[at + 2] = 255;
            normalPixels[at + 3] = (byte)(96 + 6 * x + 7 * y);
        }
        uint normal = patternedNormal ? Texture(8, 8, normalPixels)
            : Texture(1, 1, new byte[] { vertical ? (byte)128 : (byte)255, vertical ? (byte)255 : (byte)128, 255, alpha });
        byte[] rampPixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            int at = (y * 8 + x) * 4;
            rampPixels[at] = (byte)(80 + 20 * x);
            rampPixels[at + 1] = (byte)(70 + 22 * y);
            rampPixels[at + 2] = (byte)(100 + 8 * x + 10 * y);
            rampPixels[at + 3] = (byte)(60 + 12 * x + 14 * y);
        }
        uint rampTexture = rampMode == 3 ? Texture(8, 8, rampPixels)
            : Texture(1, 1, rampMode == 2 ? new byte[] { 128, 64, 192, 128 } : new byte[] { 255, 255, 255, 128 });
        uint erosionTexture = Texture(1, 1, new byte[] { 0, 0, 0, 128 });
        uint multTexture = Texture(1, 1, new byte[] { 128, 128, 128, 128 });
        var definition = new VfxEmitterDefinition("warp", VfxCurveF.Const(1), VfxCurveF.Const(1),
            null, 0, 0, false, false, 1, VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One),
            null, null, null, null, VfxCurve3.Const(Vector3.Zero), "white.tex", meshPrimitive.HasValue ? new Vector2(2, 2) : Vector2.One, 1, false, mesh,
            PrimitiveKind: meshPrimitive ?? ribbon ?? (mesh ? VfxPrimitiveKind.Mesh : VfxPrimitiveKind.CameraQuad), UvMode: uvMode,
            MeshPath: meshPrimitive == VfxPrimitiveKind.Beam ? "beam.scb" : null,
            RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true, AlphaReference = 0 },
            Distortion: new VfxDistortionDefinition(.25f, 1, "normal.tex"),
            ParticleColorTexturePath: rampMode != 0 ? "ramp.tex" : null,
            ColorLookUpTypeX: lookupDriver, ColorLookUpTypeY: lookupDriver == 3 ? 1 : lookupDriver + 1,
            ColorLookUpScales: rampMode == 3 ? (clampEdges ? new Vector2(1.8f, -1.6f) : new Vector2(.65f, .55f)) : Vector2.Zero,
            ColorLookUpOffsets: rampMode == 3 ? (clampEdges ? new Vector2(-.3f, 1.2f) : new Vector2(.12f, .18f)) : Vector2.Zero,
            TextureMultTexDiv: rampMode == 3 ? new Vector2(2, 2) : Vector2.One,
            TextureMultEmitterUvScrollRate: rampMode == 3 ? new Vector2(.17f, -.11f) : Vector2.Zero,
            TextureMultPath: multMode == 2 ? "mult.tex" : null,
            AuthoredFeatures: multMode == 1 ? new VfxEmitterAuthoredFeatures(HasTextureMultLayer: true) : null,
            AlphaErosion: erosionMode != 0
                ? new VfxAlphaErosionDefinition("erosion.tex", VfxCurveF.Const(erosionMode == 1 ? .2f : .9f),
                    .1f, .1f, 2, VfxCurve4.Const(Vector4.UnitW), SliceWidth: 1.5f) : null,
            Beam: ribbon.HasValue || meshPrimitive == VfxPrimitiveKind.Beam ? new VfxBeamDefinition(0, 0, 2, VfxCurve3.Const(Vector3.One),
                VfxCurve4.Const(Vector4.One), false, Vector3.Zero, Vector3.Zero) : null,
            Trail: ribbon.HasValue ? new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0) : null);
        var nativeEmitter = State(); var stockEmitter = State();
        if (mesh)
            foreach (var pair in new[] { (native, nativeEmitter), (stock, stockEmitter) })
                if (meshPrimitive == VfxPrimitiveKind.Beam)
                    pair.Item1.UploadEmitterMesh(pair.Item2,
                        new float[] { 0, -1, -1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, 1, 1, 0, -1, 1 },
                        new float[] { 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0 },
                        new float[] { 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1 }, null);
                else pair.Item1.UploadEmitterMesh(pair.Item2,
                    new float[] { -1, -1, 0, 1, -1, 0, -1, 1, 0, 1, 1, 0 },
                    new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    new float[] { 0, 0, 1, 0, 0, 1, 1, 1 }, null, new uint[] { 0, 1, 2, 2, 1, 3 });
        var previousFrames = new Dictionary<(int, int, float), byte[]>();
        try
        {
            foreach (var size in new[] { (64, 64), (96, 48), (48, 96), (64, 64) })
            foreach (float particleAlpha in new[] { 1f, .2f })
            for (int frame = 0; frame < 2; frame++)
            {
                int width = size.Item1, height = size.Item2;
                if (rampMode == 3)
                    foreach (var emitter in new[] { nativeEmitter, stockEmitter })
                    {
                        emitter.RenderTime = frame * .7f;
                        for (int particle = 0; particle < emitter.InstanceCount; particle++)
                        {
                            int at = particle * VfxPlaybackRuntime.InstanceStride;
                            emitter.Instances[at + 10] = frame;
                            emitter.Instances[at + 11] = (frame == 0 ? .15f : .65f) + particle * .15f;
                            emitter.Instances[at + 12] = (frame == 0 ? .25f : .6f) + particle * .1f;
                            emitter.Instances[at + 13] = frame == 0 ? .3f : .4f;
                            emitter.Instances[at + 14] = frame == 0 ? .1f : .2f;
                            emitter.Instances[at + 34] = clampEdges ? .75f : .2f;
                            emitter.Instances[at + 29] = frame * .13f;
                            emitter.Instances[at + 30] = frame * -.09f;
                            emitter.Instances[at + 33] = frame * .3f;
                            if (meshPrimitive.HasValue)
                            {
                                emitter.Instances[at + 19] = frame * .17f;
                                emitter.Instances[at + 20] = frame * -.09f;
                                emitter.Instances[at + 23] = frame * .2f;
                            }
                        }
                    }
                byte[] background = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int at = (y * width + x) * 4;
                    background[at] = (byte)(x * 255 / (width - 1));
                    background[at + 1] = (byte)(y * 255 / (height - 1));
                    background[at + 2] = frame == 0 || rampMode == 3 ? (byte)30 : (byte)190;
                    background[at + 3] = 153;
                }
                foreach (int resourceStep in meshPrimitive.HasValue ? new[] { 0, 1, 0, 2, 0 } : new[] { 0, 1, 0 })
                {
                    bool missingMap = resourceStep == 1, missingRamp = resourceStep == 2;
                    nativeEmitter.DistortionTexture = stockEmitter.DistortionTexture = missingMap ? 0 : normal;
                    nativeEmitter.ColorGradientTexture = stockEmitter.ColorGradientTexture = !missingRamp && rampMode != 0 ? rampTexture : 0;
                    for (int particle = 0; particle < nativeEmitter.InstanceCount; particle++)
                        nativeEmitter.Instances[particle * VfxPlaybackRuntime.InstanceStride + 8]
                            = stockEmitter.Instances[particle * VfxPlaybackRuntime.InstanceStride + 8] = particleAlpha;
                    byte[] expected = Draw(native, nativeEmitter), actual = Draw(stock, stockEmitter);
                    Assert.Null(native.GameParticleFallback(nativeEmitter, mesh));
                    int center = ((height / 2) * width + width / 2) * 4;
                    if (alpha != 0 && !occluded && !missingMap)
                    {
                        // A coloured ramp can cancel the warp's change in one channel while visibly changing the others.
                        int contribution = rampMode == 3
                            ? Math.Abs(expected[center] - background[center])
                                + Math.Abs(expected[center + 1] - background[center + 1])
                                + Math.Abs(expected[center + 2] - background[center + 2])
                            : Math.Abs(expected[center + (vertical ? 1 : 0)] - background[center + (vertical ? 1 : 0)]);
                        Assert.True(contribution > (rampMode == 0 ? 8 : 1),
                            $"The installed shader must visibly affect the gradient: primitive={definition.PrimitiveKind}, mult={multMode}, frame={frame}, size={width}x{height}, alpha={particleAlpha}, game=({expected[center]}, {expected[center + 1]}, {expected[center + 2]}), background=({background[center]}, {background[center + 1]}, {background[center + 2]}).");
                    }
                    if (rampMode < 2) Assert.InRange(Math.Abs(expected[center + 2] - background[center + 2]), 0, 1);
                    if (rampMode == 3)
                    {
                        if (!missingMap && !missingRamp)
                        {
                            var key = (width, height, particleAlpha);
                            if (frame == 0) previousFrames[key] = expected;
                            else
                                Assert.True(Math.Abs(expected[center] - previousFrames[key][center])
                                    + Math.Abs(expected[center + 1] - previousFrames[key][center + 1]) > 0,
                                    "Changing lookup drivers or animated UVs must change the installed shader's result over the same background.");
                        }
                        foreach (int y in new[] { height / 3, height / 2, height * 2 / 3 })
                        foreach (int x in new[] { width / 3, width / 2, width * 2 / 3 })
                        for (int channel = 0; channel < 4; channel++)
                        {
                            int sample = (y * width + x) * 4;
                            Assert.True(Math.Abs(actual[sample + channel] - expected[sample + channel]) <= 2,
                                $"Variable lookup: primitive={definition.PrimitiveKind}, uvMode={uvMode}, driver={lookupDriver}, mult={multMode}, clamp={clampEdges}, frame={frame}, pixel={sample / 4}, channel={channel}: game={expected[sample + channel]}, stock={actual[sample + channel]}.");
                        }
                    }
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
                    Assert.Equal((1, ribbon.HasValue ? 2 : 1), renderer.LastParticleDrawCounts);
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
            bool trail = ribbon is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
            int count = trail || ribbon == VfxPrimitiveKind.CameraSegmentBeam ? 2 : 1;
            if (ribbon.HasValue) row[4] = row[18] = 0;
            float[] instances = new float[count * VfxPlaybackRuntime.InstanceStride];
            var emitter = new VfxPlaybackRuntime.EmitterState
            {
                Def = definition, Texture = white, DistortionTexture = normal, TextureWidth = 1, TextureHeight = 1,
                ColorGradientTexture = rampMode != 0 ? rampTexture : 0,
                ErosionTexture = erosionMode != 0 ? erosionTexture : 0,
                TextureMult = multMode == 2 ? multTexture : 0,
                SystemOrigin = new Vector3(-.75f, 0, .2f), SystemTarget = new Vector3(.75f, 0, .2f),
                SystemOrientation = Matrix4x4.Identity, InstanceCount = count
            };
            for (int particle = 0; particle < count; particle++)
            {
                int at = particle * VfxPlaybackRuntime.InstanceStride;
                row.CopyTo(instances, at);
                if (trail) instances[at] = particle == 0 ? -.75f : .75f;
                if (ribbon == VfxPrimitiveKind.CameraSegmentBeam) instances[at] = emitter.SystemOrigin.X;
                if (ribbon.HasValue)
                    emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                    {
                        Serial = (uint)particle, Ordinal = (ushort)particle, Life = 1, BirthFrame = Matrix4x4.Identity,
                        TrailTiling = trail ? new Vector3(2, 1, 0) : Vector3.Zero,
                        BirthRotation = ribbon == VfxPrimitiveKind.ArbitraryTrail
                            ? new Vector3(0, 0, MathF.PI / 2) : Vector3.Zero
                    });
            }
            emitter.Instances = instances;
            return emitter;
        }
    }
}
