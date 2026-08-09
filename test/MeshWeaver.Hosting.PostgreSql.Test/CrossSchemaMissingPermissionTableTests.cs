using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// One partition that cannot be access-filtered must not darken the cross-schema union for every
/// other partition.
///
/// <para><b>The defect.</b> The per-schema access clause names
/// <c>{schema}.user_effective_permissions</c>. A partition that ships <c>mesh_nodes</c> WITHOUT
/// that table turns the clause into a reference to a missing relation, so Postgres fails to PLAN
/// the whole UNION with <c>42P01</c> — and <c>EnumerateReaderOrEmptyOnMissingRelationAsync</c>
/// absorbs 42P01 as "this satellite table isn't here" and yields nothing. The failure was therefore
/// SILENT and TOTAL: a single such partition returned an empty result for every authenticated
/// user's unscoped query, however healthy the other fifty partitions were. It is invisible on a
/// small local container and appears wherever such a partition accumulates — which is why it read
/// as a CI-only failure of the #697 live-query test.</para>
///
/// <para><b>Why dropping the branch is the fix, and loses nothing.</b> The clause's other half is
/// <c>public.partition_access</c>, and the SAME function that builds a partition's
/// <c>user_effective_permissions</c> is what populates its <c>partition_access</c> rows. A schema
/// without the table therefore has no <c>partition_access</c> row either, so its branch could only
/// ever have contributed zero rows to a filtered caller. Dropping it removes no row that was ever
/// visible; it only stops the branch from 42P01-ing the entire statement.</para>
///
/// <para>🚨 The tempting alternative — contribute the branch UNFILTERED, which is what
/// <c>public.search_across_schemas</c> does for this case — publishes every row of an ungranted
/// partition to every authenticated user. <c>PerSchemaAccessClauseLeakTests</c> pins that leak, and
/// it is the same class #471/#385 RC3 closed by moving <c>public_read</c> INSIDE the partition
/// gate. Fail closed.</para>
/// </summary>
[Collection("PostgreSql")]
public class CrossSchemaMissingPermissionTableTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { MaxPoolSize = 16, ConnectionIdleLifetime = 10 };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType();
    }

    // Per test instance, never static — the PostgreSql fixture is shared across the collection.
    private string Space { get; } = "HrSpace" + Guid.NewGuid().ToString("N")[..8];
    private string GroupPath => $"{Space}/HRTeam";
    private string MembershipPath => $"{GroupPath}/gm_employee_Membership";
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 180000)]
    public async Task PathLessQuery_StillSeesGrantedPartition_WhenAnotherSchemaHasNoPermissionTable()
    {
        // A partition in the shape that breaks the union: mesh_nodes present, the per-partition
        // permission table ABSENT. Registered as searchable so it really enters the fan-out.
        var crippled = "zznoperm" + Guid.NewGuid().ToString("N")[..8];
        await using (var cmd = fixture.DataSource.CreateCommand("SELECT public.ensure_partition_schema(@s)"))
        {
            cmd.Parameters.AddWithValue("s", crippled);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using (var cmd = fixture.DataSource.CreateCommand(
            $"DROP TABLE IF EXISTS \"{crippled}\".user_effective_permissions; "
            + $"DROP TABLE IF EXISTS \"{crippled}\".user_effective_permissions_shadow;"))
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using (var cmd = fixture.DataSource.CreateCommand(
            "INSERT INTO public.searchable_schemas (schema_name) VALUES (@s) ON CONFLICT DO NOTHING"))
        {
            cmd.Parameters.AddWithValue("s", crippled);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // A healthy partition the caller owns, holding the row the query must return.
        await MeshService.CreateNode(MeshNode.FromPath(Space) with
        { Name = Space, NodeType = SpaceNodeType.NodeType, Content = new Space() })
            .Should().Within(90.Seconds()).Emit();
        await MeshService.CreateNode(new MeshNode("HRTeam", Space)
        {
            Name = "HR Team", NodeType = "Group", MainNode = GroupPath,
            Content = new AccessObject { Description = "HR team" },
        }).Should().Within(90.Seconds()).Emit();
        await MeshService.CreateNode(new MeshNode("gm_employee_Membership", GroupPath)
        {
            NodeType = "GroupMembership", Name = "gm_employee membership", MainNode = MembershipPath,
            Content = new GroupMembership
            {
                Member = "gm_employee", DisplayName = "gm_employee",
                Groups = [new MembershipEntry { Group = GroupPath }],
            },
        }).Should().Within(90.Seconds()).Emit();

        var initial = await MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                "nodeType:GroupMembership scope:subtree select:path,id,namespace,name,nodeType,content"))
            .Should().Within(60.Seconds()).Match(c => c.ChangeType == QueryChangeType.Initial);

        initial.Items.Should().Contain(n => n.Path == MembershipPath,
            "a partition that cannot be access-filtered must be dropped from the union, not take "
            + "every other partition's rows down with it via a swallowed 42P01");
    }
}
