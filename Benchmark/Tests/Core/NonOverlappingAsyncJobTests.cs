using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using Serilog;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Core
{
    public sealed class NonOverlappingAsyncJobTests
    {
        [Fact]
        public async Task ConcurrentRunIsSkippedWhileFirstRunIsActive()
        {
            using var job = new NonOverlappingAsyncJob();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int executions = 0;

            Task<bool> first = job.TryRunAsync(async _ =>
            {
                Interlocked.Increment(ref executions);
                entered.SetResult();
                await release.Task;
            });
            await entered.Task;

            bool second = await job.TryRunAsync(_ =>
            {
                Interlocked.Increment(ref executions);
                return Task.CompletedTask;
            });
            release.SetResult();

            Assert.True(await first);
            Assert.False(second);
            Assert.Equal(1, executions);
        }

        [Fact]
        public async Task StopCancelsActiveRunAndRejectsNewRuns()
        {
            using var job = new NonOverlappingAsyncJob();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> active = job.TryRunAsync(async cancellationToken =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
            await entered.Task;

            job.Stop();

            Assert.False(await active);
            Assert.False(await job.TryRunAsync(_ => Task.CompletedTask));
        }

        [Fact]
        public async Task StartAllowsRunsAfterAStop()
        {
            using var job = new NonOverlappingAsyncJob();
            job.Stop();
            job.Start();

            Assert.True(await job.TryRunAsync(_ => Task.CompletedTask));
        }

        [Fact]
        public async Task PbeShutdownCancelsRequestWithoutPublishingStatusEvent()
        {
            var handler = new BlockingHandler();
            var service = new PbeStatusService(
                new HttpClient(handler),
                new LogService(Log.Logger),
                new AppSettings());
            int statusEvents = 0;
            service.StatusChecked += () => Interlocked.Increment(ref statusEvents);
            using var cancellation = new CancellationTokenSource();

            Task<string> check = service.CheckPbeStatusAsync(cancellation.Token);
            await handler.Entered.Task;
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
            Assert.Equal(0, statusEvents);
        }

        private sealed class BlockingHandler : HttpMessageHandler
        {
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }
    }
}
