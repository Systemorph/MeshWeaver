using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// End-to-end fan-out coverage for the two unscoped dashboard queries the
/// user lands on in <c>UserActivityLayoutAreas</c>:
///
/// <list type="bullet">
///   <item><b>Activity Feed</b> — <c>source:activity scope:subtree is:main
///     sort:LastModified-desc</c>. Returns main content nodes ordered by
///     their most-recent <c>_Activity</c> satellite. Must fan out across
///     every partition so a user with activity in multiple orgs sees a
///     unified feed.</item>
///   <item><b>Latest Threads</b> — <c>nodeType:Thread namespace:*/_Thread
///     content.createdBy:{userId} sort:LastModified-desc</c>. Returns the
///     user's threads from every partition where they participate.</item>
/// </list>
///
/// <para>Prod symptom: both queries return empty for users with data in
/// multiple partitions because the query plane doesn't fan out — only the
/// caller's own partition (or no partition) is consulted. This test wires
/// up the same Orleans + <c>AddPartitionedPostgreSqlPersistence</c> stack as
/// <c>Memex.Portal.Distributed</c>, creates two partitions with satellite
/// rows, and asserts the queries return rows from BOTH.</para>
///
/// <para>Skipped on CI: needs the local Aspire <c>memex-postgres</c>
/// container; supply its connection string via <c>MESHWEAVER_LOCAL_PG_CS</c>.</para>
/// </summary>
public class OrleansPostgresFanOutTest(ITestOutputHelper output)
    : OrleansTestBase<OrleansPostgresFanOutTest.PostgresFanOutSiloConfigurator>(output)
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    private static bool ShouldSkip(out string reason)
    {
        var cs = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        if (string.IsNullOrEmpty(cs))
        {
            reason = $"set ${ConnectionStringEnvVar} to enable";
            return true;
        }
        reason = "";
        return false;
    }

    [Fact(Timeout = 240000)]
    public async Task ActivityFeed_FanOutAcrossPartitions_SortedByActivityRecency()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(180.Seconds()).Token;

        var run = Guid.NewGuid().ToString("N")[..6];
        var p1 = $"pgfo_a_{run}";
        var p2 = $"pgfo_b_{run}";

        var silo = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
        var mesh = silo.GetRequiredService<IMeshService>();

        // p1 and p2 — each a partition with a main content node and one
        // _Activity satellite. Older activity goes to p1, newer to p2 so the
        // sort:LastModified-desc test asserts the fan-out preserves order.
        await SeedPartitionAsync(mesh, p1, "doc1", "p1 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-30), ct);
        await SeedPartitionAsync(mesh, p2, "doc2", "p2 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-5), ct);

        // Same query string the GUI builds for the Activity Feed area.
        var query = "source:activity scope:subtree is:main sort:LastModified-desc";
        Output.WriteLine($"Running: {query}");
        var results = await mesh.QueryAsync<MeshNode>(new MeshQueryRequest
        {
            Query = query,
            UserId = WellKnownUsers.System,
        }).ToListAsync(ct);

        Output.WriteLine($"Hits: [{string.Join(", ", results.Select(r => $"{r.Path} (lm={r.LastModified:O})"))}]");

        results.Should().Contain(n => n.Path == $"{p1}/doc1",
            "Activity Feed must surface p1's main node — fan-out across partitions");
        results.Should().Contain(n => n.Path == $"{p2}/doc2",
            "Activity Feed must surface p2's main node — fan-out across partitions");

        // p2's activity is newer than p1's → p2 must appear first under
        // sort:LastModified-desc when source:activity is the join driver
        // (LastModified resolves to the activity satellite's timestamp,
        // not the main node's).
        var indexP1 = results.FindIndex(n => n.Path == $"{p1}/doc1");
        var indexP2 = results.FindIndex(n => n.Path == $"{p2}/doc2");
        indexP2.Should().BeLessThan(indexP1,
            "sort:LastModified-desc on source:activity must order by the activity satellite's " +
            "last_modified — p2's activity is newer than p1's so p2 must come first");
    }

    [Fact(Timeout = 240000)]
    public async Task LatestThreads_FanOutAcrossPartitions_FilterByCreatedBy()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(180.Seconds()).Token;

        var run = Guid.NewGuid().ToString("N")[..6];
        var p1 = $"pgft_a_{run}";
        var p2 = $"pgft_b_{run}";
        var user = $"u_{run}";

        var silo = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
        var mesh = silo.GetRequiredService<IMeshService>();

        await SeedPartitionAsync(mesh, p1, "doc1", "p1 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-20),
            createThreadByUser: user, threadLastModified: DateTimeOffset.UtcNow.AddMinutes(-20),
            ct: ct);
        await SeedPartitionAsync(mesh, p2, "doc2", "p2 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-2),
            createThreadByUser: user, threadLastModified: DateTimeOffset.UtcNow.AddMinutes(-2),
            ct: ct);

        // Same query string the GUI builds for Latest Threads. The
        // `namespace:*/_Thread` wildcard is the fan-out hint — every
        // partition's _Thread satellite is in play.
        var query = $"nodeType:Thread namespace:*/_Thread content.createdBy:{user} sort:LastModified-desc";
        Output.WriteLine($"Running: {query}");
        var results = await mesh.QueryAsync<MeshNode>(new MeshQueryRequest
        {
            Query = query,
            UserId = WellKnownUsers.System,
        }).ToListAsync(ct);

        Output.WriteLine($"Hits: [{string.Join(", ", results.Select(r => $"{r.Path} (lm={r.LastModified:O})"))}]");

        results.Should().Contain(n => n.Path == $"{p1}/_Thread/t-{p1}",
            "Latest Threads must surface p1's thread satellite — fan-out across partitions");
        results.Should().Contain(n => n.Path == $"{p2}/_Thread/t-{p2}",
            "Latest Threads must surface p2's thread satellite — fan-out across partitions");

        // p2's thread is newer → p2 first under sort:LastModified-desc.
        var indexP1 = results.FindIndex(n => n.Path == $"{p1}/_Thread/t-{p1}");
        var indexP2 = results.FindIndex(n => n.Path == $"{p2}/_Thread/t-{p2}");
        indexP2.Should().BeLessThan(indexP1,
            "sort:LastModified-desc must order threads by their own last_modified across partitions");
    }

    /// <summary>
    /// Creates the Admin/Partition row, partition root, one main content node
    /// + one _Activity satellite, and (optionally) one _Thread satellite.
    /// Mirrors <c>UserOnboardingService</c> + the Activity Control Plane's
    /// satellite write shape.
    /// </summary>
    private static async Task SeedPartitionAsync(
        IMeshService mesh,
        string partition,
        string docId,
        string docName,
        DateTimeOffset activityLastModified,
        CancellationToken ct,
        string? createThreadByUser = null,
        DateTimeOffset? threadLastModified = null)
    {
        await mesh.CreateNode(new MeshNode(partition, "Admin/Partition")
        {
            NodeType = "Partition",
            Name = partition,
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = partition,
                DataSource = "default",
                Schema = partition,
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.StandardTableMappings,
                Versioned = true,
            },
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        await mesh.CreateNode(new MeshNode(partition)
        {
            NodeType = "User",
            Name = partition,
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        // Main content node — what the Activity Feed Where(is:main) keeps.
        var mainPath = $"{partition}/{docId}";
        await mesh.CreateNode(new MeshNode(docId, partition)
        {
            NodeType = "Markdown",
            Name = docName,
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        // _Activity satellite — JOIN target of source:activity. MainNode
        // points back at the main content node so the join projects the main
        // row, and last_modified drives the sort.
        await mesh.CreateNode(new MeshNode($"a-{partition}", $"{partition}/{docId}/_Activity")
        {
            NodeType = "Activity",
            Name = $"act-{partition}",
            State = MeshNodeState.Active,
            MainNode = mainPath,
            LastModified = activityLastModified,
            Content = new ActivityLog("DataUpdate") { HubPath = mainPath },
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        if (createThreadByUser is not null)
        {
            await mesh.CreateNode(new MeshNode($"t-{partition}", $"{partition}/_Thread")
            {
                NodeType = "Thread",
                Name = $"Thread in {partition}",
                State = MeshNodeState.Active,
                MainNode = $"{partition}/_Thread/t-{partition}",
                LastModified = threadLastModified ?? activityLastModified,
                // content.createdBy is the filter the GUI applies.
                Content = new { CreatedBy = createThreadByUser },
            }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        }
    }

    public class PostgresFanOutSiloConfigurator : ISiloConfigurator, IHostConfigurator
    {
        public static readonly string AssemblyStoreRoot =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mw-orleans-pgfo-{Guid.NewGuid():N}");

        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.ConfigureMeshWeaverServer()
                .AddMemoryGrainStorageAsDefault();
            siloBuilder.ConfigureServices(services =>
                services.AddFileSystemAssemblyStore(AssemblyStoreRoot));
        }

        public void Configure(IHostBuilder hostBuilder)
        {
            var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
                ?? "Host=localhost;Database=test;Username=postgres;Password=postgres";
            hostBuilder.UseOrleansMeshServer()
                .ConfigureServices(services => services.AddPartitionedPostgreSqlPersistence(connectionString))
                .ConfigurePortalMesh()
                .AddDocumentation()
                .AddRowLevelSecurity();
        }
    }
}
