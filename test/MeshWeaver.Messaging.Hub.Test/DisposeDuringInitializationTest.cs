using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the recognized-shutdown outcome for a hub whose initialization is terminated by
/// teardown (issues #1122–#1125, the <c>$model-probe</c> disposal race).
///
/// <para><b>The production sequence.</b> A transient probe hub (<c>$model-probe/{guid}</c>,
/// <c>NodeTypeDataModelAreas.ProbeInstanceModel</c>) is created, read once, and disposed — by
/// design. Its DataContext spins up <c>sync/{id}</c> sub-hubs whose BuildupActions wait on a
/// <c>GetDataRequest</c> whose pending response callback is registered on the PROBE hub. When
/// the probe's read times out (upstream cache unavailable) the probe is disposed mid-init:
/// its <c>CancelCallbacks</c> errors the pending subject with <see cref="ObjectDisposedException"/>
/// ("Hub … was disposed before the response arrived") straight into the child's in-flight
/// BuildupAction. <c>HandleInitialize</c>'s blanket <c>.Catch</c> then logged a fail-level
/// "initialization failed … Hub is now in FAILED state." for a hub that was itself part of the
/// SAME disposal cascade — routine teardown reported as an error, with FAILED residue.</para>
///
/// <para><b>The fix.</b> The child IS provably part of the shutdown at that moment: the
/// ancestor's <c>Dispose()</c> cascades <c>HostedHubsCollection.CloseCreation</c> through the
/// subtree at its FIRST instant, so <see cref="IMessageHub.IsShuttingDown"/> is already true on
/// the child while its own <see cref="IMessageHub.IsDisposing"/> is still false.
/// <c>HandleInitialize</c>'s catch now classifies that as a recognized shutdown: Debug log, no
/// <see cref="MessageHub.InitializationError"/>, gate still opened so disposal traffic flows.</para>
///
/// <para><b>RED before the fix:</b> the child records <c>InitializationError</c> (FAILED state)
/// after the parent's disposal. <b>GREEN after:</b> it stays null — the init ended as a
/// shutdown outcome, not a failure. The companion tests in
/// <see cref="InitializationErrorSurfacedTest"/> pin the OTHER side: a fault on a hub that is
/// NOT shutting down still enters the FAILED state.</para>
/// </summary>
public class DisposeDuringInitializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record SlowRequest : IRequest<SlowResponse>;

    private record SlowResponse;

    [Fact(Timeout = 30000)]
    public async Task InitTerminatedByAncestorDisposal_IsRecognizedShutdown_NoFailedState()
    {
        var host = GetHost();

        // The transient probe-shaped hub: it SWALLOWS SlowRequest without ever answering, so a
        // request observed through it stays pending in its response registry — exactly the
        // $model-probe hub holding its children's unanswered GetDataRequest.
        var parent = host.GetHostedHub(
            new Address("probe-parent", Guid.NewGuid().ToString("N")),
            c => c
                // Plumbing fixture, no logged-in user — post as infrastructure, like the
                // fixture's own hubs (see HubTestBase.ConfigureMesh).
                .WithPostingIdentity(PostingIdentity.System)
                // Short quiesce budget so CancelCallbacks (the step that errors the pending
                // callback into the child's init) runs fast; prod default is 2s.
                .WithQuiesceTimeout(TimeSpan.FromMilliseconds(500))
                .WithHandler<SlowRequest>((_, r) => r.Processed()));

        var buildupWaiting = new ReplaySubject<Unit>(1);

        // The child (prod: the probe's sync/{id} data-source sub-hub): its BuildupAction waits
        // on a request whose pending callback lives on the PARENT. Observe registers the
        // subject synchronously before posting, so once buildupWaiting fires the pending entry
        // exists and the child's init is parked on it.
        var child = parent.GetHostedHub(
            new Address("probe-child", Guid.NewGuid().ToString("N")),
            c => c
                .WithPostingIdentity(PostingIdentity.System)
                .WithInitialization(_ =>
            {
                    var pending = parent.Observe(new SlowRequest(), o => o.WithTarget(parent.Address));
                    buildupWaiting.OnNext(Unit.Default);
                    return pending.Select(_ => Unit.Default);
                }));

        child.Should().NotBeNull();
        await buildupWaiting.FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask();

        // Dispose the parent mid-init — the transient-probe lifecycle. CancelCallbacks errors
        // the pending request with ObjectDisposedException INTO the child's still-running
        // BuildupAction, while the child is already frozen by the disposal cascade.
        parent.Dispose();
        await parent.DisposalCompleted.FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask();

        ((MessageHub)child!).InitializationError.Should().BeNull(
            "dispose-during-init is a NORMAL path for a hub inside a disposing subtree — it must "
            + "terminate as a recognized shutdown outcome (no FAILED-state residue, no fail-level "
            + "log), not as 'initialization failed'");
    }
}
