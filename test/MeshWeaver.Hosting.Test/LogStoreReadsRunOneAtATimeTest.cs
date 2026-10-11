using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Range reads against the log store run ONE at a time per process: the
/// <see cref="IoPoolNames.LogStore"/> pool has one slot by default, so a burst of wide reads queues in
/// the caller instead of being refused by the store (MeshWeaver#5717 —
/// <c>429 too many outstanding requests</c>, <c>Doc/Architecture/ControlledIoPooling</c>).
///
/// <para>The leaves park on a volatile flag under a bounded
/// <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/> and are released in a <c>finally</c>, so
/// the bound is observed while it is being tested: a leaf that started beyond it is counted by the
/// leaves themselves, never inferred from timing.</para>
/// </summary>
public class LogStoreReadsRunOneAtATimeTest
{
    private const int Reads = 4;

    private sealed class Probe
    {
        public volatile int Release;
        public int Running;
        public int MaxRunning;
        public int Ran;
    }

    private static IObservable<int> ParkedRead(IIoPool pool, Probe probe) =>
        pool.InvokeBlocking(_ =>
        {
            var now = Interlocked.Increment(ref probe.Running);
            int seen;
            while ((seen = Volatile.Read(ref probe.MaxRunning)) < now
                   && Interlocked.CompareExchange(ref probe.MaxRunning, now, seen) != seen) { }
            SpinWait.SpinUntil(() => probe.Release != 0, TimeSpan.FromSeconds(20));
            Interlocked.Decrement(ref probe.Running);
            return Interlocked.Increment(ref probe.Ran);
        });

    private static async Task<Probe> RunParked(IIoPool pool, int expectedAtOnce, CancellationToken ct)
    {
        var probe = new Probe();
        try
        {
            var all = Enumerable.Range(0, Reads).Select(_ => ParkedRead(pool, probe).Await(ct)).ToArray();

            SpinWait.SpinUntil(() => Volatile.Read(ref probe.Running) == expectedAtOnce, TimeSpan.FromSeconds(10))
                .Should().BeTrue($"{expectedAtOnce} read(s) must be running while the rest queue");
            // Negative "nothing happened" check, bounded: with the slots taken, a further read can
            // only start if the cap is broken.
            SpinWait.SpinUntil(() => Volatile.Read(ref probe.Running) > expectedAtOnce, TimeSpan.FromMilliseconds(300))
                .Should().BeFalse($"no more than {expectedAtOnce} read(s) may run at once");

            probe.Release = 1;
            await Task.WhenAll(all);
        }
        finally
        {
            probe.Release = 1;
        }
        return probe;
    }

    [Fact(Timeout = 60_000)]
    public async Task ABurstOfLogStoreReads_RunsOneAtATime_AndEveryReadStillRuns()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions());
        var probe = await RunParked(registry.Get(IoPoolNames.LogStore), 1, TestContext.Current.CancellationToken);

        probe.MaxRunning.Should().Be(1, "the log store sees one range read per process at a time");
        probe.Ran.Should().Be(Reads, "a queued read is delayed, never dropped or refused");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the same burst through <see cref="IoPoolNames.Http"/>, the pool these reads
    /// used before: every read of the burst runs at once. That is the fan-out the store refused, and
    /// without it the "one at a time" above could be an instrument that only ever counts to one.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Control_TheSameBurstThroughTheHttpPool_RunsAllAtOnce()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions());
        var probe = await RunParked(registry.Get(IoPoolNames.Http), Reads, TestContext.Current.CancellationToken);

        probe.MaxRunning.Should().Be(Reads, "control: the general HTTP pool admits the whole burst together");
    }

    [Fact]
    public void TheCapIsAnOption_SoAHostWithALargerStoreCanRaiseIt()
    {
        new IoPoolOptions().MaxConcurrencyFor(IoPoolNames.LogStore).Should().Be(1);
        new IoPoolOptions { LogStore = 3 }.MaxConcurrencyFor(IoPoolNames.LogStore).Should().Be(3);
    }
}
