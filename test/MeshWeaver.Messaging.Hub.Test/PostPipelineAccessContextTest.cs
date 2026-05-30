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
/// <para>Four tests below pin the new behaviour: sub-hub fails closed, mesh
/// hub fails closed (unchanged), explicit ImpersonateAsSystem propagates,
/// explicit ImpersonateAsHub propagates.</para>
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
    /// A non-mesh hub (the test client at <c>client/1</c>) posts WITHOUT
    /// any user context set. After the 2026-05-21 cleanup, expectation:
    /// PostPipeline fails closed — the receiver sees a NULL AccessContext.
    ///
    /// <para>This inverts the previous assertion: hub-self-impersonation
    /// was deleted because it silently masked the prod EventCalendar bug
    /// (background activations writing as <c>sync/...</c> / <c>activity/...</c>
    /// → denied because those addresses match no AccessAssignment). Legitimate
    /// hub-internal flows now MUST opt in explicitly — see the two
    /// <c>ImpersonateAs...</c> tests below.</para>
    /// </summary>
    [Fact]
    public async Task SubHub_with_no_user_context_leaves_context_null_and_fails_closed()
    {
        var client = GetClient();

        var response = await client
            .Observe(new CaptureContextRequest(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync()
            .ToTask(new CancellationTokenSource(10.Seconds()).Token);

        response.Message.HasAccessContext.Should().BeFalse(
            because: "after the 2026-05-21 cleanup the PostPipeline fails closed when no " +
                     "ambient context is set — for ALL hub kinds, not just mesh. Legitimate " +
                     "hub-internal flows must wrap with PostOptions.ImpersonateAsHub or " +
                     "AccessService.ImpersonateAsSystem at the callsite. Silently stamping " +
                     "the hub address was the silent-mask the prod EventCalendar bug " +
                     "depended on.");
    }

    /// <summary>
    /// The mesh hub posts WITHOUT any user context set. Expectation:
    /// PostPipeline does NOT fall back to mesh-self-impersonation
    /// (would stamp <c>mesh/{guid}</c> as a fake principal, the bug
    /// commit <c>08a9a27c1</c> set out to fix). The delivery arrives with
    /// AccessContext null and downstream code is expected to fail-closed.
    ///
    /// In practice the mesh hub almost never posts on its own behalf —
    /// callers wrap with <c>ImpersonateAsHub</c> / <c>ImpersonateAsSystem</c>.
    /// This test is the negative pin: if anyone reintroduces the mesh-side
    /// fallback, it will fail.
    /// </summary>
    [Fact]
    public async Task Mesh_hub_with_no_user_context_does_NOT_self_impersonate()
    {
        var meshHub = Mesh; // injected by HubTestBase — Address = mesh/1

        var response = await meshHub
            .Observe(new CaptureContextRequest(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync()
            .ToTask(new CancellationTokenSource(10.Seconds()).Token);

        // The response can come back with no AccessContext — that's the
        // documented fail-closed behaviour for mesh-typed senders.
        // We only require: NOT identified as `mesh/...`. If a mesh-fallback
        // crept back in, ObjectId would start with "mesh/" and SecurityService
        // would silently match no AAs → the original prod bug.
        if (response.Message.HasAccessContext)
        {
            response.Message.ObjectId.Should().NotStartWith("mesh/",
                because: "the mesh hub must never identify itself as `mesh/{guid}` " +
                         "when posting — that's the exact silent-mismatch the fail-closed " +
                         "branch was added to prevent (commit 08a9a27c1).");
        }
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
                .FirstAsync()
                .ToTask(new CancellationTokenSource(10.Seconds()).Token);
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
                .FirstAsync()
                .ToTask(new CancellationTokenSource(10.Seconds()).Token);
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
