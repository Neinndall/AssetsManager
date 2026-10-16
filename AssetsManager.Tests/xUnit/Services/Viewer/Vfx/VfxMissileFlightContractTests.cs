using System;
using System.Numerics;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using Xunit;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

[Collection("VFX missile files")]
public sealed class VfxMissileFlightContractTests
{
    private static VfxSpellMissilePreview Missile(VfxSpellMissileMovementKind kind) =>
        new(kind, 1000, 1, null, null, null, null, null);

    [Fact]
    public void RepeatedRigSamplesReuseTheCompiledFlight()
    {
        var rig = VfxRigSettings.ForPreset(VfxRigPreset.Missile) with { Missile = Missile(VfxSpellMissileMovementKind.FixedSpeed) };
        VfxRigMotion.OriginAt(rig, .1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 16; i++) VfxRigMotion.OriginAt(rig, i / 60d);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 65536);
    }

    [Fact]
    public void GroundTargetOmitsTargetHeightAugmentUnlessEndAtLaunchHeightIsEnabled()
    {
        var missile = Missile(VfxSpellMissileMovementKind.FixedSpeed) with
        { HeightOffsetAtEnd = false, TargetHeight = 30, InitialTargetHeight = 10 };
        var track = VfxSpellFlightPath.Compile(missile, new(0,100,0), new(1000,0,0), true);
        Assert.Equal(10, track.End.Y);
    }

    [Fact]
    public void CircleRigUsesTheFullFlightRangeAsItsRadius()
    {
        var rig = VfxRigSettings.ForPreset(VfxRigPreset.Missile) with
        { Missile = Missile(VfxSpellMissileMovementKind.Circle) with { AngularVelocity = 1, Lifetime = 1 } };
        var origin = VfxRigMotion.OriginAt(rig, 0);
        Assert.Equal(rig.FlightRange, MathF.Sqrt(origin.X * origin.X + origin.Z * origin.Z));
    }

    [Fact]
    public void AFiniteRadialOnlyCircleCanBePreviewedAsAnAbility()
    {
        var missile = Missile(VfxSpellMissileMovementKind.Circle) with { RadialVelocity = 100, Lifetime = 1 };
        Assert.NotNull(VfxSpellPreviewComposer.CompileFlightPath(missile, new(1000,0,0), Vector3.Zero));
    }

    [Fact]
    public void AFixedTimeRigWithOmittedDurationDoesNotFallBackToAnUnrelatedStraightFlight()
    {
        var rig = VfxRigSettings.ForPreset(VfxRigPreset.Missile) with
        { Missile = Missile(VfxSpellMissileMovementKind.FixedTime) with { Duration = null } };
        Assert.NotNull(VfxRigMotion.CompileRigTrack(rig));
    }

    [Fact]
    public void UnmatchedTrailUsesAStraightMissileInsteadOfOrbit()
    {
        var emitter = VfxQualityFlexTests.Definition() with
        {
            PrimitiveKind = VfxPrimitiveKind.CameraTrail, IsSingleParticle = false,
            BirthVelocity = null, Acceleration = null, BirthAcceleration = null,
            Trail = new(VfxCurve3.Const(Vector3.One),0,1,0,0)
        };
        var system = new VfxSystemDefinition(1,"trail","",[emitter]);
        var rig = VfxSystemRigResolver.ResolveSettings(system,new VfxLoadingService.Bundle()).Settings;
        Assert.Equal(VfxRigMotionKind.Path,rig.MotionKind);
    }

    [Theory]
    [InlineData("mMissileEffectKey", false)]
    [InlineData("mMissileEffectPlayerKey", false)]
    [InlineData("mMissileEffectEnemyKey", false)]
    [InlineData("mMissileEffectKey", true)]
    [InlineData("mMissileEffectPlayerKey", true)]
    [InlineData("mMissileEffectEnemyKey", true)]
    public void OpeningASystemFindsTheCharacterMissileSpellsWithoutAnExplicitBinDependency(string keyField, bool isolated)
    {
        string root = Path.Combine(Path.GetTempPath(), "AssetsManagerReview708", Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root,"data","characters","hero");
        Directory.CreateDirectory(Path.Combine(folder,"skins"));
        void Write(string path, params BinTreeObject[] objects)
        {
            using var stream=File.Create(path);
            new BinTree(objects,Array.Empty<string>()).Write(stream);
        }
        try
        {
            string skin=Path.Combine(folder,"skins","skin0.bin");
            var effect=new BinTreeObject("Characters/Hero/Skins/Skin0/Particles/Q","VfxSystemDefinitionData",Array.Empty<BinTreeProperty>());
            uint key=Fnv1a.HashLower("HeroQ");
            var resolver=new BinTreeObject("Characters/Hero/Skins/Skin0/Resolver","ResourceResolver",new BinTreeProperty[]
            {
                new BinTreeMap(Fnv1a.HashLower("resourceMap"),BinPropertyType.Hash,BinPropertyType.ObjectLink,
                    new[]{new System.Collections.Generic.KeyValuePair<BinTreeProperty,BinTreeProperty>(new BinTreeHash(0,key),new BinTreeObjectLink(0,effect.PathHash))})
            });
            Write(skin,effect,resolver);
            string opened = isolated ? Path.Combine(folder,"q.bin") : skin;
            if (isolated) Write(opened,effect);
            var spell=new BinTreeObject("Characters/Hero/Spells/Q","SpellObject",new BinTreeProperty[]
            {
                new BinTreeStruct(Fnv1a.HashLower("mSpell"),Fnv1a.HashLower("SpellDataResource"),new BinTreeProperty[]
                {
                    new BinTreeHash(Fnv1a.HashLower(keyField),key),
                    new BinTreeStruct(Fnv1a.HashLower("mMissileSpec"),Fnv1a.HashLower("MissileSpecification"),new BinTreeProperty[]
                    {
                        new BinTreeStruct(Fnv1a.HashLower("movementComponent"),Fnv1a.HashLower("FixedSpeedMovement"),new BinTreeProperty[]{new BinTreeF32(Fnv1a.HashLower("mSpeed"),2400)})
                    })
                })
            });
            Write(Path.Combine(folder,"hero.bin"),spell);
            using var service=new VfxLoadingService(binEntryPathResolver: hash => hash == effect.PathHash ? "Characters/Hero/Skins/Skin0/Particles/Q" : null);
            var bundle=service.Load(opened,null);
            var selected = VfxSystemRigResolver.ResolveSettings(bundle.Systems[effect.PathHash], bundle).Settings;
            Assert.Equal(2400, selected.Missile?.Speed);
            Assert.Empty(bundle.SpellPreviews);
            Assert.Single(bundle.LoadedBins);
        }
        finally { Directory.Delete(root,true); }
    }
    public static IEnumerable<object[]> ReferenceFlights()
    {
        using var stream=typeof(VfxMissileFlightContractTests).Assembly.GetManifestResourceStream(
            "AssetsManager.Tests.xUnit.Services.Viewer.Vfx.Fixtures.MissileFlightReference.json");
        using var json=JsonDocument.Parse(stream);
        foreach(var item in json.RootElement.GetProperty("Cases").EnumerateArray())
            yield return [item.GetProperty("Name").GetString(),item.GetRawText()];
    }

    [Theory]
    [InlineData(VfxSpellMissileMovementKind.FixedSpeed)]
    [InlineData(VfxSpellMissileMovementKind.FixedSpeedSpline)]
    [InlineData(VfxSpellMissileMovementKind.FixedTime)]
    [InlineData(VfxSpellMissileMovementKind.Accelerating)]
    [InlineData(VfxSpellMissileMovementKind.Circle)]
    public void SpeedTuningPreservesTheAuthoredMovement(VfxSpellMissileMovementKind kind)
    {
        var rig=VfxRigSettings.ForPreset(VfxRigPreset.Missile) with { Missile=Missile(kind) };
        var tuned=rig.WithFlightSpeed(2400);
        Assert.Equal(kind,tuned.Missile.MovementKind);
        if(kind is VfxSpellMissileMovementKind.FixedSpeed or VfxSpellMissileMovementKind.FixedSpeedSpline)
            Assert.Equal(2400,tuned.Missile.Speed);
        else Assert.Equal(rig,tuned);
    }

    [Fact]
    public void CachedTracksFollowHeightRangeAndMovementChanges()
    {
        var rig=VfxRigSettings.ForPreset(VfxRigPreset.Missile) with { Missile=Missile(VfxSpellMissileMovementKind.FixedSpeed) };
        var first=VfxRigMotion.CompileRigTrack(rig);
        Assert.Same(first,VfxRigMotion.CompileRigTrack(rig));
        Assert.Equal(200,VfxRigMotion.OriginAt(rig with {Height=200},0).Y);
        Assert.Equal(-1000,VfxRigMotion.OriginAt(rig with {FlightRange=2000},0).X);
        Assert.NotSame(first,VfxRigMotion.CompileRigTrack(rig.WithFlightSpeed(2400)));
    }

    [Theory,MemberData(nameof(ReferenceFlights))]
    public void FlightMatchesPinnedReferenceSamples(string name,string raw)
    {
        using var json=JsonDocument.Parse(raw);
        var data=json.RootElement;
        var options=new JsonSerializerOptions{IncludeFields=true};
        options.Converters.Add(new JsonStringEnumConverter());
        var missile=JsonSerializer.Deserialize<VfxSpellMissilePreview>(data.GetProperty("Input").GetRawText(),options);
        var track=VfxSpellFlightPath.Compile(missile,new(0,100,0),new(900,0,400),true);
        Assert.NotNull(track);
        if(data.GetProperty("Arrival").ValueKind==JsonValueKind.Null) Assert.Null(track.Arrival);
        else Assert.InRange(Math.Abs(track.Arrival.Value-data.GetProperty("Arrival").GetDouble()),0,VfxSpellFlightPath.FlightStep+1e-6);
        foreach(var sample in data.GetProperty("Samples").EnumerateArray())
        {
            double time=sample.GetProperty("Time").GetDouble();
            Vector3 Read(string key){var v=sample.GetProperty(key);return new(v[0].GetSingle(),v[1].GetSingle(),v[2].GetSingle());}
            var expected=Read("Position"); var actual=track.PositionAt(time);
            float length=expected.Length();
            // Integration uses floats; extreme spline/gravity heights also need a few ULPs.
            float tolerance=.1f+4f*(MathF.BitIncrement(length)-length);
            Assert.True(Vector3.Distance(expected,actual)<tolerance,$"{name} at {time}: expected position {expected}, got {actual}, tolerance {tolerance}");
            expected=Read("Direction");actual=track.DirectionAt(time);
            Assert.True(Vector3.Distance(expected,actual)<.01f,$"{name} at {time}: expected direction {expected}, got {actual}");
        }
    }

}

[CollectionDefinition("VFX missile files", DisableParallelization=true)]
public sealed class VfxMissileFileCollection { }
