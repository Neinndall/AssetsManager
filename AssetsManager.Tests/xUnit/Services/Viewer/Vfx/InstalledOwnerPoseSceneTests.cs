using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using Silk.NET.OpenGL;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class InstalledOwnerPoseSceneTests(ITestOutputHelper output, InstalledChampionProjects projects)
    : IClassFixture<InstalledChampionProjects>
{
    [Theory]
    [InlineData("Aatrox", 40, "Aatrox_Skin40_E_Active_buff")]
    [InlineData("Ezreal", 5, "Ezreal_Skin05_E_casInAnim")]
    public void InstalledSkinRendersAttachedVfxWithDeferredBindPoseAndHiddenSubmeshes(
        string champion, int skinId, string attachedSystemName)
    {
        string install = InstalledSkins.FindInstall();
        if (install == null)
            return;

        string root = projects.GetRoot(champion);
        string skinBin = Path.Combine(root, "data", "characters", champion.ToLowerInvariant(), "skins", $"skin{skinId}.bin");
        using var loader = new VfxLoadingService();
        VfxLoadingService.Bundle bundle = loader.Load(skinBin, null);
        Assert.NotNull(bundle.OwnerSceneContext);
        Assert.NotEmpty(bundle.OwnerSceneContext.InitialHiddenSubmeshHashes);

        string sklPath = Path.Combine(root, bundle.OwnerSceneContext.SkeletonPath);
        string sknPath = Path.Combine(root, bundle.OwnerSceneContext.MeshPath);
        Assert.True(File.Exists(sklPath), $"Missing skeleton {sklPath}");
        Assert.True(File.Exists(sknPath), $"Missing simple skin {sknPath}");

        using var sklStream = File.OpenRead(sklPath);
        var skeleton = new RigResource(sklStream);
        Matrix4x4[] bindMatrices = AnimationService.CreateBindSkinningMatrices(skeleton);
        Assert.Equal(skeleton.Joints.Count, bindMatrices.Length);

        var (minBounds, maxBounds) = ReadBounds(sknPath);
        Vector3 center = (minBounds + maxBounds) * 0.5f;
        float radius = MathF.Max(100f, (maxBounds - minBounds).Length() * 0.6f);

        VfxSystemDefinition attachedSystem = bundle.Systems.Values.First(system =>
            string.Equals(system.Name, attachedSystemName, StringComparison.OrdinalIgnoreCase) &&
            system.Emitters.Any(emitter => emitter.PrimitiveKind == VfxPrimitiveKind.AttachedMesh));

        Assert.True(CharacterViewportSemantics.ShouldHoldOwnerBindPose(
            isBindPoseClip: false,
            hasSelectedSystem: true,
            hasSelectedSpell: false,
            hasCurrentAnimation: false));
        Assert.True(CharacterViewportSemantics.ShouldHoldOwnerBindPose(
            isBindPoseClip: false,
            hasSelectedSystem: false,
            hasSelectedSpell: true,
            hasCurrentAnimation: false));

        foreach (VfxIdleEffectDefinition idle in bundle.IdleEffects)
        {
            if (!string.IsNullOrWhiteSpace(idle.BoneName))
            {
                Assert.True(
                    AnimationService.TryGetBindBoneTransform(skeleton, idle.BoneName, out Matrix4x4 boneTransform),
                    $"Missing bind transform for idle bone '{idle.BoneName}' in {champion} Skin{skinId}.");
                Assert.True(float.IsFinite(boneTransform.Translation.Length()));
            }
        }

        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var session = new VfxRenderSession();

        // Populate system, bind-pose owner skinning, and authored hidden submeshes BEFORE Initialize(gl).
        session.SetSystem(new VfxSystemModel
        {
            Name = attachedSystem.Name,
            ParticlePath = attachedSystem.ParticlePath,
            Definition = attachedSystem,
            SystemCatalog = bundle.Systems,
            ResourceMap = bundle.ResourceMap,
            SearchDirectory = root,
            OwnerSceneContext = bundle.OwnerSceneContext,
            PlaybackSeed = 1
        });
        session.SetBoneTransformSampler((_, name, _) =>
            AnimationService.TryGetBindBoneTransform(skeleton, name, out Matrix4x4 bone) ? bone : null);
        session.SetOwnerSkinningMatrices(bindMatrices);
        session.SetOwnerHiddenSubmeshes(bundle.OwnerSceneContext.InitialHiddenSubmeshHashes);

        session.Initialize(gl, InstalledSkins.Settings(install));
        session.SetViewportSize(256, 256);
        session.Play();

        uint target = CreateColorTexture(gl, 256);
        uint framebuffer = gl.GenFramebuffer();
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            Matrix4x4 view = Matrix4x4.CreateLookAt(
                center + new Vector3(0f, radius * 0.25f, radius * 2.2f),
                center,
                Vector3.UnitY);
            Matrix4x4 viewProjection = view * Matrix4x4.CreateOrthographic(radius * 2.5f, radius * 2.5f, 1f, radius * 6f);

            byte[] pixels = new byte[256 * 256 * 4];
            int covered = 0;
            for (int frame = 0; frame < 8; frame++)
            {
                session.Update(0.04f);
                gl.Viewport(0, 0, 256, 256);
                gl.ClearColor(0f, 0f, 0f, 0f);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                session.Render(viewProjection, view);

                gl.ReadPixels(0, 0, 256, 256, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                covered = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if ((pixels[i] | pixels[i + 1] | pixels[i + 2] | pixels[i + 3]) > 0)
                        covered++;
                }

                if (covered > 0 && session.Graphs.Any(g => g.Runtimes.SelectMany(r => r.Emitters).Any(e => e.Def.PrimitiveKind == VfxPrimitiveKind.AttachedMesh && e.InstanceCount > 0)))
                    break;
            }

            VfxPlaybackGraphRuntime graph = Assert.Single(session.Graphs);
            var allAttached = graph.Runtimes
                .SelectMany(r => r.Emitters)
                .Where(e => e.Def.PrimitiveKind == VfxPrimitiveKind.AttachedMesh)
                .ToArray();
            var attachedEmitters = allAttached
                .Where(e => e.InstanceCount > 0)
                .ToArray();
            Assert.True(attachedEmitters.Length > 0,
                $"allAttached={allAttached.Length}: [{string.Join(", ", allAttached.Select(e => $"{e.Def.Name}(inst={e.InstanceCount},vao={e.MeshVao},ranges={e.MeshRanges.Length},delay={e.Def.TimeBeforeFirstEmission},disabled={e.Def.Disabled},t={session.CurrentTime:F3})"))}] live={session.LiveParticleCount}");
            Assert.All(attachedEmitters, emitter =>
            {
                Assert.True(emitter.MeshVao != 0, $"{emitter.Def.Name}: AttachedMesh VAO was not uploaded.");
                Assert.NotEmpty(emitter.MeshRanges);
                Assert.Contains(emitter.MeshRanges, range => !IsHiddenByDefault(range.Hash, emitter.Def, bundle.OwnerSceneContext.InitialHiddenSubmeshHashes));
            });

            Assert.True(covered > 0, $"{champion} Skin{skinId} ({attachedSystemName}) rendered 0 covered pixels.");
            Assert.Equal(GLEnum.NoError, gl.GetError());
            output.WriteLine($"{champion} Skin{skinId} ({attachedSystemName}): joints={skeleton.Joints.Count} attachedEmitters={attachedEmitters.Length} coveredPixels={covered}");
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(target);
        }
    }

    [Fact]
    public void InstalledEzrealSkin5RunsBindPoseSpellWithPersistentIdleAndHiddenSubmeshes()
    {
        string install = InstalledSkins.FindInstall();
        if (install == null)
            return;

        string root = projects.GetRoot("Ezreal");
        string skinBin = Path.Combine(root, "data", "characters", "ezreal", "skins", "skin5.bin");
        using var loader = new VfxLoadingService();
        VfxLoadingService.Bundle bundle = loader.Load(skinBin, null);
        Assert.NotEmpty(bundle.IdleEffects);
        Assert.NotEmpty(bundle.MissileSpells);

        using var sklStream = File.OpenRead(Path.Combine(root, bundle.OwnerSceneContext.SkeletonPath));
        var skeleton = new RigResource(sklStream);
        Matrix4x4[] bindMatrices = AnimationService.CreateBindSkinningMatrices(skeleton);

        uint firstSystemHash = bundle.MissileSpells.Keys.First();
        var spellStep = new VfxSpellPlaybackStep(
            Id: "spell-step-0",
            System: bundle.Systems[firstSystemHash],
            EffectKey: 0u,
            StartTime: 0d,
            StopTime: 1d,
            From: Vector3.Zero,
            To: new Vector3(0f, 0f, 300f),
            Motion: VfxSpellPlaybackMotion.Static,
            Seed: 1);

        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var session = new VfxRenderSession();

        Assert.True(session.SetSpellSession(
            new[] { spellStep },
            bundle.Systems,
            bundle.ResourceMap,
            root,
            0d,
            bundle.OwnerSceneContext,
            castAnimation: null,
            idleEffects: bundle.IdleEffects));
        session.SetBoneTransformSampler((_, name, _) =>
            AnimationService.TryGetBindBoneTransform(skeleton, name, out Matrix4x4 bone) ? bone : null);
        session.SetOwnerSkinningMatrices(bindMatrices);
        session.SetOwnerHiddenSubmeshes(bundle.OwnerSceneContext.InitialHiddenSubmeshHashes);

        session.Initialize(gl, InstalledSkins.Settings(install));
        session.SetViewportSize(256, 256);
        session.Play();
        session.Update(0.15f);

        Assert.True(session.Graphs.Count >= 2, "Expected both spell step and idle effect graphs on Ezreal Skin5.");
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, 100f, 350f), new Vector3(0f, 80f, 0f), Vector3.UnitY);
        Matrix4x4 viewProjection = view * Matrix4x4.CreateOrthographic(300f, 300f, 1f, 1000f);

        uint target = CreateColorTexture(gl, 256);
        uint framebuffer = gl.GenFramebuffer();
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            gl.Viewport(0, 0, 256, 256);
            gl.ClearColor(0f, 0f, 0f, 0f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            session.Render(viewProjection, view);

            byte[] pixels = new byte[256 * 256 * 4];
            gl.ReadPixels(0, 0, 256, 256, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            int covered = 0;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 0)
                    covered++;
            }

            Assert.True(covered > 0, "Ezreal Skin5 bind-pose spell + idle session rendered 0 covered pixels.");
            Assert.Equal(GLEnum.NoError, gl.GetError());
            output.WriteLine($"Ezreal Skin5 spell+idle: graphs={session.Graphs.Count} liveParticles={session.LiveParticleCount} coveredPixels={covered}");
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(target);
        }
    }

    private static bool IsHiddenByDefault(
        uint materialHash,
        VfxEmitterDefinition emitter,
        IReadOnlyList<uint> hiddenSubmeshes)
    {
        if (hiddenSubmeshes is null || hiddenSubmeshes.Count == 0)
            return false;
        if (emitter.SubmeshesToDrawAlways?.Contains(materialHash) == true)
            return false;
        return hiddenSubmeshes.Contains(materialHash);
    }

    private static (Vector3 Min, Vector3 Max) ReadBounds(string sknPath)
    {
        using var mesh = SkinnedMesh.ReadFromSimpleSkin(sknPath);
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        var accessor = mesh.VerticesView.GetAccessor(ElementName.Position);
        var positions = accessor.AsVector3Array();
        for (int i = 0; i < positions.Count; i++)
        {
            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }
        return (min, max);
    }

    private static uint CreateColorTexture(GL gl, int size)
    {
        uint texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)size, (uint)size, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }
}
