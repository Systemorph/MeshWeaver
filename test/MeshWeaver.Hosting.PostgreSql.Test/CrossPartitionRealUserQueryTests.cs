using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Prod repro (atioz 2026-07-02/03): an UNPINNED structured query (no <c>namespace:</c>, no
/// <c>path:</c> — e.g. <c>nodeType:Document</c> / <c>nodeType:Markdown</c> from the MCP
/// <c>search</c> tool) issued by a REAL user hung forever — the merged query stream never
/// emitted its Initial, the caller's <c>Take(1)</c> waited indefinitely, and the MCP client
/// aborted at 300s. System-identity queries (boot/background) and partition-pinned queries
/// answered instantly, and PostgreSQL was idle throughout — the emission is lost in the
/// provider→merge pipeline, not in SQL.
///
/// <para>These tests pin the CONTRACT the production flow relies on: an unpinned structured
/// query through <see cref="IMeshService.Query{T}"/> must emit its Initial snapshot promptly
/// — for the auto-admin caller, for an ordinary sample user, and for the System identity
/// alike. A hang shows up as the 20s Rx timeout, well inside the test timeout.</para>
/// </summary>
[Collection("PostgreSql")]
public class CrossPartitionRealUserQueryTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

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
            .AddGraph();
    }

    private Task ProvisionPartition(string ns) =>
        Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(p => p.EnsurePartitionProvisioned(ns))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .ToTask();

    /// <summary>Two provisioned partitions with one Markdown node each — the minimal cross-schema world.</summary>
    private async Task<(string NsA, string NsB)> SeedTwoPartitions()
    {
        var nsA = $"xq_{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        var nsB = $"xq_{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        await ProvisionPartition(nsA);
        await ProvisionPartition(nsB);

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        foreach (var ns in new[] { nsA, nsB })
            await meshService.CreateNode(new MeshNode("doc1", ns)
            {
                Name = $"Doc in {ns}",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            }).FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask();

        return (nsA, nsB);
    }

    private IObservable<QueryResultChange<MeshNode>> RunUnpinned(string? userId) =>
        Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(new MeshQueryRequest
            {
                Query = "nodeType:Markdown",
                Limit = 10,
                UserId = userId!,
            });

    /// <summary>
    /// The exact production shape: the MCP search tool builds the request WITHOUT a UserId,
    /// so the ambient caller identity (the auto-admin circuit user here, rbuergi on prod)
    /// drives the secured provider surface. The Initial must arrive promptly.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task UnpinnedStructuredQuery_AsAmbientRealUser_EmitsInitial()
    {
        await SeedTwoPartitions();

        var change = await RunUnpinned(userId: null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask();

        change.ChangeType.Should().BeOneOf(QueryChangeType.Initial, QueryChangeType.Reset);
    }

    /// <summary>Explicit non-admin sample user — the per-result RLS path.</summary>
    [Fact(Timeout = 60_000)]
    public async Task UnpinnedStructuredQuery_AsExplicitUser_EmitsInitial()
    {
        await SeedTwoPartitions();

        var change = await RunUnpinned("some.user")
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask();

        change.ChangeType.Should().BeOneOf(QueryChangeType.Initial, QueryChangeType.Reset);
    }

    /// <summary>Control: the System identity (boot/background queries) — known-good on prod.</summary>
    [Fact(Timeout = 60_000)]
    public async Task UnpinnedStructuredQuery_AsSystem_EmitsInitial()
    {
        var (nsA, _) = await SeedTwoPartitions();

        var change = await RunUnpinned(WellKnownUsers.System)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask();

        change.ChangeType.Should().BeOneOf(QueryChangeType.Initial, QueryChangeType.Reset);
        change.Items.Should().Contain(n => n.Path == $"{nsA}/doc1",
            "System sees every partition's rows");
    }
}
