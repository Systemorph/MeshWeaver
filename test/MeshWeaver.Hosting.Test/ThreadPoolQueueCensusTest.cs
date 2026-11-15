using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The queue census on the <c>[POOLSTALL]</c> line (MeshWeaver#5388, MeshWeaver#5799): what the ThreadPool's
/// backlog consisted of, keyed by the code each queued item will run. The 2026-11-11 capture had 20 of 44
/// workers IDLE beside 69,012 queued items, so the producer was in the queue and on no stack.
/// </summary>
public class ThreadPoolQueueCensusTest
{
    /// <summary>Named producers, so a key can be asserted by name.</summary>
    private static class Producers
    {
        public static void Drain() { }

        public static void Other() { }

        public static void Marker(object? state) { }
    }

    /// <summary>A work-item wrapper in the shape of <c>QueueUserWorkItemCallback</c>: it holds the delegate it runs.</summary>
    private sealed class Wrapper(Action callback)
    {
        private readonly Action callback = callback;

        public void Run() => callback();
    }

    /// <summary>
    /// A stream whose reads never complete until the test says so: the BCL's own async methods
    /// over it suspend at their first await, which is the state a queued or parked continuation
    /// is in.
    /// </summary>
    private sealed class PendingStream(Task<int> gate) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => gate;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(gate);
    }

    /// <summary>
    /// 🚨 On .NET 11 the runtime libraries are compiled with Runtime Async, so a BCL async method is
    /// an <c>AsyncHelpers+RuntimeAsyncTask&lt;T&gt;</c> whose delegate is the runtime's own
    /// <c>DispatchContinuations</c>, not an <c>AsyncStateMachineBox</c>. Keyed by that delegate,
    /// every BCL continuation of every producer read as ONE group per result type. The key must be
    /// the method that resumes. Negative control: without the runtime-async branch in
    /// <see cref="ThreadPoolQueueCensus.ProducerOf"/> both assertions fail with
    /// <c>…RuntimeAsyncTask`1[…].DispatchContinuations</c>.
    /// </summary>
    [Fact]
    public void ARuntimeAsyncBclContinuation_IsKeyedByTheMethodThatResumes_NotTheDispatcher()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copy = new PendingStream(gate.Task).CopyToAsync(Stream.Null);
        var readToEnd = new StreamReader(new PendingStream(gate.Task)).ReadToEndAsync();
        try
        {
            copy.GetType().Name.Should().Be("RuntimeAsyncTask`1",
                "this pins the premise: the runtime libraries are compiled with runtime-async on .NET 11");

            ThreadPoolQueueCensus.ProducerOf(copy).Should().StartWith("System.IO.Stream.<CopyToAsync>")
                .And.NotContain("DispatchContinuations");
            ThreadPoolQueueCensus.ProducerOf(readToEnd).Should().Be("System.IO.StreamReader.ReadBufferAsync",
                "the innermost method in the chain is the one that resumes first");
        }
        finally
        {
            gate.SetResult(0);
        }
    }

    [Fact]
    public void EachItemIsKeyedByTheCodeItWillRun_LargestProducerFirst()
    {
        object[] queued =
        [
            new Task(Producers.Drain), new Task(Producers.Drain), new Task(Producers.Drain),
            new Task(Producers.Other),
            new Wrapper(Producers.Drain), new Wrapper(Producers.Drain),
        ];

        var census = ThreadPoolQueueCensus.Of(queued, ThreadPoolQueueCensus.MaxItems);

        census.Read.Should().Be(6);
        census.Cut.Should().BeFalse();
        census.Groups.Select(group => group.Producer).Should().Equal(
            $"{typeof(Producers)}.{nameof(Producers.Drain)}",
            $"{typeof(Wrapper)} → {typeof(Producers)}.{nameof(Producers.Drain)}",
            $"{typeof(Producers)}.{nameof(Producers.Other)}");
        census.Groups.Select(group => group.Count).Should().Equal(3, 2, 1);

        var section = census.Render();
        section.Should().Contain("QUEUED WORK: 6 item(s) read, in 3 producer(s)");
        section.IndexOf($"×3 {typeof(Producers)}.Drain", StringComparison.Ordinal).Should()
            .BeLessThan(section.IndexOf($"×1 {typeof(Producers)}.Other", StringComparison.Ordinal),
                "the largest producer is printed first");
    }

    /// <summary>An item carrying no delegate is still counted — under its own type, never dropped.</summary>
    [Fact]
    public void AnItemWithNoDelegate_IsCountedUnderItsType()
        => ThreadPoolQueueCensus.ProducerOf(new object()).Should().Be("System.Object");

    [Fact]
    public void TheWalkIsBounded_AndSaysWhenItStoppedShort()
    {
        var census = ThreadPoolQueueCensus.Of(Enumerable.Range(0, 10).Select(_ => (object)new Task(Producers.Other)), maxItems: 4);

        census.Read.Should().Be(4);
        census.Cut.Should().BeTrue();
        census.Render().Should().Contain("the walk stopped at");
    }

    [Fact]
    public void AQueueThatCannotBeRead_SaysSo_InsteadOfPrintingAnEmptyCensus()
        => new ThreadPoolQueueCensus([], 0, false, "no enumeration on this runtime").Render()
            .Should().Contain("the queue could NOT be read (no enumeration on this runtime)");

    /// <summary>
    /// The production walk on the REAL ThreadPool sees the items actually queued. Every worker is parked
    /// first (the park is the subject, released in a finally), so marker items queued from this thread sit
    /// in the global queue behind more parked items than the pool has threads, and the census must count
    /// every one of them under its method. Negative control: before the markers are queued, the census
    /// holds none of them — so a count of 1000 is the walk reading the queue, not a key that always matches.
    /// </summary>
    [Fact]
    public void OnTheRealThreadPool_TheCensusNamesTheItemsActuallyQueued()
    {
        ThreadPoolQueueCensus.IsAvailable.Should().BeTrue(
            "this runtime exposes ThreadPool.GetQueuedWorkItems(); if it stops doing so the census degrades to a stated failure");
        var marker = $"{typeof(ThreadPoolQueueCensusTest)}+{nameof(Producers)}.{nameof(Producers.Marker)}";
        var released = 0;
        var parkers = ThreadPool.ThreadCount + 64;
        try
        {
            for (var i = 0; i < parkers; i++)
                ThreadPool.UnsafeQueueUserWorkItem(
                    _ => SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, TimeSpan.FromSeconds(30)), null);

            var before = ThreadPoolQueueCensus.Take();
            before.Failure.Should().BeNull();
            before.Groups.Where(group => group.Producer.EndsWith(marker, StringComparison.Ordinal))
                .Sum(group => group.Count).Should().Be(0, "no marker has been queued yet");

            for (var i = 0; i < 1000; i++)
                ThreadPool.UnsafeQueueUserWorkItem(Producers.Marker, null);

            var after = ThreadPoolQueueCensus.Take();
            after.Failure.Should().BeNull();
            after.Groups.Where(group => group.Producer.EndsWith(marker, StringComparison.Ordinal))
                .Sum(group => group.Count).Should().Be(1000,
                    "every marker sits in the global queue behind parked items, and the walk reads that queue");
        }
        finally
        {
            Volatile.Write(ref released, 1);
        }
    }

    /// <summary>
    /// A runtime-private read that THROWS costs that ONE item its name, never the census: the item
    /// is keyed with the exception's type and message and every other item is still counted.
    /// Negative control: the same items with the runtime's own resolver name the BCL method, so the
    /// unresolved key is the injected fault, not a resolver that never works.
    /// </summary>
    [Fact]
    public void AThrowingRuntimeRead_UnresolvesThatOneItem_AndTheCensusGoesOn()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        object[] queued = [new PendingStream(gate.Task).CopyToAsync(Stream.Null), new Task(Producers.Drain)];
        try
        {
            var faulted = ThreadPoolQueueCensus.Of(queued, ThreadPoolQueueCensus.MaxItems,
                _ => throw new InvalidOperationException("runtime layout moved"));

            faulted.Failure.Should().BeNull();
            faulted.Read.Should().Be(2);
            faulted.Groups.Select(group => group.Producer).OrderBy(key => key, StringComparer.Ordinal).Should().Equal(
                $"{typeof(Producers)}.{nameof(Producers.Drain)}",
                "runtime-async continuation (method unresolved: InvalidOperationException: runtime layout moved)");

            var real = ThreadPoolQueueCensus.Of(queued, ThreadPoolQueueCensus.MaxItems, linkResolver: null);
            real.Groups.Select(group => group.Producer)
                .Count(key => key.StartsWith("System.IO.Stream.<CopyToAsync>", StringComparison.Ordinal)).Should().Be(1);
        }
        finally
        {
            gate.SetResult(0);
        }
    }

    /// <summary>A pool item that parks its worker until the test releases it.</summary>
    private sealed class Parker
    {
        private int released;

        public void Park(object? state) => SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, TimeSpan.FromSeconds(30));

        public void Release() => Volatile.Write(ref released, 1);
    }

    /// <summary>
    /// The same reading on the REAL ThreadPool for runtime-async continuations: every worker is
    /// parked, 200 BCL <c>Stream.CopyToAsync</c> calls suspend on one gate, and opening the gate
    /// queues all 200 continuations behind the parked items. The census must count them under the
    /// method that resumes. Negative control: before the gate opens none is counted there, and
    /// without the runtime-async branch they are counted under <c>DispatchContinuations</c>.
    ///
    /// <para><b>Why a drain cannot shrink the exact 200.</b> More parkers are queued than the pool
    /// has threads, a dequeued parker holds its thread until release, and the global queue is FIFO.
    /// So while any parker is still queued no worker can reach a continuation: one queued globally
    /// is behind the parkers, and one in the test thread's local queue is stolen only from an empty
    /// global queue. The test ASSERTS that precondition after the census
    /// (<c>ThreadPool.ThreadCount &lt; parkers</c>, because dequeued parkers ≤ live threads), so a pool
    /// that injected past the surplus fails by saying so, never by a short count.</para>
    /// </summary>
    [Fact]
    public void OnTheRealThreadPool_QueuedRuntimeAsyncContinuations_AreNamedByTheirMethod()
    {
        const string copyToAsync = "System.IO.Stream.<CopyToAsync>";
        var parker = new Parker();
        var parkers = ThreadPool.ThreadCount + 64;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            for (var i = 0; i < parkers; i++)
                ThreadPool.UnsafeQueueUserWorkItem(parker.Park, null);
            for (var i = 0; i < 200; i++)
                _ = new PendingStream(gate.Task).CopyToAsync(Stream.Null);

            var before = ThreadPoolQueueCensus.Take();
            before.Failure.Should().BeNull();
            before.Groups.Where(group => group.Producer.StartsWith(copyToAsync, StringComparison.Ordinal))
                .Sum(group => group.Count).Should().Be(0, "no continuation has been queued yet");

            gate.SetResult(0);
            var after = ThreadPoolQueueCensus.Take();
            ThreadPool.ThreadCount.Should().BeLessThan(parkers,
                "parkers must still be queued after the census, or a worker could have reached a continuation");
            after.Failure.Should().BeNull();
            after.Groups.Where(group => group.Producer.StartsWith(copyToAsync, StringComparison.Ordinal))
                .Sum(group => group.Count).Should().Be(200,
                    "every continuation sits behind queued parkers, keyed by the method that resumes");
            after.Groups.Should().NotContain(group => group.Producer.Contains("DispatchContinuations", StringComparison.Ordinal));
        }
        finally
        {
            gate.TrySetResult(0);
            parker.Release();
        }
    }
}
