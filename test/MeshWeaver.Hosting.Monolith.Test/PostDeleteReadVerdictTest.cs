using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end repro and regression for #1029: a read that races a deleted node's hub teardown
/// must reach a DEFINITIVE verdict — never sit silent until the caller's budget expires.
///
/// <para><b>The failure.</b> <c>MeshPluginTest.FullCrudWorkflow_CreateGetUpdateDelete</c>'s
/// post-delete read asserted <c>"Not found"</c> and intermittently got
/// <c>"Unavailable: … reached no verdict within 10s"</c>, with an authoritative routing NotFound
/// sitting in the log FIVE SECONDS into that budget. The five seconds are the tell: they are
/// <c>JsonSynchronizationStream.FirstHeartbeat</c>, the only 5&#160;s timer in the read path — so
/// the logged NotFound belonged to a keep-alive HEARTBEAT of a stream that was still alive, not to
/// the read's own SubscribeRequest. A heartbeat can only fire if the initial subscribe did NOT take
/// the terminal arm (which disposes the keep-alive), i.e. the subscribe was answered and the answer
/// was ridden out.</para>
///
/// <para><b>The mechanism</b>, read straight off the <c>MESHWEAVER_MSG_TRACE</c> capture of this
/// test before the fix:</para>
/// <code>
///   TestData/…  RawJson id=Rqn…  ScheduleNotify runLevel=DisposeHostedHubs
///   TestData/…  RawJson id=Rqn…  DROPPED_SHUTTING_DOWN
///   mesh/…      SubscribeRequest id=Rqn…  routed state=Forwarded isOnTarget=False
///   mesh/…      DeliveryFailure  id=Cu1…  NotifyAsync ENTER
///   cache/…     DeliveryFailure  id=Cu1…  routed isOnTarget=True     ← the NACK DID arrive
/// </code>
/// <para>The delete tore the per-node hub down; the fresh read's <c>SubscribeRequest</c> landed on
/// it at <c>RunLevel=DisposeHostedHubs</c>, was dropped, and was NACKed as the TRANSIENT
/// <c>ErrorType.ShuttingDown</c>. <c>JsonSynchronizationStream</c>'s error arm rides that
/// classification out on purpose — a recycling owner reactivates and the change-feed resubscribe
/// latch rehydrates the stream. For a DELETED node nothing ever comes back, so the stream parked in
/// silence: no value, no error, no verdict. The reply was never lost; it was ridden out.</para>
///
/// <para><b>The fix</b> is at the one place that can tell the two teardowns apart: the abandoned-
/// delivery NACK now consults the mesh's delete tombstone (<see cref="IAddressTombstones"/>, i.e.
/// <see cref="RecentlyDeletedRegistry"/>, written synchronously by the delete handler before its
/// response returns) and answers an authoritative <c>NotFound</c> for a deleted address while every
/// other teardown keeps <c>ShuttingDown</c>. See
/// <c>MeshWeaver.Messaging.Hub.Test.DeletedAddressNackClassificationTest</c> for the fork itself and
/// <c>DeferredDeliveryNackedOnDisposeTest</c> for the transient half.</para>
///
/// <para>🚨 The 10&#160;s budget in <c>MeshOperations.FetchNode</c> is NOT the problem and must not
/// be touched: the answer existed within milliseconds and was thrown away.</para>
/// </summary>
public class PostDeleteReadVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    /// <summary>
    /// Create → read → delete → read. The second read must produce a terminal, definitive
    /// "no node found" — quickly, and classified so that every downstream consumer reads it as
    /// ABSENCE rather than as a retryable availability failure.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ReadRacingDeletedNodeTeardown_ReachesDefinitiveNotFound()
    {
        var cache = Cache;
        var path = $"{TestPartition}/delete-verdict-{Guid.NewGuid():N}";
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "Original",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();

        // Warm the read path exactly as the CRUD workflow's Get does: this activates the per-node
        // hub and opens the shared cache upstream, so the delete below has a LIVE hub to tear down
        // and the second read genuinely races that teardown. Without this warm-up the node is never
        // activated, routing NotFounds the second read outright, and the defect cannot appear.
        var warm = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Should().Within(30.Seconds()).Emit();
        warm!.Path.Should().Be(path);

        await NodeFactory.DeleteNode(path).Should().Within(60.Seconds()).Emit();

        // 30 s is a FAILURE bound, not a budget being tested: the verdict lands in tens of
        // milliseconds. Before the fix this waited the full window and reported "emitted nothing
        // at all" — the silent park itself.
        var outcome = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(30.Seconds()).Match(
                n => n.Kind != NotificationKind.OnNext || n.Value is null,
                "a read of a deleted path must reach a verdict; sitting silent is the defect");

        Output.WriteLine($"[post-delete read] {outcome.Kind} {outcome.Exception?.GetType().Name}: "
                         + outcome.Exception?.Message);

        outcome.Kind.Should().Be(NotificationKind.OnError,
            "the node is gone — the reader is owed an authoritative absence, not silence");

        var error = outcome.Exception!;
        // The classifiers are what turn this error into the caller-facing sentence. Both are
        // MESSAGE matches, so they are the actual contract: "missing" makes MeshOperations report
        // `Not found`, "transient" would make it report `Unavailable` and retry forever.
        MeshNodeStreamCache.IsMissingNodeFailure(error).Should().BeTrue(
            "MeshOperations.FetchNode classifies through FromReadFailure → IsMissingNodeFailure; "
            + "an unrecognised message degrades a definitive absence into 'Unavailable', which is "
            + "the exact lie #974/#989 exist to prevent — in the opposite direction");
        MeshNodeStreamCache.IsTransientOwnerFailure(error).Should().BeFalse(
            "a deleted node is not momentarily unreachable: classifying it transient sends every "
            + "reader back to re-probing an address that will never answer");
    }
}
