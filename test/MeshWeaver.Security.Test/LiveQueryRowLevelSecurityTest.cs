using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Row-level security must hold on the LIVE leg of a secure query, not just on its
/// Initial snapshot (issue #1250).
///
/// <para>A live <c>IMeshService.Query&lt;MeshNode&gt;</c> subscription is served by
/// <c>StorageAdapterMeshQueryProvider.ObserveQueryInternal</c>: the Initial payload comes from an
/// RLS-filtered read, and every later frame comes from a re-query on the same filtered read. The
/// re-query used to be SUPPLEMENTED with the raw entity carried on the storage adapter's change
/// notification, admitted on <c>QueryEvaluator.Matches</c> (filter + free text) alone — a predicate
/// that performs no permission check whatsoever. A write under the subscription's base path to a
/// node the subscriber cannot read was therefore emitted to them as an <c>Added</c>, carrying the
/// node AND its <c>Content</c>.</para>
///
/// <para>The subscriber picks the base path freely — the query need not be over anything they can
/// read, because an RLS-filtered Initial over an unreadable path simply comes back empty. So the
/// exposure needed nothing but a live subscription plus a same-process write.</para>
///
/// <para>This test pins the invariant end-to-end with real access control: every frame a secure
/// subscription emits — Initial, Added, Updated, Removed — contains only nodes the subscriber is
/// entitled to read, and the legitimate live delta still arrives.</para>
/// </summary>
public class LiveQueryRowLevelSecurityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Subscriber = "eve";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    protected override async Task SetupAccessRightsAsync()
    {
        // Grant the test runner Admin on TestPartition so the System-impersonated seed writes
        // land deterministically (same rationale as SyncedQueryPerUserIsolationTest: TestData is
        // a statically seeded partition, so routing the first write through a non-System identity
        // races PartitionWriteGuard's cold-partition provisioning).
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(
                    AssignmentNodeFactory.UserRole(
                        Mesh.Address.ToFullString(), "Admin", TestPartition))
                .Should().Emit();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task LiveSecureQuery_WriteToADeniedSibling_IsNeverEmittedToTheSubscriber()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var ns = $"{TestPartition}/live_rls_{Guid.NewGuid().AsString()}";
        var readablePath = $"{ns}/readable";
        var deniedPath = $"{ns}/denied";
        const string secretText = "TOP-SECRET-PAYLOAD-1250";

        // Seed: one node the subscriber may read (explicit Editor grant on that node only),
        // and NO grant anywhere else under the namespace.
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode("readable", ns)
            {
                Name = "v1",
                NodeType = "Markdown",
                Content = MarkdownContent.Parse("visible", "", readablePath)
            }).Should().Emit();

            await meshService.CreateNode(
                    AssignmentNodeFactory.UserRole(Subscriber, "Editor", readablePath))
                .Should().Emit();
        }

        await Mesh.GetEffectivePermissions(readablePath, Subscriber)
            .Should().Match(p => p.HasFlag(Permission.Read));
        await Mesh.GetEffectivePermissions(deniedPath, Subscriber)
            .Should().Match(p => p == Permission.None);

        // The live SECURE subscription, taken as the subscriber. UserId is stamped explicitly so
        // the provider's identity resolution cannot pick up the test runner's ambient admin.
        var request = MeshQueryRequest.FromQuery($"namespace:{ns}") with { UserId = Subscriber };
        var seen = new ConcurrentQueue<QueryResultChange<MeshNode>>();
        var hot = meshService.Query<MeshNode>(request).Replay();
        using var collector = hot.Subscribe(seen.Enqueue);
        using var conn = hot.Connect();

        var initial = await hot.Should().Within(20.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Initial);
        initial.Items.Select(n => n.Path).Should().Contain(readablePath,
            "the subscriber has an explicit Read grant on this node");

        // 1. Write the node the subscriber may NOT read. On the pre-#1250 code this notification's
        //    raw entity was injected straight into the live result set as an Added.
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode("denied", ns)
            {
                Name = "Denied",
                NodeType = "Markdown",
                Content = MarkdownContent.Parse(secretText, "", deniedPath)
            }).Should().Emit();
        }

        // 2. Then touch the node the subscriber MAY read. The live pipeline is Concat-serialised
        //    per notification, so the denied write's frame is fully processed BEFORE this one's —
        //    seeing "v2" is a deterministic barrier, not a sleep.
        using (accessService.ImpersonateAsSystem())
        {
            await Mesh.GetMeshNodeStream(readablePath)
                .Update(n => n with { Name = "v2" })
                .Should().Within(20.Seconds()).Emit();
        }

        // The legitimate live delta must still arrive — the fix must not go the other way and
        // silence the live leg.
        await hot.Should().Within(20.Seconds())
            .Match(c => c.ChangeType is QueryChangeType.Added or QueryChangeType.Updated
                        && c.Items.Any(n => n.Path == readablePath && n.Name == "v2"));

        // The security invariant: no frame — of any change type — ever carried the denied node.
        var leaked = seen.SelectMany(c => c.Items.Select(n => (c.ChangeType, Node: n)))
            .Where(x => x.Node.Path == deniedPath)
            .ToArray();

        leaked.Should().BeEmpty(
            "a live secure subscription must never emit a node the subscriber cannot read; " +
            "leaked as: " + string.Join(", ", leaked.Select(x => x.ChangeType)));

        seen.SelectMany(c => c.Items)
            .Select(n => (n.Content as MarkdownContent)?.Content)
            .Should().NotContain(secretText,
                "the denied node's CONTENT must never reach an unentitled subscriber");
    }
}
