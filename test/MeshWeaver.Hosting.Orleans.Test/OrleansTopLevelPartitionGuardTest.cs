using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// DISTRIBUTED (Orleans + Postgres) repro for the atioz "top-level non-partition node" bug:
/// <c>rsalzmann</c> created a node at path <c>HelloWorld</c> (empty namespace ⇒ top-level)
/// with <c>NodeType = "Markdown"</c>. A top-level node IS a partition root, and the only
/// node types that may root a partition are the partition-owning ones (<c>User</c>,
/// <c>Space</c> — <c>NodeTypeDefinition.OwnsPartition = true</c>). A top-level
/// <c>Markdown</c> is therefore illegal and must be rejected at create-validation time.
///
/// <para><b>Why it slipped through.</b> The only structural gate was
/// <see cref="MeshWeaver.Graph.Security.PartitionWriteGuardValidator"/> rule 2
/// ("no partition, no write"), which probes
/// <see cref="IPartitionStorageProvider.PartitionExists"/> across providers and
/// <i>fails open</i>: it rejects only when EVERY provider answers <c>false</c>. In the
/// portal the Postgres provider answers <c>false</c> for an unknown schema, but the
/// static-node / embedded-resource providers return <c>null</c> (indeterminate) — so the
/// OR-fold never reaches "all false" and the write is allowed. A pre-existing
/// ("ghost") schema makes it worse: the probe then answers <c>true</c> and the guard
/// positively believes it is a real partition. Either way nothing checks that a top-level
/// node's NodeType actually owns a partition.</para>
///
/// <para>This test provisions the target schema up front (the ghost-schema state on atioz),
/// so the create reaches the storage write deterministically — isolating the missing
/// structural check from any 42P01. A non-System user with root admin holds Create at
/// every scope, so RLS is NOT the gate; the only thing that may reject the Markdown is the
/// partition-type invariant. The positive control (<see cref="CreateTopLevelSpace_AsUser_Succeeds"/>)
/// proves the same user CAN create a top-level partition when the type owns one.</para>
///
/// <para>Requires a Postgres with the partition stored-procs installed. Set
/// <c>MESHWEAVER_LOCAL_PG_CS</c> to enable; skips otherwise (CI / local Aspire PG).</para>
/// </summary>
public class OrleansTopLevelPartitionGuardTest(ITestOutputHelper output)
    : OrleansTestBase<OrleansTopLevelPartitionGuardTest.GuardSiloConfigurator>(output)
{
    internal const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";
    private static string? Cs => Environment.GetEnvironmentVariable(ConnectionStringEnvVar);

    /// <summary>A real, NON-System user (the rsalzmann stand-in). Root admin is granted below.</summary>
    internal const string TestUserId = "rsalzmann";

    private IServiceProvider Silo => ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;

    private static long NodeRows(string schema, string id)
    {
        using var conn = new NpgsqlConnection(Cs);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{schema}\".mesh_nodes WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// THE BUG. A non-System user creates a top-level <c>Markdown</c> node (empty namespace).
    /// Markdown does not own a partition, so the create must be rejected. Pre-fix this
    /// succeeds (the node persists at top level — the atioz "HelloWorld" state); post-fix the
    /// partition-type guard rejects it before the write.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task CreateTopLevelNonPartitionNode_AsUser_IsRejected()
    {
        if (string.IsNullOrEmpty(Cs)) { Output.WriteLine($"SKIPPED: set ${ConnectionStringEnvVar}"); return; }
        var ct = new CancellationTokenSource(110.Seconds()).Token;

        var meshService = Silo.GetRequiredService<IMeshService>();
        var access = Silo.GetRequiredService<AccessService>();

        // Unique top-level id so concurrent runs don't collide on a fixed schema name.
        var topLevelId = "Helloworld" + Guid.NewGuid().ToString("N")[..8];
        var schema = topLevelId.ToLowerInvariant();

        // Ghost-schema state: provision the backing schema up front so the storage write
        // would succeed. This removes 42P01 as a confound — the ONLY thing that may stop
        // the create is the partition-type invariant under test.
        foreach (var p in Silo.GetServices<IPartitionStorageProvider>())
            await p.EnsurePartitionProvisioned(topLevelId)
                .Catch((Exception _) => Observable.Return(default(System.Reactive.Unit)))
                .FirstAsync().ToTask(ct);

        var node = new MeshNode(topLevelId) // empty namespace ⇒ top-level partition root
        {
            NodeType = "Markdown",
            Name = "Hello World",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = "# Hello World" }
        };

        // Act AS the real user (NOT System — System legitimately provisions partitions and
        // bypasses the guard). Root admin (seeded below) means RLS grants Create everywhere,
        // so a rejection here can only come from the partition-type invariant.
        access.SetCircuitContext(new AccessContext
        {
            ObjectId = TestUserId, Name = TestUserId, Email = $"{TestUserId}@meshweaver.io"
        });
        try
        {
            Exception? rejection = null;
            try
            {
                await meshService.CreateNode(node).FirstAsync().Timeout(60.Seconds()).ToTask(ct);
            }
            catch (Exception ex)
            {
                rejection = ex;
            }

            rejection.Should().NotBeNull(
                "a top-level node must own a partition; Markdown does not, so the create must be rejected " +
                "(pre-fix it is silently accepted — the atioz HelloWorld bug)");

            // And nothing must have landed at the top level.
            NodeRows(schema, topLevelId).Should().Be(0,
                "the rejected top-level Markdown node must not be persisted");
        }
        finally
        {
            access.SetCircuitContext(null);
        }
    }

    /// <summary>
    /// POSITIVE CONTROL. The same non-System user creates a top-level <c>Space</c> — a
    /// partition-owning type — and it SUCCEEDS. Proves RLS is not the gate in
    /// <see cref="CreateTopLevelNonPartitionNode_AsUser_IsRejected"/>: the difference is
    /// solely that Space owns a partition and Markdown does not.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task CreateTopLevelSpace_AsUser_Succeeds()
    {
        if (string.IsNullOrEmpty(Cs)) { Output.WriteLine($"SKIPPED: set ${ConnectionStringEnvVar}"); return; }
        var ct = new CancellationTokenSource(110.Seconds()).Token;

        var meshService = Silo.GetRequiredService<IMeshService>();
        var access = Silo.GetRequiredService<AccessService>();

        var spaceId = "Sp" + Guid.NewGuid().ToString("N")[..10];
        var schema = spaceId.ToLowerInvariant();

        access.SetCircuitContext(new AccessContext
        {
            ObjectId = TestUserId, Name = TestUserId, Email = $"{TestUserId}@meshweaver.io"
        });
        try
        {
            var created = await meshService.CreateNode(new MeshNode(spaceId)
            {
                NodeType = "Space",
                Name = spaceId,
                State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = $"# {spaceId}\n\nwelcome" }
            }).FirstAsync().Timeout(60.Seconds()).ToTask(ct);

            created.Should().NotBeNull("a top-level partition-owning Space create must be allowed for a user with root Create");

            // The Space owns its partition: OwnsPartitionProvisioningValidator provisioned the
            // schema and the root persisted there.
            await WaitUntil(() => NodeRows(schema, spaceId) == 1, ct,
                "the Space partition root must persist in its own provisioned schema");
        }
        finally
        {
            access.SetCircuitContext(null);
        }
    }

    private async Task WaitUntil(Func<bool> condition, CancellationToken ct, string because)
    {
        for (var i = 0; i < 60; i++)
        {
            try { if (condition()) return; } catch { /* schema may not exist yet */ }
            await Task.Delay(1000, ct);
        }
        condition().Should().BeTrue(because);
    }

    /// <summary>Silo wiring: PG persistence + portal mesh + graph + Space type + RLS, plus a
    /// root admin grant so the test user passes RLS at every scope (isolating the partition-type
    /// invariant from permissions).</summary>
    public class GuardSiloConfigurator : ISiloConfigurator, IHostConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) =>
            siloBuilder.ConfigureMeshWeaverServer().AddMemoryGrainStorageAsDefault();

        public void Configure(IHostBuilder hostBuilder)
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
                ?? "Host=localhost;Database=test;Username=postgres;Password=postgres";
            hostBuilder.UseOrleansMeshServer()
                .ConfigureServices(services =>
                {
                    services.AddPartitionedPostgreSqlPersistence(cs);
                    return services;
                })
                .ConfigurePortalMesh()
                .AddGraph()
                .AddSpaceType()
                .AddRowLevelSecurity()
                .AddMeshNodes(RootAdminGrant());
        }

        /// <summary>
        /// Root-scope ("global") admin for every user. Lives at namespace <c>_Access</c> so
        /// SecurityService maps it to scope "" (global) — see TestUsers.PublicAdminAccess. With
        /// this, the test user holds Create at the root scope, so RLS never gates a top-level
        /// create and the only possible rejection is the partition-type invariant.
        /// </summary>
        private static MeshNode RootAdminGrant() =>
            new(WellKnownUsers.Public + "_Access", "_Access")
            {
                NodeType = "AccessAssignment",
                Name = "Public Root Access",
                Content = new AccessAssignment
                {
                    AccessObject = WellKnownUsers.Public,
                    DisplayName = "Public",
                    Roles = [new RoleAssignment { Role = "Admin" }]
                }
            };
    }
}
