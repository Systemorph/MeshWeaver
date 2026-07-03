using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Deterministic repro for the TEARDOWN STRAGGLER left open by #228 (task #18, defect 1):
/// at hub disposal, <c>MessageHub.CancelCallbacks</c> pushes
/// <see cref="ObjectDisposedException"/> ("Hub … was disposed before the response arrived
/// (request type SubscribeRequest …)") into every pending <c>hub.Observe</c> subject. For a
/// remote sync subscription whose owner never answered, that error is routed by
/// <c>JsonSynchronizationStream</c> into <c>reduced.OnError</c> →
/// <c>SynchronizationStream.OnError</c> → <c>Hub.FailStartup(error)</c> on the INNER sync hub
/// (<c>sync/{streamId}</c>) — whose <c>Started</c> gate is still closed because the initial
/// <c>DataChangedEvent</c> never arrived.
///
/// <para><b>The defect:</b> <c>FailStartup</c> faulted the <c>hasStarted</c>
/// <c>TaskCompletionSource</c> with the teardown <see cref="ObjectDisposedException"/>. A
/// sync hub's <c>Started</c> task has NO awaiter at teardown (the only frameworks awaiting
/// <c>Started</c> — the DataContext init gate — cover data-source streams, not ad-hoc remote
/// streams), so the fault sat in an unreferenced Task until GC finalized it and raised
/// <c>TaskScheduler.UnobservedTaskException</c> — which xUnit v3 escalates to a
/// "Catastrophic failure" that poisons whichever test class runs next (#228's capture).</para>
///
/// <para><b>The contract under test:</b> teardown-disposal is a CANCELLATION of startup, not
/// a startup failure — the hub will simply never start. <c>FailStartup</c> must transition
/// <c>Started</c> to Canceled (a canceled Task never raises UnobservedTaskException; a live
/// awaiter still gets a graceful, typed <see cref="TaskCanceledException"/>). A REAL startup
/// error must still fault <c>Started</c> — that path is pinned by
/// <c>FailStartupTeardownClassificationTest</c> in Messaging.Hub.Test.</para>
/// </summary>
public class TeardownPendingSubscribeGracefulTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Signals each <see cref="SubscribeRequest"/> the host swallows. Instance state —
    /// never static (no cross-test bleed); completes with the stream id of the request.
    /// </summary>
    private readonly ReplaySubject<string> subscribeReceived = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            // The owner SWALLOWS every SubscribeRequest — no ack, no DataChangedEvent, no
            // failure. This pins the client's pending-callback state: the SubscribeRequest
            // stays in the client hub's responseSubjects until disposal cancels it.
            .WithHandler<SubscribeRequest>((_, delivery) =>
            {
                subscribeReceived.OnNext(delivery.Message.StreamId);
                return delivery.Processed();
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            // Workspace only — no data sources. The stream under test is the AD-HOC remote
            // stream (GetRemoteStream), whose inner sync hub's Started task has no awaiter —
            // exactly the shape (mesh-node/user-driven subscriptions) that fataled in CI.
            .AddData();

    [HubFact]
    public async Task DisposingHubWithPendingSubscribe_CancelsSyncHubStartup_InsteadOfFaultingIt()
    {
        GetHost(); // activate the swallowing owner
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        // Capture unobserved task faults of the incident's exact shape. Registered BEFORE
        // the stream is opened so nothing can slip past; filtered so unrelated teardown
        // noise from parallel infrastructure can never fail this test.
        var unobservedFatals = new ConcurrentBag<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            var matches = e.Exception?.Flatten().InnerExceptions
                .Where(ex => ex is ObjectDisposedException
                             && ex.Message.Contains("disposed before the response arrived"))
                .ToArray();
            if (matches is { Length: > 0 })
            {
                foreach (var ex in matches)
                    unobservedFatals.Add(ex);
                e.SetObserved(); // keep the repro's own fatal from poisoning sibling tests
            }
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            // Open the remote stream: posts SubscribeRequest to the owner and spins up the
            // inner sync hub, whose SynchronizationGate stays closed until the initial
            // DataChangedEvent (which never comes).
            var stream = workspace.GetRemoteStream<EntityStore, CollectionsReference>(
                CreateHostAddress(), new CollectionsReference("ghost"));
            var syncHub = stream.Hub;

            // Wait on the actual condition: the owner HAS the SubscribeRequest (so the
            // pending callback exists in the client hub) — no sleeps.
            await subscribeReceived
                .Where(id => id == stream.StreamId)
                .FirstAsync()
                .Timeout(10.Seconds())
                .ToTask();

            // Precondition that makes the seam live: the sync hub never started (its
            // initialization gate is still waiting on the initial state).
            syncHub.Started.IsCompleted.Should().BeFalse(
                "the owner never sent the initial DataChangedEvent, so the sync hub's startup gate must still be closed");

            // THE RACE, DISTILLED: dispose the client while the SubscribeRequest is pending.
            // CancelCallbacks pushes the ObjectDisposedException; the sync stream routes it
            // into FailStartup on the never-started sync hub.
            client.Dispose();
            await client.DisposalCompleted
                .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
                .FirstOrDefaultAsync()
                .ToTask()
                .WaitAsync(30.Seconds());

            // The seam contract. Pre-fix: IsFaulted == true (the unobserved fatal armed and
            // waiting for GC). Post-fix: startup is CANCELED — graceful for live awaiters,
            // benign for abandoned ones.
            syncHub.Started.IsFaulted.Should().BeFalse(
                "teardown-disposal must never fault the sync hub's Started task — a faulted Started with no awaiter "
                + "is exactly the unobserved-task fatal that poisoned CI (#228 capture)");
            syncHub.Started.IsCanceled.Should().BeTrue(
                "disposal before the initial state is a graceful CANCELLATION of startup — the hub will simply never start");

            // Incident-shaped proof: release every reference to the sync hub's task graph and
            // force finalization — no unobserved fatal of the captured shape may surface.
            stream = null!;
            syncHub = null!;
        }
        finally
        {
            // The GC sweep runs with the handler still attached, then detaches it.
            for (var i = 0; i < 3; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
            }
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }

        unobservedFatals.Should().BeEmpty(
            "no teardown-disposal may ever surface as an unobserved task fault (the CI-poisoning catastrophic)");
    }
}
