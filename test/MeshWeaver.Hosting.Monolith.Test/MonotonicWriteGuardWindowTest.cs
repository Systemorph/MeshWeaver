#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Hosting.Monolith.TestBase;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// DETERMINISTIC pin of the write-rollback window in <c>MonotonicWriteGuardStorageAdapter</c> —
/// the mechanism behind the post-recycle store rollback that
/// <see cref="StaleActivationSeedRollbackTest"/> and <see cref="StaleActivationDurableFirstSeedTest"/>
/// sampled as a CI flake (issue #826: <c>Expected 2 to be greater than 7000</c>, and
/// <c>Expected value to be 9000 … but found 1</c> — an ACKED out-of-band advance whose row was
/// back at the create-time version by the very next read).
///
/// <para><b>The defect.</b> The guard's two-stage design uses a per-path in-process high-water
/// mark as a cheap FILTER: a write at or above the mark skips the verification read. But the mark
/// was recorded in a <c>.Do(Observe)</c> hung off the COMPLETED inner write — i.e. AFTER the row
/// had already been mutated. <c>InMemoryStorageAdapter.Write</c> (like every backend that
/// publishes <see cref="IStorageAdapter.Changes"/>) fans the change notification out from INSIDE
/// that write, so there is a real window in which the store already holds the NEW version while
/// the guard still advertises the OLD mark. Any writer that lands in that window — and the
/// framework's own topology puts writers there: every per-node hub subscribes to
/// <c>storage.Changes</c> and reconciles its own node from the notification — presents a stale
/// snapshot whose version is at-or-below the stale mark, skips verification, and OVERWRITES the
/// newer row. The store is then genuinely behind; a hub reactivating on it seeds the create-time
/// node and mints one above THAT (<c>version=2</c>), which the guard now accepts because its
/// verification read confirms the (rolled-back) row really is older. Acknowledged writes are
/// destroyed exactly as in the production shape the guard was written to stop.</para>
///
/// <para><b>Why this is deterministic where the load repro is not.</b> The flaky siblings have to
/// RACE some component into that window under CI scheduling (~4 occurrences in 40 runs; it does
/// not reproduce on an idle multi-core dev box at any iteration count). Here the window is entered
/// BY CONSTRUCTION: the test subscribes to the framework's own <c>IStorageAdapter.Changes</c> feed
/// — which is published from inside the write, before the guard records its mark — and issues the
/// stale write from that notification. No sleeps, no timing budget, no retry.</para>
///
/// <para>🚨 <b>And the assertion is SEQUENCED on that write, not merely placed after it.</b> The
/// durable read is composed off the forced write's completion (an <see cref="AsyncSubject{T}"/>
/// the write is subscribed into), so the store can never be inspected before the rollback had its
/// chance. Reverting the fix proves this test CAN fail; composing the read is what makes it
/// ALWAYS fail when it should — a pin that could pass without the forced write having landed
/// would be a lucky observation, which is precisely the property this whole change relies on.</para>
///
/// <para><b>Fail-without:</b> the stale write takes the unverified fast path (<c>version 1 >=
/// mark 1</c>) and, once it has settled, the durable row is the create-time node.
/// <b>Pass-with:</b> the guard claims the mark BEFORE mutating the row, so the same settled write
/// was verified against durable truth and refused; the store never moves backward.</para>
/// </summary>
public class MonotonicWriteGuardWindowTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private JsonSerializerOptions JsonOptions => Mesh.JsonSerializerOptions;

    /// <summary>Reads the node straight out of durable storage — no hub, no stream, no cache.</summary>
    private IObservable<MeshNode?> ReadDurable(string path) => Storage.Read(path, JsonOptions);

    [Fact(Timeout = 55_000)]
    public async Task StaleWriteInsideTheHighWaterWindow_IsRefused_AndTheStoreNeverMovesBackward()
    {
        var id = $"guard-window-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created", NodeType = "Markdown", State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        var created = await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "created" });
        Output.WriteLine($"[created] version={created!.Version}");
        created.Version.Should().Be(1, "the create is revision 1 — the stale snapshot below rides that version");

        // The stale snapshot a recycled/lagging writer still holds: the create-time node,
        // unchanged Version. This is the shape that rolls a store back.
        var stale = created with { Name = "stale-snapshot" };

        const long durableVersion = 7000L;

        // 🚨 The stale write's COMPLETION, as an observable — the durable read below is composed
        // OFF this, never merely sequenced after it in wall-clock terms. Without that composition
        // the assertion could observe the store BEFORE the forced write landed and pass for the
        // wrong reason (a pin that can pass without the thing it pins having happened is not a
        // pin). AsyncSubject is what makes it airtight in both directions: it replays its final
        // value to a late subscriber, so the read is correctly ordered whether the forced write
        // settles inside the advance write (the synchronous in-memory fan-out) or after it.
        var staleWriteSettled = new AsyncSubject<MeshNode?>();

        // 🚨 The forcing. IStorageAdapter.Changes is published from INSIDE the write (see
        // InMemoryStorageAdapter.Write: `_nodes[path] = node` then `_changes.OnNext(...)`), so this
        // handler runs while the row already carries durableVersion and the guard has not yet
        // recorded it. That is precisely the window a per-node hub's own storage.Changes
        // subscriber (MeshDataSourceExtensions.SubscribeToOwnDeletion) occupies in production.
        using var armed = Storage.Changes
            .Where(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)
                        && c.Entity is MeshNode { Version: durableVersion })
            .Take(1)
            .Subscribe(
                _ => Storage.Write(stale, JsonOptions).Subscribe(staleWriteSettled),
                ex => staleWriteSettled.OnError(ex));

        await Storage.Write(created with
        {
            Name = "durable-advance", Version = durableVersion
        }, JsonOptions).Should().Within(30.Seconds()).Emit();

        // Read durable state ONLY once the forced stale write has SETTLED — the read is chained
        // off its completion, so "the store was not rolled back" can never be asserted against a
        // moment before the rollback had its chance. A forcing that never fired never completes
        // this subject and the test fails here rather than passing blind.
        var durable = await staleWriteSettled
            .Do(n => Output.WriteLine(
                $"[stale write] settled result={(n is null ? "null" : $"v{n.Version}/{n.Name}")}"))
            .SelectMany(_ => ReadDurable(path))
            .Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"[durable] {(durable is null ? "null" : $"v{durable.Version}/{durable.Name}")}");

        durable.Should().NotBeNull();
        durable!.Version.Should().BeGreaterThanOrEqualTo(durableVersion,
            "MeshNode.Version is the node's forward-only revision counter and the write-integrity "
            + "chain's whole contract is that the store cannot move backward — a stale snapshot that "
            + "lands while the guard's high-water mark still advertises the PRE-write version must be "
            + "verified against durable truth, not waved through on the unverified fast path");
        durable.Name.Should().Be("durable-advance",
            "the refused write must leave the newer row intact — content included");
    }
}
