using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Deterministic repro for the WEDGE half of the cross-hub-patch regression (invariant B):
/// a cross-hub <c>GetMeshNodeStream(path).Update(...)</c> against a per-node hub MUST emit AS SOON
/// AS THE PATCH IS ACCEPTED (the e2bf63ae1 emit-onstart contract) — it must NOT be held hostage to
/// the owner's DURABLE persistence.
///
/// <para>The atomic owner-side apply (<c>DataExtensions.ApplyMeshNodePatchAtomic</c>) chains its
/// <see cref="PatchDataResponse"/> ack off a post-commit observer that subscribes
/// <c>IPostCommitFlush.Flush</c> — in production <c>adapter.Write</c> = <c>Observable.FromAsync</c>
/// over a DB round-trip. So the owner only acks AFTER durable persistence. The cross-hub caller's
/// <c>UpdateRemote</c> then never receives a prompt ack and can only fall back to its ~2s optimistic
/// timeout — and the owner-side per-patch flush IO, subscribed on the stream turn, is what pinned
/// the per-node hub and timed out a concurrent <c>SubscribeRequest</c> in prod
/// (<c>AgenticPension/ArbeitsanweisungenListe2</c>, 60s).</para>
///
/// <para>This pins it deterministically by injecting an <see cref="IPostCommitFlush"/> whose
/// durable commit is slow (the production <c>FromAsync</c> DB-write shape). With the unfixed apply
/// the ack is gated by that flush, so the cross-hub Update only emits at UpdateRemote's ~2s
/// optimistic bound. The fix makes the apply ack ON ACCEPT (emit-onstart) — independent of the
/// durable flush — so the Update emits immediately. The assertion fails before the fix
/// (emits ≈ optimistic bound) and passes after (emits promptly).</para>
/// </summary>
public class CrossHubPatchSubscribeStarvationTest : AITestBase
{
    public CrossHubPatchSubscribeStarvationTest(ITestOutputHelper output) : base(output) { }

    // Simulates the production durable flush (adapter.Write = Observable.FromAsync over a DB
    // round-trip): the commit signal is delayed. If the apply gates its ack on this, the cross-hub
    // Update can't emit until it completes.
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(5);
    private static int _flushInvocations;

    private sealed class SlowPostCommitFlush : IPostCommitFlush
    {
        public IObservable<bool> Flush(object committed)
        {
            Interlocked.Increment(ref _flushInvocations);
            return Observable.Return(true).Delay(FlushDelay);
        }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                // Override the durable flush with a slow one (last registration wins for GetService).
                services.AddSingleton<IPostCommitFlush>(new SlowPostCommitFlush());
                return services;
            });

    [Fact(Timeout = 120_000)]
    public async Task CrossHubUpdate_EmitsOnAccept_NotGatedByDurableFlush()
    {
        Interlocked.Exchange(ref _flushInvocations, 0);
        var ct = TestContext.Current.CancellationToken;
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        var u1 = Guid.NewGuid().AsString();
        var r1 = Guid.NewGuid().AsString();

        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"EmitOnAccept Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread
            {
                CreatedBy = "rbuergi@systemorph.com",
                Status = ThreadExecutionStatus.Done,
                Messages = ImmutableList.Create(u1, r1),
                UserMessageIds = ImmutableList.Create(u1),
                IngestedMessageIds = ImmutableList.Create(u1)
            }
        }).FirstAsync().ToTask(ct);

        // Warm the cross-hub stream so the first cross-hub Update doesn't pay cold-activation cost.
        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Where(n => n is not null).Take(1).Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);

        // ACT — a cross-hub stream.Update on the per-node hub (the SubmitMessage / Stop-button path).
        // Time how long until it emits.
        var sw = Stopwatch.StartNew();
        var updated = await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Update(n => n with { Name = "touched" })
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(ct);
        sw.Stop();
        Output.WriteLine($"Cross-hub Update emitted in {sw.ElapsedMilliseconds}ms; flush invocations: {_flushInvocations}");

        // NOTE: the slow flush is the MECHANISM that makes the UNFIXED apply fail — it chains its
        // ack off the durable flush, so with a 5s flush the Update can only emit at UpdateRemote's
        // ~2s optimistic bound. The fixed apply acks on ACCEPT and never touches the flush at all
        // (durability is the data source's Synchronize concern), so the Update emits in ~20ms and
        // _flushInvocations is legitimately 0 — proof the ack is decoupled from durable persistence.

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("touched", "the optimistic post-patch snapshot must carry the write");

        // 🚦 THE EMIT-ONSTART GUARD: the cross-hub Update must emit as soon as the patch is ACCEPTED
        // — far below the FlushDelay AND below UpdateRemote's ~2s optimistic bound. Before the fix the
        // owner gates its ack on the durable flush, so the Update can only emit at the optimistic
        // bound (~2s); after the fix it acks on accept and emits promptly.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1500),
            "a cross-hub stream.Update must emit when the patch is accepted (emit-onstart), never "
            + "wait on the owner's durable persistence — gating the ack on IPostCommitFlush defeats "
            + "the e2bf63ae1 contract and starves the per-node hub turn under a storm of patches");
    }
}
