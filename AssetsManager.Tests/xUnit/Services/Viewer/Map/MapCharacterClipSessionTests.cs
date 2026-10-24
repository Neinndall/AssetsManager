using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using AssetsManager.Tests.Support;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map;

public sealed class MapCharacterClipSessionTests(ITestOutputHelper output)
{
    [Fact]
    public void InstalledOwnerConditionsSurviveMapCatalogAndStartWithoutIdleOrClipEvents()
    {
        string install = InstalledSkins.FindInstall();
        Assert.NotNull(install);
        using var wad = new WadFile(Path.Combine(install, @"Game\DATA\FINAL\Champions\Aatrox.wad.client"));
        var documents = new List<BinTree>();
        var pending = new Queue<string>(new[] { "data/characters/aatrox/skins/skin33.bin" });
        var seen = new HashSet<ulong>();
        while (pending.Count > 0 && documents.Count < 64)
        {
            string path = pending.Dequeue().ToLowerInvariant();
            ulong hash = XxHash64Ext.Hash(path);
            if (!seen.Add(hash) || !wad.Chunks.ContainsKey(hash)) continue;
            using var chunk = wad.LoadChunkDecompressed(hash);
            byte[] bytes = chunk.Span.ToArray();
            output.WriteLine($"{path}: {Convert.ToHexString(SHA256.HashData(bytes))}");
            using var stream = new MemoryStream(bytes, writable: false);
            var tree = new BinTree(stream);
            documents.Add(tree);
            foreach (string dependency in tree.Dependencies) pending.Enqueue(dependency);
        }
        var catalog = MapCharacterLoadingService.BuildVfxCatalog(documents, null, null, null, null);
        Assert.NotEmpty(catalog.OwnerSceneContext.SubmeshConditions);
        // Isolate the persistent-only host gate while preserving the installed owner's conditions and resolver.
        catalog = catalog with { IdleEffects = Array.Empty<VfxIdleEffectDefinition>() };
        Assert.NotEmpty(StudioControl.RequiredMapClipSystems(catalog, null));
        using var group = Group(catalog);
        group.SetPreviewClip(Clip("Idle"));
        using var session = new VfxRenderSession();
        Assert.True(group.StartPreviewVfxSession(session, null, Path.GetTempPath(), 42, 2));
        Assert.All(session.Graphs, graph => Assert.Equal(1337, graph.InitialSeed));
        output.WriteLine($"BINs={documents.Count}; conditions={catalog.OwnerSceneContext.SubmeshConditions.Count}; persistent graphs={session.Graphs.Count}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentOnlyClipLoadsAndStartsWithItsOwnStateAndVisibility(bool animationCondition)
    {
        var predicate = animationCondition
            ? new GameMaterialBoolCondition(GameMaterialBoolKind.Animation, Animations: new[] { H("Dance") })
            : new GameMaterialBoolCondition(GameMaterialBoolKind.All);
        var condition = new VfxSubmeshCondition(predicate, new uint[] { 7 }, new uint[] { 8 })
        {
            Effects = new[] { new VfxIdleEffectDefinition(1, "glow", "Root", H("Root"), null, 0, Vector3.Zero) }
        };
        var catalog = Catalog(condition);
        using var group = Group(catalog);
        group.SetPreviewClip(Clip("Dance"));

        var required = StudioControl.RequiredMapClipSystems(catalog, null);
        Assert.Single(required);
        Assert.Empty(catalog.IdleEffects);
        using var session = new VfxRenderSession();
        Assert.True(group.StartPreviewVfxSession(session, null, Path.GetTempPath(), 42, 2));
        Assert.Equal(1337, Assert.Single(session.Graphs).InitialSeed);
        session.SetBoneTransformSampler((time, name, hash) => Matrix4x4.CreateTranslation((float)time * 10, 0, 0));
        session.Seek(0.5);
        Assert.True(session.LiveParticleCount > 0);
        var instances = (float[])session.Graphs[0].Root.Emitters[0].Instances.Clone();
        session.Seek(0);
        session.Seek(0.5);
        Assert.Equal(instances, session.Graphs[0].Root.Emitters[0].Instances);

        var fromClip = new HashSet<uint> { 7, 9 };
        group.SetPreviewHiddenSubmeshes(fromClip);
        Assert.DoesNotContain(7u, group.PreviewHiddenSubmeshes);
        Assert.Contains(8u, group.PreviewHiddenSubmeshes);
        Assert.Contains(9u, group.PreviewHiddenSubmeshes);
        Assert.Contains(7u, fromClip);
        Assert.Equal(-1, group.PreviewGameState.Gear);
        Assert.Empty(group.PreviewGameState.Buffs);

        group.SetPreviewClip(Clip("Idle"));
        bool idleReady = group.StartPreviewVfxSession(session, null, Path.GetTempPath(), 42, 2);
        Assert.Equal(!animationCondition, idleReady);
        group.SetPreviewHiddenSubmeshes(fromClip);
        Assert.Equal(animationCondition, group.PreviewHiddenSubmeshes.Contains(7));
        Assert.Equal(!animationCondition, group.PreviewHiddenSubmeshes.Contains(8));
        group.ClearPreviewClip();
        Assert.Empty(group.PreviewGameState.Animations);
        Assert.Empty(group.PreviewHiddenSubmeshes);
    }

    [Fact]
    public void UnresolvedPersistentKeysAndUnknownOrBuffPredicatesDoNotCreateEffects()
    {
        foreach (var predicate in new[] { new GameMaterialBoolCondition(GameMaterialBoolKind.Unsupported),
            new GameMaterialBoolCondition(GameMaterialBoolKind.Buff, Name: "Empowered") })
        {
            var condition = new VfxSubmeshCondition(predicate, Array.Empty<uint>(), new uint[] { 8 })
            { Effects = new[] { new VfxIdleEffectDefinition(1, "", null, 0, null, 0, Vector3.Zero) } };
            var catalog = Catalog(condition);
            using var group = Group(catalog);
            group.SetPreviewClip(Clip("Dance"));
            using var session = new VfxRenderSession();
            Assert.False(group.StartPreviewVfxSession(session, null, Path.GetTempPath(), 1, 2));
            Assert.Empty(session.Graphs);
            group.SetPreviewHiddenSubmeshes(new HashSet<uint>());
            Assert.Empty(group.PreviewHiddenSubmeshes);

            var unresolved = catalog with { ResourceMap = new Dictionary<uint, uint> { [1] = 0 } };
            Assert.Empty(StudioControl.RequiredMapClipSystems(unresolved, null));
        }
    }

    private static MapCharacterRuntimeGroup Group(MapCharacterVfxCatalog catalog) => new(
        new MapCharacterAssetData(null, null, null, null, null, null, catalog), null, Array.Empty<MapCharacterData>());

    private static AnimationClipDefinition Clip(string name) => new(H(name), 0, 1f / 30f, 0, 60,
        Array.Empty<AnimationClipEventDefinition>(), name, "test.anm");

    private static uint H(string name) => Fnv1a.HashLower(name);

    private static MapCharacterVfxCatalog Catalog(VfxSubmeshCondition condition)
    {
        var emitter = new VfxEmitterDefinition(Name: "glow", Rate: VfxCurveF.Const(6), ParticleLifetime: VfxCurveF.Const(2),
            EmitterLifetime: null, ParticleLinger: 0, TimeBeforeFirstEmission: 0, IsSingleParticle: false,
            Disabled: false, BlendMode: 1, BirthScale: VfxCurve3.Const(Vector3.One), ScaleOverLife: null,
            BirthColor: VfxCurve4.Const(Vector4.One), ColorOverLife: null, BirthVelocity: null, Acceleration: null,
            BirthRotationalVelocity: null, EmitterPosition: VfxCurve3.Const(Vector3.Zero), TexturePath: "",
            TexDiv: Vector2.One, NumFrames: 1, RandomStartFrame: false, IsMeshPrimitive: false,
            RenderState: VfxEmitterRenderState.Default);
        return new(new Dictionary<uint, VfxSystemDefinition> { [9] = new(9, "glow", "glow", new[] { emitter }) },
            new Dictionary<uint, uint> { [1] = 9 }, Array.Empty<VfxIdleEffectDefinition>(),
            new VfxOwnerSceneContext(null, null, 1) { SubmeshConditions = new[] { condition } });
    }
}
