using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Rendering;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class PreparedParticlePassesTests
    {
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

            public RecordingPass(string name, List<string> log, bool failOnColor = false)
            {
                _name = name;
                _log = log;
                _failOnColor = failOnColor;
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
