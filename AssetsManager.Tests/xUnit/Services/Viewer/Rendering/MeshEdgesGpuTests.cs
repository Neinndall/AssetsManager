using System;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

public sealed class MeshEdgesGpuTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProductionShadersDrawTheSharedDiagonalOnceAndRestoreSurfaceIndices(int host)
    {
        using var context = new HiddenWglContext();
        using var gl = GL.GetApi(context.GetProcAddress);
        uint program = GlShaderCompiler.CreateProgram(gl, false,
            host == 0 ? GlMeshShaderSource.Vertex : host == 1 ? MapCharacterShaderSource.Vertex : MapGeometryShaderSource.Vertex,
            host == 0 ? GlMeshShaderSource.Fragment : host == 1 ? MapCharacterShaderSource.Fragment : MapGeometryShaderSource.Fragment);
        uint vao = gl.GenVertexArray(); gl.BindVertexArray(vao);
        uint vertices = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertices);
        Vector3[] positions = { new(-.75f,-.75f,0), new(.75f,-.75f,0), new(-.75f,.75f,0), new(.75f,.75f,0) };
        gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<Vector3>(positions), BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
        gl.VertexAttrib3(1, 0f, 0f, 1f); gl.VertexAttrib4(3, 1f, 0f, 0f, 1f);
        gl.VertexAttrib4(5, 0f, 0f, 0f, 0f); gl.VertexAttrib4(6, 1f, 0f, 0f, 0f);
        uint[] triangles = { 0,1,2, 2,1,3 };
        uint surface = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, surface);
        gl.BufferData(BufferTargetARB.ElementArrayBuffer, new ReadOnlySpan<uint>(triangles), BufferUsageARB.StaticDraw);
        uint bones = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.UniformBuffer, bones);
        gl.BufferData(BufferTargetARB.UniformBuffer, new ReadOnlySpan<Matrix4x4>(Enumerable.Repeat(Matrix4x4.Identity, 512).ToArray()), BufferUsageARB.StaticDraw);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, bones);
        uint block = gl.GetUniformBlockIndex(program, "BoneTransforms");
        if (block != uint.MaxValue) gl.UniformBlockBinding(program, block, 0);
        uint target = gl.GenTexture(); gl.BindTexture(TextureTarget.Texture2D, target);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, 64,64,0,PixelFormat.Rgba,PixelType.Float,ReadOnlySpan<float>.Empty);
        uint framebuffer = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
        Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        gl.Viewport(0,0,64,64); gl.UseProgram(program);
        Matrix4x4 identity = Matrix4x4.Identity;
        foreach (string name in new[] { "uWorld", "uViewProjection", "uViewProj" })
            gl.UniformMatrix4(gl.GetUniformLocation(program,name),1,false,in identity.M11);
        gl.Uniform1(gl.GetUniformLocation(program,"uWireframePass"),1);
        gl.Uniform4(gl.GetUniformLocation(program,"uWireframeColor"),0f,1f,0f,.6f);
        gl.Disable(EnableCap.DepthTest); gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(BlendingFactor.SrcAlpha,BlendingFactor.OneMinusSrcAlpha,BlendingFactor.One,BlendingFactor.OneMinusSrcAlpha);
        using (var edges = new GlMeshEdges(gl,triangles,positions))
        {
            foreach (bool overlay in new[] { false,true })
            {
                GlMeshEdges.Frame(gl,program,identity,identity,overlay);
                gl.ClearColor(0,0,0,0); gl.Clear(ClearBufferMask.ColorBufferBit);
                edges.Draw(program,surface,0,6);
                Assert.Equal((int)surface,gl.GetInteger(GetPName.ElementArrayBufferBinding));
                float[] pixels = new float[16*16*4];
                gl.ReadPixels(24,24,16,16,PixelFormat.Rgba,PixelType.Float,pixels.AsSpan());
                float[] green = Enumerable.Range(0,256).Select(i => pixels[i*4+1]).Where(v => v > 0).ToArray();
                Assert.NotEmpty(green);
                Assert.All(green,v => Assert.InRange(v,.599f,.601f));
            }
        }
        Assert.True(gl.IsBuffer(vertices)); Assert.True(gl.IsBuffer(surface));
        Assert.Equal(GLEnum.NoError,gl.GetError());
        gl.DeleteFramebuffer(framebuffer); gl.DeleteTexture(target); gl.DeleteBuffer(bones);
        gl.DeleteBuffer(surface); gl.DeleteBuffer(vertices); gl.DeleteVertexArray(vao); gl.DeleteProgram(program);
    }
}
