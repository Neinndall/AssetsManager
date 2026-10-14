using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.Tests.xUnit.Utils
{
    public sealed class DispatcherBatchProgressTests
    {
        [Fact]
        public void BurstIsBatchedInOrderAndYieldsToInput()
        {
            RunOnDispatcher(async dispatcher =>
            {
                var values = new List<int>();
                int batches = 0;
                using var progress = new DispatcherBatchProgress<int>(dispatcher, TimeSpan.FromHours(1), value =>
                {
                    Assert.True(dispatcher.CheckAccess());
                    values.Add(value);
                }, () => batches++);
                await Task.Run(() =>
                {
                    for (int i = 0; i < 4096; i++) progress.Report(i);
                });
                Assert.Empty(values);
                int countAtInput = -1;
                _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => countAtInput = values.Count));
                await progress.DrainAsync();
                Assert.Equal(Enumerable.Range(0, 4096), values);
                Assert.InRange(countAtInput, 1, 256);
                Assert.InRange(batches, 16, 4095);
                await progress.DrainAsync();
                Assert.Equal(4096, values.Count);
            });
        }

        [Fact]
        public void CancellationDrainsAllReportedPartialResultsBeforeCompletion()
        {
            RunOnDispatcher(async dispatcher =>
            {
                var values = new List<int>();
                using var progress = new DispatcherBatchProgress<int>(dispatcher, TimeSpan.FromHours(1), values.Add);
                using var cancellation = new CancellationTokenSource();
                try
                {
                    await Task.Run(() =>
                    {
                        for (int i = 0; i < 1000; i++) progress.Report(i);
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                    });
                    Assert.Fail("The worker should cancel.");
                }
                catch (OperationCanceledException)
                {
                    await progress.DrainAsync();
                }
                Assert.Equal(Enumerable.Range(0, 1000), values);
            });
        }

        private static void RunOnDispatcher(Func<Dispatcher, Task> action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await action(dispatcher); }
                    catch (Exception ex) { failure = ex; }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
                }));
                Dispatcher.Run();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Dispatcher test timed out.");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
