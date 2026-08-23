using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Coordinates demand-driven invalidation for all Viewer OpenGL surfaces.
    /// A single WPF rendering hook drives only sessions that currently need continuous frames.
    /// </summary>
    public sealed class RenderDemandService : IDisposable
    {
        internal static readonly TimeSpan TargetFrameInterval = TimeSpan.FromSeconds(1.0 / 60.0);

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<RenderDemandSession> _sessions = new();
        private bool _isFrameCapEnabled;
        private bool _isRenderingHooked;
        private bool _isDisposed;

        public bool IsFrameCapEnabled
        {
            get => _isFrameCapEnabled;
            set
            {
                if (_isFrameCapEnabled == value)
                    return;

                _isFrameCapEnabled = value;
                TimeSpan now = _clock.Elapsed;
                foreach (RenderDemandSession session in _sessions.ToArray())
                    session.ResetSchedule(now, value);

                UpdateRenderingSubscription();
            }
        }

        public RenderDemandSession Register(
            Func<bool> canRender,
            Func<bool> requiresContinuousFrames,
            Action invalidate)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(RenderDemandService));

            var session = new RenderDemandSession(
                this,
                canRender ?? throw new ArgumentNullException(nameof(canRender)),
                requiresContinuousFrames ?? throw new ArgumentNullException(nameof(requiresContinuousFrames)),
                invalidate ?? throw new ArgumentNullException(nameof(invalidate)),
                _clock.Elapsed,
                _isFrameCapEnabled);

            _sessions.Add(session);
            UpdateRenderingSubscription();
            return session;
        }

        internal void Refresh(RenderDemandSession session)
        {
            if (_isDisposed || !_sessions.Contains(session))
                return;

            session.ResetSchedule(_clock.Elapsed, _isFrameCapEnabled);
            UpdateRenderingSubscription();
        }

        internal void Unregister(RenderDemandSession session)
        {
            if (!_sessions.Remove(session))
                return;

            session.MarkDisposed();
            UpdateRenderingSubscription();
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (_isDisposed)
                return;

            TimeSpan now = _clock.Elapsed;
            for (int i = 0; i < _sessions.Count; i++)
                _sessions[i].TryInvalidate(now, _isFrameCapEnabled);

            UpdateRenderingSubscription();
        }

        private void UpdateRenderingSubscription()
        {
            bool shouldRenderContinuously = !_isDisposed &&
                _sessions.Any(session => session.NeedsContinuousFrames);

            if (shouldRenderContinuously == _isRenderingHooked)
                return;

            if (shouldRenderContinuously)
            {
                CompositionTarget.Rendering += OnRendering;
                _isRenderingHooked = true;
            }
            else
            {
                CompositionTarget.Rendering -= OnRendering;
                _isRenderingHooked = false;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            CompositionTarget.Rendering -= OnRendering;
            _isRenderingHooked = false;

            foreach (RenderDemandSession session in _sessions.ToArray())
                session.MarkDisposed();

            _sessions.Clear();
        }
    }

    public sealed class RenderDemandSession : IDisposable
    {
        private readonly RenderDemandService _owner;
        private readonly Func<bool> _canRender;
        private readonly Func<bool> _requiresContinuousFrames;
        private readonly Action _invalidate;
        private TimeSpan _nextFrameDueAt;
        private bool _isDisposed;

        internal RenderDemandSession(
            RenderDemandService owner,
            Func<bool> canRender,
            Func<bool> requiresContinuousFrames,
            Action invalidate,
            TimeSpan now,
            bool isFrameCapEnabled)
        {
            _owner = owner;
            _canRender = canRender;
            _requiresContinuousFrames = requiresContinuousFrames;
            _invalidate = invalidate;
            ResetSchedule(now, isFrameCapEnabled);
        }

        internal bool NeedsContinuousFrames =>
            !_isDisposed && _canRender() && _requiresContinuousFrames();

        public void Refresh()
        {
            if (!_isDisposed)
                _owner.Refresh(this);
        }

        internal void ResetSchedule(TimeSpan now, bool isFrameCapEnabled)
        {
            _nextFrameDueAt = isFrameCapEnabled
                ? now + RenderDemandService.TargetFrameInterval
                : now;
        }

        internal void TryInvalidate(TimeSpan now, bool isFrameCapEnabled)
        {
            if (!NeedsContinuousFrames)
                return;

            if (isFrameCapEnabled)
            {
                if (now < _nextFrameDueAt)
                    return;

                TimeSpan lateness = now - _nextFrameDueAt;
                _nextFrameDueAt = lateness > RenderDemandService.TargetFrameInterval
                    ? now + RenderDemandService.TargetFrameInterval
                    : _nextFrameDueAt + RenderDemandService.TargetFrameInterval;
            }
            else
            {
                _nextFrameDueAt = now;
            }

            _invalidate();
        }

        internal void MarkDisposed() => _isDisposed = true;

        public void Dispose()
        {
            if (!_isDisposed)
                _owner.Unregister(this);
        }
    }
}
