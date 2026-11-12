using System;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

public sealed class SknVertexInspectionGpuTests
{
    [Fact]
    public void PointsNormalsDepthVisibilityAndGlStateArePreserved()
    {
        using var context = new HiddenWglContext();
        using var gl = GL.GetApi(context.GetProcAddress);
        using var scene = new SceneModel();
        uint target = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, target);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 64, 64, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
        uint framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, target, 0);
        uint depth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, 64, 64);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
        Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        gl.Viewport(0, 0, 64, 64);
        var part = new SknVertexInspectionPart("Body", new[] { new Vector3(-0.5f, 0, 0) }, new[] { Vector3.UnitX });
        var batches = new[] { new SknVertexInspectionBatch(scene, part, new Vector4(1, 0, 0, 1)) };
        using (var renderer = new SknVertexInspectionRenderer(gl))
        {
            gl.Enable(EnableCap.Blend);
            gl.Enable(EnableCap.DepthTest);
            gl.Disable(EnableCap.ProgramPointSize);
            gl.DepthMask(true);
            gl.DepthFunc(DepthFunction.Greater);
            gl.ClearColor(0, 0, 0, 0);
            gl.ClearDepth(1);
            Clear();
            renderer.Render(batches, Matrix4x4.Identity, _ => true, true, false, false, 6, 0.5f);
            Assert.InRange(RedPixels(), 20, 40);
            Assert.True(gl.IsEnabled(EnableCap.Blend));
            Assert.True(gl.IsEnabled(EnableCap.DepthTest));
            Assert.False(gl.IsEnabled(EnableCap.ProgramPointSize));
            Assert.Equal((int)DepthFunction.Greater, gl.GetInteger(GetPName.DepthFunc));
            gl.GetBoolean(GetPName.DepthWritemask, out bool depthWrite);
            Assert.True(depthWrite);
            Assert.Equal(0, gl.GetInteger(GetPName.CurrentProgram));
            Assert.Equal(0, gl.GetInteger(GetPName.VertexArrayBinding));

            Clear();
            renderer.Render(batches, Matrix4x4.Identity, _ => true, false, true, false, 6, 0.5f);
            Assert.InRange(RedPixels(), 14, 18);

            Clear();
            renderer.Render(batches, Matrix4x4.Identity, _ => false, true, true, false, 6, 0.5f);
            Assert.Equal(0, RedPixels());

            gl.ClearDepth(0.1);
            Clear();
            renderer.Render(batches, Matrix4x4.Identity, _ => true, true, false, false, 6, 0.5f);
            Assert.Equal(0, RedPixels());
            renderer.Render(batches, Matrix4x4.Identity, _ => true, true, false, true, 6, 0.5f);
            Assert.InRange(RedPixels(), 20, 40);

            // A new data snapshot replaces the previous upload and keeps its own color and transform.
            gl.ClearDepth(1);
            Clear();
            scene.PositionX = 0.5;
            var green = new[] { new SknVertexInspectionBatch(scene, part, new Vector4(0, 1, 0, 1)) };
            renderer.Render(green, Matrix4x4.Identity, _ => true, true, false, false, 6, 0.5f);
            Assert.Equal(0, RedPixels());
            byte[] center = new byte[4];
            gl.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, center.AsSpan());
            Assert.Equal(255, center[1]);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        gl.DeleteFramebuffer(framebuffer);
        gl.DeleteRenderbuffer(depth);
        gl.DeleteTexture(target);

        void Clear() => gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        int RedPixels()
        {
            byte[] pixels = new byte[64 * 64 * 4];
            gl.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            return Enumerable.Range(0, 64 * 64).Count(index => pixels[index * 4] > 200);
        }
    }
}
