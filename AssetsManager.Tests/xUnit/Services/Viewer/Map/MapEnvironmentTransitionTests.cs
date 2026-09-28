using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapEnvironmentTransitionTests
    {
        [Fact]
        public void TransitionPermutationReplacesOnlyTheEnvTransitionDefine()
        {
            GameMaterialProgram program = Program(
                new GameMaterialDefine("ENV_TRANSITION", "0", GameMaterialDefineSource.Pass),
                new GameMaterialDefine("FEATURE_MASKED", "1", GameMaterialDefineSource.Pass));

            GameMaterialProgram transitioning = MapEnvironmentTransition.Transitioning(program);

            Assert.True(MapEnvironmentTransition.Supports(program));
            GameMaterialDefine[] defines = transitioning.Passes.Single().Defines.ToArray();
            Assert.Equal("1", defines.Single(define => define.Name == "ENV_TRANSITION").Value);
            Assert.Equal("1", defines.Single(define => define.Name == "FEATURE_MASKED").Value);
            Assert.Equal("0", program.Passes.Single().Defines.Single(define => define.Name == "ENV_TRANSITION").Value);
        }

        [Fact]
        public void ProgramsWithoutTheDefineAreNotTransitioned()
        {
            Assert.False(MapEnvironmentTransition.Supports(Program(new GameMaterialDefine("FEATURE_MASKED", "1", GameMaterialDefineSource.Pass))));
            Assert.False(MapEnvironmentTransition.Supports(null));
        }

        [Theory]
        [InlineData(1000, 1000, 0f)]
        [InlineData(5000, 1000, 0.5f)]
        [InlineData(9000, 1000, 1f)]
        [InlineData(20000, 1000, 1f)]
        public void ProgressRunsFromZeroToOneOverTheTransitionTime(long now, long start, float expected)
        {
            Assert.Equal(expected, MapEnvironmentTransition.Progress(start, 8000, now), 5);
            Assert.Equal(1f, MapEnvironmentTransition.Progress(start, 0, now));
        }

        private static GameMaterialProgram Program(params GameMaterialDefine[] defines) =>
            new(
                GameMaterialKind.StaticMesh,
                false,
                new[]
                {
                    new GameMaterialPass(
                        1,
                        "Shaders/StaticMesh/Env_Elemental",
                        defines,
                        Array.Empty<KeyValuePair<string, bool>>(),
                        Array.Empty<GameMaterialTexture>(),
                        Array.Empty<GameMaterialParameter>(),
                        GameMaterialPassState.Default)
                });
    }
}
