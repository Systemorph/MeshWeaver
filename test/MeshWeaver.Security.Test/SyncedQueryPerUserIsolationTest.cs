using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Pins the architectural invariant the user pushed back hard on 2026-05-22:
/// <b>users have to have Read access on all nodes in the synced query — one
/// user must NEVER inherit another user's filtered view through a shared
/// cache</b>.
///
/// <para>Before the per-user fix, <c>workspace.GetQuery(id, queries)</c>
/// cached by <c>id</c> only. Two users calling with the same id reused the
/// SAME observable, which the SyncedQueryMeshNodes loaded under
/// <c>WellKnownUsers.System</c> — so user B saw user A's RLS-restricted
/// content via the shared cache.</para>
///
/// <para>The fix: cache by <c>(id, userId)</c>. Each user opens their own
/// SyncedQueryMeshNodes with their own identity; the upstream
/// <see cref="IMeshQueryCore.Query"/> dispatcher routes real-user
/// requests to the secured provider surface where the per-result RLS
/// validators apply. System callers (SecurityService's _Access walks etc.)
/// keep their validator-bypassing unsecured surface — their cache key
/// stays under the well-known <c>system-security</c> user.</para>
/// </summary>
public class SyncedQueryPerUserIsolationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(30.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    protected override Task SetupAccessRightsAsync()
    {
        // Grant the test runner Admin on TestPartition so the setup writes go
        // through; the actual assertions then switch identity to Alice/Bob.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(
            AssignmentNodeFactory.UserRole(
                Mesh.Address.ToFullString(), "Admin", TestPartition))
            .Should().Emit();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test 1 — the canonical regression catch. Alice has Read on a private
    /// node; Bob does not. Alice subscribes to a synced query that includes
    /// the node; Bob subscribes to the SAME logical query id from a freshly
    /// switched context. Bob's emission must NOT include Alice's node.
    ///
    /// <para>The disambiguator: if the per-user cache regressed back to
    /// shared-by-id caching, Bob would inherit Alice's filter and see the
    /// node. This test fails loudly.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void SyncedQuery_TwoUsersWithDifferentGrants_DoNotShareSnapshot()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();

        // Create an "Alice-only" private namespace + node under TestPartition.
        var nodeId = $"alice_only_{Guid.NewGuid().AsString()}";
        var nodePath = $"{TestPartition}/{nodeId}";
        using (accessService.ImpersonateAsSystem())
        {
            meshService.CreateNode(new MeshNode(nodeId, TestPartition)
            {
                Name = "Alice's private doc",
                NodeType = "Markdown"
            }).Should().Emit();
            // Grant Editor (includes Read) explicitly to alice; Bob has no grant.
            meshService.CreateNode(
                AssignmentNodeFactory.UserRole("alice", "Editor", nodePath))
                .Should().Emit();
        }
        Mesh.GetEffectivePermissions(nodePath, "alice")
            .Should().Match(p => p.HasFlag(Permission.Read));

        const string queryId = "shared-query-id";
        var query = $"path:{nodePath}";

        // Alice subscribes — sees her node.
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });
        var aliceSnapshot = workspace
            .GetQuery(queryId, query)
            .Should().Within(10.Seconds()).Match(items => items.Any(n => n.Path == nodePath));

        aliceSnapshot.Should().ContainSingle(n => n.Path == nodePath,
            "Alice has the Editor grant on this node, her secured query " +
            "surface emits it");

        // Switch to Bob — same logical query id, different identity. The
        // CRITICAL assertion: Bob's snapshot must NOT include Alice's node.
        // If the cache is shared-by-id, Bob sees Alice's data; if it's
        // (id, userId)-keyed, Bob opens a fresh SyncedQueryMeshNodes under
        // his identity and the secured provider surface filters out the
        // Alice-only node.
        accessService.SetContext(new AccessContext { ObjectId = "bob", Name = "Bob" });
        var bobSnapshot = workspace
            .GetQuery(queryId, query)
            .Should().Within(10.Seconds()).Emit();

        bobSnapshot.Should().NotContain(n => n.Path == nodePath,
            "Bob has no Read grant on Alice's node; the per-user-keyed " +
            "SyncedQuery cache must NEVER let him inherit her view through " +
            "a shared cache. If this assertion trips, the cross-user leak " +
            "regression is back.");
    }

    /// <summary>
    /// Test 2 — same cache id, different identities, each call returns a
    /// distinct per-subscriber wrapper. With the per-subscriber RLS filter
    /// design (2026-05-22), every <c>GetQuery</c> call returns a fresh
    /// <c>Observable.Defer</c> wrapper that captures the caller's identity
    /// at Subscribe time and filters the SHARED upstream snapshot.
    /// Reference identity at the outer wrapper level is by-design non-stable —
    /// what stays stable is the underlying upstream
    /// (Replay(1).RefCount-cached <see cref="SyncedQueryMeshNodes"/>).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void SyncedQuery_PerCall_ReturnsDistinctWrapper()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        const string queryId = "distinct-cache-id";
        var query = $"namespace:{TestPartition}";

        accessService.SetContext(new AccessContext { ObjectId = "carol", Name = "Carol" });
        var carolObs = workspace.GetQuery(queryId, query);

        accessService.SetContext(new AccessContext { ObjectId = "dave", Name = "Dave" });
        var daveObs = workspace.GetQuery(queryId, query);

        ((object)carolObs).Should().NotBeSameAs(daveObs,
            "every GetQuery call yields a fresh Defer wrapper — distinct " +
            "callers get distinct per-subscriber filter chains, even when " +
            "they share the same upstream.");

        accessService.SetContext(new AccessContext { ObjectId = "carol", Name = "Carol" });
        var carolObs2 = workspace.GetQuery(queryId, query);
        ((object)carolObs2).Should().NotBeSameAs(carolObs,
            "wrappers are NOT cached — each call returns a new Defer. The " +
            "underlying upstream IS cached (verified by " +
            "LanguageModelSyncedQueryTest.SyncedQuery_GetQueryById_ReusesSameUpstreamAcrossCalls).");
    }

    /// <summary>
    /// Test 3 — System-impersonated GetQuery (infrastructure path) uses the
    /// well-known <c>system-security</c> cache key, NOT the ambient AsyncLocal.
    /// Two consecutive infrastructure callers (e.g. SecurityService's
    /// per-scope <c>_Access</c> walks) hit the same cache entry — that's the
    /// performance win that justifies a SHARED cache for infrastructure.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void SyncedQuery_SystemImpersonation_SharesCacheAcrossCallers()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        const string queryId = "system-shared-cache-id";
        var query = $"namespace:{TestPartition}";

        IObservable<IEnumerable<MeshNode>> first, second;
        using (accessService.ImpersonateAsSystem())
        {
            first = workspace.GetQuery(queryId, query);
        }
        // Different ambient user this time — but ImpersonateAsSystem flips
        // AsyncLocal Context to system-security, so the cache key is
        // (queryId, "system-security") inside the using block. The second
        // System call hits the same key.
        accessService.SetContext(new AccessContext { ObjectId = "carol", Name = "Carol" });
        using (accessService.ImpersonateAsSystem())
        {
            second = workspace.GetQuery(queryId, query);
        }

        ((object)second).Should().BeSameAs(first,
            "infrastructure callers wrapped in ImpersonateAsSystem share the " +
            "system-security cache key — this is the perf shortcut SecurityService " +
            "and NodeType compile-watchers rely on");
    }

    /// <summary>
    /// Test 4 — Bob's query identity reaches the provider. The provider sees
    /// <c>request.UserId = "bob"</c> (not "system-security") so the secured
    /// surface applies Bob's RLS validators. We exercise the secured surface
    /// dispatch by setting a non-System ambient and observing that the
    /// emission is filtered to Bob's grants.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void SyncedQuery_NonSystemUser_HitsSecuredProviderSurface()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();

        // Public node — every user with Read on TestPartition should see it.
        var publicId = $"public_{Guid.NewGuid().AsString()}";
        var publicPath = $"{TestPartition}/{publicId}";
        // Bob-only node — only Bob should see it.
        var bobOnlyId = $"bob_only_{Guid.NewGuid().AsString()}";
        var bobOnlyPath = $"{TestPartition}/{bobOnlyId}";

        using (accessService.ImpersonateAsSystem())
        {
            meshService.CreateNode(new MeshNode(publicId, TestPartition)
            { Name = "Public doc", NodeType = "Markdown" }).Should().Emit();
            meshService.CreateNode(new MeshNode(bobOnlyId, TestPartition)
            { Name = "Bob-only doc", NodeType = "Markdown" }).Should().Emit();
            // Bob has Editor on both nodes; Eve has Editor only on the public.
            meshService.CreateNode(
                AssignmentNodeFactory.UserRole("bob", "Editor", publicPath))
                .Should().Emit();
            meshService.CreateNode(
                AssignmentNodeFactory.UserRole("bob", "Editor", bobOnlyPath))
                .Should().Emit();
            meshService.CreateNode(
                AssignmentNodeFactory.UserRole("eve", "Editor", publicPath))
                .Should().Emit();
        }
        Mesh.GetEffectivePermissions(publicPath, "bob")
            .Should().Match(p => p.HasFlag(Permission.Read));
        Mesh.GetEffectivePermissions(bobOnlyPath, "bob")
            .Should().Match(p => p.HasFlag(Permission.Read));
        Mesh.GetEffectivePermissions(publicPath, "eve")
            .Should().Match(p => p.HasFlag(Permission.Read));

        const string queryId = "secured-surface-probe";
        var query = $"namespace:{TestPartition}";

        // Bob sees both nodes.
        accessService.SetContext(new AccessContext { ObjectId = "bob", Name = "Bob" });
        var bobSnapshot = workspace
            .GetQuery(queryId, query)
            .Should().Within(10.Seconds()).Match(items =>
                items.Any(n => n.Path == publicPath) &&
                items.Any(n => n.Path == bobOnlyPath));

        bobSnapshot.Select(n => n.Path).Should().Contain(new[] { publicPath, bobOnlyPath },
            "Bob has Editor on both — both must appear in his secured-surface snapshot");

        // Eve sees only the public node.
        accessService.SetContext(new AccessContext { ObjectId = "eve", Name = "Eve" });
        var eveSnapshot = workspace
            .GetQuery(queryId, query)
            .Should().Within(10.Seconds()).Match(items => items.Any(n => n.Path == publicPath));

        eveSnapshot.Select(n => n.Path).Should().Contain(publicPath,
            "Eve has Editor on the public node — must appear in her snapshot");
        eveSnapshot.Select(n => n.Path).Should().NotContain(bobOnlyPath,
            "Eve has NO grant on Bob's private node — the secured provider " +
            "surface filters it out per Eve's RLS validators. If she sees it, " +
            "the dispatcher fell back to the unsecured (System-only) surface.");
    }
}
