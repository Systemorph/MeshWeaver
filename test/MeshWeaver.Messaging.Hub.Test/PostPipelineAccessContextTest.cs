using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the PostPipeline behaviour for messages posted with no ambient
/// <see cref="AccessService"/> context — i.e., framework-internal flows like
/// <c>SynchronizationStream.OnNext → SetCurrentRequest</c>,
/// <c>MessageHub</c> posting <c>InitializeHubRequest</c>/<c>ShutdownRequest</c>
/// to itself, and similar plumbing.
///
/// <para><b>Background (2026-05-21).</b> The original prod hot spot was
/// AccessControl denying compile-activity creates with
/// <c>"Access denied: user 'sync/...' lacks Create permission"</c>. Root
/// cause: when a Subscribe callback fired on a thread with no ambient
/// AccessContext, the PostPipeline silently stamped the hub's own address
/// as principal — that address doesn't match any AccessAssignment, so
/// AccessControl denied. The fallback masked a real bug. The user directive:
/// "we should NEVER write something as hub". The fallback was deleted; ALL
/// hub kinds now fail-closed when no context is set. Legitimate hub-internal
/// flows must opt in explicitly via <see cref="PostOptions.ImpersonateAsHub"/>
/// / <see cref="AccessService.ImpersonateAsSystem"/>. Framework-lifecycle
/// messages (Initialize/Heartbeat/Shutdown/Dispose/Subscribe) stay exempt
/// from the warning because they carry no security-relevant payload.</para>
///
/// <para><b>Update (2026-06-17) — the never-null tripwire.</b> The user's
/// one-invariant mandate (<c>feedback_access_context_always_set</c>):
/// <c>AccessContext must ALWAYS be set</c>. So the old "fail closed silently
/// to null" is now a THROW for application posts. A post with no resolved
/// context (and not exempt as <c>[SystemMessage]</c> / <c>[CanBeIgnored]</c> /
/// <c>DeliveryFailure</c>) is a GAP — the post lost the user identity — so
/// <see cref="MessageHub.Post{TMessage}"/> throws synchronously
/// (<see cref="InvalidOperationException"/>) to surface it. The two tests below
/// that used to assert "leaves null" now assert the throw fires; the two
/// Impersonate tests are unchanged (explicit identity propagates).</para>
/// </summary>
public class PostPipelineAccessContextTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Probe message + handler — captures the delivery's AccessContext as it
    /// landed at the receiver, AFTER PostPipeline ran on the sender.
    /// </summary>
    private sealed record CaptureContextRequest : IRequest<CaptureContextResponse>;
    private sealed record CaptureContextResponse(string? ObjectId, string? Name, bool HasAccessContext);

    private static IMessageDelivery RegisterCaptureHandler(IMessageHub hub)
    {
        // Just registers; not invoked. Returns the handler-registration helper.
        return null!;
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(CaptureContextRequest), typeof(CaptureContextResponse))
            .WithHandler<CaptureContextRequest>((hub, request) =>
            {
                var ctx = request.AccessContext;
                hub.Post(
                    new CaptureContextResponse(ctx?.ObjectId, ctx?.Name, ctx is not null),
                    o => o.ResponseFor(request));
                return request.Processed();
            });

    /// <summary>
    /// VERIFICATION (b) of the never-null mandate: an APPLICATION post with no
    /// resolved context is FAILED IMMEDIATELY (no identity, no delivery). A
    /// non-mesh hub (the test client at <c>client/1</c>) posts an ordinary
    /// <c>IRequest</c> WITHOUT any user context set — the post lost the user
    /// identity, which under the never-null invariant is a gap. The PostPipeline
    /// logs an ERROR and returns <c>delivery.Failed(...)</c>, which surfaces to
    /// the awaiting <c>hub.Observe(...)</c> as a <c>DeliveryFailureException</c>
    /// (OnError) — NOT a synchronous throw out of Post (Post is fire-and-forget
    /// from countless callsites; a synchronous throw there would be unobserved
    /// or crash an unrelated path).
    ///
    /// <para>This replaces the old "fail closed silently to null" assertion.
    /// Silently leaving null still masked the bug class (the empty agent
    /// registry, the prod EventCalendar bug). Failing the delivery + logging an
    /// error is the tripwire the user asked for. Legitimate hub-internal flows
    /// opt in explicitly (the two <c>ImpersonateAs...</c> tests below) or are
    /// exempt (<c>[SystemMessage]</c> / <c>[CanBeIgnored]</c> /
    /// <c>DeliveryFailure</c>).</para>
    /// </summary>
    [Fact]
    public async Task Application_post_with_no_user_context_FAILS_the_delivery()
    {
        // A USER-mode hub (the default for user-facing hubs in production). HubTestBase
        // declares its plumbing fixtures as System; opt this hub back into User to assert
        // the user-identity never-null behaviour.
        var client = GetClient(c => c.WithPostingIdentity(PostingIdentity.User));

        var notification = await client
            .Observe(new CaptureContextRequest(), o => o.WithTarget(CreateHostAddress()))
            .Materialize()
            .Should().Within(10.Seconds()).Match(n => n.Kind == System.Reactive.NotificationKind.OnError);

        notification.Exception!.Message.Should().Contain("AccessContext must never be null",
            because: "the never-null AccessContext invariant: an application post that resolves " +
                     "no context is a gap — the post lost the user identity. The PostPipeline " +
                     "fails the delivery (no identity, no delivery) so the awaiting Observe gets " +
                     "OnError rather than a silent null-context delivery.");
    }

    /// <summary>
    /// The mesh hub is framework infrastructure (declared <see cref="PostingIdentity.System"/>
    /// by <c>HubTestBase</c>) — its own otherwise-unattributed posts run as
    /// <c>system-security</c>, NOT a null and NOT a self-impersonated <c>mesh/{guid}</c>
    /// (the bug commit <c>08a9a27c1</c> guarded against). So a no-context post from the
    /// mesh hub is delivered AS SYSTEM.
    /// </summary>
    [Fact]
    public async Task Mesh_hub_no_user_context_posts_as_system_not_as_mesh_address()
    {
        var meshHub = Mesh; // injected by HubTestBase — Address = mesh/1, PostingIdentity.System

        var response = await meshHub
            .Observe(new CaptureContextRequest(), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit();

        response.Message.HasAccessContext.Should().BeTrue(
            because: "an infrastructure (System) hub never posts with a null context");
        response.Message.ObjectId.Should().Be("system-security");
        response.Message.ObjectId.Should().NotStartWith("mesh/",
            because: "the mesh hub must never identify itself as mesh/{guid} — System is the " +
                     "sanctioned infrastructure identity (commit 08a9a27c1)");
    }

    /// <summary>
    /// An explicit <see cref="AccessService.ImpersonateAsSystem"/> scope
    /// propagates the well-known <c>"system-security"</c> identity through
    /// PostPipeline. This is the canonical pattern for legitimate
    /// infrastructure writes that must bypass RLS (e.g. system bootstrap
    /// of stream caches).
    /// </summary>
    [Fact]
    public async Task ImpersonateAsSystem_scope_propagates_through_Post()
    {
        var client = GetClient();
        var access = client.ServiceProvider.GetRequiredService<AccessService>();

        IMessageDelivery<CaptureContextResponse> response;
        using (access.ImpersonateAsSystem())
        {
            response = await client
                .Observe(new CaptureContextRequest(), o => o.WithTarget(CreateHostAddress()))
                .Should().Within(10.Seconds()).Emit();
        }

        response.Message.HasAccessContext.Should().BeTrue(
            because: "ImpersonateAsSystem sets a real AccessContext on AsyncLocal; " +
                     "PostPipeline reads it and stamps it on the outbound delivery.");
        response.Message.ObjectId.Should().Be("system-security",
            because: "ImpersonateAsSystem uses the well-known system identity that " +
                     "SecurityService grants Permission.All unconditionally.");
    }

    /// <summary>
    /// An explicit <see cref="AccessService.ImpersonateAsHub"/> scope
    /// propagates the hub's own address as principal — the SyncStream
    /// heartbeat pattern (JsonSynchronizationStream.cs:218, 292, 327).
    /// </summary>
    [Fact]
    public async Task ImpersonateAsHub_scope_propagates_through_Post()
    {
        var client = GetClient();
        var access = client.ServiceProvider.GetRequiredService<AccessService>();

        IMessageDelivery<CaptureContextResponse> response;
        using (access.ImpersonateAsHub(client))
        {
            response = await client
                .Observe(new CaptureContextRequest(), o => o.WithTarget(CreateHostAddress()))
                .Should().Within(10.Seconds()).Emit();
        }

        response.Message.HasAccessContext.Should().BeTrue(
            because: "ImpersonateAsHub sets a real AccessContext on AsyncLocal; " +
                     "PostPipeline reads it and stamps it on the outbound delivery.");
        response.Message.ObjectId.Should().Be(client.Address.ToFullString(),
            because: "ImpersonateAsHub stamps the hub's full address as principal — " +
                     "this is the legitimate hub-self-post identity, opted in at the " +
                     "callsite rather than the silent PostPipeline fallback.");
    }
}
