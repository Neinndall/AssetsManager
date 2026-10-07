using System;
using System.IO;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxMeshVertexColorGpuTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void StaticMeshColorsReachPixelsWithParticleAndMaterialTint(bool fallback, bool custom, bool binary)
    {
        string install = InstalledSkins.FindInstall();
        if (!fallback) Assert.NotNull(install);
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl, fallback ? null : InstalledSkins.Settings(install));
        uint target = Texture(gl, 64, new byte[64 * 64 * 4]);
        uint white = Texture(gl, 1, new byte[] { 255, 255, 255, 255 });
        uint framebuffer = gl.GenFramebuffer();
        string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxMeshColor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            Vector4 particleTint = new(.5f, .75f, 1, .8f);
            Vector4 materialTint = new(.6f, 1, .5f, .5f);
            foreach (int alpha in new[] { -1, 0, 128, 255 })
            {
                // No colour block must behave as white. The other cases retain RGB and authored alpha.
                string name = $"colors-{alpha}.sco";
                File.WriteAllText(Path.Combine(root, name),
                    "[ObjectBegin]\nName= triangle\nCentralPoint= 0 0 0\n" +
                    $"VertexColors= {(alpha < 0 ? 0 : 1)}\nVerts= 3\n-1 -1 0\n1 -1 0\n0 1 0\n" +
                    (alpha < 0 ? "" : $"255 102 51 {alpha}\n255 102 51 {alpha}\n255 102 51 {alpha}\n") +
                    "Faces= 1\n3 0 1 2 material 0 0 1 0 .5 1\n");
                if (binary)
                {
                    name = Path.ChangeExtension(name, ".scb");
                    WriteBinaryFixture(Path.Combine(root, name), alpha);
                }
                var mesh = new VfxResourceResolver().ResolveMesh(name, root);
                Assert.True(mesh.HasValue);
                var definition = new VfxEmitterDefinition("vertex-color", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "white.tex", Vector2.One, 1, false, true, PrimitiveKind: VfxPrimitiveKind.Mesh,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true, AlphaReference = 0 },
                    CustomMaterial: custom ? ModelMaterialDefinition.Default with
                    {
                        Color = materialTint, AlphaCutoff = 0, BindingKind = ModelMaterialBindingKind.Authored,
                        RenderState = ModelMaterialRenderState.Default with { DoubleSided = true }
                    } : null);
                var instances = new float[VfxPlaybackRuntime.InstanceStride];
                instances[3] = instances[4] = instances[18] = 1;
                instances[5] = particleTint.X;
                instances[6] = particleTint.Y;
                instances[7] = particleTint.Z;
                instances[8] = particleTint.W;
                instances[21] = instances[22] = instances[31] = instances[32] = 1;
                instances[36] = instances[40] = instances[44] = 1;
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = white, TextureWidth = 1, TextureHeight = 1,
                    Instances = instances, InstanceCount = 1
                };
                renderer.UploadEmitterMesh(emitter, mesh.Value.Positions, mesh.Value.Normals,
                    mesh.Value.Uvs, mesh.Value.Colors, mesh.Value.Indices);
                gl.Viewport(0, 0, 64, 64);
                gl.ClearColor(0, 0, 0, 0);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
                if (!fallback) Assert.Null(renderer.GameParticleFallback(emitter, true));
                var pixel = new byte[4];
                gl.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
                Vector4 vertexTint = alpha < 0 ? Vector4.One : new(1, .4f, .2f, alpha / 255f);
                Vector4 expected = vertexTint * particleTint * (custom ? materialTint : Vector4.One);
                for (int channel = 0; channel < 4; channel++)
                    Assert.True(Math.Abs(pixel[channel] - expected[channel] * 255) <= 2,
                        $"fallback={fallback}, custom={custom}, binary={binary}, alpha={alpha}, channel={channel}: " +
                        $"expected {expected[channel] * 255}, got {pixel[channel]}.");
            }
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(target);
            gl.DeleteTexture(white);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteBinaryFixture(string path, int alpha)
    {
        // SCB 3.2 stores BGRA bytes. Use an independent fixture rather than a writer round trip.
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("r3d2Mesh"u8);
        writer.Write((ushort)3);
        writer.Write((ushort)2);
        writer.Write(new byte[128]);
        writer.Write(3);
        writer.Write(1);
        writer.Write(0u);
        foreach (float value in new float[] { -1, -1, 0, 1, 1, 0 }) writer.Write(value);
        writer.Write(alpha < 0 ? 0 : 1);
        foreach (float value in new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 }) writer.Write(value);
        if (alpha >= 0)
            for (int vertex = 0; vertex < 3; vertex++)
                writer.Write(new byte[] { 51, 102, 255, (byte)alpha });
        foreach (float value in new float[] { 0, 0, 0 }) writer.Write(value);
        writer.Write(0u);
        writer.Write(1u);
        writer.Write(2u);
        writer.Write(new byte[64]);
        foreach (float value in new float[] { 0, 1, .5f, 0, 0, 1 }) writer.Write(value);
    }

    private static uint Texture(GL gl, uint size, byte[] pixels)
    {
        uint texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, size, size, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        return texture;
    }
}
