using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the answer a <see cref="GetDataRequest"/> gets when the read FAULTS because its owner is
/// tearing down: a transient <see cref="ErrorType.ShuttingDown"/> NACK — never a
/// <see cref="GetDataResponse"/> the caller reads as "this node does not exist".
///
/// <para><b>The defect (#1470).</b> <c>DataExtensions.HandleGetDataRequest</c> caught EVERY
/// exception into <c>GetDataResponse { Error = ex.Message }</c>. During teardown
/// <c>SynchronizationStream</c>'s constructor legitimately refuses (it cannot own its sub-hub once
/// hosted-hub creation is frozen) and throws <see cref="HubDisposingException"/>, which the
/// reflective reduce wraps — so the CI log read verbatim
/// <c>Error = Exception has been thrown by the target of an invocation.</c> That fabricated
/// response CLAIMED THE ONCE-ONLY ANSWER SLOT: the <c>ShuttingDown</c> NACK could no longer be
/// posted, <c>GetMeshNode</c> mapped the empty response to <c>null</c>, and its re-probe — which
/// lives only in the <c>OnError</c> arm — never ran. A transient, retryable condition was rendered
/// as a definitive absence.</para>
///
/// <para>It is #1362 reproduced by its own fix: #1362 closed the case where the request produced
/// NO answer; this was the same request producing a WRONG one, from the line above.</para>
///
/// <para><b>How the window is reached</b> — by ORDER, never by waiting. The owner's action block is
/// occupied first, so the <c>DisposeRequest</c> and the read under test are both ENQUEUED before any
/// disposal work is handled; FIFO then fixes the sequence for good: <c>DisposeRequest</c> → the read
/// → the <c>ShutdownRequest</c> that <c>Dispose()</c> posts from inside that handler. The read is
/// dequeued with hosted-hub creation already frozen (<c>CloseCreation</c> is synchronous inside
/// <c>Dispose</c>, before it posts anything) and the run level still <c>Started</c>. A second park —
/// one un-answered response callback — keeps the <c>Quiescing</c> drain from completing underneath
/// the assertions.</para>
///
/// <para>🚨 <b>It did not always order it (#3827).</b> The read used to be posted AFTER
/// <c>DisposeRequest</c> with nothing occupying the block, on the stated premise that "message
/// intake stays open until <c>DisposeHostedHubs</c>". That was TRUE when it was written and #3506
/// removed it — the intake gate used to open one phase too late, and
/// <c>QuiescingHubRefusesNewWorkTest</c> now describes the same sentence as the defect it fixed. What
/// was left was an unordered race between the test's own post and the hub's <c>ShutdownRequest</c>:
/// when the latter won, the read was refused at INTAKE, naming the run level rather than the
/// stream-creation refusal this test exists for. Measured 1 in 4 on unmodified main.</para>
/// </summary>
public class ReadDuringDisposalWindowTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly Address OwnerAddress = new("data-owner", "1");

    private record Item(string Id, string Text);

    /// <summary>Accepted by the owner and never answered — parks one pending response callback.</summary>
    private record HoldRequest : IRequest<HoldResponse>;

    private record HoldResponse;

    /// <summary>
    /// Occupies the owner's ACTION BLOCK until the test releases it — a different thing from
    /// <see cref="HoldRequest"/>, which parks a response CALLBACK. This one is what makes the
    /// ordering below deterministic: while its handler is executing, nothing else on that hub is
    /// dequeued, so the test can place several messages in the queue and know exactly what order
    /// they will be handled in.
    /// </summary>
    private record ParkRequest;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData()
            .WithTypes(typeof(HoldRequest), typeof(HoldResponse), typeof(ParkRequest), typeof(Item));

    /// <summary>Set to 1 to let the parked action block go. Volatile — read from the hub's thread.</summary>
    private int release;

    /// <summary>Completed by the park handler the moment it OWNS the action block.</summary>
    private readonly AsyncSubject<Unit> parked = new();

    [HubFact]
    public async Task ReadThatFaultsOnTeardown_IsNackedShuttingDown_NotAnsweredAsAbsent()
    {
        var host = GetHost();

        var owner = host.GetHostedHub(
            OwnerAddress,
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse), typeof(ParkRequest), typeof(Item))
                .WithHandler<HoldRequest>((_, d) => d.Processed())
                // 🚨 The sanctioned park: a volatile int under a BOUNDED SpinUntil, released in a
                // finally so a failing assertion upstream can never strand the hub's only thread.
                // Never a SemaphoreSlim — this runs ON the action block, which is the one place a
                // hand-rolled async gate deadlocks by construction.
                .WithHandler<ParkRequest>((_, d) =>
                {
                    try
                    {
                        parked.OnNext(Unit.Default);
                        parked.OnCompleted();
                        SpinWait.SpinUntil(
                            // TestTimeouts.Convergence, never a literal: it scales with
                            // MW_TEST_TIMEOUT_FACTOR on CI, and TestTimeoutLiteralRatchetGuard
                            // holds the hand-written count to a number that only goes down.
                            () => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
                    }
                    finally
                    {
                        Volatile.Write(ref release, 1);
                    }
                    return d.Processed();
                })
                .AddData(data => data.AddSource(source =>
                    source.WithType<Item>(type => type
                        .WithKey(i => i.Id)
                        .WithInitialData(new[] { new Item("1", "one") }))))
                // Plumbing fixture, no logged-in user: post as infrastructure, exactly like
                // HubTestBase does for its own host/client hubs (never-null AccessContext).
                .WithPostingIdentity(PostingIdentity.System));
        owner.Should().NotBeNull();
        await owner!.Started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The happy path first, so a later non-answer can never be blamed on the source not
        // existing.
        //
        // 🚨 A DIFFERENT REFERENCE from the one read under test, and that is load-bearing since
        // Systemorph/MeshWeaver#3432. The read path now resolves a SHARED stream per reference
        // (IWorkspace.GetNullableStream), so warming the SAME reference here would leave a live
        // mirror in the cache and the read below would be ANSWERED FROM IT instead of having to
        // build a stream in the frozen window — this test would then pass on a hub that never
        // refused anything. That answered case is real and is pinned by
        // AWarmedReferenceIsAnsweredFromTheLiveMirror below; THIS test is about the read that
        // genuinely FAULTS, so its reference must be one this owner has never reduced.
        var warm = await host
            .Observe<GetDataResponse>(
                new GetDataRequest(new EntityReference(nameof(Item), "1")),
                o => o.WithTarget(OwnerAddress))
            .Should().Within(30.Seconds()).Emit();
        warm.Message.Data.Should().NotBeNull("the read must work before the teardown, or this test proves nothing");

        // Park an un-answered callback so the Quiescing drain cannot complete: the owner then
        // stays in the disposal window instead of racing through teardown in under a millisecond.
        using var held = owner
            .Observe<HoldResponse>(new HoldRequest(), o => o.WithTarget(OwnerAddress))
            // Never throw from these callbacks — they run on the hub's scheduler, where an
            // exception would be unobserved.
            .Subscribe(
                d => Output.WriteLine($"Hold callback answered unexpectedly: {d.Message}"),
                ex => Output.WriteLine($"Hold callback released: {ex.GetType().Name}: {ex.Message}"));

        // 🔻 ORDER BY CAUSATION, NOT BY WAITING. The owner's action block is single-threaded and
        // FIFO — the guarantee the mesh hub itself relies on ("Reply + DisposeRequest(s) from the
        // mesh hub so FIFO guarantees the caller sees the Ok before the deleted hubs tear down").
        // DisposeRequest's handler calls Dispose(), whose very FIRST statement freezes hosted-hub
        // creation SYNCHRONOUSLY; message intake stays open until DisposeHostedHubs. So a read
        // posted after it, from the same sender to the same target, is dequeued INSIDE the window
        // by construction. No poll, no sleep, no sampled property — and no wait that could fall
        // through and let the test assert against a hub that never started disposing.
        // 🚨 OCCUPY THE BLOCK FIRST (#3827). The ordering above was stated as holding "by
        // construction" and did not: the read was posted AFTER DisposeRequest, so its
        // ScheduleNotify raced the ShutdownRequest that Dispose() posts from inside that handler.
        // Whichever won decided the outcome — if ShutdownRequest was handled first the RunLevel had
        // already reached Quiescing and the read was refused at INTAKE, naming the run level instead
        // of the stream-creation refusal this test is about. Measured 1 in 4 on unmodified main.
        //
        // The premise was true when it was written and #3506 removed it: the intake gate used to
        // open one phase too late, so "message intake stays open until DisposeHostedHubs" was a
        // real guarantee. QuiescingHubRefusesNewWorkTest now describes that same sentence as the
        // defect it fixed — two tests on one main, and only one of them could be right.
        //
        // With the block occupied, both posts below are ENQUEUED before any disposal work is
        // handled, and FIFO then fixes the order for good: DisposeRequest → the read →
        // ShutdownRequest. The read is therefore dequeued with creation already frozen (CloseCreation
        // is synchronous inside Dispose, before it posts anything) and the run level still Started.
        host.Post(new ParkRequest(), o => o.WithTarget(OwnerAddress));
        await parked.Should().Within(TestTimeouts.Convergence).Emit(
            "PRECONDITION: the park must OWN the action block before anything else is posted — "
            + "without that the queue order is exactly the race this test is fixing");

        host.Post(new DisposeRequest(), o => o.WithTarget(OwnerAddress));

        // THE READ UNDER TEST. Creation is frozen by the time it is handled, so building the
        // reference's stream throws HubDisposingException — the exact production fault #1470 is
        // about. Subscribed (which is what POSTS it) before the release, awaited after: awaiting
        // first would block this thread and the park would never be let go.
        var answers = new AsyncSubject<object?>();
        using var reading = host
            .Observe<GetDataResponse>(
                new GetDataRequest(new CollectionReference(nameof(Item))),
                o => o.WithTarget(OwnerAddress))
            .Select(d => (object?)d.Message)
            // A DeliveryFailure arrives as OnError (DeliveryFailureException) — turn it into a
            // value so ONE assertion covers both shapes and a hang is the only other outcome.
            .Catch<object?, Exception>(ex => Observable.Return<object?>(ex))
            .Take(1)
            .Subscribe(answers);

        Volatile.Write(ref release, 1);

        var answer = await answers.Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"[TEST] answer: {answer} (owner IsShuttingDown={owner.IsShuttingDown}, RunLevel={owner.RunLevel})");

        // The window was real — read AFTER the fact, so this is a statement about what happened,
        // not a gate that could pass before anything did.
        owner.IsShuttingDown.Should().BeTrue(
            "the read must have been served while the owner's hosted-hub creation was frozen — "
            + "that IS the condition under test, and asserting it here means a routing change that "
            + "broke the FIFO ordering would fail loudly instead of silently answering the read "
            + $"from a healthy hub (RunLevel={owner.RunLevel})");

        answer.Should().BeOfType<DeliveryFailureException>(
            "a read that faulted because its owner is tearing down must be NACKed as transient. "
            + "The pre-fix answer was a GetDataResponse whose Error read 'Exception has been thrown "
            + "by the target of an invocation' — a fabricated success that GetMeshNode maps to null, "
            + "i.e. the caller is told the node does not exist");
        var failure = ((DeliveryFailureException)answer!).Failure;
        failure.Should().NotBeNull();
        failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the owner may reactivate — this is 'ask again', NOT 'gone'. It is also the only "
            + "classification GetMeshNode's re-probe and MeshNodeStreamCache.IsTransientOwnerFailure "
            + "act on");
        failure.Message.Should().Contain("shutting down",
            "MeshNodeStreamCache.IsTransientOwnerFailure classifies by this marker once the typed "
            + "failure has been flattened into a message");
        failure.Message.Should().Contain(nameof(HubDisposingException),
            "the NACK must name the real cause — the stream refused to exist because hosted-hub "
            + "creation is frozen. This also proves the test exercised the STREAM-CREATION refusal "
            + "and was not answered by some other, already-fixed NACK path");
        failure.Message.Should().NotContain("No node found",
            "that phrase turns a retryable stall into a PROVABLE absence "
            + "(MeshNodeStreamCache.IsMissingNodeFailure) — the exact confusion this NACK avoids");
    }

    /// <summary>
    /// 🚨 <b>The OTHER half of the same window, after Systemorph/MeshWeaver#3432.</b> A read whose
    /// stream ALREADY EXISTS is answered from that live mirror — with the data — rather than
    /// faulting because a fresh stream cannot be built.
    ///
    /// <para>This is not a weakening of the test above: what #1470 forbids is answering a teardown
    /// with a fabricated <c>GetDataResponse{Error}</c> that reads as "this node does not exist".
    /// Real data is the opposite of that. The two arms together state the whole contract — a read
    /// that CAN be served is served, a read that cannot be built is NACKed transient — and they are
    /// the reason the read path may share one stream per reference instead of minting a permanent
    /// <c>sync/</c> hub per request.</para>
    /// </summary>
    [HubFact]
    public async Task AWarmedReferenceIsAnsweredFromTheLiveMirror()
    {
        var host = GetHost();

        var owner = host.GetHostedHub(
            new Address("data-owner", "2"),
            c => c.WithTypes(typeof(ParkRequest), typeof(Item))
                .WithHandler<ParkRequest>((_, d) =>
                {
                    try
                    {
                        parked.OnNext(Unit.Default);
                        parked.OnCompleted();
                        SpinWait.SpinUntil(
                            () => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
                    }
                    finally
                    {
                        Volatile.Write(ref release, 1);
                    }
                    return d.Processed();
                })
                .AddData(data => data.AddSource(source =>
                    source.WithType<Item>(type => type
                        .WithKey(i => i.Id)
                        .WithInitialData(new[] { new Item("1", "one") }))))
                .WithPostingIdentity(PostingIdentity.System));
        owner.Should().NotBeNull();
        await owner!.Started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var reference = new CollectionReference(nameof(Item));

        // WARM THE SAME REFERENCE — this is the difference from the test above, and the whole point.
        var warm = await host
            .Observe<GetDataResponse>(new GetDataRequest(reference), o => o.WithTarget(owner.Address))
            .Should().Within(30.Seconds()).Emit();
        warm.Message.Data.Should().NotBeNull("the warm read must land, or nothing is cached to serve from");

        // Same ordering discipline as above: occupy the block, enqueue DisposeRequest, then the read.
        host.Post(new ParkRequest(), o => o.WithTarget(owner.Address));
        await parked.Should().Within(TestTimeouts.Convergence).Emit(
            "PRECONDITION: the park must OWN the action block before anything else is posted");

        host.Post(new DisposeRequest(), o => o.WithTarget(owner.Address));

        var answers = new AsyncSubject<object?>();
        using var reading = host
            .Observe<GetDataResponse>(new GetDataRequest(reference), o => o.WithTarget(owner.Address))
            .Select(d => (object?)d.Message)
            .Catch<object?, Exception>(ex => Observable.Return<object?>(ex))
            .Take(1)
            .Subscribe(answers);

        Volatile.Write(ref release, 1);

        var answer = await answers.Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"[TEST] answer: {answer} (owner IsShuttingDown={owner.IsShuttingDown}, RunLevel={owner.RunLevel})");

        owner.IsShuttingDown.Should().BeTrue(
            "the read must have been served inside the disposal window — that IS the condition "
            + $"under test (RunLevel={owner.RunLevel})");

        var response = answer.Should().BeOfType<GetDataResponse>(
            "a read the owner can still serve from its live mirror is SERVED, not refused — the "
            + "stream-creation refusal only applies when a stream has to be built").Which;
        response.Error.Should().BeNull(
            "🚨 an Error here is exactly #1470's fabricated absence — the failure this whole file "
            + "exists to keep out");
        response.Data.Should().NotBeNull(
            "the answer carries the owner's actual data; a null payload is what GetMeshNode maps to "
            + "'this node does not exist'");
    }

}
