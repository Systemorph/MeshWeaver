using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using MeshWeaver.ShortGuid;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the StreamMessage routing hot path: <c>GetHostedHub(sync/{id}, Never)</c> is a pure
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> read keyed by <see cref="AddressComparer"/>.
/// <c>RouteStreamMessage</c> probes it per parent-chain level for EVERY inbound
/// <c>DataChangedEvent</c>. Under multi-round chat load (many accumulated sync streams still being
/// fanned change events while a circuit is DOWN-but-not-closed) this probe runs at a very high rate.
///
/// <para>The defect: <see cref="AddressComparer.GetHashCode"/> / <see cref="AddressComparer.Equals"/>
/// materialise <see cref="Address.Id"/> — a LINQ <c>Segments.Skip(1)</c> + <c>string.Join</c> that
/// ALLOCATES on every probe. A dotnet-stack of the wedged e2e pod showed this as the ONLY CPU_TIME
/// frame (<c>RouteStreamMessage → GetHostedHub → AddressComparer.GetHashCode</c>), pegging ~1.2
/// cores and starving the Blazor circuit's SignalR keepalive → circuit drop → composer vanishes.</para>
///
/// <para>The fix makes the comparer allocation-free (hash/compare directly over segments, no
/// materialised <c>Id</c>). This asserts the per-lookup allocation is bounded near-zero and the
/// lookup cost does NOT scale with the number of live sync hubs (O(1) in N).</para>
/// </summary>
public class StreamRoutingHotPathTest
{
    private static Address SyncAddress(string id) => new(SynchronizationAddressType, id);
    private const string SynchronizationAddressType = "sync";

    private static ConcurrentDictionary<Address, object> BuildHostedHubMap(int count)
    {
        // Mirrors HostedHubsCollection.messageHubs exactly: keyed by AddressComparer.Instance.
        var map = new ConcurrentDictionary<Address, object>(new AddressComparer());
        for (var i = 0; i < count; i++)
            map[SyncAddress(Guid.NewGuid().AsString())] = new object();
        return map;
    }

    private static long MeasureAllocPerLookup(ConcurrentDictionary<Address, object> map, int lookups)
    {
        // Each iteration builds a FRESH miss target (as RouteStreamMessage does per message —
        // SynchronizationAddress.Create allocates a new Address per DataChangedEvent) and probes.
        var targets = new Address[lookups];
        for (var i = 0; i < lookups; i++)
            targets[i] = SyncAddress(Guid.NewGuid().AsString()); // guaranteed miss (fresh guid)

        // Warm up JIT + comparer.
        for (var i = 0; i < 1000; i++)
            map.TryGetValue(targets[i % lookups], out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < lookups; i++)
            map.TryGetValue(targets[i], out _);
        var after = GC.GetAllocatedBytesForCurrentThread();
        // Subtract nothing but the pre-allocated targets array (built above, outside the window).
        return (after - before) / lookups;
    }

    [Fact]
    public void MissLookup_IsAllocationFree_AndBounded()
    {
        const int lookups = 200_000;

        // The map size (number of live sync hubs) must NOT change per-lookup cost.
        var small = BuildHostedHubMap(50);
        var large = BuildHostedHubMap(5_000);

        var allocSmall = MeasureAllocPerLookup(small, lookups);
        var allocLarge = MeasureAllocPerLookup(large, lookups);

        var sw = Stopwatch.StartNew();
        MeasureAllocPerLookup(large, lookups);
        sw.Stop();
        var nsPerLookup = sw.Elapsed.TotalMilliseconds * 1_000_000 / lookups;

        Console.WriteLine($"[HOTPATH] bytes/lookup small(N=50)={allocSmall}  large(N=5000)={allocLarge}  ~{nsPerLookup:F1} ns/lookup");

        // O(1) in N: cost independent of the number of live sync hubs.
        allocLarge.Should().Be(allocSmall, "per-lookup allocation must not scale with the number of live sync hubs");
        // Allocation-free routing probe: the whole point of the fix.
        allocLarge.Should().Be(0, "the sync-hub routing probe must not allocate — an allocating hot comparer pegs the drain thread and starves the SignalR keepalive");
    }

    // ---- Semantics: the allocation-free comparer must be IDENTICAL to the old "Type + Id(join)" one ----

    [Fact]
    public void Equals_MatchesTypePlusJoinedId_Exactly()
    {
        var cmp = new AddressComparer();

        // Same type + same joined tail, DIFFERENT segmentation → equal (Id = "a/b" both ways).
        var multi = new Address("node", "a", "b");   // Segments = [node, a, b]
        var single = new Address("node", "a/b");      // Segments = [node, a/b] (embedded slash)
        cmp.Equals(multi, single).Should().BeTrue("Type 'node' + Id 'a/b' match regardless of segmentation");
        cmp.GetHashCode(multi).Should().Be(cmp.GetHashCode(single), "equal addresses must hash equally");

        // This join-equivalence is exercised for real: kernelExec/{path} ids embed '/'.
        var exec = new Address("kernelExec", "activity/x/y");
        var execParsed = (Address)"kernelExec/activity/x/y"; // parsed → [kernelExec, activity, x, y]
        cmp.Equals(exec, execParsed).Should().BeTrue();
        cmp.GetHashCode(exec).Should().Be(cmp.GetHashCode(execParsed));

        // Different id → not equal.
        cmp.Equals(new Address("node", "a", "b"), new Address("node", "a", "c")).Should().BeFalse();
        // Different type → not equal.
        cmp.Equals(new Address("sync", "x"), new Address("node", "x")).Should().BeFalse();
        // Single-segment addresses.
        cmp.Equals(new Address("mesh"), new Address("mesh")).Should().BeTrue();
        cmp.Equals(new Address("mesh"), new Address("app")).Should().BeFalse();
        // Host is ignored by AddressComparer (Type + Id only).
        var bare = new Address("sync", "x");
        var hosted = new Address("sync", "x").WithHost(new Address("portal", "c1"));
        cmp.Equals(bare, hosted).Should().BeTrue("AddressComparer ignores Host");
        cmp.GetHashCode(bare).Should().Be(cmp.GetHashCode(hosted));
    }

    [Fact]
    public void HashConsistency_EqualImpliesSameHash_OverManySamples()
    {
        var cmp = new AddressComparer();
        // A fresh guid id parsed vs multi-arg must round-trip through a dictionary correctly.
        var dict = new System.Collections.Generic.Dictionary<Address, int>(cmp);
        for (var i = 0; i < 2000; i++)
        {
            var a = i.ToString();
            var b = Guid.NewGuid().AsString();
            var stored = new Address("node", a + "/" + b);          // [node, "a/b"]
            var lookup = new Address("node", a, b);                  // [node, a, b] — Id joins to "a/b"
            dict[stored] = i;
            dict.TryGetValue(lookup, out var found).Should().BeTrue("join-equivalent lookup must hit");
            found.Should().Be(i);
        }
    }
}
