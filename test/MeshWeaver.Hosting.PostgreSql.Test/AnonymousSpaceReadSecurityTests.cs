using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// SECURITY repro — an UNAUTHENTICATED (anonymous) user must NOT be able to read a
/// PRIVATE Space's content. Configured the SAME way as the memex portal
/// (<c>MemexConfiguration.cs:466-477</c>): <c>AddRowLevelSecurity()</c> + <c>AddGraph()</c> +
/// <c>AddSpaceType()</c> over partitioned PostgreSQL.
///
/// <para><b>The bug.</b> Reading a Space page opens a remote layout-area / node
/// <see cref="MeshWeaver.Data.Contract.SubscribeRequest"/>, which carries
/// <c>[RequiresPermission(Permission.Read)]</c>. When the subscribing circuit has NO real
/// user context (anonymous), <c>JsonSynchronizationStream.CreateExternalClient</c>
/// (<c>JsonSynchronizationStream.cs:251-255,274</c>) sends the subscribe as the System
/// identity (<c>"system-security"</c>) under <c>ImpersonateAsSystem()</c>, and
/// <c>PermissionEvaluator</c> grants System <see cref="Permission.All"/> — so the Read gate
/// passes and anonymous reads private content. (Confirmed live: anyone can open
/// <c>https://atioz.meshweaver.cloud/AgenticPension/</c> unauthenticated.)</para>
///
/// <para>The first assertion shows the access LOGIC is correct
/// (<c>GetEffectivePermissions(space, Anonymous) == None</c>) — proving the leak is purely
/// the subscribe path's System upgrade, not the permission evaluator. The subscribe
/// assertion is the actual repro: today it does NOT fail with "Access denied" (anonymous is
/// upgraded to System and wrongly succeeds); after the fix it MUST fail closed.</para>
/// </summary>
[Collection("PostgreSql")]
public class AnonymousSpaceReadSecurityTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddRowLevelSecurity()   // same access wiring as memex
            .AddGraph()
            .AddSpaceType();
    }

    // The client needs a workspace/data context to host the remote stream
    // (mirrors HubSubscriptionSecurityTest).
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddData(d => d);

    // No blanket admin grant — permissions must be granular so the anonymous caller
    // genuinely has none (mirrors HubSubscriptionSecurityTest).
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 60000)]
    public async Task AnonymousUser_CannotRead_PrivateSpace()
    {
        var ct = TestContext.Current.CancellationToken;
        const string space = "PrivateAnonSpace";

        // ── Provision the Space's partition (same routing prep as
        //    EffectivePermissionPostgresTest — the PG provider's Matches() reflects
        //    partition-table state, so register the schema + partition before writing).
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var pgProvider = Mesh.ServiceProvider.GetRequiredService<PostgreSqlPartitionStorageProvider>();
        var partitionDef = new PartitionDefinition
        {
            Namespace = space,
            DataSource = "default",
            Schema = space.ToLowerInvariant(),
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
            Versioned = true,
        };
        await pgProvider.EnsureSchemaForPartitionAsync(partitionDef, ct).ToObservable()
            .Should().Within(60.Seconds()).Emit();
        pgProvider.RegisterPartition(partitionDef);
        await meshService.CreateNode(new MeshNode(space, "Admin/Partition")
        {
            NodeType = "Partition",
            Name = space,
            State = MeshNodeState.Active,
            Content = partitionDef
        }).Should().Within(90.Seconds()).Emit();

        // ── Create the PRIVATE Space (creator = the DevLogin admin context). No public
        //    policy, no grant for the anonymous user → only the creator can read it.
        var spaceNode = MeshNode.FromPath(space) with
        {
            Name = space,
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };
        await NodeFactory.CreateNode(spaceNode).Should().Within(90.Seconds()).Emit();

        // ── The access LOGIC denies Anonymous on the private space (proves the leak is the
        //    subscribe path, not the evaluator).
        await Mesh.GetEffectivePermissions(space, WellKnownUsers.Anonymous)
            .Should().Within(60.Seconds()).Match(p => p == Permission.None);

        // ── Now read AS AN ANONYMOUS user. CircuitAccessHandler routes a logged-out circuit
        //    through an anonymous VUser (vuser == anonymous; IsVirtual, ObjectId = Anonymous) —
        //    NOT null. Set exactly that identity and open the remote subscribe the page render
        //    uses; it must be DENIED (the VUser has no grant on this private Space, and the Space
        //    has no PublicRead policy). Before the fix the circuit had a NULL context that was
        //    elevated to system-security and wrongly succeeded.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = WellKnownUsers.Anonymous,
            Name = "Guest",
            IsVirtual = true
        });
        try
        {
            var client = GetClient();
            var workspace = client.GetWorkspace();
            var stream = workspace.GetRemoteStream<EntityStore>(
                new Address(space), new CollectionsReference("test"));

            // Materialize folds OnError into a value so we assert reactively. The Read gate
            // runs BEFORE any collection-mapping error, so a DENIED read surfaces as
            // "Access denied" (cf. HubSubscriptionSecurityTest's positive case asserting the
            // opposite for a granted user).
            var notification = await stream
                .Timeout(8.Seconds())
                .Take(1)
                .Materialize()
                .Should().Within(30.Seconds()).Match(_ => true);

            notification.Kind.Should().Be(NotificationKind.OnError,
                "an anonymous subscribe to a PRIVATE Space must fail closed, not emit content");
            notification.Exception!.ToString().Should().Contain("Access denied",
                "anonymous (no grant, no public policy) must be DENIED Read on a private Space — " +
                "today the subscribe is upgraded to 'system-security' (Permission.All) and wrongly succeeds");
        }
        finally
        {
            accessService.SetCircuitContext(null);
        }
    }
}
