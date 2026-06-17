using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Dedicated tests for the ONE access-context invariant
/// (<c>feedback_access_context_always_set</c>):
///
/// <para><b>AccessContext must ALWAYS be set — never null.</b> Three sources, no
/// fourth: framework/infrastructure → <c>System</c>; user-based contexts (portal
/// hub, HTTP) → the user; threads/activities → the owner. There is no legitimate
/// application post with a null resolved context once those sources are wired, so
/// a null is a GAP — the post lost the user identity.</para>
///
/// <para><b>No identity, no delivery.</b> When an application post resolves no
/// context, the PostPipeline logs an ERROR (the CI tripwire on the
/// <c>MeshWeaver.AccessContext</c> channel) and FAILS the delivery — it does NOT
/// throw out of <c>Post</c> (Post is fire-and-forget from countless callsites; a
/// synchronous throw would be unobserved or crash an unrelated path) and it does
/// NOT silently leave a null context (that masked the empty-agent-registry / prod
/// EventCalendar bug). The awaiting <c>hub.Observe(...)</c> gets a clean OnError.</para>
///
/// <para>Genuinely identity-free framework traffic is EXEMPT and must NOT be failed:
/// <c>[SystemMessage]</c>, <c>[CanBeIgnored]</c>, and <c>DeliveryFailure</c>.</para>
///
/// <para>The portal SOURCE fix is the mirror image: a per-hub PostPipeline step
/// (as <c>PortalApplication.DefaultPortalConfig</c> installs) stamps the circuit
/// user when a post has no context, so the per-circuit portal hub's
/// layout/agent/model subscribes carry the user — the last test pins that shape.</para>
/// </summary>
public class AccessContextNeverNullTest(ITestOutputHelper output) : HubTestBase(output)
{
    // ---- probe message + handler: captures the AccessContext as it landed ----
    private sealed record CaptureCtx : IRequest<CaptureCtxResponse>;
    private sealed record CaptureCtxResponse(string? ObjectId, bool HasAccessContext);

    // A fire-and-forget control message — [CanBeIgnored] ⇒ exempt from the never-null
    // rule. Must NOT be failed even with no ambient context.
    [CanBeIgnored]
    private sealed record IgnorableControl;

    // Framework-lifecycle traffic — [SystemMessage] ⇒ exempt. Must NOT be failed.
    [SystemMessage]
    private sealed record SystemControl;

    // Side channel the host handlers push to on receipt, so the exemption tests can
    // assert the exempt message was DELIVERED (handler ran) without posting a
    // non-exempt response that would itself trip the never-null rule. Instance
    // ReplaySubject (per-test-method instance — no static) so a subscriber that
    // attaches after the handler fired still observes the emission (no subscribe/post
    // race). Captured in the ConfigureHost closure (this is an instance method).
    private readonly System.Reactive.Subjects.ReplaySubject<string> _delivered = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(CaptureCtx), typeof(CaptureCtxResponse),
                typeof(IgnorableControl), typeof(SystemControl))
            .WithHandler<CaptureCtx>((hub, request) =>
            {
                var ctx = request.AccessContext;
                hub.Post(new CaptureCtxResponse(ctx?.ObjectId, ctx is not null),
                    o => o.ResponseFor(request));
                return request.Processed();
            })
            // Exempt-message handlers push to the side channel — no response post, so the
            // never-null rule is exercised ONLY by the exempt inbound message itself.
            .WithHandler<IgnorableControl>((hub, request) =>
            {
                _delivered.OnNext(nameof(IgnorableControl));
                return request.Processed();
            })
            .WithHandler<SystemControl>((hub, request) =>
            {
                _delivered.OnNext(nameof(SystemControl));
                return request.Processed();
            });

    /// <summary>
    /// VERIFICATION (b): an application post with NO resolved context FAILS the
    /// delivery — the awaiting Observe gets OnError naming the invariant. Proves the
    /// tripwire fires on a genuinely null post and surfaces cleanly (no hang, no throw).
    /// </summary>
    [Fact]
    public async Task Application_post_with_no_context_fails_the_delivery()
    {
        // A USER-mode hub (the default in production for user-facing hubs). The test base
        // declares its plumbing fixtures as System; here we explicitly assert the User-mode
        // never-null behaviour, so opt this hub back into User.
        var client = GetClient(c => c.WithPostingIdentity(PostingIdentity.User));

        var notification = await client
            .Observe(new CaptureCtx(), o => o.WithTarget(CreateHostAddress()))
            .Materialize()
            .Should().Within(10.Seconds()).Match(n => n.Kind == NotificationKind.OnError);

        notification.Exception!.Message.Should().Contain("AccessContext must never be null",
            because: "no identity, no delivery — the never-null invariant fails the post and " +
                     "surfaces it as OnError to the awaiting Observe, with a message that names " +
                     "the invariant so the gap is fixable at the source");
    }

    /// <summary>
    /// VERIFICATION (a) — the portal SOURCE fix shape: a hub whose configuration adds
    /// the same stamp-the-circuit-user-when-null PostPipeline step that
    /// <c>PortalApplication.DefaultPortalConfig</c> installs posts WITH the circuit
    /// user even though no ambient AccessContext is set on the action-block thread.
    /// The receiver sees that user — NOT null — so its RLS returns the user's data
    /// instead of denying (the empty agent registry is fixed at the source).
    /// </summary>
    [Fact]
    public async Task Portal_style_hub_stamps_the_circuit_user_when_no_ambient_context()
    {
        var circuitUser = new AccessContext { ObjectId = "rbuergi", Name = "Roland" };

        // Mirror PortalApplication.DefaultPortalConfig: add a PostPipeline step that
        // stamps the per-circuit user when a post has no AccessContext. Added after
        // UserServicePostPipeline; AddPipeline wraps outer-first, so this runs BEFORE
        // the never-null check and the post carries the user (so it is never failed).
        var portalLike = GetClient(c => c
            .WithPostingIdentity(PostingIdentity.User) // the portal is a user-facing hub
            .AddPostPipeline(syncPipeline =>
                syncPipeline.AddPipeline((d, next) =>
                    next(d.AccessContext is null ? d.SetAccessContext(circuitUser) : d))));

        var response = await portalLike
            .Observe(new CaptureCtx(), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit();

        response.Message.HasAccessContext.Should().BeTrue(
            because: "the portal's per-hub PostPipeline step stamps the circuit user when a " +
                     "post has no ambient context — so the portal hub's subscribes carry the " +
                     "user regardless of which thread the post lands on");
        response.Message.ObjectId.Should().Be("rbuergi",
            because: "the stamped identity is the resolved circuit user, the SOURCE of the " +
                     "never-null invariant for the portal hub");
    }

    /// <summary>
    /// A <c>[CanBeIgnored]</c> fire-and-forget control message is EXEMPT — it is
    /// DELIVERED (the handler runs) even with no context, never failed by the
    /// never-null rule.
    /// </summary>
    [Fact]
    public async Task CanBeIgnored_message_is_exempt_and_delivered_with_no_context()
    {
        // Ensure the host hub exists so its handler is registered before we post.
        GetHost();
        var client = GetClient();

        var seen = _delivered.Where(t => t == nameof(IgnorableControl))
            .Should().Within(10.Seconds()).Match(_ => true);
        client.Post(new IgnorableControl(), o => o.WithTarget(CreateHostAddress()));

        (await seen).Should().Be(nameof(IgnorableControl),
            because: "[CanBeIgnored] control traffic carries no security-relevant payload and " +
                     "is exempt from the never-null rule — it must be delivered, not failed");
    }

    /// <summary>
    /// A <c>[SystemMessage]</c> framework-lifecycle message is EXEMPT — delivered with
    /// no context.
    /// </summary>
    [Fact]
    public async Task SystemMessage_is_exempt_and_delivered_with_no_context()
    {
        GetHost();
        var client = GetClient();

        var seen = _delivered.Where(t => t == nameof(SystemControl))
            .Should().Within(10.Seconds()).Match(_ => true);
        client.Post(new SystemControl(), o => o.WithTarget(CreateHostAddress()));

        (await seen).Should().Be(nameof(SystemControl),
            because: "[SystemMessage] framework traffic is exempt from the never-null rule");
    }

    /// <summary>
    /// A hub declared <see cref="PostingIdentity.System"/> at startup (routing /
    /// persistence) stamps <c>system-security</c> on its own otherwise-unattributed
    /// posts — no per-callsite <c>ImpersonateAsSystem</c> needed, never null, never
    /// failed. This is the declarative form of the never-null invariant's System source.
    /// </summary>
    [Fact]
    public async Task System_identity_hub_posts_as_system_with_no_ambient_context()
    {
        // A client hub declared as System-posting infrastructure.
        var systemHub = GetClient(c => c.WithPostingIdentity(PostingIdentity.System));

        var response = await systemHub
            .Observe(new CaptureCtx(), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit();

        response.Message.HasAccessContext.Should().BeTrue(
            because: "a System-posting hub never posts with a null context");
        response.Message.ObjectId.Should().Be("system-security",
            because: "WithPostingIdentity(System) stamps the well-known system identity on " +
                     "the hub's own posts — the declarative form of ImpersonateAsSystem for " +
                     "routing/persistence infrastructure");
    }

    /// <summary>
    /// An explicit <see cref="AccessService.ImpersonateAsSystem"/> at the callsite is
    /// the sanctioned infrastructure source — the delivery carries System, never null,
    /// never failed.
    /// </summary>
    [Fact]
    public async Task ImpersonateAsSystem_carries_identity_and_is_delivered()
    {
        var client = GetClient();
        var access = client.ServiceProvider.GetRequiredService<AccessService>();

        IMessageDelivery<CaptureCtxResponse> response;
        using (access.ImpersonateAsSystem())
        {
            response = await client
                .Observe(new CaptureCtx(), o => o.WithTarget(CreateHostAddress()))
                .Should().Within(10.Seconds()).Emit();
        }

        response.Message.HasAccessContext.Should().BeTrue();
        response.Message.ObjectId.Should().Be("system-security",
            because: "infrastructure that bypasses RLS opts in explicitly via " +
                     "ImpersonateAsSystem — the never-null invariant's System source");
    }
}
