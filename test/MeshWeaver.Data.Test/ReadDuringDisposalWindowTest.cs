using System;
using System.Reactive.Linq;
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
/// <para><b>How the window is held open</b> — the technique
/// <c>SubscribeDuringRecycleTest</c> established, no sleeps and no racing: one un-answered response
/// callback is parked on the owner, so its <c>Quiescing</c> phase cannot drain and the hub sits in
/// the disposal window (creation frozen, message intake still open) for its whole quiesce budget.
/// The test then WAITS until <c>RunLevel</c> has demonstrably reached <c>Quiescing</c> before
/// reading — the state is verified, not timed.</para>
/// </summary>
public class ReadDuringDisposalWindowTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly Address OwnerAddress = new("data-owner", "1");

    private record Item(string Id, string Text);

    /// <summary>Accepted by the owner and never answered — parks one pending response callback.</summary>
    private record HoldRequest : IRequest<HoldResponse>;

    private record HoldResponse;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData()
            .WithTypes(typeof(HoldRequest), typeof(HoldResponse), typeof(Item));

    [HubFact]
    public async Task ReadThatFaultsOnTeardown_IsNackedShuttingDown_NotAnsweredAsAbsent()
    {
        var host = GetHost();

        var owner = host.GetHostedHub(
            OwnerAddress,
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse), typeof(Item))
                .WithHandler<HoldRequest>((_, d) => d.Processed())
                .AddData(data => data.AddSource(source =>
                    source.WithType<Item>(type => type
                        .WithKey(i => i.Id)
                        .WithInitialData(new[] { new Item("1", "one") }))))
                // Plumbing fixture, no logged-in user: post as infrastructure, exactly like
                // HubTestBase does for its own host/client hubs (never-null AccessContext).
                .WithPostingIdentity(PostingIdentity.System));
        owner.Should().NotBeNull();
        await owner!.Started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The happy path first, so a later non-answer can never be blamed on the reference not
        // resolving or the source not existing.
        var warm = await host
            .Observe<GetDataResponse>(
                new GetDataRequest(new CollectionReference(nameof(Item))),
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
        host.Post(new DisposeRequest(), o => o.WithTarget(OwnerAddress));

        // THE READ UNDER TEST. Creation is frozen, so building the reference's stream throws
        // HubDisposingException — the exact production fault #1470 is about.
        var answer = await host
            .Observe<GetDataResponse>(
                new GetDataRequest(new CollectionReference(nameof(Item))),
                o => o.WithTarget(OwnerAddress))
            .Select(d => (object?)d.Message)
            // A DeliveryFailure arrives as OnError (DeliveryFailureException) — turn it into a
            // value so ONE assertion covers both shapes and a hang is the only other outcome.
            .Catch<object?, Exception>(ex => Observable.Return<object?>(ex))
            .Should().Within(30.Seconds()).Emit();
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

}
