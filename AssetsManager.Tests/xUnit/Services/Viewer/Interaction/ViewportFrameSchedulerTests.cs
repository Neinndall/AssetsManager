using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using AssetsManager.Services.Viewer.Interaction;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Interaction
{
    [Collection("Viewport native graphics")]
    public sealed class ViewportFrameSchedulerTests
    {
        [Fact]
        public void PendingInputUpdatesCameraBeforeOneCoalescedFrame()
        {
            OnSta(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                var camera = new PerspectiveCamera();
                var order = new List<string>();
                Point3D paintedPosition = default;
                using var scheduler = new ViewportFrameScheduler(dispatcher, () =>
                {
                    paintedPosition = camera.Position;
                    order.Add("draw");
                });
                scheduler.Start();
                for (int frame = 1; frame <= 100; frame++) scheduler.RequestFrame(TimeSpan.FromMilliseconds(frame));
                dispatcher.InvokeAsync(() =>
                {
                    camera.Position = new Point3D(10, 20, 30);
                    order.Add("mouse");
                }, DispatcherPriority.Input);
                Pump(dispatcher);
                Assert.Equal(new[] { "mouse", "draw" }, order);
                Assert.Equal(camera.Position, paintedPosition);
            });
        }

        [Fact]
        public void RepeatedStartDoesNotQueueAnotherFrameAndRestartResumesDrawing()
        {
            OnSta(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                int frames = 0;
                int queued = 0;
                dispatcher.Hooks.OperationPosted += (_, args) =>
                {
                    if (args.Operation.Priority == DispatcherPriority.Background) queued++;
                };
                using var scheduler = new ViewportFrameScheduler(dispatcher, () => frames++);
                scheduler.Start();
                scheduler.Start();
                Assert.Equal(1, queued);
                Pump(dispatcher);
                Assert.True(frames >= 1);
                int beforeFrames = frames;
                scheduler.Stop();
                int beforeQueued = queued;
                scheduler.Start();
                scheduler.Start();
                Assert.Equal(beforeQueued + 1, queued);
                Pump(dispatcher);
                Assert.True(frames > beforeFrames);
            });
        }

        [Fact]
        public void SustainedInputCannotPostponePendingFrameIndefinitely()
        {
            OnSta(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                var frame = new DispatcherFrame();
                int updates = 0, draws = 0;
                using var scheduler = new ViewportFrameScheduler(dispatcher, () =>
                {
                    draws++;
                    frame.Continue = false;
                });
                void Input()
                {
                    if (!frame.Continue) return;
                    updates++;
                    dispatcher.InvokeAsync(Input, DispatcherPriority.Input);
                }
                using var timeout = new Timer(_ => dispatcher.InvokeAsync(
                    () => frame.Continue = false, DispatcherPriority.Send), null, 1000, Timeout.Infinite);
                scheduler.Start();
                dispatcher.InvokeAsync(Input, DispatcherPriority.Input);
                Dispatcher.PushFrame(frame);
                scheduler.Stop();
                Assert.True(updates > 0);
                Assert.Equal(1, draws);
            });
        }

        [Fact]
        public void HiddenOrDisposedHostCancelsQueuedFramesAndCanResume()
        {
            OnSta(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                int frames = 0;
                using var scheduler = new ViewportFrameScheduler(dispatcher, () => frames++);
                scheduler.Start();
                scheduler.Stop();
                scheduler.RequestFrame(TimeSpan.FromMilliseconds(16));
                Pump(dispatcher);
                Assert.Equal(0, frames);
                scheduler.Start();
                Pump(dispatcher);
                Assert.Equal(1, frames);
                scheduler.RequestFrame(TimeSpan.FromMilliseconds(16));
                scheduler.Dispose();
                scheduler.Stop();
                Pump(dispatcher);
                Assert.Equal(1, frames);
                Assert.Throws<ObjectDisposedException>(scheduler.Start);
            });
        }

        private static void Pump(Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            dispatcher.InvokeAsync(() => frame.Continue = false, DispatcherPriority.SystemIdle);
            Dispatcher.PushFrame(frame);
        }

        private static void OnSta(Action test)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { test(); }
                catch (Exception exception) { failure = exception; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Dispatcher check timed out.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
