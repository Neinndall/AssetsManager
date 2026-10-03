using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class GlDrawBindingsTests
{
    [Fact]
    public void RepeatedProgramBindingsPreserveTheActiveProgramAndRebindAfterExternalDrawing()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        var bindings = new GlDrawBindings(gl);
        uint first = Program(gl), second = Program(gl);
        try
        {
            bindings.Begin();
            bindings.UseProgram(first);
            bindings.UseProgram(first);
            Assert.Equal(1, bindings.ProgramBindCount);
            AssertProgram(gl, first);
            bindings.UseProgram(second);
            bindings.UseProgram(first);
            AssertProgram(gl, first);
            Assert.Equal(3, bindings.ProgramBindCount);
            bindings.End();

            gl.UseProgram(second);
            bindings.Begin();
            bindings.UseProgram(first);
            AssertProgram(gl, first);
            Assert.Equal(1, bindings.ProgramBindCount);
            gl.UseProgram(second);
            bindings.Invalidate();
            bindings.UseProgram(first);
            AssertProgram(gl, first);
            bindings.End();
            gl.UseProgram(second);
            bindings.UseProgram(first);
            AssertProgram(gl, first);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally { gl.UseProgram(0); gl.DeleteProgram(first); gl.DeleteProgram(second); }
    }

    [Fact]
    public void UniformBufferBindingsTrackEachSlotAndInvalidateBetweenDrawOwners()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        var bindings = new GlDrawBindings(gl);
        uint first = gl.GenBuffer(), second = gl.GenBuffer();
        try
        {
            gl.BindBuffer(BufferTargetARB.UniformBuffer, first);
            gl.BufferData(BufferTargetARB.UniformBuffer, new System.ReadOnlySpan<float>(new float[4]), BufferUsageARB.DynamicDraw);
            gl.BindBuffer(BufferTargetARB.UniformBuffer, second);
            gl.BufferData(BufferTargetARB.UniformBuffer, new System.ReadOnlySpan<float>(new float[4]), BufferUsageARB.DynamicDraw);
            bindings.Begin();
            bindings.BindUniformBuffer(0, first);
            bindings.BindUniformBuffer(1, second);
            bindings.BindUniformBuffer(0, first);
            bindings.BindUniformBuffer(1, second);
            Assert.Equal(2, bindings.UniformBufferBindCount);
            AssertBuffer(gl, 0, first);
            AssertBuffer(gl, 1, second);
            bindings.BindUniformBuffer(0, second);
            AssertBuffer(gl, 0, second);
            bindings.End();

            gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, first);
            bindings.Begin();
            bindings.BindUniformBuffer(0, second);
            AssertBuffer(gl, 0, second);
            Assert.Equal(1, bindings.UniformBufferBindCount);
            gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, first);
            bindings.Invalidate();
            bindings.BindUniformBuffer(0, second);
            AssertBuffer(gl, 0, second);
            bindings.End();
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally { gl.DeleteBuffer(first); gl.DeleteBuffer(second); }
    }

    private static uint Program(GL gl) => GlShaderCompiler.CreateProgram(gl, false,
        "void main() { gl_Position = vec4(0.0); }",
        "out vec4 color; void main() { color = vec4(1.0); }");

    private static void AssertProgram(GL gl, uint expected)
    {
        gl.GetInteger(GLEnum.CurrentProgram, out int program);
        Assert.Equal(expected, (uint)program);
    }

    private static void AssertBuffer(GL gl, uint binding, uint expected)
    {
        gl.GetInteger(GLEnum.UniformBufferBinding, binding, out int buffer);
        Assert.Equal(expected, (uint)buffer);
    }
}
