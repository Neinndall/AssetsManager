using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxDepthPushCameraGpuTests
{
    [Theory]
    [InlineData(true, false, -2f)]
    [InlineData(true, false, 0f)]
    [InlineData(true, false, 2f)]
    [InlineData(true, true, -2f)]
    [InlineData(true, true, 0f)]
    [InlineData(true, true, 2f)]
    [InlineData(false, false, -2f)]
    [InlineData(false, false, 0f)]
    [InlineData(false, false, 2f)]
    [InlineData(false, true, -2f)]
    [InlineData(false, true, 0f)]
    [InlineData(false, true, 2f)]
    public void StockDepthPushPreservesScreenCoverageWithTranslatedAndRotatedCameras(
        bool orthographic, bool rotated, float push)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl, null);
        uint framebuffer = gl.GenFramebuffer();
        uint color = gl.GenRenderbuffer();
        uint depth = gl.GenRenderbuffer();
        const int width = 128, height = 96;
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, color);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, width, height);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer, color);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, width, height);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, depth);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var eye = rotated ? new Vector3(13, 7, 18) : new Vector3(0, 0, 10);
            var view = Matrix4x4.CreateLookAt(eye, rotated ? new Vector3(3, 2, 1) : Vector3.Zero, Vector3.UnitY);
            Assert.True(Matrix4x4.Invert(view, out var cameraWorld));
            var projection = orthographic ? Matrix4x4.CreateOrthographic(8, 6, 1, 41)
                : Matrix4x4.CreatePerspectiveFieldOfView(.7f, (float)width / height, 1, 41);
            var center = new Vector3(2, 1, -10);
            var world = Vector3.Transform(center, cameraWorld);
            var definition = new VfxEmitterDefinition("depth push", VfxCurveF.Const(1), VfxCurveF.Const(1),
                null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                "", Vector2.One, 1, false, false, PrimitiveKind: VfxPrimitiveKind.CameraQuad,
                DepthPushPull: push,
                RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true });
            var instance = new float[VfxPlaybackRuntime.InstanceStride];
            instance[0] = world.X; instance[1] = world.Y; instance[2] = world.Z;
            instance[3] = .6f; instance[4] = .4f; instance[18] = 1;
            instance[5] = instance[6] = instance[7] = instance[8] = 1;
            instance[21] = instance[22] = instance[31] = instance[32] = 1;
            instance[36] = instance[40] = instance[44] = 1;
            var emitter = new VfxPlaybackRuntime.EmitterState
            { Def = definition, Instances = instance, InstanceCount = 1,
                Texture = renderer.UploadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1) };
            gl.Viewport(0, 0, width, height);
            // Reuse the stock program after a draw with the other projection type.
            var previousProjection = orthographic
                ? Matrix4x4.CreatePerspectiveFieldOfView(.7f, (float)width / height, 1, 41)
                : Matrix4x4.CreateOrthographic(8, 6, 1, 41);
            renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, view * previousProjection, view);
            gl.DepthMask(true);
            gl.ClearColor(0, 0, 0, 0);
            gl.ClearDepth(1);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, view * projection, view);
            Assert.NotNull(renderer.GameParticleFallback(emitter, false));
            var pixels = new byte[width * height * 4];
            gl.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            // A view-axis push in orthographic and a ray push in perspective preserve projected XY.
            Vector4 lower = Vector4.Transform(new Vector4(center + new Vector3(-.6f, -.4f, 0), 1), projection);
            Vector4 upper = Vector4.Transform(new Vector4(center + new Vector3(.6f, .4f, 0), 1), projection);
            float left = (lower.X / lower.W + 1) * width / 2;
            float right = (upper.X / upper.W + 1) * width / 2;
            float bottom = (lower.Y / lower.W + 1) * height / 2;
            float top = (upper.Y / upper.W + 1) * height / 2;
            int interior = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float margin = MathF.Min(MathF.Min(x + .5f - left, right - x - .5f),
                    MathF.Min(y + .5f - bottom, top - y - .5f));
                if (MathF.Abs(margin) < .8f) continue;
                Assert.True((pixels[(y * width + x) * 4 + 3] > 0) == (margin > 0),
                    $"Coverage changed at ({x}, {y}), orthographic={orthographic}, rotated={rotated}, push={push}.");
                if (margin > 0) interior++;
            }
            Assert.True(interior > 20);
            if (orthographic)
            {
                Vector4 pushed = Vector4.Transform(new Vector4(center.X, center.Y, center.Z - push, 1), projection);
                float expectedDepth = (pushed.Z / pushed.W + 1) * .5f;
                int sampleX = (int)((left + right) * .5f), sampleY = (int)((bottom + top) * .5f);
                var stored = new float[1];
                gl.ReadPixels(sampleX, sampleY, 1, 1, PixelFormat.DepthComponent, PixelType.Float, stored.AsSpan());
                Assert.InRange(MathF.Abs(stored[0] - expectedDepth), 0, .00001f);
            }
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteRenderbuffer(color);
            gl.DeleteRenderbuffer(depth);
        }
    }
}
