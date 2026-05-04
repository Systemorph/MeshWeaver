using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the PostPipeline behaviour for messages posted with no ambient
/// <c>AccessService</c> context — i.e., framework-internal flows like
/// <c>SynchronizationStream.OnNext → SetCurrentRequest</c>,
/// <c>MessageHub</c> posting <c>InitializeHubRequest</c>/<c>ShutdownRequest</c>
/// to itself, and similar plumbing.
///
/// <para><b>Background.</b> An earlier change (commit <c>08a9a27c1</c>) made
/// PostPipeline fail-closed when no user identity was present. The intent
/// was right for the <em>mesh hub</em> — it routes for everyone, so stamping
/// <c>mesh/{guid}</c> as a fake principal would silently match the wrong
/// AccessAssignments. But the same fail-closed gate ALSO triggered on every
/// non-mesh hub's legitimate self-posts (sync/, portal/, apitoken/, …). The
/// SyncStream's <c>SetCurrentRequest</c>, fired on every layout-area state
/// push, started arriving with <c>AccessContext = null</c> → downstream
/// rejected them → blank screens / endless spinners on prod.</para>
///
/// <para><b>Fix pinned here.</b> PostPipeline keeps fail-closed for the
/// mesh hub specifically, and falls back to hub-self-impersonation for every
/// other hub kind. Two tests, one per branch, both routed via the regular
/// HubTestBase fixture so they exercise the real PostPipeline.</para>
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
    /// any user context set. Expectation: PostPipeline auto-impersonates the
    /// hub itself, so the receiver sees a non-null AccessContext whose
    /// ObjectId equals the sending hub's full-string address.
    ///
    /// Without the fix this test would observe <c>HasAccessContext == false</c>
    /// (the broken prod behaviour: SyncStream-style internal posts arriving
    /// with null context).
    /// </summary>
    [Fact]
    public async Task SubHub_with_no_user_context_falls_back_to_self_impersonation()
    {
        var client = GetClient();

        var response = await client
            .Observe(new CaptureContextRequest(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync()
            .ToTask(new CancellationTokenSource(10.Seconds()).Token);

        response.Message.HasAccessContext.Should().BeTrue(
            because: "non-mesh hubs are legitimate self-posters for their internal flows " +
                     "(SyncStream → SetCurrentRequest, MessageHub → ShutdownRequest, …) and " +
                     "the PostPipeline must stamp the hub's own address as the identity when " +
                     "no user context is ambient — anything else turns into the prod cascade " +
                     "where SetCurrentRequest arrives with null context and downstream rejects.");
        response.Message.ObjectId.Should().NotBeNullOrEmpty();
        response.Message.ObjectId.Should().Contain("client",
            because: "the post originated on the client hub, so its self-impersonation should " +
                     "stamp the client's address — anything else means routing or PostPipeline " +
                     "stamped the wrong hub's identity.");
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
}
