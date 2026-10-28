using System.Collections.Concurrent;
using System.Diagnostics;
using InfuseSync.EntryPoints;
using InfuseSync.Models;
using Xunit;

namespace InfuseSync.Tests.EntryPoints;

public sealed class CoalescingBatchWriterTests
{
    [Fact]
    public void RemovalDominatesUpdatesWithinOneBatch()
    {
        var writes = new List<IReadOnlyCollection<ItemRec>>();
        var writer = CreateWriter(batch => writes.Add(batch));
        var key = Guid.NewGuid();

        writer.Enqueue(key, Item(key, ItemStatus.Updated));
        writer.Enqueue(key, Item(key, ItemStatus.Removed));
        writer.Enqueue(key, Item(key, ItemStatus.Updated));

        Assert.True(writer.FlushNow());
        Assert.Equal(ItemStatus.Removed, Assert.Single(Assert.Single(writes)).Status);
        Assert.True(Stop(writer).Succeeded);
    }

    [Fact]
    public async Task ChangeQueuedDuringSuccessfulWriteIsSavedInNextBatch()
    {
        using var writeStarted = new ManualResetEventSlim();
        using var allowWrite = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<ItemRec[]>();
        var writer = CreateWriter(batch =>
        {
            if (writes.IsEmpty)
            {
                writeStarted.Set();
                Assert.True(allowWrite.Wait(TimeSpan.FromSeconds(5)));
            }

            writes.Enqueue(batch.ToArray());
        });
        var key = Guid.NewGuid();

        writer.Enqueue(key, Item(key, ItemStatus.Removed));
        var firstFlush = Task.Run(() => writer.FlushNow());
        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(5)));
        writer.Enqueue(key, Item(key, ItemStatus.Updated));
        allowWrite.Set();

        Assert.True(await firstFlush);
        Assert.Equal(1, writer.PendingCount);
        Assert.True(writer.FlushNow());
        Assert.Equal(ItemStatus.Removed, Assert.Single(writes.ElementAt(0)).Status);
        Assert.Equal(ItemStatus.Updated, Assert.Single(writes.ElementAt(1)).Status);
        Assert.True(Stop(writer).Succeeded);
    }

    [Fact]
    public async Task FailedRemovalStillDominatesConcurrentUpdate()
    {
        using var writeStarted = new ManualResetEventSlim();
        using var allowWrite = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<ItemRec[]>();
        var attempts = 0;
        var writer = CreateWriter(batch =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                writeStarted.Set();
                Assert.True(allowWrite.Wait(TimeSpan.FromSeconds(5)));
                throw new InvalidOperationException("database unavailable");
            }

            writes.Enqueue(batch.ToArray());
        });
        var key = Guid.NewGuid();

        writer.Enqueue(key, Item(key, ItemStatus.Removed));
        var firstFlush = Task.Run(() => writer.FlushNow());
        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(5)));
        writer.Enqueue(key, Item(key, ItemStatus.Updated));
        allowWrite.Set();

        Assert.False(await firstFlush);
        Assert.Equal(1, writer.PendingCount);
        Assert.True(writer.FlushNow());
        Assert.Equal(ItemStatus.Removed, Assert.Single(Assert.Single(writes)).Status);
        Assert.True(Stop(writer).Succeeded);
    }

    [Fact]
    public async Task LateFailureIsRetriedByDeferredDrain()
    {
        using var writeStarted = new ManualResetEventSlim();
        using var allowWrite = new ManualResetEventSlim();
        var attempts = 0;
        var writer = CreateWriter(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                writeStarted.Set();
                allowWrite.Wait(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("database unavailable");
            }
        });
        var key = Guid.NewGuid();
        writer.Enqueue(key, Item(key, ItemStatus.Updated));
        var activeFlush = Task.Run(() => writer.FlushNow());
        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(5)));

        var provisional = writer.StopAndFlush(
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        Assert.False(provisional.IsFinal);
        Assert.Equal(1, provisional.Attempts);
        Assert.Equal(1, provisional.UnsavedCount);

        allowWrite.Set();
        Assert.False(await activeFlush);
        var final = await writer.DeferredStop;

        Assert.True(final.IsFinal);
        Assert.True(final.Succeeded);
        Assert.Equal(2, final.Attempts);
        Assert.Equal(0, final.UnsavedCount);
        Assert.Equal(0, writer.PendingCount);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task LateSuccessUpdatesFinalStopResult()
    {
        using var writeStarted = new ManualResetEventSlim();
        using var allowWrite = new ManualResetEventSlim();
        var attempts = 0;
        var writer = CreateWriter(_ =>
        {
            Interlocked.Increment(ref attempts);
            writeStarted.Set();
            allowWrite.Wait(TimeSpan.FromSeconds(5));
        });
        var key = Guid.NewGuid();
        writer.Enqueue(key, Item(key, ItemStatus.Updated));
        var activeFlush = Task.Run(() => writer.FlushNow());
        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(5)));

        var provisional = writer.StopAndFlush(
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        Assert.False(provisional.IsFinal);
        Assert.Equal(1, provisional.UnsavedCount);

        allowWrite.Set();
        Assert.True(await activeFlush);
        var final = await writer.DeferredStop;

        Assert.True(final.IsFinal);
        Assert.True(final.Succeeded);
        Assert.Equal(1, final.Attempts);
        Assert.Equal(0, final.UnsavedCount);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ConcurrentStopsUseOwnDeadlinesAndShareFinalDrain()
    {
        using var writeStarted = new ManualResetEventSlim();
        using var allowWrite = new ManualResetEventSlim();
        var attempts = 0;
        var writer = CreateWriter(_ =>
        {
            Interlocked.Increment(ref attempts);
            writeStarted.Set();
            allowWrite.Wait(TimeSpan.FromSeconds(5));
        });
        var key = Guid.NewGuid();
        writer.Enqueue(key, Item(key, ItemStatus.Updated));

        var firstStop = Task.Run(() => Stop(writer));
        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(5)));
        var provisional = writer.StopAndFlush(TimeSpan.Zero, CancellationToken.None);
        var secondStop = Task.Run(() => Stop(writer));
        allowWrite.Set();
        var firstResult = await firstStop;
        var secondResult = await secondStop;

        Assert.False(provisional.IsFinal);
        Assert.Equal(1, provisional.Attempts);
        Assert.Same(firstResult, secondResult);
        Assert.True(firstResult.Succeeded);
        Assert.Equal(1, firstResult.Attempts);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void SchedulingHandlesTimestampRollbackAndLargeForwardJump()
    {
        var clock = new MutableClock(100);
        var attempts = 0;
        var writer = CreateWriter(
            _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("database unavailable");
                }
            },
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            clock);
        var key = Guid.NewGuid();
        writer.Enqueue(key, Item(key, ItemStatus.Updated));

        Assert.False(writer.FlushNow());
        Assert.Equal(TimeSpan.FromSeconds(5), writer.ScheduledDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), writer.DelayAt(90));
        Assert.Equal(TimeSpan.Zero, writer.DelayAt(long.MaxValue));

        var result = Stop(writer);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ScheduledTimerDelayIsClampedToSupportedRange()
    {
        var clock = new MutableClock(0);
        var writer = CreateWriter(
            _ => { },
            TimeSpan.MaxValue,
            TimeSpan.MaxValue,
            TimeSpan.MaxValue,
            clock);
        var key = Guid.NewGuid();

        writer.Enqueue(key, Item(key, ItemStatus.Updated));

        Assert.Equal(
            CoalescingBatchWriter<Guid, ItemRec>.MaximumTimerDelay,
            writer.ScheduledDelay);
        Assert.True(Stop(writer).Succeeded);
    }

    [Fact]
    public void StopRetriesTransientFailures()
    {
        var attempts = 0;
        var writer = CreateWriter(_ =>
        {
            if (Interlocked.Increment(ref attempts) < 3)
            {
                throw new InvalidOperationException("database unavailable");
            }
        });
        var key = Guid.NewGuid();
        writer.Enqueue(key, Item(key, ItemStatus.Updated));

        var result = Stop(writer);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(0, result.UnsavedCount);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task StopReportsActiveWriteAndBoundedRetriesExactly()
    {
        using var writeStarted = new ManualResetEventSlim();
        using var allowWrite = new ManualResetEventSlim();
        var attempts = 0;
        var writer = CreateWriter(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                writeStarted.Set();
                allowWrite.Wait(TimeSpan.FromSeconds(5));
            }

            throw new InvalidOperationException("database unavailable");
        });
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        writer.Enqueue(first, Item(first, ItemStatus.Updated));
        writer.Enqueue(second, Item(second, ItemStatus.Removed));

        var activeFlush = Task.Run(() => writer.FlushNow());
        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(5)));
        var releaseWrite = Task.Run(async () =>
        {
            await Task.Delay(100);
            allowWrite.Set();
        });
        var result = Stop(writer);

        await releaseWrite;
        Assert.False(await activeFlush);
        Assert.False(result.Succeeded);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal(4, result.Attempts);
        Assert.Equal(2, result.UnsavedCount);
        Assert.Equal(4, attempts);
        Assert.Equal(2, writer.PendingCount);
        var third = Guid.NewGuid();
        Assert.False(writer.Enqueue(third, Item(third, ItemStatus.Updated)));
    }

    [Fact]
    public async Task StopWaitsForActiveWriteAndFlushesRemainingChanges()
    {
        using var writeStarted = new ManualResetEventSlim();
        using var allowWrite = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<ItemRec[]>();
        var attempts = 0;
        var writer = CreateWriter(batch =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                writeStarted.Set();
                Assert.True(allowWrite.Wait(TimeSpan.FromSeconds(5)));
            }

            writes.Enqueue(batch.ToArray());
        });
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        writer.Enqueue(first, Item(first, ItemStatus.Updated));

        var activeFlush = Task.Run(() => writer.FlushNow());
        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(5)));
        writer.Enqueue(second, Item(second, ItemStatus.Removed));
        var releaseWrite = Task.Run(async () =>
        {
            await Task.Delay(100);
            allowWrite.Set();
        });
        var result = Stop(writer);

        await releaseWrite;
        Assert.True(await activeFlush);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, writes.Count);
    }

    [Fact]
    public async Task AcceptedHandlerIsFlushedByDeferredDrain()
    {
        var writes = new List<IReadOnlyCollection<ItemRec>>();
        var writer = CreateWriter(batch => writes.Add(batch));
        var handlers = new EventHandlerTracker();
        Assert.True(handlers.TryEnter());

        var error = handlers.StopAndWait(TimeSpan.Zero, CancellationToken.None);
        var deferred = handlers.ContinueWhenIdle(() => Stop(writer));
        var key = Guid.NewGuid();
        writer.Enqueue(key, Item(key, ItemStatus.Updated));

        Assert.IsType<TimeoutException>(error);
        Assert.False(deferred.IsCompleted);
        Assert.False(handlers.TryEnter());

        handlers.Exit();
        var result = await deferred;

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(ItemStatus.Updated, Assert.Single(Assert.Single(writes)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopReturnsWithinDeadlineWhenWriterIsBlocked(bool cancel)
    {
        using var writeStarted = new ManualResetEventSlim();
        using var allowWrite = new ManualResetEventSlim();
        using var writeFinished = new ManualResetEventSlim();
        var attempts = 0;
        var writer = CreateWriter(_ =>
        {
            Interlocked.Increment(ref attempts);
            writeStarted.Set();
            allowWrite.Wait(TimeSpan.FromSeconds(10));
            writeFinished.Set();
        });
        var key = Guid.NewGuid();
        writer.Enqueue(key, Item(key, ItemStatus.Updated));
        using var cancellation = new CancellationTokenSource();
        var timeout = TimeSpan.FromMilliseconds(250);
        if (cancel)
        {
            cancellation.CancelAfter(timeout);
            timeout = TimeSpan.FromSeconds(5);
        }

        var elapsed = Stopwatch.StartNew();
        var result = writer.StopAndFlush(timeout, cancellation.Token);
        elapsed.Stop();

        try
        {
            Assert.True(writeStarted.IsSet);
            Assert.False(result.Succeeded);
            Assert.False(result.IsFinal);
            Assert.IsType(cancel ? typeof(OperationCanceledException) : typeof(TimeoutException), result.Error);
            Assert.Equal(1, result.Attempts);
            Assert.Equal(1, result.UnsavedCount);
            Assert.Equal(1, Volatile.Read(ref attempts));
            Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));

            var lateKey = Guid.NewGuid();
            Assert.False(writer.Enqueue(lateKey, Item(lateKey, ItemStatus.Updated)));
        }
        finally
        {
            allowWrite.Set();
        }

        Assert.True(writeFinished.Wait(TimeSpan.FromSeconds(5)));
        var final = await writer.DeferredStop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(final.IsFinal);
        Assert.True(final.Succeeded);
        Assert.Equal(1, final.Attempts);
        Assert.Equal(0, final.UnsavedCount);
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    private static BatchStopResult Stop(CoalescingBatchWriter<Guid, ItemRec> writer)
    {
        return writer.StopAndFlush(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    private static CoalescingBatchWriter<Guid, ItemRec> CreateWriter(
        Action<IReadOnlyCollection<ItemRec>> writeBatch)
    {
        return new CoalescingBatchWriter<Guid, ItemRec>(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromSeconds(1),
            LibrarySyncManager.MergeItemChanges,
            writeBatch,
            (_, _) => { });
    }

    private static CoalescingBatchWriter<Guid, ItemRec> CreateWriter(
        Action<IReadOnlyCollection<ItemRec>> writeBatch,
        TimeSpan debounceDelay,
        TimeSpan maximumDelay,
        TimeSpan retryDelay,
        MutableClock clock)
    {
        return new CoalescingBatchWriter<Guid, ItemRec>(
            debounceDelay,
            maximumDelay,
            retryDelay,
            LibrarySyncManager.MergeItemChanges,
            writeBatch,
            (_, _) => { },
            () => clock.Timestamp,
            1);
    }

    private static ItemRec Item(Guid id, ItemStatus status)
    {
        return new ItemRec
        {
            Guid = id,
            Status = status,
            Type = "Movie"
        };
    }

    private sealed class MutableClock
    {
        public MutableClock(long timestamp)
        {
            Timestamp = timestamp;
        }

        public long Timestamp { get; set; }
    }
}
