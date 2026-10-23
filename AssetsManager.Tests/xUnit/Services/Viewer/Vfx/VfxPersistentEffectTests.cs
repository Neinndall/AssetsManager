using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using LeagueToolkit.Core.Wad;
using AssetsManager.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxPersistentEffectTests(ITestOutputHelper output)
{
    [Fact]
    public void AuditInstalledAatrox33RestingEffects()
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
            using var stream = new MemoryStream(chunk.Span.ToArray(), false);
            var tree = new BinTree(stream);
            documents.Add(tree);
            foreach (string dependency in tree.Dependencies) pending.Enqueue(dependency);
        }
        var owner = VfxAnimationParser.ExtractOwnerSceneContext(documents[0]);
        var keys = VfxResourceParser.ExtractSkinResourceMap(documents[0], documents);
        var systems = documents.SelectMany(tree => VfxSystemParser.ExtractAll(tree)).GroupBy(pair => pair.Key)
            .ToDictionary(group => group.Key, group => group.First().Value);
        output.WriteLine($"skinScale={owner.SkinScale}; BINs={documents.Count}; mesh={owner.MeshPath}");
        using (var meshChunk = wad.LoadChunkDecompressed(XxHash64Ext.Hash(owner.MeshPath.ToLowerInvariant())))
        using (var meshStream = new MemoryStream(meshChunk.Span.ToArray(), false))
        using (var mesh = LeagueToolkit.Core.Mesh.SkinnedMesh.ReadFromSimpleSkin(meshStream))
            output.WriteLine("SUBMESHES: " + string.Join(", ", mesh.Ranges.Select(range => $"{range.Material}:{H(range.Material):x8}")));
        for (int index = 0; index < owner.SubmeshConditions.Count; index++)
        {
            var condition = owner.SubmeshConditions[index];
            output.WriteLine($"Condition {index + 1}: {condition.Condition}; resting(-1)={condition.Condition.Evaluate(GameMaterialState.Resting with { Gear = -1 })}; resting(0)={condition.Condition.Evaluate(GameMaterialState.Resting)}");
            foreach (var effect in condition.Effects) Print(effect);
        }
        output.WriteLine("IDLE:");
        foreach (var effect in VfxAnimationParser.ExtractIdleEffects(documents[0])) Print(effect);

        // Reproduce the resting turbine effect that previously replaced its SCB with the entire owner.
        var active = owner.SubmeshConditions.Where(condition =>
            condition.Condition.Evaluate(GameMaterialState.Resting with { Gear = -1 }) == true).SelectMany(condition => condition.Effects);
        var turbine = active.Select(effect => systems[keys[effect.EffectKey]]).SelectMany(system => system.Emitters)
            .First(emitter => emitter.PrimitiveKind == VfxPrimitiveKind.AttachedMesh && !string.IsNullOrWhiteSpace(emitter.MeshPath));
        string root = Path.Combine(Path.GetTempPath(), "AssetsManagerTurbine", Guid.NewGuid().ToString("N"));
        try
        {
            Extract(turbine.MeshPath);
            Extract(owner.MeshPath);
            Extract(owner.SkeletonPath);
            using var loading = new VfxLoadingService();
            var fixture = new VfxSystemDefinition(7, "turbine", "turbine", new[] { turbine });
            var runtime = loading.PreparePlaybackAtWorldTransform(fixture, root, Matrix4x4.Identity, 1337, null, owner);
            var emitter = Assert.Single(runtime.Emitters);
            Assert.Equal(VfxPrimitiveKind.Mesh, emitter.Def.PrimitiveKind);
            VfxMeshData mesh = Assert.IsType<VfxMeshData>(emitter.PendingMesh);
            Assert.False(mesh.HasSkinning);
            output.WriteLine($"Turbine geometry: {mesh.Positions.Length / 3} vertices; authored scale={emitter.Def.BirthScale.Sample(0)}; owner mesh is not substituted.");
            var missing = fixture with { Emitters = new[] { turbine with { MeshPath = "missing.scb", MeshFallbackPath = null } } };
            var unresolved = loading.PreparePlaybackAtWorldTransform(missing, root, Matrix4x4.Identity, 1337, null, owner);
            Assert.Null(Assert.Single(unresolved.Emitters).PendingMesh);

            void Extract(string path)
            {
                if (string.IsNullOrEmpty(path)) return;
                using var chunk = wad.LoadChunkDecompressed(XxHash64Ext.Hash(path.ToLowerInvariant()));
                string destination = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllBytes(destination, chunk.Span.ToArray());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        void Print(VfxIdleEffectDefinition effect)
        {
            if (!keys.TryGetValue(effect.EffectKey, out uint target) || !systems.TryGetValue(target, out var system))
            { output.WriteLine($"  UNRESOLVED {effect.EffectKey:x8} {effect.EffectName}"); return; }
            output.WriteLine($"  {effect.EffectKey:x8} -> {target:x8} {system.Name}; source={effect.BoneName}; aim={effect.TargetBoneName}");
            foreach (var emitter in system.Emitters)
                output.WriteLine($"    {emitter.Name} primitive={emitter.PrimitiveKind} rate={emitter.Rate.Sample(0)} scale={emitter.BirthScale.Sample(0)} mesh={emitter.MeshPath} draw=[{string.Join(",", emitter.SubmeshesToDraw ?? Array.Empty<uint>())}] always=[{string.Join(",", emitter.SubmeshesToDrawAlways ?? Array.Empty<uint>())}]");
        }
    }
    private static uint H(string text) => Fnv1a.HashLower(text);

    [Fact]
    public void AttachedSkinWithASkinnedPathKeepsTheOwnerOverlayContract()
    {
        var original = System(1);
        original = original with { Emitters = new[] { original.Emitters[0] with
        { PrimitiveKind = VfxPrimitiveKind.AttachedMesh, IsMeshPrimitive = true, MeshIsSkinned = true,
            MeshPath = "skin.skn", MeshSkeletonPath = "skin.skl" } } };
        using var loading = new VfxLoadingService();
        Assert.Same(original, loading.ResolveMeshAvailability(original, Path.GetTempPath()));
    }

    [Fact]
    public void MergedBuffPreviewRestoresSpellSelectionAndStillActivatesBothPredicates()
    {
        uint hash = H("AatroxR");
        string key = GameMaterialState.SpellBuffKey(hash);
        var spells = new Dictionary<uint, VfxSpellPreview>
        {
            [hash] = new(null, null, null, 0, null, null, null, null, 0, null, Array.Empty<VfxSpellIssue>())
                { ScriptName = "AatroxR" }
        };
        var model = new StudioModel();
        model.SetCharacterGameStates(new[] { "AatroxR", key }, new HashSet<string> { key }, null,
            canonicalName: name => name == key ? spells[hash].ScriptName : name);
        var option = Assert.Single(model.CharacterGameStates);
        Assert.Equal("AatroxR", option.Name);
        Assert.True(option.IsEnabled);
        var state = GameMaterialState.Preview(model.CharacterGameStates.Where(item => item.IsEnabled).Select(item => item.Name), null, spells);
        Assert.True(state.HasBuff("AatroxR"));
        Assert.True(state.HasBuff(key));
        option.IsEnabled = false;
        Assert.Null(GameMaterialState.Preview(model.CharacterGameStates.Where(item => item.IsEnabled).Select(item => item.Name), null, spells));
    }

    [Fact]
    public void PersistentPreviewOptionsStaySeparateFromBuffsAndKeepTheirSavedIdentity()
    {
        var model = new StudioModel();
        string condition = VfxPersistentEffectSemantics.PreviewKey(13);
        int changes = 0;
        model.SetCharacterGameStates(new[] { "AatroxR", condition }, new HashSet<string> { condition }, () => changes++,
            key => key == condition ? "Not (AatroxR)" : key,
            key => VfxPersistentEffectSemantics.TryPreviewIndex(key, out _), _ => "Effect details");
        Assert.Equal("AatroxR", Assert.Single(model.CharacterGameStates).Name);
        var preview = Assert.Single(model.CharacterPersistentConditions);
        Assert.Equal(condition, preview.Name);
        Assert.Equal("Not (AatroxR)", preview.DisplayName);
        Assert.True(preview.IsEnabled);
        preview.IsEnabled = false;
        Assert.Equal(1, changes);
        preview.IsAutomaticallyActive = true;
        Assert.False(preview.IsEnabled);
        Assert.Equal(1, changes);
        model.SetCharacterGameStates(Array.Empty<string>(), null, () => changes++);
        Assert.False(model.HasCharacterGameStates);
        Assert.False(model.HasCharacterPersistentConditions);
    }
    private static VfxIdleEffectDefinition Effect(uint key) => new(key, "", "Root", H("Root"), "Aim", H("Aim"), Vector3.Zero);
    private static VfxOwnerSceneContext Owner(params VfxSubmeshCondition[] conditions) =>
        new("mesh.skn", "mesh.skl", 2f) { SubmeshConditions = conditions };
    private static VfxSubmeshCondition Condition(GameMaterialBoolCondition predicate, params VfxIdleEffectDefinition[] effects) =>
        new(predicate, Array.Empty<uint>(), Array.Empty<uint>()) { Effects = effects };

    [Fact]
    public void InstalledSkinsExposeAuthoredPersistentVfxWithoutInventingOffsets()
    {
        string install = InstalledSkins.FindInstall();
        Assert.NotNull(install);
        int total = 0;
        foreach (var (champion, skin) in new[] { ("Aatrox", 33), ("Aatrox", 40), ("Akali", 32),
            ("Sona", 6), ("Sona", 32), ("Ezreal", 5), ("Morgana", 10), ("Kayn", 1), ("Zac", 31) })
        {
            using var wad = new WadFile(Path.Combine(install, @"Game\DATA\FINAL\Champions", champion + ".wad.client"));
            string path = $"data/characters/{champion.ToLowerInvariant()}/skins/skin{skin}.bin";
            ulong hash = XxHash64Ext.Hash(path);
            if (!wad.Chunks.ContainsKey(hash)) continue;
            using var chunk = wad.LoadChunkDecompressed(hash);
            using var stream = new MemoryStream(chunk.Span.ToArray(), writable: false);
            var tree = new BinTree(stream);
            var owner = VfxAnimationParser.ExtractOwnerSceneContext(tree);
            var effects = VfxPersistentEffectSemantics.AllEffects(owner).ToArray();
            output.WriteLine($"{champion} Skin{skin}: conditions={owner?.SubmeshConditions.Count ?? 0}, persistent VFX={effects.Length}");
            Assert.All(effects, effect => Assert.Equal(Vector3.Zero, effect.Position));
            total += effects.Length;
        }
        Assert.True(total > 0, "Installed fixtures must include authored PersistentVfxs.");
    }

    [Fact]
    public void PrimarySkinLoadsItsResolverFromAChainedDependencyAndKeepsCyclesBounded()
    {
        string root = Path.Combine(Path.GetTempPath(), "AssetsManagerPersistent", Guid.NewGuid().ToString("N"), "Hero.wad.client");
        try
        {
            string primary = Write("data/characters/hero/skins/skin0.bin",
                new[] { Skin(new BinTreeObjectLink(H("mResourceResolver"), 77)) }, "data/middle.bin");
            Write("data/middle.bin", Array.Empty<BinTreeObject>(), "data/resources.bin");
            Write("data/resources.bin", new[] { new BinTreeObject(77, H("ResourceResolver"), new BinTreeProperty[]
            {
                new BinTreeMap(H("resourceMap"), BinPropertyType.Hash, BinPropertyType.ObjectLink,
                    new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 1), new BinTreeObjectLink(0, 9)) })
            }), new BinTreeObject(9, H("VfxSystemDefinitionData"), Array.Empty<BinTreeProperty>()) },
                "data/characters/hero/skins/skin0.bin");
            using var loader = new VfxLoadingService();
            var bundle = loader.Load(primary, null);
            Assert.Equal(3, bundle.LoadedBins.Count);
            Assert.Equal(9u, bundle.ResourceMap[1]);
            Assert.Contains(9u, bundle.Systems.Keys);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        string Write(string relative, BinTreeObject[] objects, params string[] dependencies)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var stream = File.Create(path);
            new BinTree(objects, dependencies).Write(stream);
            return path;
        }
    }

    [Fact]
    public void ParserKeepsEffectOnlyAndEmptyConditionsInAuthoredOrderAndHashesMissingKeys()
    {
        BinTreeStruct effect = new(0, H("PersistentVfxData"), new BinTreeProperty[]
        {
            new BinTreeString(H("effectName"), "Glow"),
            new BinTreeString(H("boneName"), "Root"), new BinTreeString(H("targetBoneName"), "Aim")
        });
        BinTreeStruct condition = new(0, H("PersistentEffectConditionData"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("PersistentVfxs"), BinPropertyType.Struct, new BinTreeProperty[] { effect })
        });
        var skin = Skin(new BinTreeContainer(H("persistentEffectConditions"), BinPropertyType.Struct,
            new BinTreeProperty[] { new BinTreeStruct(0, H("PersistentEffectConditionData"), Array.Empty<BinTreeProperty>()), condition }));
        VfxOwnerSceneContext owner = VfxAnimationParser.ExtractOwnerSceneContext(new BinTree(new[] { skin }, Array.Empty<string>()));
        Assert.Equal(2, owner.SubmeshConditions.Count);
        Assert.Empty(owner.SubmeshConditions[0].Effects);
        var parsed = Assert.Single(owner.SubmeshConditions[1].Effects);
        Assert.Equal(H("Glow"), parsed.EffectKey);
        Assert.Equal("Root", parsed.BoneName);
        Assert.Equal("Aim", parsed.TargetBoneName);
        Assert.Equal(Vector3.Zero, parsed.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HideWinsAcrossActiveConditionsRegardlessOfOrderAndOverClipVisibility(bool reverse)
    {
        var yes = new GameMaterialBoolCondition(GameMaterialBoolKind.All);
        var conditions = new[] { new VfxSubmeshCondition(yes, Array.Empty<uint>(), new uint[] { 7 }),
            new VfxSubmeshCondition(yes, new uint[] { 7 }, Array.Empty<uint>()) };
        if (reverse) Array.Reverse(conditions);
        Assert.Contains(7u, CharacterFormSemantics.HiddenSubmeshes(Array.Empty<uint>(), null, conditions: conditions));
        Assert.Contains(7u, CharacterFormSemantics.ConditionedHiddenSubmeshes(Array.Empty<uint>(), conditions, null));
    }

    [Fact]
    public void PreviewOverrideEnablesUnknownPredicatesAndEffectsOnlyConditionsExposeTheirBuffs()
    {
        var owner = Owner(Condition(new(GameMaterialBoolKind.Unsupported), Effect(1)),
            Condition(new(GameMaterialBoolKind.Buff, Name: "Empowered"), Effect(2)));
        var choices = CharacterFormSemantics.GameStateBuffs(null, owner);
        Assert.Contains("Empowered", choices);
        Assert.Contains(VfxPersistentEffectSemantics.PreviewKey(0), VfxPersistentEffectSemantics.PreviewKeys(owner));
        Assert.False(VfxPersistentEffectSemantics.Active(owner.SubmeshConditions[0], 0, GameMaterialState.Resting));
        Assert.True(VfxPersistentEffectSemantics.Active(owner.SubmeshConditions[0], 0,
            GameMaterialState.From(-1, new[] { VfxPersistentEffectSemantics.PreviewKey(0) }, null)));
    }

    [Fact]
    public void LinkedResolverUsesTheReferencedObjectWithFirstDeclarationAndNullSuppression()
    {
        var primary = new BinTree(new[] { Skin(new BinTreeObjectLink(H("mResourceResolver"), 77)) }, Array.Empty<string>());
        BinTree first = Resolver(0), later = Resolver(9);
        Assert.Empty(VfxResourceParser.ExtractSkinResourceMap(primary));
        Assert.Equal(0u, VfxResourceParser.ExtractSkinResourceMap(primary, new[] { first, later })[1]);
        var catalog = MapCharacterLoadingService.BuildVfxCatalog(new[] { primary, first, later }, null, null, null, null);
        Assert.Equal(0u, catalog.ResourceMap[1]);
        static BinTree Resolver(uint target) => new(new[] { new BinTreeObject(77, Fnv1a.HashLower("ResourceResolver"),
            new BinTreeProperty[] { new BinTreeMap(Fnv1a.HashLower("resourceMap"), BinPropertyType.Hash, BinPropertyType.ObjectLink,
                new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 1), new BinTreeObjectLink(0, target)) }) }) }, Array.Empty<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConditionsJoinPausedClockWithBonesAndClearObsoleteCheckpoints(bool spell)
    {
        var condition = Condition(new(GameMaterialBoolKind.Buff, Name: "Empowered"), Effect(1));
        var owner = Owner(condition);
        var system = System(9);
        var systems = new Dictionary<uint, VfxSystemDefinition> { [9] = system };
        var map = new Dictionary<uint, uint> { [1] = 9 };
        using var session = new VfxRenderSession();
        if (spell) session.SetSpellSession(null, systems, map, Path.GetTempPath(), 3, owner);
        else session.SetAnimationSession(null, Array.Empty<VfxIdleEffectDefinition>(), systems, map, Path.GetTempPath(), 42, 3, owner);
        session.SetBoneTransformSampler((time, name, hash) => Matrix4x4.CreateTranslation(name == "Aim" ? 100 : (float)time * 10, 0, 0));
        session.Seek(1);
        Assert.Empty(session.Graphs);
        var active = GameMaterialState.From(-1, new[] { "Empowered" }, null);
        session.SetOwnerGameState(active);
        Assert.Equal(1d, session.PlaybackTime, 6);
        var graph = Assert.Single(session.Graphs);
        Assert.Equal(1337, graph.InitialSeed);
        Assert.True(session.LiveParticleCount > 0);
        Assert.True(session.CheckpointCount > 0);
        session.SetOwnerGameState(active);
        Assert.Same(graph, Assert.Single(session.Graphs));
        session.Seek(0.5);
        float[] replay = (float[])graph.Root.Emitters[0].Instances.Clone();
        session.Seek(0);
        session.Seek(0.5);
        Assert.Equal(replay, graph.Root.Emitters[0].Instances);
        session.SetOwnerGameState(null);
        Assert.Empty(session.Graphs);
        Assert.Equal(0, session.LiveParticleCount);
        session.Seek(0.25);
        Assert.Empty(session.Graphs);
        session.SetOwnerGameState(active);
        Assert.Single(session.Graphs);
        Assert.Equal(0.25, session.PlaybackTime, 6);
    }

    [Fact]
    public void DuplicateEffectKeysKeepDistinctAttachmentsAndUnmappedEffectsStayAbsent()
    {
        var owner = Owner(Condition(new(GameMaterialBoolKind.All), Effect(1), Effect(1), Effect(99)));
        using var session = new VfxRenderSession();
        session.SetAnimationSession(null, new[] { Effect(1) }, new Dictionary<uint, VfxSystemDefinition> { [9] = System(9) },
            new Dictionary<uint, uint> { [1] = 9 }, Path.GetTempPath(), 7, 2, owner);
        Assert.Equal(3, session.Graphs.Count);
        Assert.NotSame(session.Graphs[1], session.Graphs[2]);
        session.SetSystem(null);
        Assert.Empty(session.Graphs);
        session.SetOwnerGameState(GameMaterialState.Resting);
        Assert.Empty(session.Graphs);
    }

    [Fact]
    public void PersistentEffectChildrenReplayWithTheirParentAndLeaveNoLineageWhenDisabled()
    {
        var parent = System(9);
        parent = parent with { Emitters = new[] { parent.Emitters[0] with
        {
            ChildParticleSet = new VfxChildParticleSetDefinition(new[] { new VfxChildSystemReference("", 10, 0) },
                false, VfxCurveF.Const(1), VfxCurve3.Const(Vector3.Zero), 0)
        } } };
        using var session = new VfxRenderSession();
        session.SetAnimationSession(null, null, new Dictionary<uint, VfxSystemDefinition> { [9] = parent, [10] = System(10) },
            new Dictionary<uint, uint> { [1] = 9 }, Path.GetTempPath(), 7, 3,
            Owner(Condition(new(GameMaterialBoolKind.Unsupported), Effect(1))));
        var active = GameMaterialState.From(-1, new[] { VfxPersistentEffectSemantics.PreviewKey(0) }, null);
        session.SetOwnerGameState(active);
        session.Seek(.5);
        Assert.True(session.LiveChildSystemCount > 0);
        int children = session.LiveChildSystemCount, particles = session.LiveParticleCount;
        session.Seek(.25);
        session.Seek(.5);
        Assert.Equal(children, session.LiveChildSystemCount);
        Assert.Equal(particles, session.LiveParticleCount);
        session.SetOwnerGameState(null);
        Assert.Equal(0, session.LiveChildSystemCount);
        session.SetOwnerGameState(active);
        Assert.Equal(children, session.LiveChildSystemCount);
        Assert.Equal(particles, session.LiveParticleCount);
    }

    private static BinTreeObject Skin(params BinTreeProperty[] properties) => new("Skin", "SkinCharacterDataProperties",
        properties.Append(new BinTreeStruct(H("skinMeshProperties"), H("SkinMeshDataProperties"),
            new BinTreeProperty[] { new BinTreeString(H("simpleSkin"), "mesh.skn") })).ToArray());

    private static VfxSystemDefinition System(uint hash) => new(hash, "persistent", "persistent", new[]
    {
        new VfxEmitterDefinition(Name: "glow", Rate: VfxCurveF.Const(6), ParticleLifetime: VfxCurveF.Const(2),
            EmitterLifetime: null, ParticleLinger: 0, TimeBeforeFirstEmission: 0, IsSingleParticle: false,
            Disabled: false, BlendMode: 1, BirthScale: VfxCurve3.Const(Vector3.One), ScaleOverLife: null,
            BirthColor: VfxCurve4.Const(Vector4.One), ColorOverLife: null, BirthVelocity: null, Acceleration: null,
            BirthRotationalVelocity: null, EmitterPosition: VfxCurve3.Const(Vector3.Zero), TexturePath: "",
            TexDiv: Vector2.One, NumFrames: 1, RandomStartFrame: false, IsMeshPrimitive: false,
            RenderState: VfxEmitterRenderState.Default)
    });
}
