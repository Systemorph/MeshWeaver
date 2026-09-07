using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#3510 — the UPSERT lane goes silent when the owner is disposed under it, and an install
/// then runs out its ten-minute bound with nothing to say.</b>
///
/// <para><b>Measured, core CD 7950</b> (<c>f55b9f4ab</c>, bake job 101577531852). A package root is
/// disposed under its OWN install — by design, by <c>PackageInstaller.SettleRetypedRoot</c>, which
/// fires for every package whose root NodeType is dynamic (every plugin root is
/// <c>Store/Plugin</c>) — and the recycle itself SUCCEEDS: zero <c>root … never answered after its
/// recycle</c> warnings, and the same recycle happens 7–12× per bake in every run examined,
/// including the run that SEALED. The recycle is not the defect. The defect is that the
/// <c>CreateOrUpdateNodeRequest</c> in flight across it is never answered:</para>
///
/// <code>
/// HANDLER_ENTER → UPSERT_READ absent → create → HANDLER_EXIT state=Processed  ⇒ (nothing)
/// </code>
///
/// <para>Confirmed by the denominator, not by a missing line: <b>zero</b> <c>[CreateOrUpdate]</c>
/// log lines of ANY kind in the whole run — so neither terminal arm of the inner
/// <c>CreateNode</c>/<c>UpdateNode</c> subscription ever ran. Silence, not a fault. Unlike the patch
/// lane there is no <c>WriteVerdictBound</c> equivalent on this lane to turn that silence into a
/// verdict, which is why every counter built for #3499 and #3498 reads zero on that run.</para>
///
/// <para><b>What this pins, and what it deliberately does NOT.</b> The upsert handler's READ leg
/// already reaches all of Rx's terminations (#2454, <c>MeshExtensions</c>: "THREE terminal states,
/// not two"). Its two WRITE legs — <c>DispatchInnerCreate</c> and <c>WriteThroughStream</c> —
/// subscribe with <c>onNext</c>/<c>onError</c> only. This test drives the owner away between the
/// handler entering and the write being acked on the <b>UPDATE</b> leg, and measures it GREEN: the
/// owner's <c>RegisterOwnerDisposingNack</c> mints an <c>OwnerDisposing</c> verdict, the writer
/// re-enqueues against the fresh activation, and the caller is answered <c>success=True</c>. That is
/// the machinery working, and this is a REGRESSION PIN for it — the same role the teardown twin
/// beside this file plays for #2778.</para>
///
/// <para>🚨 <b>It is therefore not #3510's acceptance test, and must not be read as one.</b> CD
/// 7950's trail is <c>UPSERT_READ absent → create</c> — the CREATE leg, which has no disposal-NACK
/// registration and no <c>WriteVerdictBound</c> equivalent. Reproducing that needs a lever this
/// rig does not yet have: parking the partition root's primary stream does NOT put the create
/// behind it (measured — the root's <c>Version</c> never advances after the post, so the create
/// does not route through the parked hub at all). What would produce it is a park on the seam the
/// inner <c>CreateNode</c> actually traverses, or an extraction of the inner-write subscription
/// into a pure composition the way <c>ArmPatchAckWatcher</c> was for the patch lane, so its
/// totality is drivable without a mesh.</para>
///
/// <para>🚨 <b>The owner hub is disposed, NOT the mesh.</b> Disposing the mesh takes the caller's
/// own subscription down with it, so its callback could not run whatever the framework did — the
/// lesson <c>NackReachesTheWaiterDuringTeardownTest</c> records in its own remarks. Here the parent
/// stays <c>Started</c> precisely so "the caller was answered" is an observable fact.</para>
///
/// <para>🚨 <b>No hand-woven gate.</b> The turn → test signal is an <see cref="AsyncSubject{T}"/>
/// the parked turn completes; the release travels back INTO the deliberately parked turn, so it is
/// a volatile flag polled under a bounded <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>
/// and written in a <c>finally</c> so a failing assertion cannot strand it. Same idiom, and same
/// reasons, as the teardown twin next to this file.</para>
/// </summary>
public class UpsertAnswersWhenTheOwnerGoesAwayTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The owner is disposed while the upsert's write is parked at it. Measured GREEN today: the
    /// update leg's disposal NACK and re-enqueue answer the caller. Goes RED if either is lost —
    /// which is the point of pinning it while #3510's create leg is still open.
    /// </summary>
    [Fact]
    public async Task OwnerDisposedUnderTheWrite_AnswersTheCallerInsteadOfGoingSilent()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/upsert-teardown-node";
        await NodeFactory.CreateNode(
                new MeshNode("upsert-teardown-node", TestPartition)
                {
                    Name = "initial",
                    NodeType = "Markdown",
                })
            .Should().Emit();

        // Force the owner's activation and wait until the node is durable, so the upsert's
        // existing-node read provably takes the UPDATE branch rather than racing the create.
        await RequestHub.Observe(
                new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null)
            .FirstAsync().Timeout(10.Seconds()).Await(ct);

        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("the owner must be activated before it can be disposed under a write");

        // Park the owner's merge executor: the upsert's write is ACCEPTED by the owner (which is
        // what registers its disposal NACK) but provably cannot be answered before the owner goes.
        var primary = nodeHub!.GetWorkspace().DataContext
            .GetDataSourceForType(typeof(MeshNode))!
            .GetStreamForPartition(null)!;
        var gateEntered = new AsyncSubject<Unit>();
        var releaseGate = 0;
        var owner = nodeHub!;
        primary.Update((Func<EntityStore?, ChangeItem<EntityStore>?>)(_ =>
        {
            gateEntered.OnNext(Unit.Default);
            gateEntered.OnCompleted();
            SpinWait.SpinUntil(
                () => Volatile.Read(ref releaseGate) == 1 || owner.IsShuttingDown,
                TimeSpan.FromSeconds(60));
            return null;
        }), _ => { });
        try
        {
            await gateEntered.Should().Within(10.Seconds()).Emit(
                "the parked turn must own the primary stream's executor before the upsert is issued");

            var marker = $"upsert-teardown-{Guid.NewGuid():N}"[..24];
            CreateOrUpdateNodeResponse? answer = null;
            Exception? callerError = null;
            var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
            using var upsert = access
                .RunAsSystem(() => ObserveNodeOperation(
                    new CreateOrUpdateNodeRequest(
                        new MeshNode("upsert-teardown-node", TestPartition)
                        {
                            Name = marker,
                            NodeType = "Markdown",
                        })))
                .Subscribe(d => answer = d.Message, ex => callerError = ex);
            Output.WriteLine($"[upsert] issued with marker {marker}; the owner's merge is parked");

            // Fence on the write actually WAITING at the owner. Without it this test could pass by
            // asserting about a caller that had not yet reached the owner at all.
            var registry = Mesh.ServiceProvider.GetRequiredService<LatePatchResponseRegistry>();
            await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .Where(_ => registry.ArmedCount > 0)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            Output.WriteLine($"[fence] the upsert's write is armed at the owner (ArmedCount={registry.ArmedCount})");

            // 🚨 The scenario: the owner goes away under the accepted write. The PARENT stays
            // Started, so the caller's subscription survives and "was it answered" is observable.
            owner.Post(new DisposeRequest(), o => o.WithTarget(owner.Address));
            Output.WriteLine("[dispose] DisposeRequest posted to the owner while the write is in flight");

            // The assertion. The bound is deliberately far below the caller's own budget: an answer
            // that only arrived at a bound would be indistinguishable from the defect.
            await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                .Where(_ => answer is not null || callerError is not null)
                .FirstAsync().Timeout(15.Seconds()).Await(ct);

            Output.WriteLine(
                $"[caller] answer={(answer is null ? "<null>" : $"success={answer.Success} reason={answer.RejectionReason} error={answer.Error}")} "
                + $"error={(callerError is null ? "<null>" : callerError.GetType().Name)}");

            (answer is not null || callerError is not null).Should().BeTrue(
                "an upsert whose owner is disposed under it must reach a terminal — the owner going "
                + "away is not a reason to say NOTHING. Silence here is the #3510 defect: the "
                + "installer then runs out its whole bound with no name for what happened");
        }
        finally
        {
            // In a `finally` so a failing assertion above cannot strand the parked executor turn.
            Volatile.Write(ref releaseGate, 1);
        }
    }
}
