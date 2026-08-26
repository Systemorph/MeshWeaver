using System;
using System.Collections.Generic;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the two unbounded <c>GetHashCode</c> cycles that killed portal pods with an uncatchable
/// <c>StackOverflowException</c> (#2163/#2164/#2172/#2173/#2174/#2175 and #2170/#2171).
///
/// <para><b>Cycle 1 — record-synthesized structural hashing over a back-reference.</b>
/// <c>SynchronizationStream&lt;T&gt;</c> is a <c>record</c>, so the compiler synthesized a
/// <c>GetHashCode</c> walking every instance field — including <c>Configuration</c>.
/// <c>StreamConfiguration&lt;T&gt;</c>'s primary-ctor property <c>Stream</c> is a back-pointer to
/// the owning stream (<c>new StreamConfiguration&lt;T&gt;(this)</c>), so hashing a stream ran
/// <c>SynchronizationStream.GetHashCode → StreamConfiguration.GetHashCode →
/// EqualityComparer&lt;ISynchronizationStream&lt;T&gt;&gt;.Default.GetHashCode(Stream) → …</c>
/// forever — exactly the captured production stack.</para>
///
/// <para><b>Cycle 2 — hand-written structural hashing over an arbitrary object graph.</b>
/// <c>InstanceCollection.GetHashCode</c> aggregated the hashes of its VALUES (arbitrary user
/// objects), <c>EntityStore.GetHashCode</c> aggregated its collections, and
/// <c>ChangeItem&lt;T&gt;</c>'s synthesized hash walked its <c>Value</c> — so a value that
/// transitively holds the store closed the loop
/// <c>InstanceCollection → EntityStore → ChangeItem → InstanceCollection</c>.</para>
///
/// <para><b>The fix is semantic, not a depth guard.</b> A stream is a live identity object, so it
/// hashes by reference; a configuration hashes its own values and treats its owner as a reference;
/// stores and collections hash their KEYS and counts, never the instance values they carry.</para>
///
/// <para>A <c>StackOverflowException</c> cannot be caught in .NET, so these tests never let the
/// recursion run free: <see cref="ReentrancyProbe"/> is the cycle-closing value and it THROWS once
/// it has been re-entered, turning a pre-fix regression into a clean red test instead of a dead
/// test host. The assertion is stronger than "it terminated" — it is that the instance value was
/// never traversed at all.</para>
/// </summary>
public class HashRecursionTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const int ReentrancyBudget = 8;

    /// <summary>
    /// A value stored inside an <see cref="InstanceCollection"/> that points back at the graph
    /// containing it — the shape a running mesh actually produces. Hashing it re-enters the graph,
    /// so it counts its own invocations and refuses to recurse past
    /// <see cref="ReentrancyBudget"/>: pre-fix this surfaces as a deterministic exception naming
    /// the defect, instead of a StackOverflow that takes the test host down with it.
    /// </summary>
    private sealed class ReentrancyProbe
    {
        /// <summary>The back-pointer that closes the cycle (a ChangeItem holding the store).</summary>
        public object? Back { get; set; }

        /// <summary>How many times structural hashing has reached this instance value.</summary>
        public int HashCalls { get; private set; }

        /// <summary>Reference identity — this probe measures hashing, not equality.</summary>
        public override bool Equals(object? obj) => ReferenceEquals(this, obj);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCalls++;
            if (HashCalls > ReentrancyBudget)
                throw new InvalidOperationException(
                    $"Instance-value hashing re-entered {HashCalls} times: the store/collection "
                    + "hash is walking its VALUES again and would StackOverflow in production "
                    + "(#2170/#2171).");
            return Back?.GetHashCode() ?? 0;
        }
    }

    /// <summary>
    /// Builds the exact production cycle: an <see cref="EntityStore"/> whose
    /// <see cref="InstanceCollection"/> holds a value that points back at a
    /// <see cref="ChangeItem{TStream}"/> carrying that same store.
    /// </summary>
    private static (EntityStore Store, InstanceCollection Collection, ChangeItem<EntityStore> Change, ReentrancyProbe Probe)
        BuildCyclicGraph()
    {
        var probe = new ReentrancyProbe();
        var collection = new InstanceCollection(
            new Dictionary<object, object> { ["id-1"] = probe, ["id-2"] = "a plain value" });
        var store = new EntityStore(
            new Dictionary<string, InstanceCollection> { ["Probes"] = collection });
        var change = new ChangeItem<EntityStore>(store, "stream-1", 1);
        probe.Back = change;   // ← closes the loop
        return (store, collection, change, probe);
    }

    // ---------------------------------------------------------------------------------------
    // Cycle 2 — InstanceCollection / EntityStore / ChangeItem
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Hashing anywhere on the cyclic store graph terminates, and does so because the instance
    /// VALUES are never traversed — the property that makes it bounded for ANY object graph, not
    /// just this one.
    /// </summary>
    [HubFact]
    public void CyclicStoreGraph_HashesWithoutTraversingInstanceValues()
    {
        var (store, collection, change, probe) = BuildCyclicGraph();

        // Each of these was an entry point into the unbounded walk in production.
        _ = collection.GetHashCode();
        _ = store.GetHashCode();
        _ = change.GetHashCode();

        probe.HashCalls.Should().Be(0,
            "InstanceCollection/EntityStore/ChangeItem must hash their KEYS and counts, never the "
            + "arbitrary instance values — a value that reaches back into the store is exactly how "
            + "the production StackOverflow (#2170/#2171) was reached");
    }

    /// <summary>
    /// The hash must be stable, not merely terminating — a hash that varies between calls silently
    /// corrupts every dictionary it is used in.
    /// </summary>
    [HubFact]
    public void CyclicStoreGraph_HashIsStableAcrossCalls()
    {
        var (store, collection, change, _) = BuildCyclicGraph();

        collection.GetHashCode().Should().Be(collection.GetHashCode());
        store.GetHashCode().Should().Be(store.GetHashCode());
        change.GetHashCode().Should().Be(change.GetHashCode());
    }

    /// <summary>
    /// The seedless <c>Aggregate((x, y) =&gt; x ^ y)</c> in <c>EntityStore.GetHashCode</c> threw
    /// <see cref="InvalidOperationException"/> ("Sequence contains no elements") for a store with
    /// no collections — a latent crash on the emptiest possible input.
    /// </summary>
    [HubFact]
    public void EmptyEntityStore_Hashes_WithoutThrowing()
    {
        Action hashEmptyStore = () => { _ = new EntityStore().GetHashCode(); };
        hashEmptyStore.Should().NotThrow(
            "an empty store is a legal store; the hash used Aggregate with no seed");

        Action hashEmptyCollection = () => { _ = new InstanceCollection().GetHashCode(); };
        hashEmptyCollection.Should().NotThrow();
    }

    /// <summary>
    /// The hash/equals contract: equal objects hash equal. The hashes got WEAKER (keys and counts
    /// rather than values) — weaker is fine, inconsistent is not.
    /// </summary>
    [HubFact]
    public void EqualStoresAndCollections_HashEqual()
    {
        InstanceCollection Collection() => new(
            new Dictionary<object, object> { ["a"] = "one", ["b"] = "two" });
        EntityStore Store() => new(
            new Dictionary<string, InstanceCollection> { ["C"] = Collection() });

        var c1 = Collection();
        var c2 = Collection();
        c1.Equals(c2).Should().BeTrue();
        c1.GetHashCode().Should().Be(c2.GetHashCode());

        var s1 = Store();
        var s2 = Store();
        s1.Equals(s2).Should().BeTrue();
        s1.GetHashCode().Should().Be(s2.GetHashCode());

        var ch1 = new ChangeItem<EntityStore>(s1, "stream-1", 7);
        var ch2 = new ChangeItem<EntityStore>(s2, "stream-1", 7);
        ch1.Equals(ch2).Should().BeTrue(
            "value equality drives the SetCurrent dedup and is deliberately unchanged");
        ch1.GetHashCode().Should().Be(ch2.GetHashCode());
    }

    /// <summary>
    /// The weakened hash still has to discriminate on the things it claims to hash — otherwise it
    /// degenerates every dictionary into a linked list.
    /// </summary>
    [HubFact]
    public void DifferentKeysOrSizes_HashDifferently()
    {
        var a = new InstanceCollection(new Dictionary<object, object> { ["a"] = "x" });
        var b = new InstanceCollection(new Dictionary<object, object> { ["b"] = "x" });
        var ab = new InstanceCollection(
            new Dictionary<object, object> { ["a"] = "x", ["b"] = "x" });

        a.GetHashCode().Should().NotBe(b.GetHashCode(), "different keys");
        a.GetHashCode().Should().NotBe(ab.GetHashCode(), "different size");

        var s1 = new EntityStore(new Dictionary<string, InstanceCollection> { ["One"] = a });
        var s2 = new EntityStore(new Dictionary<string, InstanceCollection> { ["Two"] = a });
        s1.GetHashCode().Should().NotBe(s2.GetHashCode(), "different collection names");
    }

    // ---------------------------------------------------------------------------------------
    // Cycle 1 — SynchronizationStream / StreamConfiguration
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A stream and its configuration hash without recursing through the configuration's
    /// back-pointer to the stream — and the stream compares by REFERENCE, because two distinct
    /// live streams (each owning its own ReplaySubject, hub and disposal state) are never the same
    /// stream.
    /// </summary>
    [HubFact]
    public void Stream_And_ItsConfiguration_HashByIdentity_NotStructurally()
    {
        var host = GetHost();
        using var stream = new SynchronizationStream<EntityStore>(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("Probes", "id-1"),
            new ReduceManager<EntityStore>(host),
            null);

        // Pre-fix each of these entered the unbounded stream ↔ configuration cycle
        // and never returned.
        var streamHash = stream.GetHashCode();
        var configHash = stream.Configuration.GetHashCode();

        streamHash.Should().Be(stream.GetHashCode(), "an identity hash must be stable");
        configHash.Should().Be(stream.Configuration.GetHashCode());

        // A configuration constructed independently over the same stream also terminates —
        // the back-pointer is never traversed structurally, whoever holds it.
        _ = new StreamConfiguration<EntityStore>(stream).GetHashCode();

        stream.Equals(stream).Should().BeTrue();
    }

    /// <summary>
    /// Reference identity is the SEMANTICS, not just the mechanism: two streams built with the
    /// same identity, host and reference are still two different live streams.
    /// </summary>
    [HubFact]
    public void TwoDistinctStreams_AreNeverEqual()
    {
        var host = GetHost();
        using var a = new SynchronizationStream<EntityStore>(
            new StreamIdentity(host.Address, null), host,
            new EntityReference("Probes", "id-1"), new ReduceManager<EntityStore>(host), null);
        using var b = new SynchronizationStream<EntityStore>(
            new StreamIdentity(host.Address, null), host,
            new EntityReference("Probes", "id-1"), new ReduceManager<EntityStore>(host), null);

        a.Equals(b).Should().BeFalse(
            "a stream owns a ReplaySubject, mutable current state, a hosted hub and disposal state "
            + "— distinct instances are distinct streams");
        (a == b).Should().BeFalse();
    }

    /// <summary>
    /// <c>StreamConfiguration</c> keeps value semantics over its OWN settings (it is used as a
    /// <c>with</c>-based builder throughout: WithClientId / WithSubscriber / AsInfrastructure / …),
    /// while its owning stream participates by reference only.
    /// </summary>
    [HubFact]
    public void StreamConfiguration_KeepsValueSemantics_OverItsOwnSettings()
    {
        var host = GetHost();
        using var stream = new SynchronizationStream<EntityStore>(
            new StreamIdentity(host.Address, null), host,
            new EntityReference("Probes", "id-1"), new ReduceManager<EntityStore>(host), null);

        // Derive both from the same base so the delegate-valued settings are literally shared —
        // the difference under test is the client id, nothing else.
        var baseConfig = new StreamConfiguration<EntityStore>(stream);
        var sameA = baseConfig.WithClientId("client-a");
        var sameB = baseConfig.WithClientId("client-a");
        var other = baseConfig.WithClientId("client-b");

        sameA.Equals(sameB).Should().BeTrue(
            "the settings are identical and the owner is the same stream");
        sameA.GetHashCode().Should().Be(sameB.GetHashCode());
        sameA.Equals(other).Should().BeFalse("the client id differs");
        sameA.GetHashCode().Should().NotBe(other.GetHashCode());

        baseConfig.AsInfrastructure().Equals(baseConfig).Should().BeFalse();
        baseConfig.ReturnNullWhenNotPresent().Equals(baseConfig).Should().BeFalse();
    }
}
