using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Seam contract for <see cref="IMessageHub.FailStartup"/> (task #18, defect 1 — the
/// TEARDOWN STRAGGLER adjacent to #228):
///
/// <para><b>Teardown-disposal is a CANCELLATION of startup, not a startup failure.</b>
/// At hub disposal, <c>MessageHub.CancelCallbacks</c> pushes
/// <see cref="ObjectDisposedException"/> ("Hub … was disposed before the response arrived
/// …") into every pending <c>Observe</c> subject; <c>SynchronizationStream.OnError</c>
/// forwards that into <c>Hub.FailStartup</c> on its inner sync hub. Faulting the
/// <c>Started</c> <c>TaskCompletionSource</c> with it armed an unobserved-task fatal:
/// a sync hub's <c>Started</c> has NO awaiter at teardown, so the fault detonated at GC as
/// <c>TaskScheduler.UnobservedTaskException</c> → xUnit v3 "Catastrophic failure" (#228's
/// capture). A CANCELED task, by contrast, never raises UnobservedTaskException — and a
/// live awaiter still gets a graceful, typed <see cref="TaskCanceledException"/>.</para>
///
/// <para><b>A REAL startup error must still FAULT <c>Started</c></b> — dependents awaiting
/// startup (the DataContext init gate) must observe the actual exception. Only the
/// disposal shape (an <see cref="ObjectDisposedException"/> anywhere in the cause chain,
/// matching <c>SynchronizationStream.IsObjectDisposed</c>) is classified as cancellation.</para>
///
/// <para>End-to-end repro of the incident chain (pending SubscribeRequest → dispose →
/// sync hub startup canceled, no unobserved fatal):
/// <c>MeshWeaver.Data.Test.TeardownPendingSubscribeGracefulTest</c>.</para>
/// </summary>
public class FailStartupTeardownClassificationTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Creates a client hub whose initialization never completes on its own (the init
    /// observable is gated on a subject this test controls), so <c>Started</c> is still
    /// pending when <c>FailStartup</c> is invoked — the exact state of a sync hub whose
    /// initial DataChangedEvent never arrived.
    /// </summary>
    private IMessageHub CreateUnstartedHub(out AsyncSubject<Unit> releaseInit)
    {
        var gate = new AsyncSubject<Unit>();
        releaseInit = gate;
        return GetClient(c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithInitialization(_ => gate.AsObservable()));
    }

    private static void Release(AsyncSubject<Unit> gate)
    {
        gate.OnNext(Unit.Default);
        gate.OnCompleted();
    }

    [Fact(Timeout = 30000)]
    public void FailStartup_WithTeardownDisposal_CancelsStarted_InsteadOfFaultingIt()
    {
        var hub = CreateUnstartedHub(out var gate);
        try
        {
            hub.Started.IsCompleted.Should().BeFalse("the init gate is still closed");

            // The exact shape CancelCallbacks pushes at teardown.
            hub.FailStartup(new ObjectDisposedException(nameof(MessageHub),
                $"Hub {hub.Address} was disposed before the response arrived (request type SubscribeRequest, target host/1)."));

            hub.Started.IsCanceled.Should().BeTrue(
                "disposal before startup is a graceful cancellation — the hub will simply never start");
            hub.Started.IsFaulted.Should().BeFalse(
                "a faulted Started with no awaiter is an unobserved-task fatal armed for the next GC (the #228 catastrophic)");
        }
        finally
        {
            Release(gate);
        }
    }

    [Fact(Timeout = 30000)]
    public void FailStartup_WithWrappedDisposal_IsAlsoClassifiedAsCancellation()
    {
        var hub = CreateUnstartedHub(out var gate);
        try
        {
            // The JsonSynchronizationStream DataChangeRequest error shape: the teardown
            // ObjectDisposedException arrives WRAPPED (InvalidOperationException → ODE).
            // Classification must walk the cause chain, mirroring
            // SynchronizationStream.IsObjectDisposed.
            hub.FailStartup(new InvalidOperationException(
                "DataChangeRequest failed for stream sync-1",
                new ObjectDisposedException(nameof(MessageHub), "Hub client/1 was disposed before the response arrived.")));

            hub.Started.IsCanceled.Should().BeTrue("the disposal cause must be recognised anywhere in the chain");
            hub.Started.IsFaulted.Should().BeFalse();
        }
        finally
        {
            Release(gate);
        }
    }

    [Fact(Timeout = 30000)]
    public void FailStartup_WithRealError_StillFaultsStarted_ForDependentsToObserve()
    {
        var hub = CreateUnstartedHub(out var gate);
        try
        {
            var error = new InvalidOperationException("schema load failed: column mismatch");
            hub.FailStartup(error);

            hub.Started.IsFaulted.Should().BeTrue(
                "a genuine startup failure must fault Started so dependents (the DataContext init gate) observe the real error");
            hub.Started.Exception!.InnerExceptions.Should().Contain(error);

            // Observe the fault here so this test's own assertion never becomes
            // the unobserved fatal it is guarding against.
            _ = hub.Started.Exception;
        }
        finally
        {
            Release(gate);
        }
    }
}
