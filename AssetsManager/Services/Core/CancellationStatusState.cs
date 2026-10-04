using System;

namespace AssetsManager.Services.Core
{
    /// <summary>Keeps unified cancellation feedback visible even when a task completes immediately.</summary>
    public sealed class CancellationStatusState
    {
        public const string Message = "Cancelling Task...";
        public const int HoldMilliseconds = 1500;
        private readonly TimeProvider _clock;
        private long _started;
        private bool _hasStarted;
        private string _message = Message;

        public CancellationStatusState(TimeProvider clock = null) => _clock = clock ?? TimeProvider.System;

        public void Begin(string message = Message)
        {
            _started = _clock.GetTimestamp();
            _hasStarted = true;
            _message = message;
        }

        public int RemainingMilliseconds => !_hasStarted ? 0 :
            Math.Max(0, (int)Math.Ceiling(HoldMilliseconds - _clock.GetElapsedTime(_started).TotalMilliseconds));
        public bool IsActive => RemainingMilliseconds > 0;
        public bool Allows(string message) => !IsActive || message == _message;
    }
}
