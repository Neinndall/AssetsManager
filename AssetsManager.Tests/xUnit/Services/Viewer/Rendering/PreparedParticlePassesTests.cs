using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Rendering;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class PreparedParticlePassesTests
    {
        [Fact]
        public void GroundPassesCloseBeforeCharactersAndDepthIsCapturedFromTheScenePrepass()
        {
            var log = new List<string>();
            var passes = new IPreparedParticlePass[]
            {
                new RecordingPass("map", log, ground: true),
                new RecordingPass("actor", log, ground: true)
            };

            PreparedParticlePasses.Render(passes, new List<IDisposable>(),
                () => log.Add("characters"), capture =>
                {
                    log.Add("depth:begin");
                    capture();
                    log.Add("depth:end");
                });

            Assert.Equal(new[]
            {
                "depth:begin", "map:depth", "actor:depth", "depth:end",
                "map:begin", "map:ground", "map:end",
                "actor:begin", "actor:ground", "actor:end", "characters",
                "map:begin", "actor:begin",
                "map:earlyCapture", "actor:earlyCapture", "map:early", "actor:early",
                "map:color", "actor:color", "map:capture", "actor:capture",
                "map:distortion", "actor:distortion", "map:post", "actor:post",
                "actor:end", "map:end"
            }, log);
        }

        [Fact]
        public void CharactersStillDrawWithoutParticles()
        {
            int drawn = 0;
            PreparedParticlePasses.Render(Array.Empty<IPreparedParticlePass>(), new List<IDisposable>(),
                () => drawn++, _ => throw new InvalidOperationException());
            Assert.Equal(1, drawn);
        }

        [Fact]
        public void FramesWithoutGroundSkipTheExtraDepthRender()
        {
            var log = new List<string>();
            PreparedParticlePasses.Render(new[] { new RecordingPass("actor", log) }, new List<IDisposable>(),
                () => log.Add("characters"), _ => throw new InvalidOperationException());
            Assert.Equal("characters", log[0]);
            Assert.DoesNotContain("actor:ground", log);
        }

        [Fact]
        public void EarlyDistortionKeepsGroundInTheFollowingColorPhase()
        {
            var log = new List<string>();
            var passes = new[] { new RecordingPass("actor", log, ground: true, early: true) };
            PreparedParticlePasses.Render(passes, new List<IDisposable>(),
                () => log.Add("characters"), _ => throw new InvalidOperationException());
            Assert.True(log.IndexOf("characters") < log.IndexOf("actor:early"));
            Assert.True(log.IndexOf("actor:early") < log.IndexOf("actor:ground"));
            Assert.True(log.IndexOf("actor:ground") < log.IndexOf("actor:color"));
            Assert.Single(log, item => item == "actor:ground");
        }

        [Fact]
        public void EveryOwnerCompletesEachPhaseBeforeTheNextPhaseStarts()
        {
            var log = new List<string>();
            var passes = new List<IPreparedParticlePass> { new RecordingPass("map", log), new RecordingPass("actor", log) };

            PreparedParticlePasses.Render(passes, new List<IDisposable>());

            Assert.Equal(
                new[]
                {
                    "map:begin", "actor:begin",
                    "map:depth", "actor:depth",
                    "map:earlyCapture", "actor:earlyCapture",
                    "map:early", "actor:early",
                    "map:color", "actor:color",
                    "map:capture", "actor:capture",
                    "map:distortion", "actor:distortion",
                    "map:post", "actor:post",
                    "actor:end", "map:end"
                },
                log);
        }

        [Fact]
        public void BatchesCloseEvenWhenAPhaseFails()
        {
            var log = new List<string>();
            var batches = new List<IDisposable>();
            var passes = new List<IPreparedParticlePass>
            {
                new RecordingPass("map", log),
                new RecordingPass("actor", log, failOnColor: true)
            };

            Assert.Throws<InvalidOperationException>(() => PreparedParticlePasses.Render(passes, batches));

            Assert.Contains("map:end", log);
            Assert.Contains("actor:end", log);
            Assert.Empty(batches);
        }

        private sealed class RecordingPass : IPreparedParticlePass
        {
            private readonly string _name;
            private readonly List<string> _log;
            private readonly bool _failOnColor;
            public bool HasPreparedGroundPass { get; }
            public bool HasPreparedEarlyDistortion { get; }
            public bool NeedsPreparedSceneCapture => true;

            public RecordingPass(string name, List<string> log, bool failOnColor = false, bool ground = false, bool early = false)
            {
                _name = name;
                _log = log;
                _failOnColor = failOnColor;
                HasPreparedGroundPass = ground;
                HasPreparedEarlyDistortion = early;
            }

            public IDisposable BeginPreparedRenderBatch()
            {
                _log.Add($"{_name}:begin");
                return new Scope(() => _log.Add($"{_name}:end"));
            }

            public void RenderPreparedColorPass()
            {
                _log.Add($"{_name}:color");
                if (_failOnColor) throw new InvalidOperationException();
            }

            public void CapturePreparedDistortionFrame() => _log.Add($"{_name}:capture");
            public void CapturePreparedSceneInputs() => _log.Add($"{_name}:depth");
            public void RenderPreparedGroundPass() => _log.Add($"{_name}:ground");
            public void RenderPreparedDistortionPass() => _log.Add($"{_name}:distortion");
            public void CapturePreparedEarlyDistortionFrame() => _log.Add($"{_name}:earlyCapture");
            public void RenderPreparedEarlyDistortionPass() => _log.Add($"{_name}:early");
            public void RenderPreparedPostColorPass() => _log.Add($"{_name}:post");
        }

        private sealed class Scope : IDisposable
        {
            private Action _onDispose;
            public Scope(Action onDispose) => _onDispose = onDispose;

            public void Dispose()
            {
                _onDispose?.Invoke();
                _onDispose = null;
            }
        }
    }
}
