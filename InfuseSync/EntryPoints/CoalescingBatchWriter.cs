using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfuseSync.EntryPoints
{
    internal sealed class BatchStopResult
    {
        public BatchStopResult(
            Exception error,
            int unsavedCount,
            int attempts,
            bool isFinal = true)
            => (Error, UnsavedCount, Attempts, IsFinal) =
                (error, unsavedCount, attempts, isFinal);

        public bool Succeeded => Error == null;
        public Exception Error { get; }
        public int UnsavedCount { get; }
        public int Attempts { get; }
        public bool IsFinal { get; }
    }

    internal sealed class CoalescingBatchWriter<TKey, TValue>
    {
        private const int ShutdownWriteAttempts = 3;
        internal static readonly TimeSpan MaximumTimerDelay =
            TimeSpan.FromMilliseconds(uint.MaxValue - 2d);

        private sealed class Batch
        {
            public Batch(Dictionary<TKey, TValue> items, long startedAt)
                => (Items, StartedAt) = (items, startedAt);

            public Dictionary<TKey, TValue> Items { get; }
            public long StartedAt { get; }
        }

        private readonly object _syncLock = new object();
        private readonly TimeSpan _debounceDelay;
        private readonly TimeSpan _maximumDelay;
        private readonly TimeSpan _retryDelay;
        private readonly Func<TValue, TValue, TValue> _merge;
        private readonly Action<IReadOnlyCollection<TValue>> _writeBatch;
        private readonly Action<Exception, int> _writeError;
        private readonly Func<long> _getTimestamp;
        private readonly long _timestampFrequency;
        private readonly Timer _timer;

        private Dictionary<TKey, TValue> _pending = new Dictionary<TKey, TValue>();
        private Batch _inFlight;
        private Task<Exception> _activeWrite;
        private Task<BatchStopResult> _drainTask;
        private BatchStopResult _finalStopResult;
        private long? _batchStartedAt;
        private long? _retryStartedAt;
        private TimeSpan? _scheduledDelay;
        private int _drainAttempts;
        private bool _isStopping;

        public CoalescingBatchWriter(
            TimeSpan debounceDelay,
            TimeSpan maximumDelay,
            TimeSpan retryDelay,
            Func<TValue, TValue, TValue> merge,
            Action<IReadOnlyCollection<TValue>> writeBatch,
            Action<Exception, int> writeError)
            : this(
                debounceDelay,
                maximumDelay,
                retryDelay,
                merge,
                writeBatch,
                writeError,
                Stopwatch.GetTimestamp,
                Stopwatch.Frequency)
        {
        }

        internal CoalescingBatchWriter(
            TimeSpan debounceDelay,
            TimeSpan maximumDelay,
            TimeSpan retryDelay,
            Func<TValue, TValue, TValue> merge,
            Action<IReadOnlyCollection<TValue>> writeBatch,
            Action<Exception, int> writeError,
            Func<long> getTimestamp,
            long timestampFrequency)
        {
            if (timestampFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
            }

            _debounceDelay = debounceDelay;
            _maximumDelay = maximumDelay;
            _retryDelay = retryDelay;
            _merge = merge ?? throw new ArgumentNullException(nameof(merge));
            _writeBatch = writeBatch ?? throw new ArgumentNullException(nameof(writeBatch));
            _writeError = writeError ?? throw new ArgumentNullException(nameof(writeError));
            _getTimestamp = getTimestamp ?? throw new ArgumentNullException(nameof(getTimestamp));
            _timestampFrequency = timestampFrequency;
            _timer = new Timer(
                _ => StartWrite(false, true, false),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        internal int PendingCount
        {
            get { lock (_syncLock) return _pending.Count; }
        }

        internal int OutstandingCount
        {
            get { lock (_syncLock) return GetOutstandingCount(); }
        }

        internal Task<BatchStopResult> DeferredStop
        {
            get { lock (_syncLock) return _drainTask; }
        }

        internal TimeSpan? ScheduledDelay
        {
            get { lock (_syncLock) return _scheduledDelay; }
        }

        internal TimeSpan DelayAt(long timestamp)
        {
            lock (_syncLock)
            {
                return CalculateNextFlushDelay(timestamp);
            }
        }

        public bool Enqueue(TKey key, TValue value)
        {
            lock (_syncLock)
            {
                if (_isStopping) return false;

                if (_pending.TryGetValue(key, out var current))
                {
                    value = _merge(current, value);
                }

                _pending[key] = value;
                var now = _getTimestamp();
                _batchStartedAt = _batchStartedAt ?? now;
                if (_activeWrite == null)
                {
                    ScheduleNextFlush(now);
                }

                return true;
            }
        }

        internal bool FlushNow()
        {
            var write = StartWrite(false, false, false);
            return write == null ? OutstandingCount == 0 : write.GetAwaiter().GetResult() == null;
        }

        public BatchStopResult StopAndFlush(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            var elapsed = Stopwatch.StartNew();
            TaskCompletionSource<BatchStopResult> drainSource = null;
            Task<Exception> initialWrite = null;
            Task<BatchStopResult> drainTask;
            lock (_syncLock)
            {
                if (_drainTask == null)
                {
                    _isStopping = true;
                    CancelTimer();
                    initialWrite = _activeWrite;
                    _drainAttempts = initialWrite == null ? 0 : 1;
                    drainSource = new TaskCompletionSource<BatchStopResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _drainTask = drainSource.Task;
                }

                drainTask = _drainTask;
            }

            if (drainSource != null)
            {
                _ = CompleteDrainAsync(drainSource, initialWrite);
            }

            return WaitForDrain(drainTask, elapsed, timeout, cancellationToken);
        }

        private BatchStopResult WaitForDrain(
            Task<BatchStopResult> drainTask,
            Stopwatch elapsed,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (drainTask.IsCompleted)
            {
                return drainTask.GetAwaiter().GetResult();
            }

            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return CreateProvisionalResult(
                    new TimeoutException("Timed out while draining pending batch writes."));
            }

            try
            {
                var waitMilliseconds = ToWaitMilliseconds(remaining);
                if (drainTask.Wait(waitMilliseconds, cancellationToken))
                {
                    return drainTask.GetAwaiter().GetResult();
                }

                return CreateProvisionalResult(
                    new TimeoutException("Timed out while draining pending batch writes."));
            }
            catch (OperationCanceledException exception)
            {
                return CreateProvisionalResult(exception);
            }
        }

        private BatchStopResult CreateProvisionalResult(Exception error)
        {
            lock (_syncLock)
            {
                return _finalStopResult ?? new BatchStopResult(
                    error,
                    GetOutstandingCount(),
                    _drainAttempts,
                    false);
            }
        }

        private async Task CompleteDrainAsync(
            TaskCompletionSource<BatchStopResult> drainSource,
            Task<Exception> initialWrite)
        {
            BatchStopResult result;
            try
            {
                result = await DrainAsync(initialWrite).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                result = FinishDrain(exception);
            }

            drainSource.TrySetResult(result);
        }

        private async Task<BatchStopResult> DrainAsync(Task<Exception> activeWrite)
        {
            Exception lastError = null;
            var shutdownAttempts = 0;

            while (true)
            {
                if (activeWrite != null)
                {
                    lastError = await activeWrite.ConfigureAwait(false);
                    activeWrite = null;
                }

                bool hasPending;
                lock (_syncLock)
                {
                    hasPending = _pending.Count > 0;
                }

                if (!hasPending)
                {
                    return FinishDrain(null);
                }

                if (shutdownAttempts == ShutdownWriteAttempts)
                {
                    return FinishDrain(lastError);
                }

                activeWrite = StartWrite(true, false, true);
                if (activeWrite != null)
                {
                    shutdownAttempts++;
                }
            }
        }

        private BatchStopResult FinishDrain(Exception error)
        {
            BatchStopResult result;
            lock (_syncLock)
            {
                if (_finalStopResult != null)
                {
                    return _finalStopResult;
                }

                var unsavedCount = GetOutstandingCount();
                if (unsavedCount == 0)
                {
                    error = null;
                }
                else if (error == null)
                {
                    error = new InvalidOperationException("The pending batch could not be saved.");
                }

                result = new BatchStopResult(error, unsavedCount, _drainAttempts);
                _finalStopResult = result;
                _scheduledDelay = null;
            }

            _timer.Dispose();
            return result;
        }

        private Task<Exception> StartWrite(
            bool allowDuringStop,
            bool logError,
            bool countAsDrainAttempt)
        {
            Batch snapshot;
            TaskCompletionSource<Exception> completion;
            lock (_syncLock)
            {
                if (_finalStopResult != null ||
                    (!allowDuringStop && _isStopping) ||
                    _pending.Count == 0 ||
                    _activeWrite != null)
                {
                    return null;
                }

                CancelTimer();
                snapshot = new Batch(_pending, _batchStartedAt ?? _getTimestamp());
                _inFlight = snapshot;
                _pending = new Dictionary<TKey, TValue>();
                _batchStartedAt = null;
                completion = new TaskCompletionSource<Exception>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _activeWrite = completion.Task;
                if (countAsDrainAttempt)
                {
                    _drainAttempts++;
                }
            }

            Task.Run(() => ExecuteWrite(snapshot, completion, logError));
            return completion.Task;
        }

        private void ExecuteWrite(
            Batch snapshot,
            TaskCompletionSource<Exception> completion,
            bool logError)
        {
            Exception error = null;
            try
            {
                _writeBatch(new List<TValue>(snapshot.Items.Values));
            }
            catch (Exception exception)
            {
                error = exception;
            }

            int outstandingCount;
            lock (_syncLock)
            {
                if (error == null)
                {
                    _retryStartedAt = null;
                }
                else
                {
                    Requeue(snapshot);
                    _retryStartedAt = _getTimestamp();
                }

                _inFlight = null;
                if (!_isStopping && _pending.Count > 0)
                {
                    ScheduleNextFlush(_getTimestamp());
                }

                outstandingCount = GetOutstandingCount();
                completion.SetResult(error);
                _activeWrite = null;
            }

            if (logError && error != null)
            {
                try
                {
                    _writeError(error, outstandingCount);
                }
                catch
                {
                    // Logging must not fault the writer.
                }
            }
        }

        private void Requeue(Batch snapshot)
        {
            foreach (var item in snapshot.Items)
            {
                _pending[item.Key] = _pending.TryGetValue(item.Key, out var current)
                    ? _merge(item.Value, current)
                    : item.Value;
            }

            if (!_batchStartedAt.HasValue || snapshot.StartedAt < _batchStartedAt.Value)
            {
                _batchStartedAt = snapshot.StartedAt;
            }
        }

        private int GetOutstandingCount()
        {
            if (_inFlight == null)
            {
                return _pending.Count;
            }

            var count = _inFlight.Items.Count;
            foreach (var key in _pending.Keys)
            {
                if (!_inFlight.Items.ContainsKey(key))
                {
                    count++;
                }
            }

            return count;
        }

        private void ScheduleNextFlush(long now)
        {
            var delay = CalculateNextFlushDelay(now);
            _scheduledDelay = delay;
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }

        private TimeSpan CalculateNextFlushDelay(long now)
        {
            var maximumRemaining = Remaining(
                _maximumDelay,
                Elapsed(_batchStartedAt ?? now, now));
            var delay = maximumRemaining < _debounceDelay
                ? maximumRemaining
                : _debounceDelay;
            if (_retryStartedAt.HasValue)
            {
                var retryRemaining = Remaining(
                    _retryDelay,
                    Elapsed(_retryStartedAt.Value, now));
                if (retryRemaining > delay)
                {
                    delay = retryRemaining;
                }
            }

            delay = ClampTimerDelay(delay);
            return delay;
        }

        private TimeSpan Elapsed(long startedAt, long now)
        {
            if (now <= startedAt)
            {
                return TimeSpan.Zero;
            }

            var seconds = ((double)now - startedAt) / _timestampFrequency;
            return seconds >= TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.MaxValue
                : TimeSpan.FromSeconds(seconds);
        }

        private static TimeSpan Remaining(TimeSpan duration, TimeSpan elapsed)
        {
            return duration <= TimeSpan.Zero || elapsed >= duration
                ? TimeSpan.Zero
                : duration - elapsed;
        }

        private static TimeSpan ClampTimerDelay(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero) return TimeSpan.Zero;
            return delay > MaximumTimerDelay ? MaximumTimerDelay : delay;
        }

        private static int ToWaitMilliseconds(TimeSpan timeout)
        {
            return (int)Math.Min(Math.Ceiling(timeout.TotalMilliseconds), int.MaxValue);
        }

        private void CancelTimer()
        {
            _scheduledDelay = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
