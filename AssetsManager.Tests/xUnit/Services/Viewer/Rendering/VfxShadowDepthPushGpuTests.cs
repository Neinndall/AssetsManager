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
public sealed class VfxShadowDepthPushGpuTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (float angle in new[] { 0f, .6f, -.6f })
        foreach (float push in new[] { 0f, .3f, -.3f })
        foreach (float lightAngle in new[] { 0f, -.35f })
        foreach (float lightFar in new[] { 11f, 10000f })
            yield return new object[] { angle, push, lightAngle, lightFar };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ShadowDepthPushUsesTheLightAxisWhileCornersStillFaceTheSceneEye(float angle, float push, float lightAngle, float lightFar)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl);
        uint depth = gl.GenTexture(), framebuffer = gl.GenFramebuffer();
        const int size = 96;
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, depth);
            gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent32f, size, size, 0,
                PixelFormat.DepthComponent, PixelType.Float, ReadOnlySpan<float>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, depth, 0);
            gl.DrawBuffer(DrawBufferMode.None);
            gl.ReadBuffer(ReadBufferMode.None);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var definition = VfxQualityFlexTests.Definition() with
            {
                DoesCastShadow = true, BlendMode = 3,
                RenderState = VfxEmitterRenderState.Default with { DisableBackfaceCull = true }
            };
            var instance = new float[VfxPlaybackRuntime.InstanceStride];
            instance[3] = .55f; instance[4] = .4f;
            instance[5] = instance[6] = instance[7] = instance[8] = instance[18] = 1;
            instance[21] = instance[22] = instance[31] = instance[32] = 1;
            instance[36] = instance[40] = instance[44] = 1;
            var state = new VfxPlaybackRuntime.EmitterState
            {
                Def = definition, InstanceCount = 1, Instances = instance,
                Texture = renderer.UploadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1)
            };
            var eye = new Vector3(MathF.Sin(angle) * 5, 0, MathF.Cos(angle) * 5);
            var eyeView = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
            var lightEye = new Vector3(MathF.Sin(lightAngle) * 5, 0, MathF.Cos(lightAngle) * 5);
            var light = Matrix4x4.CreateLookAt(lightEye, Vector3.Zero, Vector3.UnitY)
                * Matrix4x4.CreateOrthographic(2, 2, 1, lightFar);
            float[] Draw(float offset)
            {
                state.Def = definition with { DepthPushPull = offset };
                gl.Viewport(0, 0, size, size);
                gl.DepthMask(true);
                gl.ClearDepth(1);
                gl.Clear(ClearBufferMask.DepthBufferBit);
                renderer.RenderShadows(new[] { new VfxRenderQueueEntry(state, 0, 0) }, light, eyeView);
                var pixels = new float[size * size];
                gl.ReadPixels<float>(0, 0, size, size, PixelFormat.DepthComponent, PixelType.Float, pixels);
                Assert.Equal(GLEnum.NoError, gl.GetError());
                return pixels;
            }
            float[] baseline = Draw(0), shifted = Draw(push);
            // The orthographic light's view-space -Z push preserves projected XY and changes only depth.
            float expectedDelta = new Vector3(light.M13, light.M23, light.M33).Length() * .5f * push;
            int covered = 0;
            for (int at = 0; at < baseline.Length; at++)
            {
                if (baseline[at] == 1)
                {
                    Assert.Equal(1f, shifted[at]);
                    continue;
                }
                covered++;
                Assert.InRange(MathF.Abs(shifted[at] - baseline[at] - expectedDelta), 0, .000002f);
            }
            Assert.True(covered > 100);
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(depth);
        }
    }
}
