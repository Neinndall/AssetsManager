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
public sealed class VfxTrailInterpolationGpuTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var primitive in new[] { VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail })
        foreach (bool game in new[] { false, true })
        foreach (bool perspective in new[] { false, true })
        foreach (bool mult in new[] { false, true })
            yield return new object[] { primitive, game, perspective, mult };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PerPointUvTransformsReachPerspectiveCorrectRasterInterpolation(
        VfxPrimitiveKind primitive, bool game, bool perspective, bool mult)
    {
        string install = InstalledSkins.FindInstall();
        Assert.NotNull(install);
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl, game ? InstalledSkins.Settings(install) : null);
        uint target = Texture(gl, 64, 64, new byte[64 * 64 * 4]);
        uint framebuffer = gl.GenFramebuffer();
        uint white = Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
        var texels = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            int at = (y * 64 + x) * 4;
            texels[at] = (byte)(4 * x + 2);
            texels[at + 1] = (byte)(4 * y + 2);
            texels[at + 2] = 128;
            texels[at + 3] = 255;
        }
        uint gradient = Texture(gl, 64, 64, texels);
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var definition = new VfxEmitterDefinition("interpolation", VfxCurveF.Const(1), VfxCurveF.Const(1),
                null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                "base.tex", Vector2.One, 1, false, false, PrimitiveKind: primitive,
                RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true, TextureAddressMode = 2 },
                UvTransformCenter: new Vector2(0.4f), TextureMultPath: mult ? "mult.tex" : null,
                TextureMultAddressMode: 2, TextureMultTransformCenter: new Vector2(0.4f),
                Trail: new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0));
            var emitter = new VfxPlaybackRuntime.EmitterState
            {
                Def = definition, Texture = mult ? white : gradient, TextureMult = mult ? gradient : 0,
                SystemOrientation = Matrix4x4.Identity, InstanceCount = 2,
                Instances = new float[2 * VfxPlaybackRuntime.InstanceStride]
            };
            for (int index = 0; index < 2; index++)
            {
                int at = index * VfxPlaybackRuntime.InstanceStride;
                var data = emitter.Instances;
                data[at] = index == 0 ? -0.9f : 0.9f;
                data[at + 2] = index == 0 ? -0.5f : 0.8f;
                data[at + 3] = data[at + 4] = data[at + 18] = 0.7f;
                for (int channel = 5; channel <= 8; channel++) data[at + channel] = 1;
                foreach (int start in new[] { 19, 29 })
                {
                    data[at + start] = (index == 0 ? 0.1f : 0.2f) + (start == 29 ? 0.09f : 0f);
                    data[at + start + 1] = index == 0 ? 0.15f : 0.1f;
                    data[at + start + 2] = index == 0 ? 0.55f : 0.8f;
                    data[at + start + 3] = index == 0 ? 0.7f : 0.55f;
                    data[at + start + 4] = index == 0 ? -0.2f : 0.35f;
                }
                data[at + 36] = data[at + 40] = data[at + 44] = 1;
                emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                {
                    Serial = (uint)index, Life = 1, BirthFrame = Matrix4x4.Identity,
                    TrailTiling = new Vector3(3, 1, 0),
                    BirthRotation = primitive == VfxPrimitiveKind.ArbitraryTrail
                        ? new Vector3(0, 0, MathF.PI / 2) : Vector3.Zero
                });
            }
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY);
            var projection = perspective
                ? Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 0.1f, 10)
                : Matrix4x4.CreateOrthographic(4, 4, 0.1f, 10);
            var geometry = new VfxTrailGeometry();
            Assert.Equal(6, geometry.Build(emitter, -Vector3.UnitZ));
            // The oracle starts at the CPU/GPU boundary. It does not certify the UV author's formula.
            var clip = new Vector4[6];
            var uv = new Vector2[6];
            for (int vertex = 0; vertex < 6; vertex++)
            {
                int at = vertex * VfxTrailGeometry.VertexStride;
                clip[vertex] = Vector4.Transform(new Vector4(geometry.Vertices[at + 2],
                    geometry.Vertices[at + 3], geometry.Vertices[at + 4], 1), view * projection);
                int layer = at + (mult ? 17 : 0);
                uv[vertex] = new Vector2(geometry.Vertices[layer], geometry.Vertices[layer + 1]);
                if (mult)
                    Assert.InRange(uv[vertex].X - geometry.Vertices[at], 0.089f, 0.091f);
            }
            Assert.True(Vector2.Distance(uv[0], uv[1]) > 0.1f);
            gl.Viewport(0, 0, 64, 64);
            gl.ClearColor(0, 0, 0, 0);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, view * projection, view);
            if (game) Assert.Null(renderer.GameParticleFallback(emitter, false));
            var pixels = new byte[64 * 64 * 4];
            gl.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            int checkedPixels = 0;
            float perspectiveDifference = 0;
            for (int y = 28; y <= 36; y += 4)
            for (int x = 28; x <= 36; x += 4)
            {
                var sample = new Vector2((x + 0.5f) / 32 - 1, (y + 0.5f) / 32 - 1);
                for (int triangle = 0; triangle < 6; triangle += 3)
                {
                    Vector2 Ndc(int v) => new(clip[v].X / clip[v].W, clip[v].Y / clip[v].W);
                    Vector3 weights = Barycentric(sample, Ndc(triangle), Ndc(triangle + 1), Ndc(triangle + 2));
                    if (weights.X < 0.01f || weights.Y < 0.01f || weights.Z < 0.01f) continue;
                    var reciprocal = weights / new Vector3(clip[triangle].W, clip[triangle + 1].W, clip[triangle + 2].W);
                    Vector2 expected = (uv[triangle] * reciprocal.X + uv[triangle + 1] * reciprocal.Y +
                        uv[triangle + 2] * reciprocal.Z) / (reciprocal.X + reciprocal.Y + reciprocal.Z);
                    Vector2 affine = uv[triangle] * weights.X + uv[triangle + 1] * weights.Y + uv[triangle + 2] * weights.Z;
                    perspectiveDifference = Math.Max(perspectiveDifference, Vector2.Distance(expected, affine));
                    expected = Vector2.Clamp(expected, new Vector2(0.5f / 64), new Vector2(63.5f / 64));
                    int pixel = (y * 64 + x) * 4;
                    Assert.InRange(Math.Abs(pixels[pixel] - expected.X * 256), 0, 2);
                    Assert.InRange(Math.Abs(pixels[pixel + 1] - expected.Y * 256), 0, 2);
                    Assert.Equal(255, pixels[pixel + 3]);
                    checkedPixels++;
                    break;
                }
            }
            Assert.True(checkedPixels >= 4);
            if (perspective) Assert.True(perspectiveDifference > 0.005f);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
            foreach (uint texture in new[] { target, white, gradient }) gl.DeleteTexture(texture);
        }
    }

    private static Vector3 Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;
        float area = Cross(b - a, c - a);
        float y = Cross(p - a, c - a) / area;
        float z = Cross(b - a, p - a) / area;
        return new Vector3(1 - y - z, y, z);
    }

    private static uint Texture(GL gl, int width, int height, byte[] data)
    {
        uint texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, data.AsSpan());
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return texture;
    }
}
