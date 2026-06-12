using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Hosting.Persistence;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Deterministic repro for the "grain wedges when a hub subscribes a query from
/// handler/init context" class of failures. The defect is NOT Orleans-specific:
/// any storage-adapter observable whose async pump is started with a bare
/// <c>Observable.Create(async ...)</c> begins executing on the SUBSCRIBER's
/// thread, and every <c>await</c> without <c>ConfigureAwait(false)</c> captures
/// the subscriber's <see cref="SynchronizationContext"/> / TaskScheduler. When
/// that subscriber is a single-threaded scheduler that is itself blocked waiting
/// for the result (a hub action block, an Orleans grain in DeliverMessage), the
/// continuation queues behind the blocked thread and the stream never emits —
/// the hard wedge on Orleans, the dropped-initial-emission flake on Monolith.
///
/// <para>This test models that subscriber exactly: a thread with a
/// single-threaded <see cref="SynchronizationContext"/> that subscribes
/// <see cref="FileSystemStorageAdapter.GetPartitionObjects"/> (the virtual
/// data-source load that runs at hub init) and then blocks without pumping.
/// The pump must run inside the IIoPool — fully decoupled from the
/// subscriber's context — for the wait to ever complete.</para>
/// </summary>
public class PartitionObjectsSubscriberIndependenceTest
{
    private const int SeededObjects = 50;

    [Fact]
    public void GetPartitionObjects_CompletesWhileSubscriberContextIsBlocked()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "mw-partobj-" + Guid.NewGuid().ToString("N"));
        var partitionDir = Path.Combine(baseDir, "Test", "Node", "Data");
        Directory.CreateDirectory(partitionDir);
        // Many files so the pump is guaranteed to hit at least one genuinely
        // asynchronous file read (a single tiny read could in principle complete
        // synchronously and mask the captured-context defect).
        for (var i = 0; i < SeededObjects; i++)
            File.WriteAllText(Path.Combine(partitionDir, $"item{i:D2}.json"),
                $$"""{"Name":"object {{i}}"}""");

        try
        {
            var adapter = new FileSystemStorageAdapter(baseDir);
            var options = new JsonSerializerOptions();

            Exception? error = null;
            var received = 0;
            var pumpCompletedWhileBlocked = false;

            var subscriberThread = new Thread(() =>
            {
                // Single-threaded context: continuations posted here can only run
                // when this thread pumps. It never pumps — it blocks below, exactly
                // like a hub action block / grain waiting on the query result.
                var ctx = new NonPumpingSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(ctx);
                try
                {
                    using var done = new ManualResetEventSlim(false);
                    using var subscription = adapter
                        .GetPartitionObjects("Test/Node", "Data", options)
                        .Subscribe(
                            _ => Interlocked.Increment(ref received),
                            ex => { error = ex; done.Set(); },
                            () => done.Set());

                    // The blocked wait — the pump must complete WITHOUT this
                    // thread's context ever executing a continuation.
                    pumpCompletedWhileBlocked = done.Wait(TimeSpan.FromSeconds(15));
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                }
            })
            {
                IsBackground = true,
                Name = "blocked-subscriber"
            };
            subscriberThread.Start();
            subscriberThread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(
                "the subscriber thread itself must finish its bounded wait");

            error.Should().BeNull();
            pumpCompletedWhileBlocked.Should().BeTrue(
                "GetPartitionObjects' async pump must run inside the IIoPool and never " +
                "depend on the subscriber's SynchronizationContext/scheduler to make progress " +
                "— a captured context here is the grain-wedge / dropped-initial-emission defect");
            received.Should().Be(SeededObjects);
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// A SynchronizationContext that queues posted continuations but never
    /// executes them (its single owning thread is blocked). Models a hub/grain
    /// scheduler that is stuck in a handler waiting on the very stream whose
    /// continuations are queued behind it.
    /// </summary>
    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        // Instance state on the test-local context — queued continuations are
        // intentionally never drained for the lifetime of the (short) test.
        private readonly List<(SendOrPostCallback Callback, object? State)> _queued = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queued)
                _queued.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
            => throw new InvalidOperationException(
                "Synchronous Send to the blocked subscriber context — would deadlock immediately.");
    }
}
