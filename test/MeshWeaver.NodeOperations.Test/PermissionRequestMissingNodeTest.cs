using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Pins the prod 2026-05-23 deadlock fix: posting a
/// <see cref="GetPermissionRequest"/> at a path whose node does NOT exist
/// must reply deterministically — either with <see cref="Permission.None"/>
/// or with a <see cref="DeliveryFailureException"/> carrying NotFound.
/// NEVER hang the caller silently.
///
/// <para>The user-visible symptom this guards against: a stuck/missing
/// ThreadMessage causes the chat view's <c>cache.GetStream(messagePath)</c>
/// to post <c>GetPermissionRequest</c> at a path that no node lives at;
/// the bubble subscription waits forever for a reply → the entire thread
/// page appears frozen ("now the parent thread got deadlocked"). The
/// framework must fail closed (visible exception or None), never silent
/// hang.</para>
///
/// <para>These tests post the message DIRECTLY via <c>hub.Observe</c> with
/// the missing path as the routing target — the exact shape
/// <c>MeshNodeStreamCache.GetStream</c> uses. <c>FirstAsync</c> with no
/// <c>Timeout</c> would hang forever if the per-node hub never replied;
/// the test's <c>Fact(Timeout)</c> would then fire and surface the hang
/// as a TimeoutException at the xUnit level. With the fix in place the
/// router returns NotFound in milliseconds and the test passes.</para>
/// </summary>
public class PermissionRequestMissingNodeTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 30_000)]
    public async Task GetPermissionRequest_MissingPath_ThrowsDeliveryFailure_NotFound()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        // A partition-rooted path that no node was ever created at.
        // First segment is the test partition (NOT a NodeType name), so the
        // request actually exercises the routing — MeshNodeStreamCache's
        // LooksLikeNodeTypePath bypass for NodeType-rooted paths doesn't
        // apply here. This mirrors the prod stuck-ThreadMessage shape.
        var missingPath = $"{TestPartition}/_Thread/never-existed-{Guid.NewGuid():N}/msg-id-{Guid.NewGuid():N}";

        // 🚨 Post directly — no .Timeout() on the observable below. If the
        // per-node hub at the missing path doesn't reply, the test hangs
        // until xUnit fires the [Fact(Timeout)] kill — surfacing the prod
        // deadlock symptom. With the fix the routing service returns a
        // NotFound DeliveryFailure within milliseconds, which the framework
        // surfaces as DeliveryFailureException on the observable.
        Func<Task> act = async () => await Mesh.Observe<GetPermissionResponse>(
                new GetPermissionRequest(),
                o => o.WithTarget(new Address(missingPath)))
            .FirstAsync()
            .ToTask(ct);

        var ex = await act.Should().ThrowAsync<DeliveryFailureException>(
            "the routing service must surface 'node missing' as a deterministic " +
            "failure on the observable — silently hanging is the prod 2026-05-23 " +
            "deadlock symptom this test pins against");

        ex.Which.Message.Should().Contain("No node found",
            "the failure must carry the NotFound semantic — chat views, cache " +
            "gates, and other callers act on this signal to render an empty / " +
            "denied bubble instead of waiting forever");
        ex.Which.Message.Should().Contain(missingPath,
            "the failure should name the missing path so the caller can log it");
    }

    [Fact(Timeout = 30_000)]
    public async Task GetPermissionRequest_DeeplyNestedMissingPath_ThrowsDeliveryFailure_NotFound()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        // The exact prod path shape that triggered the deadlock:
        // partition / _Thread / <parent-thread> / <msg-id> / <sub-thread> / <sub-msg-id>.
        // Every segment after _Thread is a fresh GUID. If the per-node hub
        // activation or the security walk hangs on the missing leaf, this
        // test hangs and the [Fact(Timeout)] surfaces it.
        var deepMissingPath =
            $"{TestPartition}/_Thread/{Guid.NewGuid():N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}";

        Func<Task> act = async () => await Mesh.Observe<GetPermissionResponse>(
                new GetPermissionRequest(),
                o => o.WithTarget(new Address(deepMissingPath)))
            .FirstAsync()
            .ToTask(ct);

        var ex = await act.Should().ThrowAsync<DeliveryFailureException>(
            "deeply nested missing satellite paths must error in bounded time " +
            "exactly like shallow missing paths — the routing service must not " +
            "get stuck on a non-existent leaf");

        ex.Which.Message.Should().Contain("No node found");
        ex.Which.Message.Should().Contain(deepMissingPath);
    }

    [Fact(Timeout = 30_000)]
    public async Task GetPermissionRequest_ExistingPath_RepliesPromptly_WithRealPermissions()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        // Negative control: the missing-node failure path must not be over-
        // eager. The partition root exists and the test user is Admin (per
        // MonolithMeshTestBase's DevLogin) — should grant a real permission
        // quickly, NOT throw DeliveryFailure.
        var existingPath = TestPartition;

        var delivery = await Mesh.Observe<GetPermissionResponse>(
                new GetPermissionRequest(),
                o => o.WithTarget(new Address(existingPath)))
            .FirstAsync()
            .ToTask(ct);

        delivery.Should().NotBeNull();
        delivery.Message.Should().NotBeNull();
        delivery.Message.Permissions.Should().NotBe(Permission.None,
            "the missing-node NotFound path must NOT accidentally deny healthy " +
            "paths — Admin on an existing partition must still see real grants");
    }
}
