using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxAttachedPlacementGpuTests
{
    [Theory]
    [InlineData(false, 1f, false)]
    [InlineData(true, 1f, false)]
    [InlineData(false, 1f, true)]
    [InlineData(true, 1f, true)]
    [InlineData(false, 2.5f, false)]
    [InlineData(true, 2.5f, false)]
    [InlineData(false, 2.5f, true)]
    [InlineData(true, 2.5f, true)]
    public void AttachedSkinAppliesOwnerPlacementOnceAfterPoseAndParticleScale(bool fallback, float skinScale, bool mirrored)
    {
        string install = InstalledSkins.FindInstall();
        if (!fallback) Assert.NotNull(install);
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
                Instances = instance, InstanceCount = 1, MeshOwnerScale = skinScale
            };
            var vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(0,1,0) };
            renderer.UploadEmitterMesh(emitter, new float[] { -1,-1,0, 1,-1,0, 0,1,0 },
                new float[] { 0,0,1, 0,0,1, 0,0,1 }, new float[] { 0,0, 1,0, .5f,1 }, null,
                boneIndices: new float[12], boneWeights: new float[] { 1,0,0,0, 1,0,0,0, 1,0,0,0 });
            Matrix4x4 pose = Matrix4x4.CreateRotationZ(.2f) * Matrix4x4.CreateTranslation(.2f,-.3f,0);
            Matrix4x4 owner = Matrix4x4.CreateScale(mirrored ? -.7f : .7f,1.2f,1) * Matrix4x4.CreateRotationZ(.5f)
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
                vertices[index] = Vector3.Transform(Vector3.Transform(vertices[index], pose) * new Vector3(2,3,1) * skinScale, owner);
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

        [Fact]
        public void SessionRetainsOwnerSkinningAndHiddenSubmeshesSetBeforeInitialize()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerSessionDefer", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string sknPath = Path.Combine(root, "owner.skn");
                string sklPath = Path.Combine(root, "owner.skl");
                WriteTwoTriangleAttachedOwnerMesh(sknPath);
                WriteWhiteDds(Path.Combine(root, "white.dds"));
                var rigBuilder = new RigResourceBuilder();
                rigBuilder.CreateJoint("Root").WithInfluence(true).WithLocalTransform(Matrix4x4.Identity)
                    .WithInverseBindTransform(Matrix4x4.Identity);
                using (var stream = File.Create(sklPath)) rigBuilder.Build().Write(stream);

                var attached = new VfxEmitterDefinition("attached", VfxCurveF.Const(1f), VfxCurveF.Const(2f),
                    null, 0f, 0f, true, false, 3, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "white.dds", Vector2.One, 1, false, true, PrimitiveKind: VfxPrimitiveKind.AttachedMesh,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true });
                var idleSystem = new VfxSystemDefinition(10u, "idle_attached", "Effects/IdleAttached", new[] { attached });
                var systems = new Dictionary<uint, VfxSystemDefinition> { [10u] = idleSystem };
                var resourceMap = new Dictionary<uint, uint> { [20u] = 10u };
                var ownerContext = new VfxOwnerSceneContext("owner.skn", "owner.skl", 1f);
                var idleEffect = new VfxIdleEffectDefinition(20u, "idle_attached", null, 0u, null, 0u, Vector3.Zero);

                using var context = new HiddenWglContext();
                using GL gl = GL.GetApi(context.GetProcAddress);
                using var session = new VfxRenderSession();
                Assert.True(session.SetSpellSession(
                    Array.Empty<VfxSpellPlaybackStep>(),
                    systems,
                    resourceMap,
                    root,
                    0d,
                    ownerContext,
                    castAnimation: null,
                    idleEffects: new[] { idleEffect }));
                session.SetOwnerSkinningMatrices(new[] { Matrix4x4.CreateTranslation(5f, 0f, 0f) });
                session.SetOwnerHiddenSubmeshes(new[] { Fnv1a.HashLower("drop") });

                session.Initialize(gl);
                session.SetViewportSize(128, 128);
                session.Play();
                session.Update(0.05f);

                uint target = Texture(gl, 128, new byte[128 * 128 * 4]);
                uint framebuffer = gl.GenFramebuffer();
                try
                {
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                    gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                        TextureTarget.Texture2D, target, 0);
                    Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

                    Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 10f), Vector3.Zero, Vector3.UnitY);
                    Matrix4x4 viewProjection = view * Matrix4x4.CreateOrthographic(20f, 20f, .1f, 100f);
                    gl.Viewport(0, 0, 128, 128);
                    gl.ClearColor(0f, 0f, 0f, 0f);
                    gl.Clear(ClearBufferMask.ColorBufferBit);
                    session.Render(viewProjection, view);

                    var pixels = new byte[128 * 128 * 4];
                    gl.ReadPixels(0, 0, 128, 128, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());

                    int PixelAlpha(float worldX, float worldY)
                    {
                        int px = Math.Clamp((int)(((worldX / 10f) + 1f) * 0.5f * 128f), 0, 127);
                        int py = Math.Clamp((int)(((worldY / 10f) + 1f) * 0.5f * 128f), 0, 127);
                        return pixels[(py * 128 + px) * 4 + 3];
                    }

                    Assert.True(PixelAlpha(5f, 0f) > 200,
                        $"Visible attached range should be skinned to X=+5 after deferred Initialize (alpha={PixelAlpha(5f, 0f)}).");
                    Assert.Equal(0, PixelAlpha(0f, 0f));
                    Assert.Equal(0, PixelAlpha(-2f, 0f));
                    Assert.Equal(0, PixelAlpha(-7f, 0f));
                    Assert.Equal(GLEnum.NoError, gl.GetError());
                }
                finally
                {
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                    gl.DeleteFramebuffer(framebuffer);
                    gl.DeleteTexture(target);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void WriteTwoTriangleAttachedOwnerMesh(string path)
        {
            VertexBufferDescription description = SkinnedMeshVertex.BASIC;
            var vertexOwner = VertexBuffer.AllocateForElements(description.Elements, 6);
            Span<byte> vertices = vertexOwner.Span;
            Vector3[] positions =
            {
                new(-1f, -1f, 0f), new(1f, -1f, 0f), new(0f, 1f, 0f),
                new(-8f, -1f, 0f), new(-6f, -1f, 0f), new(-7f, 1f, 0f)
            };
            const int stride = 52;
            for (int vertex = 0; vertex < 6; vertex++)
            {
                int at = vertex * stride;
                BitConverter.TryWriteBytes(vertices.Slice(at, 4), positions[vertex].X);
                BitConverter.TryWriteBytes(vertices.Slice(at + 4, 4), positions[vertex].Y);
                BitConverter.TryWriteBytes(vertices.Slice(at + 8, 4), positions[vertex].Z);
                vertices[at + 12] = 0;
                BitConverter.TryWriteBytes(vertices.Slice(at + 16, 4), 1f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 36, 4), 1f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 44, 4), 0.5f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 48, 4), 0.5f);
            }

            VertexBuffer vertexBuffer = VertexBuffer.Create(description.Usage, description.Elements, vertexOwner);
            MemoryOwner<byte> indexOwner = MemoryOwner<byte>.Allocate(6 * sizeof(ushort));
            Span<byte> indices = indexOwner.Span;
            for (ushort i = 0; i < 6; i++)
                BitConverter.TryWriteBytes(indices.Slice(i * sizeof(ushort), sizeof(ushort)), i);
            IndexBuffer indexBuffer = IndexBuffer.Create(IndexFormat.U16, indexOwner);

            using var mesh = new SkinnedMesh(
                new[]
                {
                    new SkinnedMeshRange("keep", 0, 3, 0, 3),
                    new SkinnedMeshRange("drop", 3, 3, 3, 3)
                },
                vertexBuffer,
                indexBuffer);
            mesh.WriteSimpleSkin(path);
        }

        private static void WriteWhiteDds(string path)
        {
            uint[] header = { 0x20534444, 124, 0x100F, 1, 1, 4, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 32, 0x41, 0, 32,
                0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000, 0x1000, 0, 0, 0, 0 };
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            foreach (uint value in header) writer.Write(value);
            writer.Write(0xFFFFFFFFu);
        }
    }
