using System;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 Pins the fix for the "successful operation wedges at 60s" bug (the prod DeleteNode wedge:
/// <c>DeleteNodeResponse posted with no AccessContext</c> → <c>[STALE-CALLBACK] DeleteNodeRequest
/// &gt; 30000ms</c> → <c>TimeoutException: No response received … within 00:01:00</c>).
///
/// <para><b>Root cause.</b> A request handler that posts its RESPONSE from an Rx continuation (a
/// <c>.SelectMany</c>/<c>.Do</c> deep in a fan-out, on the workspace emission scheduler) has NO
/// ambient <see cref="AccessService"/> context there — the AsyncLocal is wiped across the scheduler
/// hop. Posting the response with a hand-rolled <c>WithTarget(sender)+WithProperty(RequestId)</c>
/// carries NO context, so the response is attributed to a FALLBACK identity — and on a user-facing
/// hub with no fallback available (the production mesh hub) the fail-closed PostPipeline DROPS it
/// entirely, so the caller's request times out even though the operation SUCCEEDED. The fix is
/// <see cref="PostOptions.ResponseFor"/>, which auto-propagates the REQUEST's AccessContext
/// (captured on the request delivery, independent of the wiped ambient).</para>
///
/// <para>Both handlers below deliberately WIPE the ambient context (<c>SetContext(null)</c>) before
/// posting, reproducing the scheduler hop. The discriminator is which identity the DELIVERED
/// response carries: with ResponseFor it is the original caller; hand-rolled loses it to a fallback
/// (which in prod is the drop). This is exactly the one-line delete-handler fix.</para>
/// </summary>
public class ResponseFromContinuationAccessContextTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string CallerId = "user-alice";

    private sealed record PingViaResponseFor : IRequest<Pong>;
    private sealed record PingViaHandRolledTarget : IRequest<Pong>;
    private sealed record Pong;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(PingViaResponseFor), typeof(PingViaHandRolledTarget), typeof(Pong))
            .WithHandler<PingViaResponseFor>((hub, request) =>
            {
                // Model the fan-out continuation: ambient AsyncLocal context is gone here.
                hub.ServiceProvider.GetRequiredService<AccessService>().SetContext(null);
                // ResponseFor propagates request.AccessContext onto the response despite null ambient.
                hub.Post(new Pong(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .WithHandler<PingViaHandRolledTarget>((hub, request) =>
            {
                hub.ServiceProvider.GetRequiredService<AccessService>().SetContext(null);
                // The BUG shape: same target + request-id correlation, but NO AccessContext.
                hub.Post(new Pong(),
                    o => o.WithTarget(request.Sender).WithProperty(PostOptions.RequestId, request.Id));
                return request.Processed();
            });

    /// <summary>
    /// THE FIX: a response posted via <see cref="PostOptions.ResponseFor"/> from a wiped-ambient
    /// continuation carries the ORIGINAL CALLER's identity on the delivered response — proving the
    /// request's AccessContext propagated across the scheduler hop (the property DeleteNodeResponse
    /// relies on to be delivered rather than fail-closed-dropped).
    /// </summary>
    [Fact]
    public async Task Response_via_ResponseFor_from_wiped_ambient_carries_caller_identity()
    {
        var client = GetClient(c => c.WithPostingIdentity(PostingIdentity.User));
        var access = client.ServiceProvider.GetRequiredService<AccessService>();
        using (access.SwitchAccessContext(new AccessContext { ObjectId = CallerId, Name = "Alice" }))
        {
            var response = await client
                .Observe(new PingViaResponseFor(), o => o.WithTarget(CreateHostAddress()))
                .Should().Within(10.Seconds()).Emit();

            response.AccessContext?.ObjectId.Should().Be(CallerId,
                because: "ResponseFor auto-propagates the request's AccessContext, so the response is " +
                         "attributed to the caller even though the ambient context was wiped in the " +
                         "continuation — the exact reason DeleteNodeResponse must use ResponseFor.");
        }
    }

    /// <summary>
    /// THE BUG (regression guard): the same response posted with a hand-rolled
    /// <c>WithTarget+WithProperty(RequestId)</c> LOSES the caller's identity — the delivered response
    /// carries a fallback identity, never the caller. In production, on the user-facing mesh hub with
    /// no fallback, that "no context" is fail-closed-DROPPED → the caller never gets a reply → the
    /// 60s wedge. If the delete handler's success post ever regresses from ResponseFor to hand-rolled
    /// options, this fails.
    /// </summary>
    [Fact]
    public async Task Response_via_hand_rolled_target_from_wiped_ambient_loses_caller_identity()
    {
        var client = GetClient(c => c.WithPostingIdentity(PostingIdentity.User));
        var access = client.ServiceProvider.GetRequiredService<AccessService>();
        using (access.SwitchAccessContext(new AccessContext { ObjectId = CallerId, Name = "Alice" }))
        {
            var response = await client
                .Observe(new PingViaHandRolledTarget(), o => o.WithTarget(CreateHostAddress()))
                .Should().Within(10.Seconds()).Emit();

            response.AccessContext?.ObjectId.Should().NotBe(CallerId,
                because: "a hand-rolled WithTarget+WithProperty post carries no AccessContext, so the " +
                         "caller's identity is lost across the wiped-ambient continuation — in prod " +
                         "(no fallback) this is the fail-closed DROP that wedges the caller for 60s.");
        }
    }
}
