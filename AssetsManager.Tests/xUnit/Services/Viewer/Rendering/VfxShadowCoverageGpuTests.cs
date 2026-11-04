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
public sealed class VfxShadowCoverageGpuTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool mesh in new[] { false, true })
        foreach (float slice in new[] { 0f, .2f })
        for (int ramp = 0; ramp < 3; ramp++)
        foreach (float erosion in new[] { -1f, .5f, 3f })
        foreach (bool mult in new[] { false, true })
            yield return new object[] { mesh, slice, ramp, erosion, mult };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ShadowCoverageUsesItsOwnRampAndErosionRulesInsteadOfTheColorSlice(bool mesh, float slice, int ramp, float erosion, bool mult)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl);
        uint depth = gl.GenTexture(), framebuffer = gl.GenFramebuffer();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, depth);
            gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent32f, 32, 32, 0,
                PixelFormat.DepthComponent, PixelType.Float, ReadOnlySpan<float>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, depth, 0);
            gl.DrawBuffer(DrawBufferMode.None); gl.ReadBuffer(ReadBufferMode.None);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var definition = VfxQualityFlexTests.Definition() with
            {
                DoesCastShadow = true, BlendMode = 3, IsMeshPrimitive = mesh,
                PrimitiveKind = mesh ? VfxPrimitiveKind.Mesh : VfxPrimitiveKind.CameraQuad,
                SliceRange = slice,
                AuthoredFeatures = mult ? new VfxEmitterAuthoredFeatures(HasTextureMultLayer: true) : null,
                AlphaErosion = erosion < 0 ? null : new VfxAlphaErosionDefinition(null, VfxCurveF.Const(erosion), 0, 0, 0),
                RenderState = VfxEmitterRenderState.Default with { DisableBackfaceCull = true, AlphaReference = 255 }
            };
            var instance = new float[VfxPlaybackRuntime.InstanceStride];
            instance[3] = instance[4] = .7f;
            instance[5] = instance[6] = instance[7] = instance[8] = instance[18] = 1;
            instance[21] = instance[22] = instance[31] = instance[32] = 1;
            instance[24] = MathF.Max(0, erosion); instance[28] = 1;
            instance[36] = instance[40] = instance[44] = 1;
            var state = new VfxPlaybackRuntime.EmitterState
            {
                Def = definition, InstanceCount = 1, Instances = instance,
                Texture = renderer.UploadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1),
                ColorGradientTexture = ramp == 0 ? 0 : renderer.UploadTexture(new byte[] { 255, 255, 255, ramp == 1 ? (byte)0 : (byte)255 }, 1, 1)
            };
            if (mesh) renderer.UploadEmitterMesh(state,
                new float[] { -.7f, -.7f, 0, .7f, -.7f, 0, 0, .7f, 0 },
                new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, new float[] { 0, 0, 1, 0, .5f, 1 }, null);
            foreach (float alpha in new[] { 1f, .02f, .04f })
            {
                instance[8] = alpha;
                gl.Viewport(0, 0, 32, 32); gl.DepthMask(true); gl.ClearDepth(1);
                gl.Clear(ClearBufferMask.DepthBufferBit);
                renderer.RenderShadows(new[] { new VfxRenderQueueEntry(state, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
                var center = new float[1];
                gl.ReadPixels<float>(16, 16, 1, 1, PixelFormat.DepthComponent, PixelType.Float, center);
                bool usesRamp = erosion < 0 && !(mesh && mult);
                bool covered = alpha >= .025f && erosion < 3f && !(usesRamp && ramp == 1);
                Assert.InRange(MathF.Abs(center[0] - (covered ? .5f : 1f)), 0, .00001f);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer); gl.DeleteTexture(depth);
        }
    }
}
