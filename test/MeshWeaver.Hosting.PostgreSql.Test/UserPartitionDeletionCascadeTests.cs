using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Deleting a USER partition root must remove the ENTIRE partition — every mesh_nodes
/// descendant AND every satellite-table row (threads, notifications, user_activities,
/// access, …) AND the backing schema itself. This is the deletion-side mirror of the
/// eager <c>User</c>-partition provisioning (<c>OwnsPartitionProvisioningValidator</c>).
///
/// <para>Regression guard for the 2026-07-19 memex-cloud incident: a System-impersonated
/// recursive delete of a user partition root reported success but left ~30 descendant
/// nodes and satellite rows behind in the partition schema. Root cause: <c>User</c>
/// (unlike <c>Space</c>) had NO <c>PartitionDropPostDeletionHandler</c>, so the schema
/// was never dropped and the recursive fan-out (which only enumerates <c>mesh_nodes</c>)
/// never reached the satellite tables — an orphaned partition (storage- and data-leak).</para>
///
/// <para>Space-parity mesh shape (RLS + Graph, which registers the User type + its
/// teardown), same as <see cref="SpaceDeletionPartitionDropTests"/>.</para>
/// </summary>
[Collection("PostgreSql")]
public class UserPartitionDeletionCascadeTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;
    private readonly JsonSerializerOptions _options = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType();
    }

    private IObservable<long> SchemaCount(string schema) =>
        _fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s",
            new[] { ("s", (object)schema) });

    /// <summary>
    /// AUTHORITATIVE total-row count across EVERY table in the partition schema — direct PG,
    /// past every per-node-hub cache / catalog-stream layer. Returns 0 when the schema (or a
    /// table) no longer exists, so it works whether the partition teardown DROPs the schema or
    /// merely empties it. This is the ground truth for "is the partition actually gone".
    /// </summary>
    private async Task<long> PartitionRowCountAsync(string schema, CancellationToken ct)
    {
        var tables = new List<string>();
        await using (var listCmd = _fixture.DataSource.CreateCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @s"))
        {
            listCmd.Parameters.AddWithValue("s", schema);
            await using var rdr = await listCmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                tables.Add(rdr.GetString(0));
        }

        if (tables.Count == 0)
            return 0L;

        var sb = new StringBuilder("SELECT ");
        for (var i = 0; i < tables.Count; i++)
        {
            if (i > 0) sb.Append(" + ");
            sb.Append("(SELECT COUNT(*) FROM \"")
              .Append(schema.Replace("\"", "\"\""))
              .Append("\".\"")
              .Append(tables[i].Replace("\"", "\"\""))
              .Append("\")");
        }

        await using var sumCmd = _fixture.DataSource.CreateCommand(sb.ToString());
        return (long)(await sumCmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Direct-PG row count for one satellite table (0 if the table/schema is gone).</summary>
    private async Task<long> TableRowCountAsync(string schema, string table, CancellationToken ct)
    {
        await using var probe = _fixture.DataSource.CreateCommand(
            "SELECT to_regclass(@q) IS NOT NULL");
        probe.Parameters.AddWithValue("q", $"\"{schema}\".\"{table}\"");
        var exists = (bool)(await probe.ExecuteScalarAsync(ct))!;
        if (!exists) return 0L;

        await using var cmd = _fixture.DataSource.CreateCommand(
            $"SELECT COUNT(*) FROM \"{schema.Replace("\"", "\"\"")}\".\"{table.Replace("\"", "\"\"")}\"");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private IObservable<MeshNode> CreateAsSystem(MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return Observable.Using(
            () => access.ImpersonateAsSystem(),
            _ => meshService.CreateNode(node));
    }

    private IObservable<bool> DeleteAsSystem(string path)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return Observable.Using(
            () => access.ImpersonateAsSystem(),
            _ => meshService.DeleteNode(path));
    }

    /// <summary>
    /// The incident reproduction: a User partition root with mesh_nodes descendants AND
    /// satellite-table rows is deleted under System — the whole partition (schema + every
    /// table) must be gone afterwards, not just the root row.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task DeletingUserPartitionRoot_UnderSystem_DropsWholePartition()
    {
        var ct = TestContext.Current.CancellationToken;
        // Lowercase id ⇒ schema name == id (the router lowercases the first path segment).
        var userId = $"usrdel{Guid.NewGuid():N}".ToLowerInvariant()[..16];

        // 1. Create the user partition root (System = the legitimate partition provisioner).
        //    This eagerly provisions the "{userId}" schema + its satellite tables.
        await SeedTopLevel(new MeshNode(userId)
        {
            NodeType = "User",
            Name = "Delete Me",
            State = MeshNodeState.Active,
        });
        await SchemaCount(userId).Should().Within(30.Seconds()).Be(1L);

        // 2. mesh_nodes descendants — a normal child and a nested "installed course copy"
        //    child (exactly the shape the incident left behind).
        await CreateAsSystem(new MeshNode("token1", $"{userId}/ApiToken")
        {
            NodeType = "Markdown",
            Name = "API token",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        await CreateAsSystem(new MeshNode("ex1", $"{userId}/AgenticEngineering/Module1/Exercise")
        {
            NodeType = "Markdown",
            Name = "Exercise",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

        // 3. Satellite-table rows — threads / notifications / user_activities / access. These
        //    live in dedicated tables the recursive-delete enumeration (mesh_nodes only) never
        //    reaches; seeded straight into the partition schema so they exist before the delete.
        var userPartition = new PartitionDefinition
        {
            Namespace = userId,
            DataSource = "default",
            Schema = userId,
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
        };
        var (_, satelliteAdapter) = await _fixture.CreateSchemaAdapterAsync(userId, userPartition, ct);
        await satelliteAdapter.Write(new MeshNode("t1", $"{userId}/_Thread")
        {
            NodeType = "Thread", Name = "A thread", MainNode = $"{userId}/_Thread",
        }, _options).Should().Within(30.Seconds()).Emit();
        await satelliteAdapter.Write(new MeshNode("n1", $"{userId}/_Notification")
        {
            NodeType = "Notification", Name = "A notification",
        }, _options).Should().Within(30.Seconds()).Emit();
        await satelliteAdapter.Write(new MeshNode("act1", $"{userId}/_UserActivity")
        {
            NodeType = "UserActivity", Name = "An activity",
        }, _options).Should().Within(30.Seconds()).Emit();
        await satelliteAdapter.Write(new MeshNode("deny1", $"{userId}/_Access")
        {
            NodeType = "AccessAssignment", Name = "A deny",
        }, _options).Should().Within(30.Seconds()).Emit();

        // Sanity: the partition genuinely has both mesh_nodes AND satellite content now.
        (await PartitionRowCountAsync(userId, ct))
            .Should().BeGreaterThan(0L, "the partition must have content before the delete");
        (await TableRowCountAsync(userId, "threads", ct))
            .Should().BeGreaterThan(0L, "seeded a thread satellite");
        (await TableRowCountAsync(userId, "notifications", ct))
            .Should().BeGreaterThan(0L, "seeded a notification satellite");
        (await TableRowCountAsync(userId, "user_activities", ct))
            .Should().BeGreaterThan(0L, "seeded a user-activity satellite");

        // 4. Delete the user partition root under System (the incident path).
        var deleted = await DeleteAsSystem(userId).Should().Within(90.Seconds()).Emit();
        deleted.Should().BeTrue();

        // 5. The WHOLE partition is gone — schema dropped, EVERY table empty. A delete that
        //    leaves the schema or any satellite row is the orphaned-partition bug.
        await SchemaCount(userId).Should().Within(30.Seconds()).Be(0L,
            "deleting a user partition root must drop the whole partition schema");

        // Sustained-zero window: authoritative direct-PG count across every partition table
        // stays 0 — a per-node hub reactivating after the delete must not resurrect any row
        // (mesh_nodes OR satellite). Negative-assertion window — the sanctioned Task.Delay use.
        for (var i = 0; i < 10; i++)
        {
            (await PartitionRowCountAsync(userId, ct))
                .Should().Be(0L,
                    "no mesh_nodes row and no satellite row may survive the partition delete");
            await Task.Delay(100, ct);
        }
    }
}
