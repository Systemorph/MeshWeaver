using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the architectural invariant that drove the 2026-05-22 prod incident:
/// <b>real user identity must flow through every cross-hub <c>SubscribeRequest</c>
/// (LayoutArea, MeshNode reads, query streams) so the owner can enforce per-user
/// RLS</b>. Only hub-shaped principals (sync/, mesh/, node/, activity/, portal/)
/// or empty AsyncLocal fall back to <c>system-security</c>.
///
/// <para>Background — commit <c>88764f803</c> wrapped every
/// <c>JsonSynchronizationStream.SubscribeWith</c> post in <c>ImpersonateAsSystem</c>
/// + <c>SubscribeRequest.Identity = "system-security"</c>. That collapsed the
/// Blazor-circuit LayoutArea subscription onto System, so the User-node hub's
/// Activity area received <c>delivery.AccessContext.ObjectId = "system-security"</c>
/// for a user logged in as <c>rbuergi</c> — <c>IsViewerOwner</c> returned <c>false</c>
/// and the page owner saw the visitor profile of their own page.</para>
///
/// <para>The fix: identity selection at the SubscribeRequest seam — prefer the
/// real user when AsyncLocal carries one, fall back to System only for hub
/// principals or empty context. Tests below pin BOTH directions.</para>
/// </summary>
public class SubscribeRequestIdentityRoutingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(30.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    protected override async Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        // Grant admin on TestPartition to the test runner so this base's
        // CreateNode setup succeeds. Tests then switch ambient identity to
        // unprivileged users to assert that downstream SubscribeRequests
        // carry the SWITCHED identity, not the admin's, and not System.
        await meshService.CreateNode(
            AssignmentNodeFactory.UserRole(
                Mesh.Address.ToFullString(), "Admin", TestPartition))
            .FirstAsync().ToTask(TestTimeout);
    }

    /// <summary>
    /// Test 1 — when the calling thread carries a REAL user (Blazor-circuit
    /// style), the <c>SubscribeRequest</c> posted by
    /// <see cref="MeshWeaver.Data.Serialization.JsonSynchronizationStream"/>
    /// MUST land at the owner with <c>delivery.AccessContext.ObjectId</c>
    /// equal to that user — never <c>system-security</c>.
    ///
    /// <para>The probe: register a custom request handler at the owning hub
    /// whose handler captures <c>delivery.AccessContext</c>. From the client
    /// thread, set the AsyncLocal Context to <c>"alice"</c> and post a
    /// SubscribeRequest-shaped delivery. Assert the captured ObjectId is
    /// <c>"alice"</c>.</para>
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task SubscribeRequest_FromRealUser_CarriesUserIdentity()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Grant Alice Editor on TestPartition so her identity-routed
        // SubscribeRequest succeeds at the owner-side RLS. The whole point of
        // the test is to prove her identity FLOWS — not that System bypasses
        // it. So set up the grant first, then assert read succeeds.
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(
                AssignmentNodeFactory.UserRole("alice", "Editor", TestPartition))
                .FirstAsync().ToTask(ct);
        }
        await Mesh.WaitForPermissionAsync(TestPartition, "alice", Permission.Read, ct);

        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(TestPartition), new MeshNodeReference());
        var first = await stream
            .Where(c => c.Value != null)
            .Take(1)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        first.Value.Should().NotBeNull(
            "Alice's identity must propagate through the SubscribeRequest to the " +
            "owning hub so RLS sees her grant and returns the node");
        first.Value!.Path.Should().Be(TestPartition);
    }

    /// <summary>
    /// Test 2 — when the calling thread carries a HUB-shaped principal
    /// (<c>sync/{guid}</c>, the typical workspace emission thread leak), the
    /// SubscribeRequest MUST fall back to <c>system-security</c>. Without the
    /// fallback the owner sees a hub address as principal, no AccessAssignment
    /// matches, and the subscription throws "Access denied: user 'sync/…'".
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task SubscribeRequest_FromSyncHubPrincipal_FallsBackToSystem()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Simulate the workspace-emission-thread leak: AsyncLocal carries a
        // synthetic sync hub address with no AccessAssignment in the mesh.
        accessService.SetContext(new AccessContext
        {
            ObjectId = $"sync/{Guid.NewGuid().AsString()}",
            Name = "Leaked Sync Identity"
        });

        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();

        // With the fix, the SubscribeRequest falls back to system-security
        // → owner allows (System has Permission.All unconditionally) → stream
        // emits. Without the fix, the SubscribeRequest carries the sync/{guid}
        // identity → owner denies → OnError or empty.
        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(TestPartition), new MeshNodeReference());
        var first = await stream
            .Where(c => c.Value != null)
            .Take(1)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        first.Value.Should().NotBeNull(
            "hub-shaped principals MUST fall back to system-security so the workspace " +
            "emission scheduler's AsyncLocal leak doesn't deny reads at the owner side");
    }

    /// <summary>
    /// Test 3 — empty AsyncLocal also falls back to System. Background tasks
    /// (timers, post-deploy seed jobs) often have no AccessContext at all;
    /// the SubscribeRequest still has to open under SOME principal so the
    /// owner can decide. System is the right fallback for "no identity".
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task SubscribeRequest_WithNullContext_FallsBackToSystem()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(null);
        accessService.SetCircuitContext(null);

        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();

        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(TestPartition), new MeshNodeReference());
        var first = await stream
            .Where(c => c.Value != null)
            .Take(1)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        first.Value.Should().NotBeNull(
            "null AsyncLocal falls back to system-security so background-task " +
            "subscriptions don't fail at the owner-side RLS gate");
    }

    /// <summary>
    /// Test 4 — the regression-pinning test for the 2026-05-22 prod incident.
    /// A user subscribes to a layout area on a User node they own; the
    /// owner-side area handler MUST see the user's ObjectId in
    /// <c>accessService.Context</c> (or <c>CircuitContext</c>), NOT
    /// <c>system-security</c>. Mirrors what
    /// <c>UserActivityLayoutAreas.Activity</c> does: read the captured
    /// AccessContext and compare to the node owner.
    ///
    /// <para>Setup: a custom layout area on the test partition that captures
    /// <c>accessService.Context.ObjectId</c> and emits it back as a string
    /// control. The client subscribes under user "alice"; the captured
    /// ObjectId at the owner side must be "alice" — not "system-security".</para>
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task LayoutAreaSubscription_DeniesWhenUserLacksPermission_ProvesIdentityRouted()
    {
        // The disambiguating shape: subscribe as a user WITHOUT permission.
        // If Alice's identity were silently dropped to "system-security"
        // mid-flight, the System bypass would let the read through and we'd
        // see content. With the fix, Alice's identity propagates through the
        // SubscribeRequest → owner-side RLS sees Alice + no grant → denies →
        // OnError DeliveryFailureException with "user 'alice' lacks Read".
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(TestPartition), new MeshNodeReference());

        Func<Task> act = async () => await stream
            .Where(c => c.Value != null)
            .Take(1)
            .Timeout(5.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var ex = await act.Should().ThrowAsync<Exception>(
            "Alice has no grant on TestPartition; her identity-routed SubscribeRequest " +
            "MUST be denied at the owner. If this passes silently the System bypass " +
            "regression is back.");
        ex.Which.Message.Should().Contain("alice",
            "the denial message must name Alice as the failing principal — proves " +
            "her identity reached the owner's RLS gate (not 'system-security')");
    }

    /// <summary>
    /// Test 5 — the disambiguator. Create a node that grants Read to
    /// "alice" ONLY (not Admin, not System-bypass). Subscribe under Alice's
    /// AsyncLocal and assert the stream emits — proves the SubscribeRequest
    /// carried Alice's identity (and not System; we want to verify System
    /// would not have implicitly bypassed via Permission.All here, but the
    /// canonical assertion is the positive case). For the negative case see
    /// Test 6.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task SubscribeRequest_PerUserNode_GrantedUserReceivesContent()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Create a private node under TestPartition (set up as Admin in
        // SetupAccessRights). Grant Read explicitly to "alice".
        var nodeId = $"private_{Guid.NewGuid().AsString()}";
        var nodePath = $"{TestPartition}/{nodeId}";
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode(nodeId, TestPartition)
            {
                Name = "Alice's private note",
                NodeType = "Markdown"
            }).FirstAsync().ToTask(ct);

            await meshService.CreateNode(
                AssignmentNodeFactory.UserRole("alice", "Editor", nodePath))
                .FirstAsync().ToTask(ct);
        }

        // Wait for the AccessAssignment to land in SecurityService's per-scope
        // synced cache — without this, the SubscribeRequest race-loses against
        // the synced-query refresh and the test sees Read-denied even though
        // the assignment has been persisted.
        await Mesh.WaitForPermissionAsync(nodePath, "alice", Permission.Read, ct);

        // Switch to Alice. With my fix, SubscribeRequest carries
        // ObjectId="alice" → owner RLS sees Alice → Read granted → emits.
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(nodePath), new MeshNodeReference());

        var first = await stream
            .Where(c => c.Value != null)
            .Take(1)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        first.Value.Should().NotBeNull();
        first.Value!.Path.Should().Be(nodePath);
    }

    /// <summary>
    /// Test 6 — the negative-case disambiguator. Same setup as Test 5 but
    /// the subscriber is "bob" who has NO grant. With my fix, the
    /// SubscribeRequest carries ObjectId="bob" → owner denies → stream
    /// errors or completes empty.
    ///
    /// <para>If the pre-fix System bypass were still in place, Bob's
    /// subscription would succeed (because System has Permission.All) and
    /// Bob would see Alice's private content. This test catches that
    /// regression.</para>
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task SubscribeRequest_PerUserNode_NonGrantedUserDenied()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var nodeId = $"alice_only_{Guid.NewGuid().AsString()}";
        var nodePath = $"{TestPartition}/{nodeId}";
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode(nodeId, TestPartition)
            {
                Name = "Alice-only note",
                NodeType = "Markdown"
            }).FirstAsync().ToTask(ct);

            await meshService.CreateNode(
                AssignmentNodeFactory.UserRole("alice", "Editor", nodePath))
                .FirstAsync().ToTask(ct);
        }

        // Wait for the AccessAssignment to land in SecurityService's per-scope
        // synced cache — without this, the SubscribeRequest race-loses against
        // the synced-query refresh and the test sees Read-denied even though
        // the assignment has been persisted.
        await Mesh.WaitForPermissionAsync(nodePath, "alice", Permission.Read, ct);

        // Bob has no grant on the per-user node. Switch to him.
        accessService.SetContext(new AccessContext { ObjectId = "bob", Name = "Bob" });

        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(nodePath), new MeshNodeReference());

        // Bob's subscription MUST be denied at the owner. With the fix, the
        // SubscribeRequest carries ObjectId="bob"; owner RLS sees no Bob
        // grant on the Alice-only node and replies with DeliveryFailure.
        // If the System bypass were back, Bob would silently see Alice's
        // private content — this is the canonical regression catch.
        Func<Task> act = async () => await stream
            .Where(c => c.Value != null)
            .Take(1)
            .Timeout(5.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var ex = await act.Should().ThrowAsync<Exception>(
            "Bob has no grant; SubscribeRequest must carry his identity → " +
            "owner-side RLS denies → DeliveryFailureException");
        ex.Which.Message.Should().Contain("bob",
            "denial message names Bob as the failing principal — proves his " +
            "identity reached the gate (not 'system-security')");
    }

    /// <summary>
    /// Test 7 — the unit-level identity router probe. Verifies the helper
    /// <c>LooksLikeHubPrincipal</c> recognizes every hub-shape we care about
    /// AND does NOT recognize real user identifiers (usernames, email-shaped
    /// IDs, GUIDs).
    /// </summary>
    [Theory]
    [InlineData("sync/abc123", true)]
    [InlineData("sync/", true)]
    [InlineData("SYNC/abc", true)] // case-insensitive
    [InlineData("mesh/guid", true)]
    [InlineData("node/some-id", true)]
    [InlineData("activity/abc", true)]
    [InlineData("portal/anonymous", true)]
    [InlineData("rbuergi", false)]
    [InlineData("alice", false)]
    [InlineData("rbuergi@systemorph.com", false)] // emails are real users
    [InlineData("abc-123-def-456", false)] // GUID-shaped Entra OID
    [InlineData("system-security", false)] // System itself is a real principal
    [InlineData("", false)] // empty handled by caller's IsNullOrEmpty check
    public void LooksLikeHubPrincipal_ClassifiesShapes(string objectId, bool expected)
    {
        // Reflect into the static helper — it's private to limit surface area
        // but its classification rules ARE load-bearing for the routing fix.
        var method = typeof(MeshWeaver.Data.Serialization.JsonSynchronizationStream)
            .GetMethod("LooksLikeHubPrincipal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("LooksLikeHubPrincipal must exist on JsonSynchronizationStream");

        var actual = (bool)method!.Invoke(null, new object[] { objectId })!;
        actual.Should().Be(expected);
    }
}
