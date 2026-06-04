using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Unit tests for <see cref="ReadConcurrencyGate"/> — the per-adapter read-concurrency
/// cap that keeps a synced-query fan-out storm from draining the connection pool
/// (prod 2026-06-04: "connection pool has been exhausted, currently 50"). Pure
/// concurrency mechanism, no database.
/// </summary>
public class ReadConcurrencyGateTest
{
    [Fact]
    public async Task AcquireAsync_NeverExceedsMaxConcurrency()
    {
        const int max = 3;
        const int workers = 40;
        using var gate = new ReadConcurrencyGate(max);

        var current = 0;
        var peak = 0;
        var sync = new object();

        async Task Worker()
        {
            using var slot = await gate.AcquireAsync(CancellationToken.None);
            lock (sync)
            {
                current++;
                if (current > peak) peak = current;
            }
            await Task.Delay(25);
            lock (sync) { current--; }
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker()));

        // Safety invariant: the gate must NEVER admit more than max concurrent readers.
        // With 40 workers each holding 25ms, an ungated run would peak far above 3.
        peak.Should().BeLessThanOrEqualTo(max,
            "the gate must never admit more than MaxConcurrency readers at once");
        peak.Should().BeGreaterThan(1,
            "with 40 contending workers the gate should actually reach its cap (proves it gates, not serializes)");
        gate.CurrentCount.Should().Be(max, "every slot must be released after its work completes");
    }

    [Fact]
    public void MaxConcurrency_FlooredAtOne()
    {
        using var gate = new ReadConcurrencyGate(0);
        gate.MaxConcurrency.Should().Be(1, "a non-positive limit floors to 1, never 0 (which would deadlock all reads)");
    }

    [Fact]
    public async Task Releaser_IsIdempotent_NoOverRelease()
    {
        using var gate = new ReadConcurrencyGate(1);
        var slot = await gate.AcquireAsync(CancellationToken.None);
        gate.CurrentCount.Should().Be(0);

        slot.Dispose();
        gate.CurrentCount.Should().Be(1);

        // Double-dispose must NOT over-release (would corrupt the semaphore count).
        slot.Dispose();
        gate.CurrentCount.Should().Be(1, "the releaser is idempotent");
    }
}
