#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// DETERMINISTIC pins of the per-node-hub activation-seed family (#590 / #648 / #667) on an
/// ASYNC storage backend, modelled by <see cref="GatedReadStorageAdapter"/>: the durable
/// activation-seed read is held (exactly what a Postgres read under load looks like) while the
/// cache-backed routing legs (path-resolution memo, mesh-node stream replay) answer instantly.
///
/// <para><b>The mechanism under test.</b> <c>MeshNodeTypeSource.Initialize</c> feeds the
/// framework's initial-store build, which takes the FIRST accepted emission and disposes the
/// subscription. Before the fix the durable read and the routing replay were Merge-raced, so on
/// any async backend the STALE routing snapshot reliably won the seed and the late durable row
/// was discarded unobserved. The reactivated hub then applied writes onto the stale snapshot,
/// minted below the durable row, had every save refused by the MonotonicWriteGuard, and the
/// AdoptDurableTruth rebase clobbered the acked write in RAM — the exact production shapes of
/// #590 ("updates stop persisting after re-activation") and #667 ("in-flight writes are
/// silently discarded during a deploy window"). The fix concatenates the legs: the durable read
/// SETTLES first, the activation holds for its duration (#667's "wait, don't Not-found"), and a
/// held write applies onto durable truth once the read completes.</para>
///
/// <para>The load-dependent twin of these pins is <c>StaleActivationSeedRollbackTest</c> (same
/// state built via the synchronous in-memory adapter); the gate here removes the timing.</para>
/// </summary>
public class StaleActivationDurableFirstSeedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private GatedReadStorageAdapter Gated
        => Mesh.ServiceProvider.GetRequiredService<GatedReadStorageAdapter>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private JsonSerializerOptions JsonOptions => Mesh.JsonSerializerOptions;

    /// <summary>
    /// Register the gate-wrapped in-memory adapter BEFORE the base's
    /// <c>AddInMemoryPersistence</c> (whose TryAddSingleton then no-ops), so the framework's
    /// write-integrity decorators wrap THIS adapter — the gate sits exactly where a slow
    /// backend sits.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(sp => new GatedReadStorageAdapter(
                new InMemoryStorageAdapter(sp.GetService<ILogger<InMemoryStorageAdapter>>())));
            services.AddSingleton<IStorageAdapter>(sp =>
                sp.GetRequiredService<GatedReadStorageAdapter>());
            return services;
        });
        return base.ConfigureMesh(builder);
    }

    /// <summary>Reads the node straight out of durable storage — bypassing every hub, stream and
    /// cache — so an assertion about DURABILITY cannot be satisfied by an in-RAM snapshot.</summary>
    private IObservable<MeshNode?> ReadDurable(string path) => Storage.Read(path, JsonOptions);

    /// <summary>Disposes the per-node hub owning <paramref name="path"/> — the idle-recycle.</summary>
    private async Task RecycleOwner(string path)
    {
        var resolution = await PathResolver.ResolvePath(path).Should().Within(30.Seconds()).Emit();
        resolution.Should().NotBeNull($"'{path}' must resolve to an owning hub");
        var owner = Mesh.GetHostedHub(new Address(resolution!.Prefix.ToString()!), HostedHubCreation.Never);
        owner.Should().NotBeNull("the owner hub must be live after the warm read");
        owner!.Dispose();
        Output.WriteLine($"[recycle] disposed owner hub for {path}");
    }

    /// <summary>
    /// #590 / #648 / #667: a post-recycle write issued while the reactivating hub's durable
    /// seed read is IN FLIGHT must be HELD and then applied onto durable truth — never applied
    /// onto the stale routing-cache snapshot (where its mint lands below the durable row, the
    /// guard refuses every save, and the rebase clobbers the acked write), and never NACKed
    /// NotFound for a node that exists.
    /// <para><b>Fail-without:</b> with the Merge-raced seed the routing cache seeds the store
    /// the moment the hub activates (the durable read is held), the write applies onto it and
    /// is destroyed by the refusal→rebase cycle: "post-recycle" never becomes durable and the
    /// final poll times out. <b>Pass-with:</b> activation holds until the gate opens, the seed
    /// is the durable row, and the write lands strictly above it.</para>
    /// </summary>
    [Fact(Timeout = 55_000)]
    public async Task PostRecycleWrite_WhenDurableSeedLagsTheRoutingCache_LandsAboveDurableTruth()
    {
        var id = $"gated-seed-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created", NodeType = "Markdown", State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        var created = await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "created" });
        Output.WriteLine($"[created] version={created!.Version}");

        // Recycle the owner. The routing caches survive — that is the whole point.
        await RecycleOwner(path);

        // Durable state advances OUT OF BAND (the acked writes the recycled owner would not
        // know about). Straight through IStorageAdapter: no IMeshChangeFeed publish, so the
        // path-resolution cache keeps serving the pre-recycle node.
        const long durableVersion = 9000L;
        await Storage.Write(created with
        {
            Name = "durable-advance", Version = durableVersion
        }, JsonOptions).Should().Within(30.Seconds()).Emit();
        (await ReadDurable(path).Should().Within(30.Seconds()).Emit())!
            .Version.Should().Be(durableVersion, "the out-of-band advance must be durable before we reactivate");

        // Close the gate: from here, every durable read of the path (the reactivation's
        // activation seed, the cache hydration, the guard's verification read) is held —
        // the async-backend shape where the routing caches answer first.
        var heldRead = Gated.WaitForHeldRead(path);
        Gated.HoldReads(path);

        var writeErrors = new List<Exception>();
        var client = GetClient(c => c.AddData());
        client.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Name = "post-recycle" })
            .Subscribe(
                _ => { },
                ex =>
                {
                    lock (writeErrors) writeErrors.Add(ex);
                    Output.WriteLine($"[write error] {ex.Message}");
                });

        // Positive signal: a consumer of the durable row has reached the held read — the
        // reactivation is in its activation window. Give the (un-fixed) stale-seed pipeline a
        // short beat to do its damage inside the window, then open the gate. The beat only
        // widens the demonstrated defect window on pre-fix code; with the fix the outcome is
        // independent of it (activation simply holds until the release).
        await heldRead.Should().Within(20.Seconds()).Emit();
        await Observable.Timer(TimeSpan.FromMilliseconds(500)).Should().Within(10.Seconds()).Emit();
        Gated.Release(path);
        Output.WriteLine("[gate] released");

        // The write must land durably, ABOVE the durable version: the activation seed was the
        // durable row, and the write applied onto it.
        var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => ReadDurable(path))
            .Should().Within(30.Seconds()).Match(n => n is { Name: "post-recycle" },
                "a write held by a reactivating hub must apply onto the durable seed once the "
                + "read settles — a hub seeded from the stale routing cache destroys it in the "
                + "refusal→rebase cycle and the name never becomes durable");

        Output.WriteLine($"[persisted] name={persisted!.Name} version={persisted.Version}");
        persisted.Version.Should().BeGreaterThan(durableVersion,
            "the write applied onto the durable seed, so its mint is strictly above the durable row "
            + "— landing below it means the hub reactivated on the stale cache snapshot");

        // #667: the held write must never have surfaced as a discard (NotFound / abort) —
        // held-then-applied, or NACKed-retryable-and-retried, never silently dropped.
        lock (writeErrors)
            writeErrors.Should().BeEmpty(
                "a write during the activation-hold window is held and applied — it must not "
                + "error while the node provably exists");
    }

    /// <summary>
    /// #590 invariant pin — activation is a READ: reactivating a per-node hub (via a plain
    /// read) must serve durable truth and must not write ANYTHING back to storage. The #590
    /// rollback was an activation-time write-back persisting hydrated stale state over newer
    /// durable data; with the durable-first seed plus the initial-load echo suppression
    /// (<c>OwnNodeCache.PersistedSnapshot</c>) a pure reactivation leaves the store untouched.
    /// </summary>
    [Fact(Timeout = 55_000)]
    public async Task ReactivationViaRead_ServesDurableTruth_AndWritesNothingBack()
    {
        var id = $"read-reactivation-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created", NodeType = "Markdown", State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();
        await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "created" });

        await RecycleOwner(path);

        // Advance durable truth out of band so the routing memo is provably stale.
        const long durableVersion = 7000L;
        var durable = (await ReadDurable(path).Should().Within(30.Seconds()).Emit())!;
        await Storage.Write(durable with
        {
            Name = "durable-advance", Version = durableVersion
        }, JsonOptions).Should().Within(30.Seconds()).Emit();

        var writesBefore = Gated.WriteCount(path);

        // Reactivate via READ — the same surface the GUI binds to.
        var client = GetClient(c => c.AddData());
        var served = await client.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Should().Within(20.Seconds())
            .Match(n => n.Name == "durable-advance",
                "a reactivating hub must seed from (and serve) the durable row, not the stale "
                + "routing-cache snapshot");
        served.Version.Should().BeGreaterThanOrEqualTo(durableVersion);

        // Negative window: give the debounce flush and the persistence sampler several of their
        // 200 ms windows to write anything at all, then assert nothing reached the store.
        await Observable.Timer(TimeSpan.FromSeconds(2)).Should().Within(20.Seconds()).Emit();

        // 🚨 Read the durable row BEFORE sampling the write count — the same read that already
        // ran two lines below, just moved ahead of the assertion that fails. "found 3" is
        // unattributable; the durable row IS the last write that reached storage, so its
        // version+name discriminate the candidates for free:
        //   v7000/'durable-advance' → an unchanged re-persist (a lossless echo)
        //   v7001/'durable-advance' → a genuine content change arriving AT the held version
        //   v7001/'created'         → a STALE pre-recycle snapshot reached the commit path and
        //                             was re-stamped above durable truth (a #590 write-back)
        // This must stay a pure READ. #1384's window is narrow enough that instrumenting the
        // WRITE path closes it — a ConcurrentQueue.Enqueue added there went 259 iterations clean
        // against a measured 1-in-60 without it — so a per-write trace would buy the answer by
        // destroying the question. A read cannot perturb the race, and taking it before the count
        // is sampled only ever WIDENS the window for a late write to be caught.
        var after = await ReadDurable(path).Should().Within(20.Seconds()).Emit();

        Gated.WriteCount(path).Should().Be(writesBefore,
            "activation is a READ — persisting the just-loaded state back is the #590 "
            + "activation write-back (v979: stale content re-persisted by system-security "
            + $"during activation). The durable row now reads v{after!.Version}/'{after.Name}' "
            + $"(seeded v{durableVersion}/'durable-advance')");
        after.Version.Should().Be(durableVersion, "the durable row must be untouched by the reactivation");
        after.Name.Should().Be("durable-advance");
    }

    /// <summary>
    /// #1432 — adopting an ALREADY-DURABLE external write is an OBSERVATION: it must mint nothing
    /// and write nothing back. The hub here stays LIVE throughout — no recycle, no gate, no
    /// reactivation — so nothing about this depends on the #1384 race; the race only ever decided
    /// whether anyone noticed.
    ///
    /// <para><b>Fail-without:</b> the change-feed reconcile in <c>SubscribeToOwnDeletion</c> adopted
    /// the persisted node through <c>Update</c>, whose <c>UpdateOwn</c> mints unconditionally
    /// (<c>NextVersion(Math.Max(current.Version, updated.Version))</c>). Adopting durable v5000 left
    /// the hub serving <b>v5001</b> — a revision that exists nowhere, content-identical to v5000 —
    /// and because the mint is a fresh <c>with</c>-copy, <c>OwnNodeCache.PersistedSnapshot</c>'s
    /// reference gate did not suppress it and the 200 ms persistence sampler wrote the phantom BACK
    /// (an observation-time write-back, the #590 shape). <b>Pass-with:</b> <c>AdoptPersisted</c>
    /// lands the durable node verbatim at v5000 and records that version on
    /// <c>PostCommitFlushRegistry</c>, so <c>HandleSaveMeshNode</c> and the dispose flush both skip
    /// it — served version, durable version and durable write count are all unchanged.</para>
    /// </summary>
    [Fact(Timeout = 55_000)]
    public async Task LiveHub_AdoptingAnExternalDurableWrite_MintsNothing_AndWritesNothingBack()
    {
        var id = $"live-adopt-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created", NodeType = "Markdown", State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();
        await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "created" });

        // Durable state advances OUT OF BAND — a second writer / migration / GitSync / another
        // silo. Straight through IStorageAdapter, whose Changes feed is what the live owner's
        // reconcile subscribes to.
        const long externalVersion = 5000L;
        var durable = (await ReadDurable(path).Should().Within(30.Seconds()).Emit())!;
        var writesBefore = Gated.WriteCount(path);
        await Storage.Write(durable with
        {
            Name = "external-advance", Version = externalVersion
        }, JsonOptions).Should().Within(30.Seconds()).Emit();

        // The live hub adopts it — that part is required behaviour and must keep working.
        var served = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Should().Within(20.Seconds())
            .Match(n => n.Name == "external-advance",
                "the live owner must adopt an external durable write into its in-RAM node");

        // Negative window: several persistence-sampler periods (200 ms) for a write-back to appear.
        await Observable.Timer(TimeSpan.FromSeconds(2)).Should().Within(20.Seconds()).Emit();
        var after = await ReadDurable(path).Should().Within(20.Seconds()).Emit();

        served.Version.Should().Be(externalVersion,
            "adopting an already-durable external write is an OBSERVATION — minting a revision "
            + "above it makes the in-RAM node a phantom the sampler then writes back");
        after!.Version.Should().Be(externalVersion,
            $"observing someone else's durable write must not write anything back (row now reads "
            + $"v{after.Version}/'{after.Name}')");
        after.Name.Should().Be("external-advance");
        Gated.WriteCount(path).Should().Be(writesBefore + 1,
            "exactly ONE write reached storage in this window — the external one. A second is the "
            + "owner echoing its own observation back.");
    }
}
