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
/// </summary>
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

            var allKeys = new List<string>(total);
            var writes = new List<IObservable<MeshNode>>(total);
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
                        .Catch((Exception _) => Observable.Empty<MeshNode>()));
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
                Assert.Fail(
                    $"Round {round}: owner dropped {missing.Length} of {total} concurrent cross-hub adds "
                    + $"(present={finalKeys.Count}). Missing (first 20): [{string.Join(",", missing.Take(20))}]. "
                    + "The owner-side patch apply is non-atomic — a concurrent apply read a stale base and "
                    + "overwrote a sibling writer's just-added key.");
            }

            finalKeys.Count.Should().BeGreaterThanOrEqualTo(total,
                $"round {round}: every concurrent cross-hub add must survive");
        }
    }
}
