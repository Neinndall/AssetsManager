using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapCharacterRuntimeGroupTests
    {
        [Fact]
        public void GroupByAnimationPreservesAuthoredOrderAndCachesWorldTransforms()
        {
            MapCharacterData first = Character(1, "Run", new Vector3(10f, 20f, 30f));
            MapCharacterData second = Character(2, "run", new Vector3(40f, 50f, 60f));
            MapCharacterData bind = Character(3, null, new Vector3(70f, 80f, 90f));

            var groups = MapCharacterRuntimeGroup.GroupByAnimation(
                new[] { first, second, bind },
                2f);

            Assert.Equal(2, groups.Count);
            Assert.Equal("Run", groups[0].Animation);
            Assert.Equal(2, groups[0].Placements.Count);
            Assert.Equal(string.Empty, groups[1].Animation);
            Assert.Single(groups[1].Placements);

            MapCharacterRuntimePlacement cached = groups[0].Placements[0];
            Assert.Same(first, cached.Placement);
            Assert.Equal(new Vector3(-10f, 20f, 30f), cached.Position);
            Assert.Equal(-2f, cached.World.M11);
            Assert.Equal(2f, cached.World.M22);
            Assert.Equal(2f, cached.World.M33);
        }

        [Fact]
        public void DisposeCompletedCharacterLoadsReleasesOnlyOwnedCompletedGroups()
        {
            var animation = new MapCharacterAnimationRuntime(null, null);
            var group = new MapCharacterRuntimeGroup(
                null,
                animation,
                Array.Empty<MapCharacterData>());
            Task<MapCharacterRuntimeGroup> completed = Task.FromResult(group);
            Task<MapCharacterRuntimeGroup> cancelled = Task.FromCanceled<MapCharacterRuntimeGroup>(
                new CancellationToken(canceled: true));

            MapSceneRuntimeFactory.DisposeCompletedCharacterLoads(new[] { completed, cancelled });

            Assert.Throws<ObjectDisposedException>(() => animation.Evaluate(null, null, 0f));
        }

        private static MapCharacterData Character(uint key, string animation, Vector3 translation)
        {
            return new MapCharacterData(
                new MapPlaceableData(
                    0x10,
                    key,
                    0,
                    $"Character{key}",
                    Matrix4x4.CreateTranslation(translation),
                    MapPlaceableData.EveryLayer,
                    null,
                    null),
                "Characters/Test/Skins/Skin0",
                100,
                animation);
        }
    }
}
