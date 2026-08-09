using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the HANDLER-side half of the Quiescing-timeout diagnostic (issue #981).
///
/// <para>The caller-side half has always been there: when the dispose drain budget expires with
/// response callbacks outstanding, the hub names the request type, its target and its age. That is
/// enough to know a request went unanswered and nothing more — which is why two independent
/// production-shaped captures (a <c>CreateNodeRequest</c> addressed to the mesh hub itself, unanswered
/// for 2 738 ms and 6 153 ms against a 2 s budget, with every queue empty) still left the mechanism
/// unnamed. These tests pin the missing half: the failure must say what happened to the delivery at
/// the RECEIVING end — whether it was ever received, routed, deferred, dropped, handled, and whether
/// anything ever replied to that correlation.</para>
///
/// <para>The scenario is the #981 shape reduced to its essentials: a handler that RUNS and returns
/// <c>Processed</c> without posting a response. The framework only auto-NACKs an <em>unhandled</em>
/// delivery, so a handled-but-unanswered request leaves its caller's callback pending forever —
/// exactly the state the quiescing budget detects.</para>
/// </summary>
public class QuiescingTimeoutNamesHandlerSideTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record SwallowedRequest : IRequest<SwallowedResponse>;
    private record SwallowedResponse;

    /// <summary>Answered — but to an address that is not the requester (the "replied to the wrong
    /// place" shape, which must NOT read the same as a handler that replied to nothing).</summary>
    private record MisroutedReplyRequest : IRequest<SwallowedResponse>;

    /// <summary>
    /// Budget for a hub that is deliberately disposed with an outstanding callback. Short so the
    /// test costs its own budget, not the 2 s default — the assertion is about WHAT the timeout
    /// reports, never about how long it waits.
    /// </summary>
    private static readonly TimeSpan LeakQuiesceTimeout = TimeSpan.FromMilliseconds(300);

    private readonly TaskCompletionSource handlerRan = new();

    /// <summary>
    /// Host hub with the swallowing handler: it runs (so <c>HANDLER_ENTER</c>/<c>HANDLER_EXIT</c>
    /// must appear on the trail) and answers nothing (so <c>RESPONSE_POSTED</c> must not).
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithQuiesceTimeout(LeakQuiesceTimeout)
            .WithHandler<SwallowedRequest>((_, delivery) =>
            {
                handlerRan.TrySetResult();
                return delivery.Processed();
            })
            // Posts a correlated reply, but aims it at the mesh hub instead of the requester —
            // so RESPONSE_POSTED lands on the trail while the caller's callback never resolves.
            .WithHandler<MisroutedReplyRequest>((hub, delivery) =>
            {
                hub.Post(new SwallowedResponse(),
                    o => o.WithRequestIdFrom(delivery).WithTarget(CreateMeshAddress()));
                handlerRan.TrySetResult();
                return delivery.Processed();
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).WithQuiesceTimeout(LeakQuiesceTimeout);

    /// <summary>
    /// The #981 signature exactly: a hub awaiting a request it addressed to ITSELF. The summary must
    /// carry the receiving-side trail, and that trail must show the handler ran and never replied —
    /// which is a different defect from "the request was never delivered" and must not read the same.
    /// </summary>
    [Fact]
    public async Task SelfAddressedRequest_HandledButNeverAnswered_SummaryShowsHandlerRanAndDidNotReply()
    {
        var host = GetHost();

        // Deliberately abandoned: the callback stays registered, which is what the Quiescing budget
        // is there to detect. onError is supplied because disposal errors the subject.
        using var subscription = host
            .Observe<SwallowedResponse>(new SwallowedRequest(), o => o.WithTarget(CreateHostAddress()))
            .Subscribe(_ => { }, _ => { });

        await handlerRan.Task.WaitAsync(TimeSpan.FromSeconds(10));

        host.Dispose();
        await host.DisposalCompleted.FirstAsync().ToTask().WaitAsync(TimeSpan.FromSeconds(20));

        host.AnyHubQuiescingTimedOut().Should().BeTrue(
            "the abandoned callback is exactly what the Quiescing budget exists to catch");

        var summary = host.GetQuiescingTimeoutSummary();
        Output.WriteLine(summary);

        summary.Should().Contain("handler-side fate",
            "a caller-only report is what left #981's mechanism unnamed across two captures");
        summary.Should().Contain("RECEIVED",
            "the trail must say whether the delivery ever reached a hub at all");
        summary.Should().Contain("HANDLER_ENTER",
            "'a handler ran' and 'no handler matched' are different defects and must read differently");
        summary.Should().Contain("HANDLER_EXIT state=Processed",
            "the handler's terminal delivery state is what says it completed rather than faulted");
        summary.Should().NotContain("RESPONSE_POSTED",
            "nothing replied to this correlation — that absence IS the finding, so it must not be "
            + "reported the same way as a reply that was posted and then lost");

        // The raw stages are the evidence; the verdict is the answer. Pinning it here also pins the
        // branch ORDER in RequestFateLedger.Verdict — an earlier branch matching by mistake would
        // hand the next investigator a confidently wrong diagnosis.
        summary.Should().Contain("a handler was entered and no reply, completion or fault has been recorded",
            "the report must state which failure shape this is, not leave the reader to infer it "
            + "from raw stages");
    }

    /// <summary>
    /// The discriminator that matters most: a reply that WAS posted but never reached the requester
    /// must not read like a handler that replied to nothing. One says "chase the response delivery",
    /// the other says "chase the handler" — sending an investigation the wrong way is the specific
    /// cost this instrumentation exists to avoid.
    /// </summary>
    [Fact]
    public async Task ReplyPostedToTheWrongTarget_IsReportedDifferentlyFromNoReplyAtAll()
    {
        var host = GetHost();
        var client = GetClient();

        using var subscription = client
            .Observe<SwallowedResponse>(new MisroutedReplyRequest(), o => o.WithTarget(CreateHostAddress()))
            .Subscribe(_ => { }, _ => { });

        await handlerRan.Task.WaitAsync(TimeSpan.FromSeconds(10));

        client.Dispose();
        await client.DisposalCompleted.FirstAsync().ToTask().WaitAsync(TimeSpan.FromSeconds(20));

        var summary = client.GetQuiescingTimeoutSummary();
        Output.WriteLine(summary);

        summary.Should().Contain("RESPONSE_POSTED",
            "the handler did answer this correlation — the trail must say so");
        summary.Should().Contain("a reply WAS posted for this correlation and the callback is STILL pending",
            "this is the 'reply lost on the way home' shape, and it must be told apart from a "
            + "handler that never replied");
    }

    /// <summary>
    /// The same, across two hubs — which is what proves the ledger is shared by the hub TREE. The
    /// requester (client) and the receiver (host) are different hubs, so a per-hub trail would leave
    /// the requester's report exactly as blind as before. The trail must name the RECEIVING hub.
    /// </summary>
    [Fact]
    public async Task CrossHubRequest_HandledButNeverAnswered_TrailNamesTheReceivingHub()
    {
        _ = GetHost();
        var client = GetClient();

        using var subscription = client
            .Observe<SwallowedResponse>(new SwallowedRequest(), o => o.WithTarget(CreateHostAddress()))
            .Subscribe(_ => { }, _ => { });

        await handlerRan.Task.WaitAsync(TimeSpan.FromSeconds(10));

        client.Dispose();
        await client.DisposalCompleted.FirstAsync().ToTask().WaitAsync(TimeSpan.FromSeconds(20));

        client.AnyHubQuiescingTimedOut().Should().BeTrue();

        var summary = client.GetQuiescingTimeoutSummary();
        Output.WriteLine(summary);

        summary.Should().Contain($"HANDLER_ENTER@{CreateHostAddress()}",
            "the stage must be attributed to the hub that RECEIVED the delivery, not to the "
            + "requester — a per-hub ledger would report nothing here");
    }
}
