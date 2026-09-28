using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    /// <summary>
    /// Map-state switches reuse what both states share, rebind skins whose placements changed,
    /// retain what leaves for a short window and release every owner exactly once.
    /// </summary>
    public class MapStateReconciliationTests
    {
        private const string Turret = "Characters/Turret/Skins/Skin0";
        private const string Krug = "Characters/Krug/Skins/Skin0";

        [Fact]
        public void KeptSkinWithSamePlacementsStaysTheSameGroup()
        {
            MapCharacterData order = Character(Turret, 1);
            MapCharacterRuntimeGroup turret = Group(order);
            using MapSceneRuntime runtime = Runtime(turret);
            long generation = runtime.CharacterGeneration;

            Assert.True(runtime.TryApplyCharacterPlan(Plan(runtime, Reuse(turret, order))));

            Assert.Same(turret, Assert.Single(runtime.CharacterGroups));
            Assert.False(turret.IsDisposed);
            Assert.True(runtime.CharacterGeneration > generation);
        }

        [Fact]
        public void KeptSkinWithOtherPlacementsIsReboundWithoutReleasingItsOwners()
        {
            MapCharacterData order = Character(Turret, 1);
            MapCharacterData chaos = Character(Turret, 2);
            MapCharacterRuntimeGroup turret = Group(order);
            using MapSceneRuntime runtime = Runtime(turret);

            Assert.True(runtime.TryApplyCharacterPlan(Plan(runtime, Reuse(turret, order, chaos))));

            MapCharacterRuntimeGroup rebound = Assert.Single(runtime.CharacterGroups);
            Assert.NotSame(turret, rebound);
            Assert.Same(turret.Asset, rebound.Asset);
            Assert.Same(turret.Animation, rebound.Animation);
            Assert.Equal(new[] { order, chaos }, rebound.Placements);
            Assert.True(turret.IsDisposed);
            Assert.False(rebound.Animation.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => turret.Rebind(new[] { order }));
        }

        [Fact]
        public void LeavingSkinIsRetainedThenReleasedWhenItsLifetimeEnds()
        {
            var clock = TimeSpan.Zero;
            MapCharacterRuntimeGroup turret = Group(Character(Turret, 1));
            MapCharacterRuntimeGroup krug = Group(Character(Krug, 2));
            using MapSceneRuntime runtime = Runtime(turret, krug);
            runtime.Clock = () => clock;

            Assert.True(runtime.TryApplyCharacterPlan(Plan(runtime, Reuse(turret, turret.Placements[0]))));

            Assert.Same(turret, Assert.Single(runtime.CharacterGroups));
            Assert.Equal(1, runtime.RetainedCharacterGroupCount);
            Assert.False(krug.IsDisposed);
            Assert.Same(krug, runtime.CharacterReuseCandidates()[Krug]);

            clock = MapSceneRuntime.RetainedGroupLifetime - TimeSpan.FromSeconds(1);
            runtime.SweepRetainedGroups();
            Assert.False(krug.IsDisposed);

            long generation = runtime.CharacterGeneration;
            clock = MapSceneRuntime.RetainedGroupLifetime;
            runtime.SweepRetainedGroups();
            Assert.True(krug.IsDisposed);
            Assert.True(krug.Animation.IsDisposed);
            Assert.Equal(0, runtime.RetainedCharacterGroupCount);
            Assert.True(runtime.CharacterGeneration > generation);
            Assert.False(turret.IsDisposed);
        }

        [Fact]
        public void RetainedSkinReturnsWithoutLoading()
        {
            MapCharacterData rock = Character(Krug, 2);
            MapCharacterRuntimeGroup turret = Group(Character(Turret, 1));
            MapCharacterRuntimeGroup krug = Group(rock);
            using MapSceneRuntime runtime = Runtime(turret, krug);
            Assert.True(runtime.TryApplyCharacterPlan(Plan(runtime, Reuse(turret, turret.Placements[0]))));

            Assert.True(runtime.TryApplyCharacterPlan(Plan(
                runtime,
                Reuse(turret, turret.Placements[0]),
                Reuse(krug, rock))));

            Assert.Equal(new[] { turret, krug }, runtime.CharacterGroups);
            Assert.Equal(0, runtime.RetainedCharacterGroupCount);
            Assert.False(krug.IsDisposed);
        }

        [Fact]
        public void StalePlanIsRejectedAndReleasesOnlyWhatItLoaded()
        {
            MapCharacterRuntimeGroup turret = Group(Character(Turret, 1));
            using MapSceneRuntime runtime = Runtime(turret);
            MapCharacterRuntimeGroup loaded = Group(Character(Krug, 2));
            MapCharacterPlan stale = Plan(runtime, Load(loaded));
            runtime.SetCharacterGroups(new[] { turret });
            runtime.SetCharacterGroups(Array.Empty<MapCharacterRuntimeGroup>());

            Assert.False(runtime.TryApplyCharacterPlan(stale));
            stale.Dispose();

            Assert.True(loaded.IsDisposed);
            Assert.Empty(runtime.CharacterGroups);
        }

        [Fact]
        public void AdoptedPlanNoLongerOwnsItsLoadedGroups()
        {
            using MapSceneRuntime runtime = Runtime();
            MapCharacterRuntimeGroup loaded = Group(Character(Krug, 2));
            MapCharacterPlan plan = Plan(runtime, Load(loaded));

            Assert.True(runtime.TryApplyCharacterPlan(plan));
            plan.Dispose();

            Assert.False(loaded.IsDisposed);
            Assert.Same(loaded, Assert.Single(runtime.CharacterGroups));
        }

        [Fact]
        public void ReplacingGroupsKeepsOnesThatStayAndDisposingTheRuntimeReleasesRetained()
        {
            MapCharacterRuntimeGroup turret = Group(Character(Turret, 1));
            MapCharacterRuntimeGroup krug = Group(Character(Krug, 2));
            var runtime = Runtime(turret, krug);

            runtime.SetCharacterGroups(new[] { turret });
            Assert.False(turret.IsDisposed);
            Assert.True(krug.IsDisposed);

            MapCharacterRuntimeGroup golem = Group(Character(Krug, 3));
            runtime.SetCharacterGroups(new[] { turret, golem });
            Assert.True(runtime.TryApplyCharacterPlan(Plan(runtime, Reuse(turret, turret.Placements[0]))));
            Assert.Equal(1, runtime.RetainedCharacterGroupCount);

            runtime.Dispose();
            Assert.True(turret.IsDisposed);
            Assert.True(golem.IsDisposed);
        }

        [Fact]
        public async Task ReboundGroupRejectsLateVfxOverlayWork()
        {
            MapCharacterRuntimeGroup turret = Group(Character(Turret, 1));
            MapCharacterRuntimeGroup rebound = turret.Rebind(turret.Placements);
            int created = 0;

            await Assert.ThrowsAsync<ObjectDisposedException>(() => turret.EnsureVfxResourcesAsync(
                _ =>
                {
                    created++;
                    return Task.FromResult<VfxSceneResourceContext>(null);
                },
                null,
                CancellationToken.None));

            Assert.Equal(0, created);
            rebound.Dispose();
            Assert.True(rebound.Animation.IsDisposed);
        }

        [Fact]
        public async Task AnimationRuntimeReportsWhichNamesArePrepared()
        {
            using var animation = new MapCharacterAnimationRuntime(null, null);
            Assert.True(animation.IsPrepared(Array.Empty<string>()));
            Assert.False(animation.IsPrepared(new[] { "Idle1" }));

            await animation.PrepareAsync(
                new MapCharacterAssetData(null, null, null, null, null, null),
                new[] { "Idle1" },
                projectRoot: null);

            Assert.True(animation.IsPrepared(new[] { "idle1", null }));
            Assert.False(animation.IsPrepared(new[] { "Spawn" }));
        }

        [Fact]
        public void ParticlePlanKeepsContinuingRuntimesAndAdoptsItsOverlay()
        {
            MapParticleRuntime near = Particle("Near", 1);
            MapParticleRuntime far = Particle("Far", 2);
            using MapSceneRuntime runtime = Runtime();
            runtime.SetParticles(new MapParticleSceneRuntime(new[] { near, far }));
            var overlay = new Owner();
            MapParticleRuntime added = Particle("Added", 3);

            var plan = new MapParticlePlan(runtime.ParticleGeneration, Catalog(), new[] { near, added }, overlay, keptCount: 1);
            Assert.True(runtime.TryApplyParticlePlan(plan));
            plan.Dispose();

            Assert.Equal(new[] { near, added }, runtime.Particles.Runtimes);
            Assert.False(overlay.Disposed);
            Assert.Equal(1, plan.CreatedCount);

            runtime.Dispose();
            Assert.True(overlay.Disposed);
        }

        [Fact]
        public void StaleParticlePlanReleasesTheOverlayItCreated()
        {
            using MapSceneRuntime runtime = Runtime();
            var overlay = new Owner();
            var stale = new MapParticlePlan(runtime.ParticleGeneration, Catalog(), Array.Empty<MapParticleRuntime>(), overlay, 0);
            runtime.SetParticles(new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));

            Assert.False(runtime.TryApplyParticlePlan(stale));
            stale.Dispose();

            Assert.True(overlay.Disposed);
        }

        [Fact]
        public void ParticleRuntimeRefusesASecondOverlay()
        {
            using var particles = new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>(), new Owner());

            Assert.Throws<InvalidOperationException>(() =>
                particles.Reconcile(Catalog(), Array.Empty<MapParticleRuntime>(), new Owner()));
        }

        private sealed class Owner : IDisposable
        {
            public bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }

        private static MapSceneRuntime Runtime(params MapCharacterRuntimeGroup[] groups) =>
            new(Scene(), groups, new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));

        private static MapCharacterRuntimeGroup Group(params MapCharacterData[] placements) =>
            new(
                new MapCharacterAssetData(null, null, null, null, null, null),
                new MapCharacterAnimationRuntime(null, null),
                placements);

        private static MapCharacterPlan.Entry Reuse(MapCharacterRuntimeGroup group, params MapCharacterData[] placements) =>
            new(group.Skin, placements, group, null);

        private static MapCharacterPlan.Entry Load(MapCharacterRuntimeGroup group) =>
            new(group.Skin, group.Placements, null, group);

        private static MapCharacterPlan Plan(MapSceneRuntime runtime, params MapCharacterPlan.Entry[] entries) =>
            new(runtime.CharacterGeneration, entries);

        private static MapCharacterData Character(string skin, uint key) =>
            new(Placeable(key, MapCharacterParser.GdsMapObjectClass), skin, null, null);

        private static MapParticleRuntime Particle(string name, uint key)
        {
            const uint systemHash = 0x30000001;
            var particle = new MapParticleData(Placeable(key, MapParticleParser.MapParticleClass, name), systemHash, false, false);
            var system = new VfxSystemDefinition(systemHash, "MapVfx", "Maps/Test/MapVfx", Array.Empty<VfxEmitterDefinition>());
            return MapParticleRuntime.Create(
                particle,
                system,
                new Dictionary<uint, VfxSystemDefinition> { [systemHash] = system },
                new Dictionary<uint, uint>());
        }

        private static MapPlaceableData Placeable(uint key, uint type, string name = "Placement") =>
            new(0x10000001, key, type, name, Matrix4x4.Identity, MapPlaceableData.EveryLayer, null,
                new Dictionary<uint, BinTreeProperty>());

        private static MapParticleSystemCatalog Catalog() =>
            new(new Dictionary<uint, VfxSystemDefinition>(), new Dictionary<uint, uint>(), Array.Empty<MapParticleSystemGroupData>());

        private static MapSceneData Scene()
        {
            MapPath path = MapPath.FromEntryPath("maps/mapgeometry/map11/base_srx");
            var source = new MapSceneSource(path, @"C:\map\base_srx.mapgeo", @"C:\map");
            var geometry = new MapGeometryData(
                Array.Empty<Vector3>(),
                Array.Empty<Vector3>(),
                Array.Empty<Vector2>(),
                null,
                Array.Empty<uint>(),
                Array.Empty<MapGeometryMeshData>(),
                Array.Empty<MapGeometrySubmeshData>(),
                Array.Empty<string>());
            return new MapSceneData(
                source,
                new MapSceneAssets(source, null, null),
                geometry,
                null,
                Array.Empty<MapMaterialDefinition>(),
                new Dictionary<string, MapTextureImage>(),
                Array.Empty<MapPlaceableChunkData>(),
                Array.Empty<MapCharacterData>(),
                Array.Empty<MapParticleData>(),
                Catalog());
        }
    }
}
