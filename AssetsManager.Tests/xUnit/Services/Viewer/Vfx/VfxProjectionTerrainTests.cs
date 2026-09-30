using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

/// <summary>
/// A planar projection over a map lands on the surface the terrain depth holds and fades by that surface's height,
/// like UNLIT_DECAL on map triangles, instead of lying on the flat ground at height 0.
/// </summary>
public sealed class VfxProjectionTerrainTests
{
    private const int Size = 128;
    private const float TerrainHeight = 100f;

    [Fact]
    public void DecalLandsOnTheTerrainSurfaceInsideItsFootprint()
    {
        byte[] pixels = Draw(particleHeight: TerrainHeight, halfExtent: 20f, yRange: 50f, fading: 100f);

        Vector2 centre = Project(new Vector3(0f, TerrainHeight, 0f));
        Vector2 outside = Project(new Vector3(60f, TerrainHeight, 0f));
        Assert.Equal(255, Alpha(pixels, centre));
        Assert.Equal(0, Alpha(pixels, outside));
    }

    [Fact]
    public void DecalFadesByTheSurfaceHeightAboveTheParticle()
    {
        // The particle stands on y 0 and the terrain at y 100: the game fades by 1 - (100 - 5) / 200.
        byte[] pixels = Draw(particleHeight: 0f, halfExtent: 20f, yRange: 5f, fading: 200f);

        int alpha = Alpha(pixels, Project(new Vector3(0f, TerrainHeight, 0f)));
        Assert.InRange(alpha, 130, 138);
    }

    [Theory]
    [InlineData(20f, 20f, 0f)]
    [InlineData(-20f, 20f, 0f)]
    [InlineData(20f, -20f, 0f)]
    [InlineData(-20f, -20f, 0f)]
    [InlineData(20f, 20f, 1.5707963f)]
    [InlineData(-20f, 20f, 1.5707963f)]
    [InlineData(20f, -20f, 1.5707963f)]
    [InlineData(-20f, -20f, 1.5707963f)]
    public void SignedScalePreservesTheFootprintAndMirrorsItsTexture(float width, float height, float turn)
    {
        byte[] pixels = Draw(TerrainHeight, width, 50f, 100f, height, turn, textured: true);
        (float X, float Z, byte[] Color)[] samples =
        {
            (-0.5f, 0.5f, new byte[] { 255, 0, 0, 255 }),
            (0.5f, 0.5f, new byte[] { 0, 255, 0, 255 }),
            (-0.5f, -0.5f, new byte[] { 0, 0, 255, 255 }),
            (0.5f, -0.5f, new byte[] { 255, 255, 0, 255 })
        };
        foreach (var sample in samples)
        {
            float x = sample.X * width, z = sample.Z * height;
            Vector2 pixel = Project(new Vector3(x * MathF.Cos(turn) - z * MathF.Sin(turn), TerrainHeight,
                x * MathF.Sin(turn) + z * MathF.Cos(turn)));
            int at = ((int)pixel.Y * Size + (int)pixel.X) * 4;
            Assert.Equal(sample.Color, pixels[at..(at + 4)]);
        }
        Assert.Equal(0, Alpha(pixels, Project(new Vector3(60f, TerrainHeight, 0f))));
    }

    private static Matrix4x4 ViewProjection() =>
        Matrix4x4.CreateLookAt(new Vector3(0f, 600f, 400f), new Vector3(0f, TerrainHeight, 0f), Vector3.UnitY) *
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 1f, 10f, 5000f);

    private static Vector2 Project(Vector3 world)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), ViewProjection());
        return new Vector2((clip.X / clip.W * 0.5f + 0.5f) * Size, (clip.Y / clip.W * 0.5f + 0.5f) * Size);
    }

    private static int Alpha(byte[] pixels, Vector2 at) =>
        pixels[((int)at.Y * Size + (int)at.X) * 4 + 3];

    /// <returns>RGBA pixels of one decal drawn in terrain mode over a flat terrain at height 100.</returns>
    private static byte[] Draw(float particleHeight, float halfExtent, float yRange, float fading,
        float? halfHeight = null, float turn = 0f, bool textured = false)
    {
        using var context = new HiddenWglContext();
        using var gl = GL.GetApi(context.GetProcAddress);
        Matrix4x4 viewProj = ViewProjection();

        // The terrain's depth alone, as the viewport captures it before structures and characters draw.
        uint depth = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, depth);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, Size, Size, 0, PixelFormat.DepthComponent,
            PixelType.Float, ReadOnlySpan<byte>.Empty);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        uint terrainTarget = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, terrainTarget);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depth, 0);
        gl.DrawBuffer(DrawBufferMode.None);
        gl.Viewport(0, 0, Size, Size);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.Clear(ClearBufferMask.DepthBufferBit);
        uint terrain = GlShaderCompiler.CreateProgram(gl, false,
            "layout(location=0) in vec3 aPos; uniform mat4 uViewProj; void main(){ gl_Position = uViewProj * vec4(aPos, 1.0); }",
            "out vec4 fragColor; void main(){ fragColor = vec4(1.0); }");
        gl.UseProgram(terrain);
        gl.UniformMatrix4(gl.GetUniformLocation(terrain, "uViewProj"), 1, false, in viewProj.M11);
        float[] plane = { -2000f, TerrainHeight, -2000f, 2000f, TerrainHeight, -2000f, -2000f, TerrainHeight, 2000f, 2000f, TerrainHeight, 2000f };
        uint planeVao = gl.GenVertexArray();
        gl.BindVertexArray(planeVao);
        uint planeVbo = Buffer(gl, plane);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
        gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        uint colour = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, colour);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, Size, Size, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
            ReadOnlySpan<byte>.Empty);
        uint decalTarget = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, decalTarget);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colour, 0);
        gl.ClearColor(0f, 0f, 0f, 0f);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);

        uint program = GlShaderCompiler.CreateProgram(gl, false, VfxProjectionShaderSource.Vertex, VfxProjectionShaderSource.TerrainFragment);
        gl.UseProgram(program);
        Matrix4x4.Invert(viewProj, out Matrix4x4 inverse);
        gl.UniformMatrix4(gl.GetUniformLocation(program, "uViewProj"), 1, false, in viewProj.M11);
        gl.UniformMatrix4(gl.GetUniformLocation(program, "uInverseViewProj"), 1, false, in inverse.M11);
        gl.Uniform1(gl.GetUniformLocation(program, "uTerrainMode"), 1);
        gl.Uniform2(gl.GetUniformLocation(program, "uProjectionBand"), yRange, fading);
        gl.Uniform2(gl.GetUniformLocation(program, "uViewportSize"), (float)Size, (float)Size);
        gl.Uniform1(gl.GetUniformLocation(program, "uTerrainDepth"), 5);
        gl.ActiveTexture(TextureUnit.Texture5);
        gl.BindTexture(TextureTarget.Texture2D, depth);
        gl.ActiveTexture(TextureUnit.Texture0);

        uint sprite = 0;
        if (textured)
        {
            sprite = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, sprite);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 2, 2, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, new ReadOnlySpan<byte>(new byte[]
                {
                    255, 0, 0, 255, 0, 255, 0, 255,
                    0, 0, 255, 255, 255, 255, 0, 255
                }));
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.Uniform1(gl.GetUniformLocation(program, "uHasTex"), 1);
            gl.Uniform1(gl.GetUniformLocation(program, "uTex"), 0);
        }

        float[] instance = new float[VfxPlaybackRuntime.InstanceStride];
        instance[1] = particleHeight;
        instance[3] = halfExtent;
        instance[4] = halfHeight ?? halfExtent;
        instance[9] = turn;
        instance[5] = instance[6] = instance[7] = instance[8] = 1f;
        instance[31] = instance[32] = 1f;
        uint decalVao = gl.GenVertexArray();
        gl.BindVertexArray(decalVao);
        uint cornerVbo = Buffer(gl, new[] { -0.5f, -0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f });
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
        uint instanceVbo = Buffer(gl, instance);
        (uint Location, int Components, int Offset)[] lanes =
        {
            (1, 3, 0), (2, 2, 3), (3, 4, 5), (4, 2, 9), (7, 4, 19), (8, 4, 23), (9, 2, 27), (10, 4, 29), (11, 3, 33)
        };
        foreach ((uint location, int components, int offset) in lanes)
        {
            gl.EnableVertexAttribArray(location);
            gl.VertexAttribPointer(location, components, VertexAttribPointerType.Float, false,
                VfxPlaybackRuntime.InstanceStride * sizeof(float), new IntPtr(offset * sizeof(float)));
            gl.VertexAttribDivisor(location, 1);
        }
        gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, 1);

        byte[] pixels = new byte[Size * Size * 4];
        gl.ReadPixels(0, 0, Size, Size, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        Assert.Equal(GLEnum.NoError, gl.GetError());

        gl.DeleteBuffer(planeVbo);
        gl.DeleteBuffer(cornerVbo);
        gl.DeleteBuffer(instanceVbo);
        gl.DeleteVertexArray(planeVao);
        gl.DeleteVertexArray(decalVao);
        gl.DeleteFramebuffer(terrainTarget);
        gl.DeleteFramebuffer(decalTarget);
        gl.DeleteTexture(depth);
        gl.DeleteTexture(colour);
        if (sprite != 0) gl.DeleteTexture(sprite);
        gl.DeleteProgram(terrain);
        gl.DeleteProgram(program);
        return pixels;
    }

    private static uint Buffer(GL gl, float[] values)
    {
        uint buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
        gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(values), BufferUsageARB.StaticDraw);
        return buffer;
    }
}
