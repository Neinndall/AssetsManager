using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Tests.xUnit.Services.Viewer.Vfx;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxGroundPaletteGpuTests
{
    public static IEnumerable<object[]> GroundCases()
    {
        foreach (var kind in new[] { VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.ArbitraryQuad,
            VfxPrimitiveKind.Mesh, VfxPrimitiveKind.AttachedMesh, VfxPrimitiveKind.Beam,
            VfxPrimitiveKind.CameraSegmentBeam, VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail })
        foreach (bool native in new[] { false, true })
        foreach (bool rowless in new[] { false, true })
            yield return new object[] { kind, native, rowless };
    }

    [Theory]
    [MemberData(nameof(GroundCases))]
    public void GroundLayerPreservesTheNormalDrawAndRowlessPalettesKeepNativeShaders(VfxPrimitiveKind kind, bool native, bool rowless)
    {
        using var context = new HiddenWglContext();
        using var gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        string install = InstalledSkins.FindInstall();
        Assert.False(native && install == null, "The native regression requires a client install.");
        renderer.Initialize(gl, native ? InstalledSkins.Settings(install) : null);
        uint output = Texture(gl, 32, 32, new byte[32 * 32 * 4]);
        uint frame = Frame(gl, output);
        uint white = Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
        try
        {
            bool mesh = kind is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
            bool strip = kind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.CameraSegmentBeam;
            var def = VfxBeamSupportTests.Definition() with { PrimitiveKind = kind, IsMeshPrimitive = mesh,
                BlendMode = 3, RenderState = VfxEmitterRenderState.Default with { DisableBackfaceCull = true },
                PaletteDefinition = rowless ? new VfxPaletteDefinition(0, VfxCurve3.Const(Vector3.Zero)) : null,
                Trail = new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0) };
            int count = strip ? 2 : 1;
            var data = new float[count * VfxPlaybackRuntime.InstanceStride];
            var state = new VfxPlaybackRuntime.EmitterState { Def = def, Texture = white,
                InstanceCount = count, SystemOrigin = new Vector3(-0.6f, 0.4f, 0), SystemTarget = new Vector3(0.6f, 0.4f, 0) };
            for (int i = 0; i < count; i++)
            {
                int at = i * VfxPlaybackRuntime.InstanceStride;
                data[at] = strip && kind != VfxPrimitiveKind.CameraSegmentBeam ? (i == 0 ? -0.6f : 0.6f)
                    : kind == VfxPrimitiveKind.CameraSegmentBeam ? state.SystemOrigin.X : 0;
                data[at + 1] = 0.4f;
                data[at + 3] = 0.25f; data[at + 4] = mesh || def.DrawsAsQuad ? 0.25f : 0;
                data[at + 18] = mesh ? 1 : 0;
                data[at + 5] = data[at + 6] = data[at + 7] = data[at + 8] = 1;
                data[at + 21] = data[at + 22] = data[at + 31] = data[at + 32] = 1;
                data[at + 36] = data[at + 40] = data[at + 44] = 1;
                state.Particles.Add(new VfxPlaybackRuntime.Particle { Serial = (uint)i, Ordinal = (ushort)i,
                    BirthFrame = Matrix4x4.Identity, Life = 1, TrailTiling = Vector3.One,
                    BirthRotation = kind == VfxPrimitiveKind.ArbitraryTrail ? new Vector3(0,0,MathF.PI/2) : Vector3.Zero });
            }
            state.Instances = data;
            if (mesh) renderer.UploadEmitterMesh(state, new float[] { -1,-1,0, 1,-1,0, 0,1,0 },
                new float[] { 0,0,1, 0,0,1, 0,0,1 }, new float[] { 0,0, 1,0, .5f,1 }, null);
            byte[] Draw()
            {
                gl.Viewport(0, 0, 32, 32); gl.ClearColor(0, 0, 0, 0); gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(state, 0, 0) }, Matrix4x4.Identity,
                    Matrix4x4.CreateTranslation(0, 0, -10));
                var pixels = new byte[32 * 32 * 4];
                gl.ReadPixels(0, 0, 32, 32, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                return pixels;
            }
            byte[] normal = Draw();
            Assert.Contains(normal, value => value != 0);
            if (native) Assert.Null(renderer.GameParticleFallback(state, mesh));
            state.Def = def with { IsGroundLayer = true };
            Assert.Equal(normal, Draw());
            if (native) Assert.Null(renderer.GameParticleFallback(state, mesh));
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally { gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0); gl.DeleteFramebuffer(frame); gl.DeleteTexture(output); gl.DeleteTexture(white); }
    }

    public static IEnumerable<object[]> ProjectionCases()
    {
        foreach (bool terrain in new[] { false, true })
        foreach (var sample in new[] { (0,0,0f,0f,255,0,0), (0,0,.5f,0f,0,255,0),
            (0,1,0f,0f,0,0,255), (0,0,0f,.5f,0,0,255), (0,0,1f,0f,255,0,0),
            (1,0,1f,0f,0,255,0), (2,0,1f,0f,0,255,0), (3,0,1f,0f,0,0,0) })
            yield return new object[] { sample.Item1, sample.Item2, sample.Item3, sample.Item4,
                sample.Item5, sample.Item6, sample.Item7, terrain };
    }

    [Theory]
    [MemberData(nameof(ProjectionCases))]
    public void ProjectionPalettesUseRowsScrollAndAddressingWithoutReplacingAlpha(int mode, int row, float scrollU,
        float scrollV, int red, int green, int blue, bool terrain)
    {
        using var context = new HiddenWglContext();
        using var gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl, null);
        uint output = Texture(gl, 32, 32, new byte[32 * 32 * 4]); uint frame = Frame(gl, output);
        uint sprite = Texture(gl, 1, 1, new byte[] { 128, 0, 0, 128 });
        uint palette = Texture(gl, 2, 2, new byte[] { 255,0,0,0, 0,255,0,0, 0,0,255,0, 255,255,0,0 });
        uint depth = 0;
        try
        {
            if (terrain)
            {
                depth = gl.GenTexture(); gl.BindTexture(TextureTarget.Texture2D,depth);
                gl.TexImage2D(TextureTarget.Texture2D,0,InternalFormat.DepthComponent32f,1,1,0,
                    PixelFormat.DepthComponent,PixelType.Float,new float[] { .5f }.AsSpan());
                gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)GLEnum.Nearest);
                gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)GLEnum.Nearest);
                renderer.SetTerrainDepth(depth,32,32);
            }
            var def = VfxBeamSupportTests.Definition() with { PrimitiveKind = VfxPrimitiveKind.PlanarProjection,
                BlendMode = 3, Beam = null, Projection = new VfxProjectionDefinition(1, 1),
                PaletteDefinition = new VfxPaletteDefinition(2, VfxCurve3.Const(new Vector3(row, 0, 0)), "palette.tex",
                    PaletteSourceMixColor: new Vector4(.498046875f,0,0,0), ScrollU: VfxCurveF.Const(scrollU),
                    ScrollV: VfxCurveF.Const(scrollV), AddressMode: mode) };
            var data = new float[VfxPlaybackRuntime.InstanceStride];
            data[3] = data[18] = .75f; data[5] = data[6] = data[7] = data[8] = 1;
            data[21] = data[22] = data[31] = data[32] = 1; data[36] = data[40] = data[44] = 1;
            var state = new VfxPlaybackRuntime.EmitterState { Def = def, Texture = sprite, PaletteTexture = palette,
                InstanceCount = 1, Instances = data };
            gl.Viewport(0,0,32,32); gl.ClearColor(0,0,0,0); gl.Clear(ClearBufferMask.ColorBufferBit);
            Matrix4x4 top = Matrix4x4.CreateRotationX(MathF.PI / 2);
            renderer.Render(new[] { new VfxRenderQueueEntry(state, 0, 0) }, top, top);
            byte[] pixel = new byte[4]; gl.ReadPixels(16,16,1,1,PixelFormat.Rgba,PixelType.UnsignedByte,pixel.AsSpan());
            Assert.InRange((int)pixel[0], Math.Max(0,red-1), Math.Min(255,red+1));
            Assert.InRange((int)pixel[1], Math.Max(0,green-1), Math.Min(255,green+1));
            Assert.InRange((int)pixel[2], Math.Max(0,blue-1), Math.Min(255,blue+1));
            Assert.Equal(128, pixel[3]); Assert.Equal(GLEnum.NoError, gl.GetError());
            foreach (int rows in new[] { 0, -1, 2 })
            {
                state.Def = def with { PaletteDefinition = def.PaletteDefinition with { PaletteCount = rows } };
                state.PaletteTexture = rows == 2 ? 0 : palette;
                gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(state,0,0) },top,top);
                gl.ReadPixels(16,16,1,1,PixelFormat.Rgba,PixelType.UnsignedByte,pixel.AsSpan());
                Assert.Equal(new byte[] {128,0,0,128},pixel);
            }
            Assert.Equal(GLEnum.NoError,gl.GetError());
        }
        finally { gl.BindFramebuffer(FramebufferTarget.Framebuffer,0); gl.DeleteFramebuffer(frame); gl.DeleteTexture(output); gl.DeleteTexture(sprite); gl.DeleteTexture(palette); if(depth != 0) gl.DeleteTexture(depth); }
    }

    private static uint Frame(GL gl, uint texture)
    {
        uint frame = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, frame);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
        Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer)); return frame;
    }

    private static uint Texture(GL gl, uint width, uint height, byte[] data)
    {
        uint texture = gl.GenTexture(); gl.BindTexture(TextureTarget.Texture2D,texture);
        gl.TexImage2D(TextureTarget.Texture2D,0,InternalFormat.Rgba8,width,height,0,PixelFormat.Rgba,PixelType.UnsignedByte,data.AsSpan());
        gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)GLEnum.Linear); return texture;
    }
}
