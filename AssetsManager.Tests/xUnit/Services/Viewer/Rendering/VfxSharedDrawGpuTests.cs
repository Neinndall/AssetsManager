using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Hashing;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxSharedDrawGpuTests
{
    [Fact]
    public void PersistentSwitchesRetainUploadedIdleTexturesAndReuseConditionResources()
    {
        using var scene = new Scene(false);
        using var session = new AssetsManager.Services.Viewer.Vfx.Session.VfxRenderSession();
        session.Initialize(scene.Gl);
        session.SetViewportSize(64, 64);
        var definition = Definition(VfxPrimitiveKind.CameraUnitQuad) with
        { Rate = VfxCurveF.Const(12), BirthScale = VfxCurve3.Const(new Vector3(.3f)), TexturePath = "" };
        var systems = new Dictionary<uint, VfxSystemDefinition> { [9] = new(9, "persistent", "persistent", new[] { definition }) };
        var effect = new VfxIdleEffectDefinition(1, "", "", 0, "", 0, Vector3.Zero);
        var condition = new VfxSubmeshCondition(new(GameMaterialBoolKind.Unsupported), Array.Empty<uint>(), Array.Empty<uint>())
        { Effects = new[] { effect } };
        session.SetAnimationSession(null, new[] { effect }, systems, new Dictionary<uint, uint> { [1] = 9 },
            System.IO.Path.GetTempPath(), 7, 3, new VfxOwnerSceneContext("", "", 1) { SubmeshConditions = new[] { condition } });
        // Consume the initial session purge before assigning the test image.
        session.ProcessPendingGpuState();
        var image = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
        image.Freeze();
        var idle = session.Graphs[0];
        idle.Root.Emitters[0].PendingTexture = image;
        session.Seek(.5);
        Draw();
        uint texture = idle.Root.Emitters[0].Texture;
        Assert.NotEqual(0u, texture);
        Assert.True(scene.Gl.IsTexture(texture));
        var forced = GameMaterialState.From(-1, new[]
            { AssetsManager.Services.Viewer.Vfx.Semantics.VfxPersistentEffectSemantics.PreviewKey(0) }, null);
        session.SetOwnerGameState(forced);
        var persistent = session.Graphs[1];
        persistent.Root.Emitters[0].PendingTexture = image;
        Draw();
        Assert.True(scene.Gl.IsTexture(texture));
        Assert.Same(idle, session.Graphs[0]);
        Assert.Equal(texture, persistent.Root.Emitters[0].Texture);
        session.SetOwnerGameState(null);
        Draw();
        Assert.True(scene.Gl.IsTexture(texture));
        session.SetOwnerGameState(forced);
        Assert.Same(persistent, session.Graphs[1]);
        Draw();
        Assert.Equal(texture, persistent.Root.Emitters[0].Texture);
        Assert.Equal(GLEnum.NoError, scene.Gl.GetError());

        void Draw()
        {
            scene.Gl.Viewport(0, 0, 64, 64);
            session.Render(Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 0, -2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentConditionPixelsMatchIdleAttachmentsAndDisappearWhenDisabled(bool native)
    {
        using var scene = new Scene(native);
        using var applied = new AssetsManager.Services.Viewer.Vfx.Session.VfxRenderSession();
        using var baseline = new AssetsManager.Services.Viewer.Vfx.Session.VfxRenderSession();
        var definition = Definition(VfxPrimitiveKind.CameraUnitQuad) with
        { BirthScale = VfxCurve3.Const(new Vector3(.3f)), Rate = VfxCurveF.Const(12), TexDiv = Vector2.One, NumFrames = 1, TexturePath = "" };
        var system = new VfxSystemDefinition(9, "persistent", "persistent", new[] { definition });
        var systems = new Dictionary<uint, VfxSystemDefinition> { [9] = system };
        var keys = new Dictionary<uint, uint> { [1] = 9 };
        var effect = new VfxIdleEffectDefinition(1, "", "Root", 0, "", 0, Vector3.Zero);
        var owner = new VfxOwnerSceneContext("", "", 1f);
        var condition = new VfxSubmeshCondition(new(GameMaterialBoolKind.Unsupported), Array.Empty<uint>(), Array.Empty<uint>())
        { Effects = new[] { effect } };
        applied.SetAnimationSession(null, null, systems, keys, System.IO.Path.GetTempPath(), 7, 2,
            owner with { SubmeshConditions = new[] { condition } });
        baseline.SetAnimationSession(null, new[] { effect }, systems, keys, System.IO.Path.GetTempPath(), 7, 2, owner);
        applied.SetBoneTransformSampler((time, _, _) => Matrix4x4.CreateTranslation((float)time * .2f, 0, 0));
        baseline.SetBoneTransformSampler((time, _, _) => Matrix4x4.CreateTranslation((float)time * .2f, 0, 0));
        applied.Seek(.5);
        Assert.All(Draw(applied), value => Assert.Equal(0, value));
        applied.SetOwnerGameState(GameMaterialState.From(-1, new[]
            { AssetsManager.Services.Viewer.Vfx.Semantics.VfxPersistentEffectSemantics.PreviewKey(0) }, null));
        baseline.Seek(.5);
        byte[] expected = Draw(baseline);
        Assert.Contains(expected, value => value != 0);
        Assert.Equal(expected, Draw(applied));
        applied.Seek(.25);
        applied.Seek(.5);
        Assert.Equal(expected, Draw(applied));
        applied.SetOwnerGameState(null);
        Assert.All(Draw(applied), value => Assert.Equal(0, value));
        Assert.Equal(GLEnum.NoError, scene.Gl.GetError());

        byte[] Draw(AssetsManager.Services.Viewer.Vfx.Session.VfxRenderSession session)
        {
            var states = new List<VfxPlaybackRuntime.EmitterState>();
            foreach (var graph in session.Graphs)
                foreach (var runtime in graph.Runtimes)
                    foreach (var emitter in runtime.Emitters)
                    { emitter.Texture = scene.White; emitter.TextureWidth = emitter.TextureHeight = 1; states.Add(emitter); }
            return scene.Draw(states, false);
        }
    }

    public static IEnumerable<object[]> MeshCases()
    {
        foreach (bool native in new[] { false, true })
        foreach (bool indexed in new[] { false, true })
        foreach (int alignment in new[] { 0, 1, 2, 3 })
        foreach (int bones in new[] { 0, 2, 257 })
            yield return new object[] { native, indexed, alignment, bones };
    }

    [Theory]
    [MemberData(nameof(MeshCases))]
    public void SharedMeshesMatchSeparateDrawsWithIndependentColorsUvsAndPoses(bool native, bool indexed, int alignment, int bones)
    {
        using var scene = new Scene(native);
        var definition = Definition(VfxPrimitiveKind.Mesh) with
        {
            MeshAlignYawToCamera = (alignment & 1) != 0,
            MeshAlignPitchToCamera = (alignment & 2) != 0,
            MeshIsSkinned = bones > 0
        };
        var sources = new List<VfxPlaybackRuntime.EmitterState>();
        var poses = new List<VfxAnimatedMesh>();
        try
        {
            for (int index = 0; index < 3; index++)
            {
                var source = State(definition, index, scene.Texture);
                source.Particles.Add(new VfxPlaybackRuntime.Particle
                { Age = .17f + index * .23f, BirthOrbitalVelocity = new Vector3(.03f, -.1f, .2f) });
                if (bones > 0) { source.MeshAnimation = Pose(bones); poses.Add(source.MeshAnimation); }
                float[] weights = bones > 0 ? new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 } : null;
                scene.Renderer.UploadEmitterMesh(source,
                    Positions, Normals, Uvs, null, indexed ? new uint[] { 0, 1, 2 } : null,
                    bones > 0 ? new float[12] : null, weights);
                sources.Add(source);
            }
            byte[] separate = scene.Draw(sources, false);
            Assert.Equal(3, scene.Renderer.LastParticleDrawCounts.Draws);
            byte[] instanced = scene.Draw(sources, true);
            Assert.Equal(bones > 256 ? 3 : 1, scene.Renderer.LastParticleDrawCounts.Draws);
            Assert.Equal(3, scene.Renderer.LastParticleDrawCounts.Instances);
            AssertPixels(separate, instanced);
            Assert.Contains(instanced, value => value != 0);
            foreach (var source in sources)
                if (native) Assert.Null(scene.Renderer.GameParticleFallback(source, true));
            Assert.Equal(GLEnum.NoError, scene.Gl.GetError());
        }
        finally { foreach (var pose in poses) pose.Dispose(); }
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    public void SharedQuadsKeepEachPlacementClockAndUploadOnce(bool native, int blend)
    {
        using var scene = new Scene(native);
        var definition = Definition(VfxPrimitiveKind.CameraUnitQuad) with
        {
            BlendMode = blend, EmitterUvScrollRate = new Vector2(.21f, -.1f),
            TextureMultEmitterUvScrollRate = new Vector2(-.13f, .31f)
        };
        var sources = new[] { State(definition, 0, scene.Texture), State(definition, 1, scene.Texture), State(definition, 2, scene.Texture) };
        foreach (var source in sources) source.TextureMult = scene.Texture;
        var original = (float[])sources[1].Instances.Clone();
        byte[] separate = scene.Draw(sources, false);
        Assert.Equal(3, scene.Renderer.LastParticleDrawCounts.Draws);
        byte[] combined = scene.Draw(sources, true);
        Assert.Equal(1, scene.Renderer.LastParticleDrawCounts.Draws);
        Assert.Equal(3, scene.Renderer.LastParticleDrawCounts.Instances);
        AssertPixels(separate, combined);
        Assert.Equal(original, sources[1].Instances);
        // A source removed from view cannot leave stale rows in a smaller draw.
        scene.Draw(new[] { sources[1] }, true);
        Assert.Equal((1, 1), scene.Renderer.LastParticleDrawCounts);
        Assert.Equal(GLEnum.NoError, scene.Gl.GetError());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedTransparencySortsAcrossPlacementsAndHonorsCapacity(bool native)
    {
        using var scene = new Scene(native);
        var definition = Definition(VfxPrimitiveKind.CameraUnitQuad) with { BlendMode = 1 };
        var near = State(definition, 0, scene.Texture);
        var far = State(definition, 1, scene.Texture);
        foreach (var source in new[] { near, far })
        {
            source.Texture = scene.White;
            source.Instances[0] = source.Instances[1] = 0;
            source.Instances[3] = source.Instances[4] = 1;
            source.Instances[5] = source.Instances[6] = source.Instances[7] = 0;
            source.Instances[8] = .5f;
        }
        near.Instances[2] = 0; near.Instances[5] = 1;
        far.Instances[2] = -.6f; far.Instances[7] = 1;
        byte[] image = scene.Draw(new[] { near, far }, true);
        int at = (32 * 64 + 32) * 4;
        Assert.InRange((int)image[at], 126, 129);
        Assert.InRange((int)image[at + 2], 62, 65);
        scene.Renderer.SharedDrawCapacities = new Dictionary<object, int> { [scene.Group] = 1 };
        image = scene.Draw(new[] { near, far }, true);
        Assert.Equal((1, 1), scene.Renderer.LastParticleDrawCounts);
        Assert.InRange((int)image[at], 126, 129);
        Assert.Equal(0, image[at + 2]);
    }

    public static IEnumerable<object[]> RibbonCases()
    {
        foreach (bool native in new[] { false, true })
        foreach (var primitive in new[] { VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail,
                     VfxPrimitiveKind.Beam, VfxPrimitiveKind.CameraSegmentBeam, VfxPrimitiveKind.PlanarProjection })
            yield return new object[] { native, primitive };
    }

    [Theory]
    [MemberData(nameof(RibbonCases))]
    public void SharedRibbonsAndProjectionsKeepSeparateStrandsAndSourceUvs(bool native, VfxPrimitiveKind primitive)
    {
        using var scene = new Scene(native);
        bool trail = primitive is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
        bool beam = primitive is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam;
        var definition = Definition(primitive) with
        {
            EmitterUvScrollRate = new Vector2(.2f, -.1f), TextureMultEmitterUvScrollRate = new Vector2(-.13f, .3f),
            Trail = trail ? new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0) : null,
            Beam = beam ? new VfxBeamDefinition(0, 0, 2, VfxCurve3.Const(Vector3.One), VfxCurve4.Const(Vector4.One),
                false, Vector3.Zero, Vector3.Zero) : null
        };
        var sources = new List<VfxPlaybackRuntime.EmitterState>();
        for (int sourceIndex = 0; sourceIndex < 3; sourceIndex++)
        {
            var source = State(definition, sourceIndex, scene.Texture);
            source.TextureMult = scene.Texture;
            source.SystemOrigin = new Vector3(-.8f + sourceIndex * .6f, -.3f, 0);
            source.SystemTarget = source.SystemOrigin + new Vector3(.35f, .2f, 0);
            if (trail || primitive == VfxPrimitiveKind.CameraSegmentBeam)
            {
                var rows = new float[VfxPlaybackRuntime.InstanceStride * 2];
                for (int index = 0; index < 2; index++)
                {
                    source.Instances.CopyTo(rows, index * VfxPlaybackRuntime.InstanceStride);
                    int at = index * VfxPlaybackRuntime.InstanceStride;
                    rows[at] = -.8f + sourceIndex * .6f + index * .35f;
                    rows[at + 1] = -.2f + sourceIndex * .15f;
                    rows[at + 3] = .07f;
                    source.Particles.Add(new VfxPlaybackRuntime.Particle
                    {
                        Serial = (uint)index, Ordinal = (ushort)index, Life = 1, BirthFrame = Matrix4x4.Identity,
                        TrailTiling = new Vector3(2, 1, 0), BirthRotation = new Vector3(0, 0, MathF.PI / 2)
                    });
                }
                source.Instances = rows;
                source.InstanceCount = 2;
            }
            sources.Add(source);
        }
        byte[] separate = scene.Draw(sources, false);
        Assert.Contains(separate, value => value != 0);
        Assert.Equal(3, scene.Renderer.LastParticleDrawCounts.Draws);
        byte[] combined = scene.Draw(sources, true);
        Assert.Equal(1, scene.Renderer.LastParticleDrawCounts.Draws);
        AssertPixels(separate, combined);
        Assert.Equal(GLEnum.NoError, scene.Gl.GetError());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedPlacementsAndChildPathsPreserveStencilCoverage(bool native)
    {
        using var scene = new Scene(native, stencil: true);
        var writerDefinition = Definition(VfxPrimitiveKind.CameraUnitQuad) with
        { RenderState = VfxEmitterRenderState.Default with { DisableBackfaceCull = true, StencilMode = 1, StencilReference = 6 } };
        var testerDefinition = writerDefinition with
        { RenderState = writerDefinition.RenderState with { StencilMode = 2 }, MiscRenderFlags = 1 };
        var writers = new[] { State(writerDefinition, 0, scene.White), State(writerDefinition, 1, scene.White) };
        var testers = new[] { State(testerDefinition, 0, scene.White), State(testerDefinition, 1, scene.White) };
        for (int index = 0; index < 2; index++)
        {
            writers[index].RenderPath = "writer";
            writers[index].Instances[0] = index == 0 ? -.4f : .4f;
            writers[index].Instances[1] = 0;
            testers[index].RenderPath = "0.0";
            testers[index].Instances[0] = testers[index].Instances[1] = 0;
            testers[index].Instances[3] = testers[index].Instances[4] = 2;
            testers[index].Instances[5] = testers[index].Instances[7] = 0;
            testers[index].Instances[6] = 1;
        }
        var sources = new[] { writers[0], writers[1], testers[0], testers[1] };
        byte[] separate = scene.Draw(sources, false);
        Assert.Equal(4, scene.Renderer.LastParticleDrawCounts.Draws);
        byte[] grouped = scene.Draw(sources, true);
        Assert.Equal(2, scene.Renderer.LastParticleDrawCounts.Draws);
        AssertPixels(separate, grouped);
        Assert.Equal(255, grouped[(32 * 64 + 19) * 4 + 1]);
        Assert.Equal(255, grouped[(32 * 64 + 44) * 4 + 1]);
        Assert.Equal(0, grouped[(32 * 64 + 32) * 4 + 1]);
        Assert.Equal(GLEnum.NoError, scene.Gl.GetError());
    }

    [Fact]
    public void DifferentResourcesAndChildPathsKeepSeparateDraws()
    {
        using var scene = new Scene(false);
        var definition = Definition(VfxPrimitiveKind.CameraUnitQuad);
        var first = State(definition, 0, scene.Texture);
        var second = State(definition, 1, scene.White);
        scene.Draw(new[] { first, second }, true);
        Assert.Equal(2, scene.Renderer.LastParticleDrawCounts.Draws);
        second.Texture = first.Texture;
        second.RenderPath = "0.0";
        scene.Draw(new[] { first, second }, true);
        Assert.Equal(2, scene.Renderer.LastParticleDrawCounts.Draws);
        second.RenderPath = "";
        second.IsVisible = false;
        scene.Draw(new[] { first, second }, true);
        Assert.Equal((1, 1), scene.Renderer.LastParticleDrawCounts);
    }

    private static VfxEmitterDefinition Definition(VfxPrimitiveKind primitive) => new("shared", VfxCurveF.Const(1), VfxCurveF.Const(1),
        null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
        VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
        "gradient.tex", new Vector2(2), 4, false, primitive == VfxPrimitiveKind.Mesh, PrimitiveKind: primitive,
        UvTransformCenter: new Vector2(.3f, .6f), TextureMultTransformCenter: new Vector2(.7f, .2f),
        RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true, TextureAddressMode = 1 });

    private static VfxPlaybackRuntime.EmitterState State(VfxEmitterDefinition definition, int index, uint texture)
    {
        var row = new float[VfxPlaybackRuntime.InstanceStride];
        row[0] = -.6f + .6f * index;
        row[1] = .12f * (index - 1);
        row[3] = .32f; row[4] = .28f; row[18] = .3f;
        row[5] = .3f + .2f * index; row[6] = .8f - .2f * index; row[7] = .6f; row[8] = 1;
        row[10] = index; row[11] = .2f + .1f * index; row[12] = index + 1; row[34] = .7f;
        row[15] = .13f; row[16] = -.2f; row[17] = .27f;
        row[19] = .07f; row[20] = -.03f; row[21] = .7f; row[22] = .8f; row[23] = .31f;
        row[24] = .8f; row[28] = 1;
        row[29] = -.1f; row[30] = .2f; row[31] = .6f; row[32] = .9f; row[33] = -.21f;
        row[36] = row[40] = row[44] = 1;
        return new VfxPlaybackRuntime.EmitterState
        { Def = definition, Instances = row, InstanceCount = 1, Texture = texture,
            TextureWidth = 16, TextureHeight = 16, RenderGraphKey = new object(), RenderTime = .37f + index * .83f };
    }

    private static void AssertPixels(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int at = 0; at < expected.Length; at++)
            Assert.True(Math.Abs(expected[at] - actual[at]) <= 2, $"Byte {at}: expected {expected[at]}, got {actual[at]}.");
    }

    private static VfxAnimatedMesh Pose(int count)
    {
        var builder = new RigResourceBuilder();
        for (int at = 0; at < count; at++) builder.CreateJoint($"Joint{at}").WithInfluence(true)
            .WithLocalTransform(Matrix4x4.Identity).WithInverseBindTransform(Matrix4x4.Identity);
        return (VfxAnimatedMesh)Activator.CreateInstance(typeof(VfxAnimatedMesh), BindingFlags.NonPublic | BindingFlags.Instance,
            null, new object[] { Array.Empty<float>(), Array.Empty<float>(), builder.Build(), new MovingPose() }, null);
    }

    private sealed class MovingPose : IAnimationAsset
    {
        public bool IsDisposed { get; private set; }
        public float Duration => 10;
        public float Fps => 30;
        public void Evaluate(float seconds, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose) =>
            pose[Elf.HashLower("Joint0")] = (Quaternion.Identity, new Vector3(seconds * .3f, seconds * .1f, 0), Vector3.One);
        public void Dispose() => IsDisposed = true;
    }

    private static readonly float[] Positions = { -.5f, -.5f, 0, .5f, -.5f, 0, 0, .5f, 0 };
    private static readonly float[] Normals = { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
    private static readonly float[] Uvs = { 0, 0, 1, 0, .5f, 1 };

    private sealed class Scene : IDisposable
    {
        private readonly HiddenWglContext _context = new();
        private readonly uint _target, _framebuffer, _stencil;
        internal readonly GL Gl;
        internal readonly VfxOpenGlRenderer Renderer = new();
        internal readonly object Group = new();
        internal readonly uint Texture, White;
        internal Scene(bool native, bool stencil = false)
        {
            Gl = GL.GetApi(_context.GetProcAddress);
            Renderer.Initialize(Gl, native ? InstalledSkins.Settings(InstalledSkins.FindInstall()) : null);
            _target = NewTexture(64, new byte[64 * 64 * 4]);
            _framebuffer = Gl.GenFramebuffer();
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            Gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _target, 0);
            if (stencil)
            {
                _stencil = Gl.GenRenderbuffer();
                Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _stencil);
                Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, 64, 64);
                Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                    RenderbufferTarget.Renderbuffer, _stencil);
            }
            Assert.Equal(GLEnum.FramebufferComplete, Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var pixels = new byte[16 * 16 * 4];
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
            { int at = (y * 16 + x) * 4; pixels[at] = (byte)(8 + x * 16); pixels[at + 1] = (byte)(8 + y * 16); pixels[at + 2] = 100; pixels[at + 3] = 255; }
            Texture = NewTexture(16, pixels);
            White = NewTexture(1, new byte[] { 255, 255, 255, 255 });
        }
        internal byte[] Draw(IReadOnlyList<VfxPlaybackRuntime.EmitterState> sources, bool shared)
        {
            var groups = new Dictionary<object, object>();
            var queue = new List<VfxRenderQueueEntry>();
            for (int index = 0; index < sources.Count; index++)
            { groups[sources[index].RenderGraphKey] = Group; queue.Add(new(sources[index], shared ? 0 : index, index)); }
            Renderer.SharedDrawGroups = shared ? groups : null;
            Gl.Viewport(0, 0, 64, 64);
            Gl.ClearColor(0, 0, 0, 0);
            if (_stencil != 0) { Gl.StencilMask(0xff); Gl.DepthMask(true); Gl.ClearStencil(0); Gl.ClearDepth(1); }
            Gl.Clear(ClearBufferMask.ColorBufferBit | (_stencil != 0 ? ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit : 0));
            bool projection = sources.Count > 0 && sources[0].Def.DrawsAsProjection;
            Matrix4x4 view = projection ? Matrix4x4.CreateLookAt(new Vector3(0, 3, 0), Vector3.Zero, -Vector3.UnitZ)
                : Matrix4x4.CreateTranslation(0, 0, -2);
            Matrix4x4 viewProjection = projection ? view * Matrix4x4.CreateOrthographic(2, 2, .1f, 10) : Matrix4x4.Identity;
            Renderer.Render(queue, viewProjection, view);
            var pixels = new byte[64 * 64 * 4];
            Gl.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            return pixels;
        }
        private uint NewTexture(int size, byte[] data)
        {
            uint texture = Gl.GenTexture(); Gl.BindTexture(TextureTarget.Texture2D, texture);
            Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)size, (uint)size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, data.AsSpan());
            Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            return texture;
        }
        public void Dispose()
        {
            Renderer.Dispose(); Gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            Gl.DeleteFramebuffer(_framebuffer);
            if (_stencil != 0) Gl.DeleteRenderbuffer(_stencil);
            foreach (uint texture in new[] { _target, Texture, White }) Gl.DeleteTexture(texture);
            Gl.Dispose(); _context.Dispose();
        }
    }
}
