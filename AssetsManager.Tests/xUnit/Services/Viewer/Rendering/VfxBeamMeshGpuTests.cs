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
public sealed class VfxBeamMeshGpuTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool native in new[] { false, true })
        foreach (byte uvMode in new byte[] { 0, 1, 2 })
        foreach (bool distortion in new[] { false, true })
        foreach (bool wireframe in new[] { false, true })
            yield return new object[] { native, uvMode, distortion, wireframe };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void BeamMeshDrawsBetweenItsEndsAndSkipsZeroLength(bool native, byte uvMode, bool distortion, bool wireframe)
    {
        string install = InstalledSkins.FindInstall();
        Assert.False(native && string.IsNullOrWhiteSpace(install), "The native beam regression requires a client install.");
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl, native ? InstalledSkins.Settings(install) : null);
        uint target = Texture(gl, 32, 32, new byte[32 * 32 * 4]);
        uint framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, target, 0);
        Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        uint white = Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
        try
        {
            var definition = VfxBeamSupportTests.Definition() with
            {
                MeshPath = "beam.scb", UvMode = uvMode, MeshAlignYawToCamera = true, MeshAlignPitchToCamera = true,
                RenderState = VfxEmitterRenderState.Default with { DisableBackfaceCull = true, RenderPhase = distortion ? (byte)3 : (byte)7 },
                Distortion = distortion ? new VfxDistortionDefinition(0, 1, "normal.tex") : null,
                Beam = VfxBeamSupportTests.Definition().Beam with { ColorBoundToDistance = true,
                    ColorByDistance = VfxCurve4.Const(new Vector4(0.25f, 0.5f, 1, 1)) }
            };
            var instances = new float[VfxPlaybackRuntime.InstanceStride];
            instances[0] = 1000; // Beam placement must ignore the particle's own translation.
            instances[3] = instances[4] = 0.25f; instances[18] = 100;
            instances[5] = instances[6] = instances[7] = instances[8] = 1;
            instances[15] = instances[16] = instances[17] = 1;
            instances[21] = instances[22] = instances[31] = instances[32] = 1;
            instances[36] = instances[40] = instances[44] = 1;
            var emitter = new VfxPlaybackRuntime.EmitterState { Def = definition, Instances = instances, InstanceCount = 1,
                Texture = white, DistortionTexture = white, SystemOrigin = new Vector3(-0.75f, 0, 0),
                SystemTarget = new Vector3(0.75f, 0, 0) };
            renderer.UploadEmitterMesh(emitter,
                new float[] { 0, -1, -1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, 1, 1, 0, -1, 1 },
                new float[] { 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0 },
                new float[] { 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1 }, null);
            Assert.Equal(2f, emitter.MeshDepth);
            gl.Viewport(0, 0, 32, 32);
            byte[] Draw()
            {
                if (distortion)
                {
                    gl.ClearColor(1, 1, 1, 0); gl.Clear(ClearBufferMask.ColorBufferBit);
                    renderer.CaptureScene(32, 32, true, false);
                }
                gl.ClearColor(0, 0, 0, 0); gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity,
                    Matrix4x4.Identity, wireframePass: wireframe);
                var pixels = new byte[32 * 32 * 4];
                gl.ReadPixels(0, 0, 32, 32, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                return pixels;
            }
            byte[] before = Draw();
            Assert.Contains(before, channel => channel != 0);
            if (native && !wireframe) Assert.Null(renderer.GameParticleFallback(emitter, true));
            if (!wireframe)
            {
                int at = (16 * 32 + 16) * 4;
                Assert.InRange((int)before[at], 63, 65);
                Assert.InRange((int)before[at + 1], 127, 129);
                Assert.Equal(255, before[at + 2]);
            }
            Assert.Equal(before, Draw());
            emitter.SystemTarget = emitter.SystemOrigin;
            Assert.All(Draw(), channel => Assert.Equal(0, channel));
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer); gl.DeleteTexture(target); gl.DeleteTexture(white);
        }
    }

    private static uint Texture(GL gl, uint width, uint height, byte[] pixels)
    {
        uint texture = gl.GenTexture(); gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        return texture;
    }
}
