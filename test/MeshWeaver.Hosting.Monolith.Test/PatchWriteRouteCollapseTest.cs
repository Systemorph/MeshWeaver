#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
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
/// Issue #1249 — a patch-driven own-node change reached durable storage by TWO independent,
/// unordered routes, and the later-arriving stale one manufactured a write conflict on a strictly
/// sequential writer.
///
/// <list type="number">
///   <item><b>Route 1 — the post-commit flush.</b> <c>DataExtensions.ApplyMeshNodePatchInTurn</c>
///     chains <c>IPostCommitFlush.Flush</c> off the reduced stream's post-commit emission; the
///     <c>PatchDataResponse</c> ack chains off THAT, so the row must be durable before the caller's
///     <c>stream.Update</c> completes. Deliberate, and load-bearing for read-after-write.</item>
///   <item><b>Route 2 — the per-node persistence sampler.</b> <c>InstallPersistenceSampler</c>
///     samples the own stream every 200 ms and posts <c>SaveMeshNodeRequest</c>, whose handler
///     writes the sampled node again. It is what persists own writes that never went through a
///     patch, so it cannot simply be deleted — but for a patch it is a pure duplicate.</item>
/// </list>
///
/// <para><b>Why the duplicate is not merely wasteful.</b> The two routes are never ordered against
/// each other: route 1 writes from an emission thread, route 2 through the hub inbox. Under a
/// sustained write rate the row advances while route 2's message queues, so its write lands as a
/// strict version REGRESSION. <c>MonotonicWriteGuardStorageAdapter</c> correctly refuses it and
/// resolves the conflict by merging — and <c>MeshNodePatchMerge</c>'s base-less merge keeps the
/// SUPERSET of two strings and the UNION of two arrays. A deletion the newer write made is
/// therefore RE-ADDED. That trade-off is documented and deliberate for a genuine conflict; here the
/// conflict is manufactured by our own duplicate route, so nobody consented to it.</para>
///
/// <para><b>The fix</b> collapses the routes: the flush records the version it made durable in the
/// mesh-scoped <c>PostCommitFlushRegistry</c>, and the save handler skips a sampled state that is not
/// newer. A version high-water (not a reference-identity stamp) is what makes this sound: the
/// sampler's <c>Where</c> gate runs in the SAME synchronous fan-out as the flush — earlier, in fact,
/// since it subscribed first — so an identity stamp written by the flush is always too late for it.
/// The high-water is read at HANDLER time, after the flush has settled.</para>
///
/// <para>🚨 None of this relaxes the guard. Its alarm was correct; its INPUT was wrong.
/// <see cref="GenuineSecondWriter_StillTripsTheGuard_AndTheRowNeverRegresses"/> pins that the
/// real-second-writer case is untouched.</para>
/// </summary>
public class PatchWriteRouteCollapseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private WriteSequencingStorageAdapter Sequencer
        => Mesh.ServiceProvider.GetRequiredService<WriteSequencingStorageAdapter>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private JsonSerializerOptions JsonOptions => Mesh.JsonSerializerOptions;

    /// <summary>
    /// Register the write-sequencing in-memory adapter BEFORE the base's
    /// <c>AddInMemoryPersistence</c> (whose TryAddSingleton then no-ops), so the write-integrity
    /// decorators wrap THIS adapter and it sits exactly where the backend sits.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(sp => new WriteSequencingStorageAdapter(
                new InMemoryStorageAdapter(sp.GetService<ILogger<InMemoryStorageAdapter>>())));
            services.AddSingleton<IStorageAdapter>(sp =>
                sp.GetRequiredService<WriteSequencingStorageAdapter>());
            return services;
        });
        return base.ConfigureMesh(builder);
    }

    /// <summary>Reads straight out of durable storage — no hub, no stream, no cache.</summary>
    private IObservable<MeshNode?> ReadDurable(string path) => Storage.Read(path, JsonOptions);

    private async Task<MeshNode> CreateNode(string path, string id, string? description)
    {
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created",
            Description = description,
            NodeType = "Markdown",
            State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        var created = await ReadNode(path).Should().Within(30.Seconds())
            .Match(n => n is { Name: "created" });
        Output.WriteLine($"[created] {path} version={created!.Version}");
        return created;
    }

    /// <summary>
    /// ONE patch-driven own-node update must reach storage exactly ONCE.
    ///
    /// <para><b>Fail-without:</b> the post-commit flush writes the committed node, and ~200 ms later
    /// the persistence sampler writes the identical state again — two durable writes for one change.
    /// <b>Pass-with:</b> the flush records the durable version in the <c>PostCommitFlushRegistry</c>
    /// and the save handler drops the sampled duplicate.</para>
    /// </summary>
    [Fact(Timeout = 55_000)]
    public async Task PatchDrivenOwnWrite_ReachesStorageExactlyOnce()
    {
        var id = $"one-write-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        const string updated = "updated-by-patch";

        await CreateNode(path, id, description: null);

        // Every write of the PATCHED state that reaches the store. Replayed, so the assertion
        // cannot depend on when it subscribed.
        var patchedWrites = Sequencer.Writes.Where(n =>
            string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)
            && n.Name == updated);

        await Mesh.GetMeshNodeStream(path)
            .Update(n => n with { Name = updated })
            .Should().Within(30.Seconds()).Emit();

        var first = await patchedWrites.Should().Within(30.Seconds()).Emit(
            "the post-commit flush is what makes the patch durable before its ack");
        Output.WriteLine($"[route 1] durable write at version={first.Version}");

        // The sampler's duplicate would follow within its 200 ms Sample window. There is no
        // positive signal for a write that must NOT happen, so this is the framework's sanctioned
        // negative assertion — 3 s is 15x the sampler interval.
        await patchedWrites.Skip(1).Should().NotEmit(3.Seconds(),
            "one own-node change must be persisted by ONE route — the per-node persistence sampler "
            + "must not write a state the post-commit flush already made durable (#1249)");
    }

    /// <summary>
    /// 🚨 THE SEVERITY CLAIM. A strictly sequential writer that DELETES text must not have the
    /// deletion undone by our own duplicate write.
    ///
    /// <para>The forcing is deterministic and needs no timing budget: the innermost adapter holds
    /// the sampler's NON-ADVANCING write (a write at a version the row already carries — the
    /// signature of a duplicate route) until the next patch has advanced the row, then releases it.
    /// That is the production window (the inbox queue) made explicit. On release the in-memory
    /// store's version-keeping upsert refuses it and returns the newer row, which is the store-level
    /// conflict signal <c>MonotonicWriteGuardStorageAdapter</c> resolves by merging — and
    /// <c>MeshNodePatchMerge.TryMergeTwoWay</c> keeps the SUPERSET, resurrecting the deleted words.</para>
    ///
    /// <para><b>Fail-without:</b> the held duplicate exists, is released after the deletion is
    /// durable, and the merge writes the pre-deletion text back over the row — an acked deletion
    /// silently undone. <b>Pass-with:</b> there is no duplicate write to hold at all, so nothing can
    /// resurrect anything; the release is a no-op.</para>
    /// </summary>
    [Fact(Timeout = 55_000)]
    public async Task SequentialWriter_DeletedText_IsNotResurrectedByADuplicateWrite()
    {
        var id = $"no-resurrect-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        const string original = "one two three";
        const string extended = "one two three four";
        const string afterDeletion = "one three four";   // "two " deleted

        var created = await CreateNode(path, id, original);

        // Arm the forcing BEFORE the first patch: from here on, any write for this path that does
        // not advance past the highest version written is held — i.e. exactly a duplicate route's
        // write of a state another route already persisted. The create's own seed revision is
        // excluded (the create pipeline writes it more than once by design).
        Sequencer.HoldDuplicateWrites(path, created.Version);
        using var heldLog = Sequencer.HeldWrites
            .Where(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase))
            .Subscribe(n => Output.WriteLine(
                $"[route 2] HELD duplicate write version={n.Version} description='{n.Description}'"));

        await Mesh.GetMeshNodeStream(path)
            .Update(n => n with { Description = extended })
            .Should().Within(30.Seconds()).Emit();
        var afterFirst = await ReadDurable(path).Should().Within(30.Seconds())
            .Match(n => n is not null && n.Description == extended);
        Output.WriteLine($"[patch 1] durable version={afterFirst!.Version} description='{afterFirst.Description}'");

        // Did a duplicate write show up? On the defective code it does, and holding it is what makes
        // the resurrection deterministic. Once the routes are collapsed there is nothing to hold —
        // the absence IS the fix, so this observation is reported, never asserted on.
        var duplicate = await Sequencer.HeldWrites
            .Where(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)
                        && n.Description == extended)
            .Take(1)
            .Timeout(5.Seconds())
            .Catch<MeshNode, Exception>(_ => Observable.Empty<MeshNode>())
            .FirstOrDefaultAsync()
            .ToTask();
        Output.WriteLine(duplicate is null
            ? "[route 2] no duplicate write of the patched state — the persistence routes are collapsed"
            : $"[route 2] duplicate write of the patched state at version={duplicate.Version} HELD");

        // The user deletes a word. Sequential — the first update has already acked and is durable.
        await Mesh.GetMeshNodeStream(path)
            .Update(n => n with { Description = afterDeletion })
            .Should().Within(30.Seconds()).Emit();
        var afterDelete = await ReadDurable(path).Should().Within(30.Seconds())
            .Match(n => n is not null && n.Description == afterDeletion);
        Output.WriteLine($"[patch 2] durable version={afterDelete!.Version} description='{afterDelete.Description}'");

        // Release the stale duplicate into the now-advanced row — the production inbox window.
        Sequencer.Release(path);

        // The conflict resolution writes the merged node straight back over the durable row, so the
        // resurrection shows up as a durable description that is no longer the deleted-text one.
        // "It must never appear" has no positive signal, hence the framework's negative assertion.
        var resurrection = Observable.Interval(50.Milliseconds())
            .StartWith(0L)
            .SelectMany(_ => ReadDurable(path))
            .Where(n => n is not null && n.Description != afterDeletion)
            .Select(n => $"v{n!.Version} '{n.Description}'");

        await resurrection.Should().NotEmit(5.Seconds(),
            "a sequential writer's deletion must stay deleted. The base-less conflict merge keeps the "
            + "string SUPERSET, so a duplicate write of the PRE-deletion state re-adds the removed "
            + "words — and that duplicate exists only because one patch was persisted by two "
            + "independent routes (#1249). Resurrection is an accepted trade-off for a GENUINE "
            + "conflict, never for one the framework manufactured against itself");

        var final = await ReadDurable(path).Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"[final] version={final!.Version} description='{final.Description}'");
        final.Description.Should().Be(afterDeletion);
    }

    /// <summary>
    /// The guard must still fire on a genuine out-of-order write from a REAL second writer — the
    /// #826/#971 rollback class. The durable-version high-water the fix adds is consulted ONLY by the
    /// own-node save handler, never by the storage adapter chain, so a foreign writer's stale
    /// snapshot still takes the full verification-read → merge → ActivityLog treatment.
    /// </summary>
    [Fact(Timeout = 55_000)]
    public async Task GenuineSecondWriter_StillTripsTheGuard_AndTheRowNeverRegresses()
    {
        var id = $"real-conflict-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        const long advancedVersion = 7_000L;

        await CreateNode(path, id, description: "created text");

        // A patch first, so the owner hub's durable-version high-water is populated — the very
        // state the fix introduces. It must not weaken anything below.
        await Mesh.GetMeshNodeStream(path)
            .Update(n => n with { Name = "patched" })
            .Should().Within(30.Seconds()).Emit();
        var patched = await ReadDurable(path).Should().Within(30.Seconds())
            .Match(n => n is { Name: "patched" });
        Output.WriteLine($"[patched] version={patched!.Version}");

        // A genuine SECOND writer advances the durable row far ahead...
        await Storage.Write(patched with { Name = "second-writer", Version = advancedVersion }, JsonOptions)
            .Should().Within(30.Seconds()).Emit();

        // ...and then presents a stale snapshot of its own. This is a real forked lineage: the guard
        // must refuse it, resolve by merging into the durable row, and leave a durable trace.
        await Storage.Write(patched with { Name = "stale-snapshot" }, JsonOptions)
            .Should().Within(30.Seconds()).Emit();

        var durable = await ReadDurable(path).Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"[durable] version={durable!.Version} name='{durable.Name}'");
        durable.Version.Should().BeGreaterThanOrEqualTo(advancedVersion,
            "the store must never move backward for a genuine stale write — that is the whole "
            + "contract of MonotonicWriteGuard (#826/#971)");
        durable.Name.Should().Be("second-writer",
            "'second-writer' and 'stale-snapshot' diverge on both sides, so the merge keeps the "
            + "newer value and reports the drop");

        // The satellite id is keyed on the DURABLE version at resolution time, which the owner's own
        // reconcile may have advanced past `advancedVersion` — so scan for the record rather than
        // guessing its id.
        await Observable.Interval(100.Milliseconds())
            .StartWith(0L)
            .SelectMany(_ => Storage.ListDescendantPaths(path))
            .Should().Within(30.Seconds())
            .Match(paths => paths.Any(p => p.Contains("write-conflict", StringComparison.OrdinalIgnoreCase)),
                "a resolved conflict must leave a user-visible ActivityLog satellite — the alarm this "
                + "issue was devaluing, and it has to keep firing for a REAL second writer");
    }
}
