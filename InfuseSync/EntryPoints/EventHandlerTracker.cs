using System;
using System.Threading;
using System.Threading.Tasks;

namespace InfuseSync.EntryPoints
{
    internal sealed class EventHandlerTracker
    {
        private readonly object _syncLock = new object();
        private Task _idle = Task.CompletedTask;
        private TaskCompletionSource<bool> _idleSource;
        private int _activeHandlers;
        private bool _isStopping;

        public int ActiveCount
        {
            get { lock (_syncLock) return _activeHandlers; }
        }

        public bool TryEnter()
        {
            lock (_syncLock)
            {
                if (_isStopping) return false;

                if (_activeHandlers++ == 0)
                {
                    _idleSource = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _idle = _idleSource.Task;
                }

                return true;
            }
        }

        public void Exit()
        {
            TaskCompletionSource<bool> idleSource = null;
            lock (_syncLock)
            {
                _activeHandlers--;
                if (_activeHandlers == 0)
                {
                    idleSource = _idleSource;
                    _idleSource = null;
                }
            }

            idleSource?.SetResult(true);
        }

        public Exception StopAndWait(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task idle;
            lock (_syncLock)
            {
                _isStopping = true;
                idle = _idle;
            }

            if (idle.IsCompleted) return null;

            try
            {
                var waitMilliseconds = (int)Math.Min(
                    Math.Ceiling(timeout.TotalMilliseconds),
                    int.MaxValue);
                return idle.Wait(waitMilliseconds, cancellationToken)
                    ? null
                    : new TimeoutException("Timed out while waiting for event handlers.");
            }
            catch (OperationCanceledException exception)
            {
                return exception;
            }
        }

        public Task<T> ContinueWhenIdle<T>(Func<T> continuation)
        {
            if (continuation == null) throw new ArgumentNullException(nameof(continuation));

            Task idle;
            lock (_syncLock)
            {
                idle = _idle;
            }

            return idle.ContinueWith(
                _ => continuation(),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }
}
