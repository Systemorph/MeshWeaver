using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Deterministic regression gate for the cross-hub MeshNode write path: many
/// concurrent cross-MIRROR deltas to ONE node must NEVER lose an entry.
///
/// <para>K separate client mirrors each fire N concurrent <c>stream.Update</c> adds,
/// each adding a UNIQUE key to a merge-safe <see cref="ImmutableDictionary{TKey,TValue}"/>
/// (<see cref="MeshThread.PendingUserMessages"/>). RFC 7396 merges a dict key-by-key,
/// so with an ATOMIC owner-side apply every key survives. If the owner apply is a
/// non-atomic read-modify-write, a concurrent apply reads a stale base and its commit
/// overwrites a sibling writer's just-added key — a permanent loss.</para>
///
/// <para>The writes go through <see cref="MeshNodeStreamExtensions.GetMeshNodeStreamBypassCache"/>
/// so they reach the owner with NO client-side serialisation — directly stressing the OWNER apply,
/// the layer this change fixes. (The ordinary <c>GetMeshNodeStream(path)</c> path still funnels a
/// path's writes through the <c>MeshNodeStreamCache</c> per-path Update queue, which orders/spaces
/// them; bypassing it is what makes a concurrent burst hit the owner all at once.)</para>
///
/// <para>ROOT CAUSE the fix addresses (confirmed by instrumentation): the owner-side patch apply
/// is already SERIALISED on the per-node primary stream's single action block — it is NOT a
/// concurrent-apply race. The loss came from <c>MeshDataSource.SubscribeToOwnDeletion</c>: on every
/// durable-flush <c>storage.Changes</c> notification the per-node hub re-applied the node AS
/// PERSISTED via <c>Update(_ =&gt; newNode)</c> — a blind full-node overwrite. The persist + its
/// notification are OFF-TURN, so under a burst the notification LAGS the in-RAM applies and the
/// stale older node clobbered the fresher in-RAM state, dropping every entry added since it was
/// persisted. The version echo-suppression only skipped the single latest write, not the lagging
/// echoes. The fix makes that refresh FORWARD-ONLY (apply a persisted snapshot only when strictly
/// newer), so the in-RAM commit is authoritative and never moves backward.</para>
///
/// <para>SECOND ROOT CAUSE, issue #945 — the same symptom on the READ side. Under CI load this
/// test kept failing with a handful of entries missing and ZERO write errors, and the owner-truth
/// probe below proved the owner held all 288: the loss was in the shared-cache MIRROR. A write
/// burst makes the mesh change feed announce versions faster than a loaded mirror applies them, so
/// the mirror's version-gated <c>Resubscribe</c> fires; the owner answers on the
/// <c>alreadyServing</c> path in <c>JsonSynchronizationStream.CreateSynchronizationStream</c> by
/// re-asserting its snapshot. That re-assert bound <c>Current</c> OUTSIDE the stream's update turn
/// and wrote it back blindly, stamped with the per-subscriber SYNC hub's clock instead of the
/// state's own content version — so it rolled the stream BACKWARD over every frame that had
/// committed in between (measured: 245 entries@v246 → 234 entries@v235), re-based the outbound
/// JSON cursor on the rolled-back state, and left the mirror trailing the owner by a CONSTANT
/// deficit forever. The fix reads the snapshot in-turn and carries its own version
/// (<c>BuildReassertFrame</c>, pinned by <c>ResubscribeReassertFrameTest</c>).</para>
/// </summary>
/// <remarks>
/// Serialised via <see cref="ConcurrencyStressCollection"/>: 6 mirrors × 48 writes × 4 rounds are
/// merged into ONE burst so they hit the owner together, and the verdict is a 30 s settle bound on
/// that burst. Sharing a 4-vCPU runner turns the bound into a measure of the scheduler — the backed-out
/// opt-in failed here with zero write errors, all 288 entries at the owner and a mirror still advancing
/// monotonically when the bound expired.
/// </remarks>
[Collection(ConcurrencyStressCollection.Name)]
public class CrossHubPatchAtomicityTest(ITestOutputHelper output) : AITestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        // The mirror clients need MeshThread in their TypeRegistry so the cross-hub
        // patch round-trips Content typed (the owner reads it back as MeshThread).
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact(Timeout = 180_000)]
    public async Task ConcurrentCrossHubPatches_DoNotDropAQueuedMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        const int mirrors = 6;
        const int perMirror = 48;
        const int rounds = 4;
        const int total = mirrors * perMirror;

        // K separate client mirrors — each writes cross-hub to the SAME node.
        var clients = Enumerable.Range(0, mirrors).Select(_ => GetClient()).ToArray();

        for (var round = 0; round < rounds; round++)
        {
            var nodeId = Guid.NewGuid().AsString();
            var path = $"{MonolithMeshTestBase.TestPartition}/CrossHubAtomicity/{nodeId}";

            // Fresh node per round, empty merge-safe dict. NodeType is a plain "Markdown"
            // node (NOT Thread) so NO submission watcher runs — this isolates the OWNER
            // patch-apply race from any owner-side own-write.
            await NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                Name = $"Atomicity Node {nodeId}",
                NodeType = "Markdown",
                MainNode = MonolithMeshTestBase.TestPartition,
                Content = new MeshThread { CreatedBy = "rbuergi@systemorph.com" }
            }).FirstAsync().ToTask(ct);

            // Warm each mirror's remote stream (owner hub live + initial snapshot cached)
            // so the burst below all diffs against a real base and hits the owner together.
            foreach (var client in clients)
                await client.GetWorkspace().GetMeshNodeStream(path)
                    .Where(n => n.Content is MeshThread)
                    .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask(ct);

            // INSTRUMENT: record every emission the shared-cache mirror delivers, timestamped, so a
            // failure distinguishes the three shapes on its own instead of leaving it to inference:
            // went BACKWARD (a stale snapshot re-applied over newer state — the #945 defect),
            // stopped advancing (never converged), or merely arrived LATE (converged past the settle
            // bound — a slow box, not a loss). No logging pipeline involved.
            // 🚨 `using` + an explicit OnError, both load-bearing on the FAILURE path — the one
            // path this instrument exists for. Without `using`, Assert.Fail below unwinds past a
            // plain Dispose() and leaves the subscription pending into teardown (the "left Observe
            // subscriptions pending past the Quiescing budget" class). Without an OnError handler,
            // a stream fault would be rethrown on the producer's thread as an unhandled exception,
            // replacing the very diagnostics the failure message is built from; instead the fault
            // is captured and REPORTED alongside the sequence recorded so far.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var observed = new System.Collections.Concurrent.ConcurrentQueue<(long Version, int Keys, long Ms)>();
            Exception? watchError = null;
            using var watch = Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Subscribe(
                    n => observed.Enqueue(
                        (n.Version, (n.Content as MeshThread)?.PendingUserMessages.Count ?? 0,
                         clock.ElapsedMilliseconds)),
                    ex => watchError = ex);

            var allKeys = new List<string>(total);
            var writes = new List<IObservable<MeshNode>>(total);
            var writeErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
            for (var m = 0; m < mirrors; m++)
            {
                var client = clients[m];
                for (var i = 0; i < perMirror; i++)
                {
                    var key = $"r{round}-m{m}-i{i:D2}";
                    allKeys.Add(key);
                    var msg = MeshWeaver.AI.ThreadInput.CreateUserMessage(key, createdBy: "rbuergi@systemorph.com");
                    writes.Add(client.GetWorkspace().GetMeshNodeStreamBypassCache(path)
                        .Update(node =>
                        {
                            var t = node.Content as MeshThread ?? new MeshThread();
                            return node with
                            {
                                Content = t with
                                {
                                    PendingUserMessages = t.PendingUserMessages.SetItem(key, msg)
                                }
                            };
                        })
                        .Take(1)
                        // A failed write must surface as a MISSING key (the loss), not abort the burst.
                        .Catch((Exception ex) =>
                        {
                            writeErrors.Add($"{key}: {ex.GetType().Name}: {ex.Message}");
                            return Observable.Empty<MeshNode>();
                        }));
                }
            }

            // Fire ALL K*N concurrently — Merge subscribes every inner immediately, so the
            // owner receives the full burst with maximal overlap.
            await Observable.Merge(writes).ToList()
                .Timeout(TimeSpan.FromSeconds(120)).ToTask(ct);

            // Reactive settle: wait until the AUTHORITATIVE node stream shows ALL keys —
            // GetMeshNodeStream(path) is the live per-node read the GUI databinds to (CQRS:
            // never the lagged query index for single-node content). NOT a poll/sleep loop.
            var finalKeys = ImmutableHashSet<string>.Empty;
            try
            {
                await Mesh.GetWorkspace().GetMeshNodeStream(path)
                    .Select(n => (n.Content as MeshThread)?.PendingUserMessages.Keys.ToImmutableHashSet()
                                 ?? ImmutableHashSet<string>.Empty)
                    .Do(k => finalKeys = k)
                    .Where(k => allKeys.All(k.Contains))
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(30))
                    .ToTask(ct);
            }
            catch (TimeoutException)
            {
                var missing = allKeys.Where(k => !finalKeys.Contains(k)).ToArray();
                var errs = writeErrors.ToArray();
                // OWNER TRUTH: a brand-new client opens a FRESH remote subscription (bypassing the
                // shared cache), so the initial frame it receives is the owner's authoritative
                // Current. If the key is present here but absent above, the loss is on the READ
                // (mirror) side; if it is absent here too, the owner genuinely dropped it.
                var probe = GetClient();
                var ownerNode = await probe.GetWorkspace().GetMeshNodeStreamBypassCache(path)
                    .Where(n => n.Content is MeshThread)
                    .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask(ct);
                var ownerKeys = (ownerNode.Content as MeshThread)!.PendingUserMessages.Keys.ToImmutableHashSet();
                var ownerMissing = allKeys.Where(k => !ownerKeys.Contains(k)).ToArray();
                var seq = observed.ToArray();
                var regressions = seq.Zip(seq.Skip(1))
                    .Where(p => p.Second.Keys < p.First.Keys)
                    .Select(p => $"{p.First.Keys}@v{p.First.Version}(t{p.First.Ms}ms)"
                                 + $"->{p.Second.Keys}@v{p.Second.Version}(t{p.Second.Ms}ms)")
                    .ToArray();
                // Did the mirror converge at all — before or after the settle bound expired?
                var converged = seq.LastOrDefault(e => e.Keys >= total);
                Assert.Fail(
                    $"Round {round}: {missing.Length} of {total} concurrent cross-hub adds are not visible "
                    + $"(present={finalKeys.Count}). Missing: [{string.Join(",", missing)}]. "
                    + $"WRITE ERRORS ({errs.Length}): [{string.Join(" | ", errs)}]. "
                    + $"OWNER-TRUTH probe: version={ownerNode.Version} present={ownerKeys.Count} "
                    + $"missing=[{string.Join(",", ownerMissing)}]. "
                    + (watchError is null
                        ? ""
                        : $"MIRROR STREAM FAULTED: {watchError.GetType().Name}: {watchError.Message} "
                          + "(the sequence below stops there). ")
                    + $"MIRROR SEQUENCE ({seq.Length} emissions), REGRESSIONS ({regressions.Length}): "
                    + $"[{string.Join(",", regressions)}]; converged="
                    + (converged.Keys >= total ? $"YES at t{converged.Ms}ms" : "NEVER")
                    + "; tail="
                    + $"[{string.Join(",", seq.TakeLast(6).Select(e => $"{e.Keys}@v{e.Version}(t{e.Ms}ms)"))}]. "
                    + "READ THE DIAGNOSTICS BEFORE THEORISING: write errors non-empty ⇒ the write failed, "
                    + "not the merge. Owner-truth missing ⇒ the OWNER apply lost it (a non-atomic "
                    + "read-modify-write). Owner-truth complete but entries missing here ⇒ the shared-cache "
                    + "MIRROR is behind — and then: a REGRESSION names the exact frame where the mirror "
                    + "moved BACKWARD (the #945 stale resubscribe re-assert, a real loss), while "
                    + "converged=YES with no regression means the mirror got everything but only after "
                    + "the settle bound — a slow box, not a lost write.");
            }

            finalKeys.Count.Should().BeGreaterThanOrEqualTo(total,
                $"round {round}: every concurrent cross-hub add must survive");
        }
    }
}
