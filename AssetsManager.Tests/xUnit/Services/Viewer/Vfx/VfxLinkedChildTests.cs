using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxLinkedChildTests(ITestOutputHelper output)
{
    [Fact]
    public void SharedParticleBinsKeepEachSkinsKeysAndLoadItsLinkedChildClosure()
    {
        string root = Path.Combine(Path.GetTempPath(),"AssetsManagerLinkedChild",Guid.NewGuid().ToString("N"),"Hero.wad.client");
        const string first = "Characters/Hero/Skins/Skin1/Particles/Parent";
        const string second = "Characters/Hero/Skins/Skin2/Particles/Parent";
        const uint key = 9;
        uint child1 = Fnv1a.HashLower("Effects/Child1"), child2 = Fnv1a.HashLower("Effects/Child2");
        try
        {
            string main = Write("data/characters/hero/particles.bin",new[] { Parent(first),Parent(second) });
            var owner = new BinTreeObject("Skin","SkinCharacterDataProperties",new BinTreeProperty[]
            {
                new BinTreeStruct(Fnv1a.HashLower("skinMeshProperties"),Fnv1a.HashLower("SkinMeshDataProperties"),
                    new BinTreeProperty[] { new BinTreeString(Fnv1a.HashLower("simpleSkin"),"assets/hero.skn") })
            });
            Write("data/characters/hero/skins/skin1.bin",new[] { Resolver(child1),owner },"data/targets.bin");
            Write("data/characters/hero/skins/skin2.bin",new[] { Resolver(child2) },"data/targets.bin");
            Write("data/targets.bin",new[] { new BinTreeObject("Effects/Child1","VfxSystemDefinitionData",Array.Empty<BinTreeProperty>()),
                new BinTreeObject("Effects/Child2","VfxSystemDefinitionData",Array.Empty<BinTreeProperty>()) },"data/characters/hero/particles.bin");
            using var service = new VfxLoadingService(binEntryPathResolver: hash => hash == Fnv1a.HashLower(first) ? first :
                hash == Fnv1a.HashLower(second) ? second : null);
            var bundle = service.Load(main,null);
            Assert.Equal(4,bundle.LoadedBins.Count);
            Assert.Equal(4,bundle.LoadedBins.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Empty(bundle.ResourceMap);
            Assert.Null(bundle.OwnerSceneContext);
            Assert.Equal(child1,bundle.Systems[Fnv1a.HashLower(first)].ResourceMap[key]);
            Assert.Equal(child2,bundle.Systems[Fnv1a.HashLower(second)].ResourceMap[key]);
            foreach (uint parent in new[] { Fnv1a.HashLower(first),Fnv1a.HashLower(second) })
            {
                var system = bundle.Systems[parent];
                var reference = Assert.Single(Assert.Single(system.Emitters).ChildParticleSet.Children);
                Assert.Equal(system.ResourceMap[key],VfxPlaybackGraphRuntime.ResolveSystem(reference,bundle.Systems,system.ResourceMap,bundle.ResourceMap).PathHash);
            }
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }

        string Write(string relative,BinTreeObject[] objects,params string[] dependencies)
        {
            string path = Path.Combine(root,relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var stream = File.Create(path); new BinTree(objects,dependencies).Write(stream); return path;
        }
        static BinTreeObject Parent(string path)
        {
            var id = new BinTreeStruct(0,Fnv1a.HashLower("VfxChildIdentifier"),new BinTreeProperty[] { new BinTreeHash(Fnv1a.HashLower("effectKey"),key) });
            var set = new BinTreeStruct(Fnv1a.HashLower("childParticleSetDefinition"),Fnv1a.HashLower("VfxChildParticleSetDefinitionData"),
                new BinTreeProperty[] { new BinTreeContainer(Fnv1a.HashLower("childrenIdentifiers"),BinPropertyType.Struct,new BinTreeProperty[] { id }) });
            var emitter = new BinTreeStruct(0,Fnv1a.HashLower("VfxEmitterDefinitionData"),new BinTreeProperty[] { set });
            return new(path,"VfxSystemDefinitionData",new BinTreeProperty[] { new BinTreeContainer(Fnv1a.HashLower("complexEmitterDefinitionData"),
                BinPropertyType.Struct,new BinTreeProperty[] { emitter }) });
        }
        static BinTreeObject Resolver(uint target) => new("Resources","ResourceResolver",new BinTreeProperty[]
        {
            new BinTreeMap(Fnv1a.HashLower("resourceMap"),BinPropertyType.Hash,BinPropertyType.ObjectLink,new[]
                { new KeyValuePair<BinTreeProperty,BinTreeProperty>(new BinTreeHash(0,key),new BinTreeObjectLink(0,target)) })
        });
    }

    [Theory]
    [InlineData("Characters/Aatrox/Skins/Skin11/Particles/Test", "data/characters/aatrox/skins/skin11.bin")]
    [InlineData("characters/Hero/skins/skin2/Test", "data/characters/hero/skins/skin2.bin")]
    [InlineData("Characters/Hero/Skins/Skin2", null)]
    [InlineData("Maps/Shipping/Map11/Particles/Test", null)]
    public void SkinBinsComeFromTheCanonicalObjectPath(string owner, string expected)
        => Assert.Equal(expected,VfxLinkedChildResolver.SkinBin(owner));

    [Theory]
    [InlineData(null,null,3u,3u)]
    [InlineData(null,2u,3u,2u)]
    [InlineData(1u,2u,3u,1u)]
    [InlineData(0u,2u,3u,0u)]
    [InlineData(null,0u,3u,0u)]
    public void ChildKeysKeepLocalThenOpenedThenSkinResolverPriority(uint? local, uint? opened, uint? skin, uint expected)
    {
        var system = Family()[1] with { ResourceMap = Map(local), Emitters = new[] { ParentEmitter(false) with
            { ChildParticleSet = Set(new VfxChildSystemReference("",0,9),false) } } };
        var resolved = VfxLinkedChildResolver.Resolve(system,new Dictionary<uint,VfxSystemDefinition>(),Map(opened),Map(skin));
        Assert.Equal(expected,resolved.ResourceMap[9]);
        Assert.False(resolved.ResourceMap.ContainsKey(10));
        static Dictionary<uint,uint> Map(uint? value) => value.HasValue ? new() { [9] = value.Value } : new();
    }

    [Fact]
    public void HostJointsAreFallbackForTheWholeRigTableAndUnbindOnReplay()
    {
        var systems = Family(true);
        var graph = Graph(systems);
        Func<string,Matrix4x4?> host = _ => Matrix4x4.CreateTranslation(500,0,0);
        graph.SetHostJointTransformProvider(host);
        graph.Update(.02f);
        Assert.Equal(500,graph.Runtimes[1].Emitters[0].BasePos.X);
        graph.SetJointTransformProvider(_ => Matrix4x4.CreateTranslation(10,0,0));
        graph.Reset(); graph.Update(.02f);
        Assert.Equal(10,graph.Runtimes[1].Emitters[0].BasePos.X);
        graph.SetJointTransformProvider(_ => null);
        graph.Reset(); graph.Update(.02f);
        Assert.Single(graph.Runtimes);
        graph.SetJointTransformProvider(null);
        graph.Reset(); graph.Update(.02f);
        Assert.Equal(500,graph.Runtimes[1].Emitters[0].BasePos.X);
        graph.SetHostJointTransformProvider(null);
        graph.Reset(); graph.Update(.02f);
        Assert.Single(graph.Runtimes);
    }

    [Fact]
    public void LinkedChildrenDoNotReopenAnAncestorSystem()
    {
        var systems = Family();
        systems[2] = systems[2] with { Emitters = new[] { ParentEmitter(false) with
            { ChildParticleSet = Set(new VfxChildSystemReference("",1,0),false) } } };
        var graph = Graph(systems);
        for (int step=0;step<10;step++) graph.Update(.02f);
        Assert.Equal(2,graph.Runtimes.Count);
    }

    [Fact]
    public void SpellHostBonesReplayAtTheCurrentTimeWithoutAttachingTheSpellRig()
    {
        string directory = Path.Combine(Path.GetTempPath(),"AssetsManagerHostBones",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var systems = Family(true);
            using var session = new VfxRenderSession();
            Vector3 origin = new(30,0,0);
            var step = new VfxSpellPlaybackStep("cast",systems[1],0,0,1,origin,origin,VfxSpellPlaybackMotion.Static,7);
            Assert.True(session.SetSpellSession(new[] { step },systems,new Dictionary<uint,uint>(),directory,1));
            session.Seek(.3);
            Assert.Single(session.Graphs[0].Runtimes);
            session.SetBoneTransformSampler((time,name,hash) => Matrix4x4.CreateTranslation(10+(float)time*10,0,0));
            Assert.Equal(.3,session.CurrentTime,6);
            Assert.Equal(origin,session.Graphs[0].Root.WorldTransform.Translation);
            var child = session.Graphs[0].Runtimes[1];
            Vector3 sampled = child.Emitters[0].BasePos;
            Assert.InRange(Vector3.Distance(origin,sampled),12.999f,13.001f);
            session.Seek(.6); session.Seek(.3);
            Assert.True(Vector3.Distance(sampled,session.Graphs[0].Runtimes[1].Emitters[0].BasePos)<.001f);
            session.SetBoneTransformSampler(null);
            Assert.Equal(.3,session.CurrentTime,6);
            Assert.Single(session.Graphs[0].Runtimes);
            session.UpdateBoneTransforms((name,hash)=>Matrix4x4.CreateTranslation(5,0,0));
            Assert.InRange(Vector3.Distance(origin,session.Graphs[0].Runtimes[1].Emitters[0].BasePos),4.999f,5.001f);
            session.UpdateBoneTransforms(null);
            Assert.Single(session.Graphs[0].Runtimes);
        }
        finally { Directory.Delete(directory); }
    }

    private static VfxChildParticleSetDefinition Set(VfxChildSystemReference child, bool bones)
        => new(new[] { child },false,VfxCurveF.Zero,VfxCurve3.Const(Vector3.Zero),0,bones ? new[] { "R_Hand" } : null);

    private static VfxEmitterDefinition ParentEmitter(bool bones) => VfxBeamSupportTests.Definition() with
        { Rate = VfxCurveF.Const(1), Beam = null, PrimitiveKind = VfxPrimitiveKind.CameraQuad,
            ChildParticleSet = Set(new VfxChildSystemReference("",2,0),bones) };

    private static Dictionary<uint,VfxSystemDefinition> Family(bool bones = false) => new()
    {
        [1] = new(1,"parent","parent",new[] { ParentEmitter(bones) }),
        [2] = new(2,"child","child",new[] { ParentEmitter(false) with { ChildParticleSet = null } })
    };

    private static VfxPlaybackGraphRuntime Graph(Dictionary<uint,VfxSystemDefinition> systems)
        => new(systems[1],Matrix4x4.Identity,7,systems,new Dictionary<uint,uint>(),
            (definition,transform,seed) => {var runtime = new VfxPlaybackRuntime(seed); runtime.SetSystem(definition,transform); return runtime; });

    [Fact]
    public void ExtractedChampionChildKeysResolveThroughTheirOwningSkins()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Aatrox.wad.client");
        string hashes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AssetsManager", "hashes", "hashes.binentries.txt");
        if (!Directory.Exists(root) || !File.Exists(hashes)) return;
        string primary = Path.Combine(root, "data/characters/aatrox/aatrox_multi_skins_skin11_skins_skin12_skins_skin13_skins_skin14_skins_skin15_skins_skin16_skins_skin17_skins_skin18_skins_skin19.bin");
        Assert.True(File.Exists(primary));
        var names = new Dictionary<uint, string>();
        foreach (string line in File.ReadLines(hashes))
        {
            int space = line.IndexOf(' ');
            if (space > 0 && line[(space + 1)..].StartsWith("Characters/Aatrox/", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(line.AsSpan(0,space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                names.TryAdd(hash,line[(space + 1)..]);
        }
        using var loader = new VfxLoadingService(binEntryPathResolver: hash => names.GetValueOrDefault(hash));
        var original = VfxGraphParser.ParseDocument(File.ReadAllBytes(primary));
        var expected = new List<(uint Parent, uint Key, uint Target, string Owner)>();
        foreach (var system in original.Systems.Values)
        {
            if (!names.TryGetValue(system.PathHash,out string name)) continue;
            string[] parts = name.Split('/');
            if (parts.Length < 5 || !parts[2].Equals("skins",StringComparison.OrdinalIgnoreCase)) continue;
            string skin = Path.Combine(root,$"data/characters/{parts[1].ToLowerInvariant()}/skins/{parts[3].ToLowerInvariant()}.bin");
            if (!File.Exists(skin)) continue;
            var skinDocument = VfxGraphParser.ParseDocument(File.ReadAllBytes(skin));
            foreach (var emitter in system.Emitters)
            foreach (var child in emitter.ChildParticleSet?.Children ?? Array.Empty<VfxChildSystemReference>())
                if (child is { EffectKey: not 0 } && !system.ResourceMap.ContainsKey(child.EffectKey) &&
                    skinDocument.ResourceMap.TryGetValue(child.EffectKey,out uint target) && target != 0)
                    expected.Add((system.PathHash,child.EffectKey,target,name));
        }
        Assert.NotEmpty(expected);
        var bundle = loader.Load(primary,null);
        output.WriteLine($"Primary: {primary}; skin-scoped child keys: {expected.Count}; loaded BINs: {bundle.LoadedBins.Count}");
        foreach (var sample in expected)
        {
            output.WriteLine($"{sample.Owner}: key={sample.Key:x8} -> system={sample.Target:x8}");
            var system = bundle.Systems[sample.Parent];
            var resolved = VfxPlaybackGraphRuntime.ResolveSystem(new VfxChildSystemReference("",0,sample.Key),
                bundle.Systems,system.ResourceMap,bundle.ResourceMap);
            Assert.True(resolved?.PathHash == sample.Target,
                $"{sample.Owner}: key {sample.Key:x8} should resolve to {sample.Target:x8}, got {resolved?.PathHash:x8}.");
        }
    }
}
