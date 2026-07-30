#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// DETERMINISTIC repro of the 2026-07-30 memex-cloud thread wedge: a LIVE owner hub whose durable
/// row has been advanced by another lineage (a second activation's clock, a stale-seeded sibling —
/// here: an out-of-band <see cref="IStorageAdapter"/> write) keeps minting versions BELOW the
/// stored row, so <c>MonotonicWriteGuardStorageAdapter</c> refuses every persistence write it
/// makes — including the recovery paths ("Init watchdog … forcing Idle" refused every 90 s,
/// `incoming Version=834 is BELOW the stored Version=2423`). The refusal emits the STORED node,
/// which every caller used to treat as success, so the owner never learned and the fork was
/// permanent.
///
/// <para><b>The fix under test</b> (owner-side, no watchdog, no frame-version floor):
/// <c>HandleSaveMeshNode</c> / <c>MeshNodeTypeSource.FlushPendingWrites</c> detect the refusal
/// (<c>saved.Version &gt; written.Version</c>) and REBASE the owner onto the durable truth through
/// the sanctioned own-write path (<c>AdoptDurableTruth</c>); <c>UpdateOwn</c> and the
/// <c>UpdateImpl</c> re-stamp floor their mints on the adopted version, so the next write lands
/// above the stored row. One bounded reconciliation cycle per detected conflict — never a timer.</para>
///
/// <para><b>Loss semantics pinned here:</b> the write that collided with the foreign advance is
/// condemned by the guard (durable truth wins — that is its contract); what the fix guarantees is
/// that the fork HEALS: the owner converges to the durable version and every write after
/// convergence lands. Before the fix this test times out: no write ever becomes durable again.</para>
/// </summary>
public class RefusedWriteOwnerRebaseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private JsonSerializerOptions JsonOptions => Mesh.JsonSerializerOptions;

    /// <summary>Reads the node straight out of durable storage — bypassing every hub, stream and
    /// cache — so an assertion about DURABILITY cannot be satisfied by an in-RAM snapshot.</summary>
    private IObservable<MeshNode?> ReadDurable(string path) => Storage.Read(path, JsonOptions);

    [Fact(Timeout = 55_000)]
    public async Task LiveOwnerBehindTheDurableRow_RebasesOnRefusal_AndSubsequentWritesLand()
    {
        var id = $"refused-rebase-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        // 1. Create and keep the owner hub LIVE (no recycle — this is the wedged-live-hub shape,
        //    not the stale-reactivation one StaleActivationSeedRollbackTest covers).
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created", NodeType = "Markdown", State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        var created = await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "created" });
        Output.WriteLine($"[created] version={created!.Version}");

        // 2. The durable row advances OUT OF BAND to a far-ahead lineage — the fork. Straight
        //    through IStorageAdapter (forward write: the guard passes it and raises its
        //    high-water mark); no IMeshChangeFeed publish, so the live hub keeps serving and
        //    minting from its own, now-behind clock.
        var durableVersion = created.Version + 5000L;
        await Storage.Write(created with
        {
            Name = "foreign-advance", Version = durableVersion
        }, JsonOptions).Should().Within(30.Seconds()).Emit();

        (await ReadDurable(path).Should().Within(30.Seconds()).Emit())!
            .Version.Should().Be(durableVersion, "the fork must be durable before the owner writes again");

        // 3. An own write through the LIVE hub. It mints from the behind clock, the persistence
        //    sampler's save is REFUSED by the MonotonicWriteGuard — and the refusal detection
        //    must now rebase the owner onto the durable truth. (This write's content is condemned
        //    by the guard — durable truth wins; see the class doc for the loss semantics.)
        var client = GetClient(c => c.AddData());
        client.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Name = "collided-write" })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[collided-write error] {ex.Message}"));

        // 4. CONVERGENCE: the owner's live stream must come up to (past) the durable version —
        //    the rebase echo. Before the fix the stream stays on the behind lineage forever.
        var converged = await client.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Should().Within(30.Seconds())
            .Match(n => n.Version >= durableVersion,
                "a refused backward write must rebase the owner onto the durable truth "
                + "(AdoptDurableTruth) — before the fix the owner never learns and keeps minting "
                + "below the stored row forever");
        Output.WriteLine($"[converged] name={converged.Name} version={converged.Version}");

        // 5. HEALED: a write issued after convergence mints above the stored row and becomes
        //    DURABLE. Before the fix this is the permanent wedge — no write ever lands again
        //    (the prod shape: the watchdog's forced-Idle refused every 90 s for hours).
        client.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Name = "post-rebase" })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[post-rebase error] {ex.Message}"));

        var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => ReadDurable(path))
            .Should().Within(30.Seconds()).Match(n => n is { Name: "post-rebase" });

        Output.WriteLine($"[persisted] name={persisted!.Name} version={persisted.Version}");
        persisted.Version.Should().BeGreaterThan(durableVersion,
            "after the rebase the owner's clock is at-or-above the stored row, so its mints are "
            + "strictly above it — the write lands instead of being refused");

        // 6. And the durable row never went backward at any point.
        var final = await ReadDurable(path).Should().Within(20.Seconds()).Emit();
        final!.Version.Should().BeGreaterThanOrEqualTo(durableVersion,
            "the MonotonicWriteGuard's invariant holds throughout: durable state never regresses");
    }
}
