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
public sealed class VfxAttachedPlacementGpuTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttachedSkinAppliesOwnerPlacementOnceAfterPoseAndParticleScale(bool fallback)
    {
        string install = InstalledSkins.FindInstall();
        if (!fallback && install is null) return;
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl, fallback ? null : InstalledSkins.Settings(install));
        uint target = Texture(gl, 128, new byte[128 * 128 * 4]);
        uint white = Texture(gl, 1, new byte[] { 255,255,255,255 });
        uint framebuffer = gl.GenFramebuffer();
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var definition = new VfxEmitterDefinition("attached", VfxCurveF.Const(1), VfxCurveF.Const(1),
                null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                "white.tex", Vector2.One, 1, false, true, PrimitiveKind: VfxPrimitiveKind.AttachedMesh,
                RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true });
            var instance = new float[VfxPlaybackRuntime.InstanceStride];
            instance[0] = 100; // An attached skin ignores the particle's own translation.
            instance[3] = 2;
            instance[4] = 3;
            instance[18] = 1;
            instance[5] = instance[6] = instance[7] = instance[8] = 1;
            instance[21] = instance[22] = instance[31] = instance[32] = 1;
            instance[36] = instance[40] = instance[44] = 1;
            var emitter = new VfxPlaybackRuntime.EmitterState
            {
                Def = definition, Texture = white, TextureWidth = 1, TextureHeight = 1,
                Instances = instance, InstanceCount = 1
            };
            var vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(0,1,0) };
            renderer.UploadEmitterMesh(emitter, new float[] { -1,-1,0, 1,-1,0, 0,1,0 },
                new float[] { 0,0,1, 0,0,1, 0,0,1 }, new float[] { 0,0, 1,0, .5f,1 }, null,
                boneIndices: new float[12], boneWeights: new float[] { 1,0,0,0, 1,0,0,0, 1,0,0,0 });
            Matrix4x4 pose = Matrix4x4.CreateRotationZ(.2f) * Matrix4x4.CreateTranslation(.2f,-.3f,0);
            Matrix4x4 owner = Matrix4x4.CreateScale(.7f,1.2f,1) * Matrix4x4.CreateRotationZ(.5f)
                * Matrix4x4.CreateTranslation(3,1,0);
            renderer.SetOwnerSkinningMatrices(new[] { pose });
            renderer.SetOwnerWorldTransform(owner);
            Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0,0,10), Vector3.Zero, Vector3.UnitY);
            Matrix4x4 viewProjection = view * Matrix4x4.CreateOrthographic(20,20,.1f,100);
            gl.Viewport(0,0,128,128);
            gl.ClearColor(0,0,0,0);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Render(new[] { new VfxRenderQueueEntry(emitter,0,0) }, viewProjection, view);
            if (!fallback) Assert.Null(renderer.GameParticleFallback(emitter, true));
            var pixels = new byte[128 * 128 * 4];
            gl.ReadPixels(0,0,128,128,PixelFormat.Rgba,PixelType.UnsignedByte,pixels.AsSpan());
            for (int index = 0; index < vertices.Length; index++)
                vertices[index] = Vector3.Transform(Vector3.Transform(vertices[index], pose) * new Vector3(2,3,1), owner);
            float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
            Vector2 a = new(vertices[0].X, vertices[0].Y);
            Vector2 b = new(vertices[1].X, vertices[1].Y);
            Vector2 c = new(vertices[2].X, vertices[2].Y);
            float area = Cross(b-a,c-a);
            int checkedInside = 0;
            for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                Vector2 point = new(((x+.5f)/128*2-1)*10, ((y+.5f)/128*2-1)*10);
                float w0 = Cross(b-point,c-point)/area;
                float w1 = Cross(c-point,a-point)/area;
                float w2 = 1-w0-w1;
                if (MathF.Min(w0, MathF.Min(w1,w2)) > .05f)
                {
                    Assert.True(pixels[(y*128+x)*4+3] > 200, $"Missing attached skin at ({x},{y}).");
                    checkedInside++;
                }
                else if (MathF.Min(w0, MathF.Min(w1,w2)) < -.05f)
                    Assert.Equal(0, pixels[(y*128+x)*4+3]);
            }
            Assert.True(checkedInside > 30);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer,0);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(target);
            gl.DeleteTexture(white);
        }
    }

    private static uint Texture(GL gl, uint size, byte[] pixels)
    {
        uint texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D,texture);
        gl.TexImage2D(TextureTarget.Texture2D,0,InternalFormat.Rgba,(uint)size,(uint)size,0,
            PixelFormat.Rgba,PixelType.UnsignedByte,pixels.AsSpan());
        gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
        return texture;
    }
}
