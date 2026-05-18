using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using System.Text.Json;
using Pgvector.Npgsql;
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
using Microsoft.Extensions.Configuration;
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
        await SeedPartitionAsync(silo, p1, "doc1", "p1 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-30), ct);
        await SeedPartitionAsync(silo, p2, "doc2", "p2 doc",
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

        await SeedPartitionAsync(silo, p1, "doc1", "p1 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-20),
            createThreadByUser: user, threadLastModified: DateTimeOffset.UtcNow.AddMinutes(-20),
            ct: ct);
        await SeedPartitionAsync(silo, p2, "doc2", "p2 doc",
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

    [Fact(Timeout = 240000)]
    public async Task ScopedQuery_StaysOnSinglePartition_NoFanOut()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(180.Seconds()).Token;

        var run = Guid.NewGuid().ToString("N")[..6];
        var p1 = $"pgfs_a_{run}";
        var p2 = $"pgfs_b_{run}";

        var silo = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
        var mesh = silo.GetRequiredService<IMeshService>();

        await SeedPartitionAsync(silo, p1, "doc1", "p1 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-30), ct);
        await SeedPartitionAsync(silo, p2, "doc2", "p2 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-5), ct);

        // Scoped query — namespace pins to p1. The fan-out provider must
        // short-circuit (NeedsFanOut=false) and leave the per-schema
        // StorageAdapterMeshQueryProvider to handle this.
        var query = $"source:activity namespace:{p1} scope:subtree is:main sort:LastModified-desc";
        Output.WriteLine($"Running: {query}");
        var results = await mesh.QueryAsync<MeshNode>(new MeshQueryRequest
        {
            Query = query,
            UserId = WellKnownUsers.System,
        }).ToListAsync(ct);

        Output.WriteLine($"Hits: [{string.Join(", ", results.Select(r => r.Path))}]");

        results.Should().Contain(n => n.Path == $"{p1}/doc1",
            "scoped query must still surface p1's row through the per-schema provider");
        results.Should().NotContain(n => n.Path == $"{p2}/doc2",
            "scoped query MUST NOT leak rows from p2 — fan-out is gated on unscoped/wildcard");
    }

    [Fact(Timeout = 240000)]
    public async Task ActivityFeed_RespectsExplicitLimit_AcrossPartitions()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(180.Seconds()).Token;

        var run = Guid.NewGuid().ToString("N")[..6];
        var p1 = $"pgfl_a_{run}";
        var p2 = $"pgfl_b_{run}";
        var p3 = $"pgfl_c_{run}";

        var silo = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
        var mesh = silo.GetRequiredService<IMeshService>();

        await SeedPartitionAsync(silo, p1, "doc1", "p1 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-30), ct);
        await SeedPartitionAsync(silo, p2, "doc2", "p2 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-15), ct);
        await SeedPartitionAsync(silo, p3, "doc3", "p3 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-1), ct);

        var query = "source:activity scope:subtree is:main sort:LastModified-desc limit:2";
        Output.WriteLine($"Running: {query}");
        var results = await mesh.QueryAsync<MeshNode>(new MeshQueryRequest
        {
            Query = query,
            UserId = WellKnownUsers.System,
        }).ToListAsync(ct);

        Output.WriteLine($"Hits: [{string.Join(", ", results.Select(r => r.Path))}]");

        // Limit applies across the UNION'd result. Cross-test pollution can
        // dominate either slot (a prior test left rows in other schemas that
        // are still in the searchable-schemas set), so the strict assertion is:
        //   1. limit:2 is honoured — exactly 2 rows back
        //   2. p3 (the most recent of MY three seeded partitions) is present
        //      — its `_Activity` row's last_modified is ~1 minute ago, so any
        //      leftover data older than that loses the top spot
        results.Should().HaveCount(2,
            "limit:2 must cap the cross-partition UNION at two rows");
        results.Should().Contain(n => n.Path == $"{p3}/doc3",
            "p3's activity (~1 min ago) must rank at the top of the limited window");
        results.Should().NotContain(n => n.Path == $"{p1}/doc1",
            "p1's activity (~30 min ago) is the oldest of MY seeded partitions and must NOT be in the top 2");
    }

    /// <summary>
    /// Renders the actual user dashboard layout area (the same area Memex serves
    /// at <c>/{username}</c> in prod) and asserts the Latest Threads MeshSearch
    /// control surfaces a thread the user created in a remote partition. This is
    /// the end-to-end repro for the prod symptom: even with the cross-partition
    /// fan-out wired into <see cref="IMeshQueryProvider"/>, the dashboard area
    /// must dispatch the right query and the result must reach the rendered
    /// MeshSearch payload.
    ///
    /// <para>Path-of-concern: <c>UserActivityLayoutAreas.Activity</c> calls
    /// <c>BuildLatestThreads</c> which constructs a MeshSearch with the
    /// hidden query <c>nodeType:Thread namespace:*/_Thread content.createdBy:{user} sort:LastModified-desc</c>.
    /// The MeshSearch control issues that query through the same MeshQuery /
    /// IMeshQueryProvider chain the standalone QueryAsync test exercises, but
    /// it goes through the layout-area workspace stream rather than a direct
    /// IMeshService call — so a wiring bug in the dashboard hub's mesh-query
    /// resolution would surface here but not in the other tests.</para>
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task UserDashboard_RendersLatestThreads_FromRemotePartition()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(180.Seconds()).Token;

        var run = Guid.NewGuid().ToString("N")[..6];
        // The dashboard owner — they navigate to /{viewerUser}. The thread lives
        // in a DIFFERENT partition, so finding it requires the cross-partition
        // fan-out (a same-partition test would pass on the pedestrian provider).
        var viewerUser = $"viewer_{run}";
        var ownerPartition = viewerUser;
        var remotePartition = $"pgrt_{run}";

        var silo = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;

        // Owner's own partition — must exist so the user hub at /{viewerUser}
        // activates without a fan-out detour. No threads here — that's the
        // whole point: the thread lives in a DIFFERENT partition.
        await SeedPartitionAsync(silo, ownerPartition, "myhome", "Home",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-30), ct);

        // Remote partition (e.g. an org the user participates in) — has a
        // _Thread satellite created by `viewerUser`. The dashboard must
        // surface it via Latest Threads.
        await SeedPartitionAsync(silo, remotePartition, "task1", "Task 1",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-2),
            createThreadByUser: viewerUser,
            threadLastModified: DateTimeOffset.UtcNow.AddMinutes(-2),
            ct: ct);

        // Subscribe to the same MeshSearch backing query the dashboard's
        // BuildLatestThreads section uses. If this is empty, the dashboard
        // can't surface the thread no matter how the MeshSearch control
        // renders.
        var mesh = silo.GetRequiredService<IMeshService>();
        var query = $"nodeType:Thread namespace:*/_Thread content.createdBy:{viewerUser} sort:LastModified-desc";
        Output.WriteLine($"Dashboard-query: {query}");
        var threadHits = await mesh.QueryAsync<MeshNode>(new MeshQueryRequest
        {
            Query = query,
            UserId = WellKnownUsers.System,
        }).ToListAsync(ct);

        Output.WriteLine($"Thread hits: [{string.Join(", ", threadHits.Select(r => r.Path))}]");
        threadHits.Should().Contain(n => n.Path == $"{remotePartition}/_Thread/t-{remotePartition}",
            "the dashboard's Latest Threads query MUST surface threads from partitions other than the user's own — fan-out across all partitions");
    }

    [Fact(Timeout = 240000)]
    public async Task LatestThreads_FiltersOutOtherUsers()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(180.Seconds()).Token;

        var run = Guid.NewGuid().ToString("N")[..6];
        var p1 = $"pgfu_a_{run}";
        var p2 = $"pgfu_b_{run}";
        var me = $"me_{run}";
        var other = $"other_{run}";

        var silo = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
        var mesh = silo.GetRequiredService<IMeshService>();

        await SeedPartitionAsync(silo, p1, "doc1", "p1 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-30),
            createThreadByUser: me, ct: ct);
        await SeedPartitionAsync(silo, p2, "doc2", "p2 doc",
            activityLastModified: DateTimeOffset.UtcNow.AddMinutes(-5),
            createThreadByUser: other, ct: ct);

        var query = $"nodeType:Thread namespace:*/_Thread content.createdBy:{me} sort:LastModified-desc";
        Output.WriteLine($"Running: {query}");
        var results = await mesh.QueryAsync<MeshNode>(new MeshQueryRequest
        {
            Query = query,
            UserId = WellKnownUsers.System,
        }).ToListAsync(ct);

        Output.WriteLine($"Hits: [{string.Join(", ", results.Select(r => r.Path))}]");

        results.Should().Contain(n => n.Path == $"{p1}/_Thread/t-{p1}",
            "thread created by `me` must be returned");
        results.Should().NotContain(n => n.Path == $"{p2}/_Thread/t-{p2}",
            "thread created by `other` must NOT be returned — content.createdBy filter applies post-UNION");
    }

    /// <summary>
    /// Seeds a partition + its satellite rows DIRECTLY via SQL. We bypass
    /// <c>IMeshService.CreateNode</c> (RLS pipeline) and even the IStorageAdapter
    /// abstraction (we observed silent no-ops under cache-race conditions in
    /// the test harness). Raw SQL gives us deterministic state so the fan-out
    /// query is exercised against a known repository shape:
    ///   <c>CREATE SCHEMA</c>, <c>CREATE TABLE</c> (mesh_nodes + satellites
    ///   via <see cref="PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync"/>),
    ///   then per-row <c>INSERT</c> with exact <c>last_modified</c> timestamps.
    /// The Admin/Partition row at <c>admin.mesh_nodes</c> drives the runtime
    /// partition registry; the rest mirrors what UserOnboarding +
    /// ActivityControlPlane produce in prod.
    /// </summary>
    /// <summary>
    /// Single test-owned NpgsqlDataSource for all seed SQL. CreateAdapterForTable
    /// (used to materialise schemas + satellite tables) caches per-schema adapters
    /// inside the silo's <see cref="PostgreSqlPartitionStorageProvider"/> with
    /// <c>MaxPoolSize=1</c>; seeding all schemas through THOSE adapters exhausts
    /// the test container's connection limit (53300 "too many clients") by the
    /// 3rd or 4th test. Routing INSERTs through ONE shared pool keeps the
    /// connection footprint bounded.
    /// </summary>
    private Npgsql.NpgsqlDataSource? _seedDataSource;

    private Npgsql.NpgsqlDataSource GetSeedDataSource()
    {
        if (_seedDataSource is null)
        {
            var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar)!;
            var csb = new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 4 };
            var dsBuilder = new Npgsql.NpgsqlDataSourceBuilder(csb.ConnectionString);
            _seedDataSource = dsBuilder.Build();
        }
        return _seedDataSource;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_seedDataSource is not null)
        {
            await _seedDataSource.DisposeAsync();
            _seedDataSource = null;
        }
        await base.DisposeAsync();
    }

    private async Task SeedPartitionAsync(
        IServiceProvider siloSp,
        string partition,
        string docId,
        string docName,
        DateTimeOffset activityLastModified,
        CancellationToken ct,
        string? createThreadByUser = null,
        DateTimeOffset? threadLastModified = null)
    {
        var partitionDef = new PartitionDefinition
        {
            Namespace = partition,
            DataSource = "default",
            Schema = partition,
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Versioned = true,
        };

        // CreateAdapterForTable internally calls EnsureSchemaForPartitionSync
        // (CREATE SCHEMA + InitializeAsync + CreateSatelliteTablesAsync), so by
        // the time it returns the schema + mesh_nodes + activities + threads
        // tables are all in place. We only care about the side-effect — the
        // returned adapter is discarded; seed INSERTs use a single shared
        // NpgsqlDataSource (see GetSeedDataSource) to keep connection count
        // bounded across tests.
        var pgProvider = siloSp.GetRequiredService<PostgreSqlPartitionStorageProvider>();
        _ = pgProvider.CreateAdapterForTable(partitionDef, "mesh_nodes");
        var dataSource = GetSeedDataSource();

        var jsonOptions = siloSp.GetRequiredService<IMessageHub>().JsonSerializerOptions;

        // 2. Admin/Partition registry row at admin.mesh_nodes — drives the
        //    runtime partition catalog (V23 pg_notify primes the per-silo
        //    PgPartitionCache so the fan-out's GetSearchableSchemasAsync
        //    surfaces this partition).
        await InsertMeshNodeAsync(dataSource, "admin", "mesh_nodes",
            namespacePart: "Admin/Partition",
            id: partition,
            nodeType: "Partition",
            name: partition,
            mainNode: $"Admin/Partition/{partition}",
            lastModified: DateTimeOffset.UtcNow,
            content: new PartitionDefinition
            {
                Namespace = partition,
                DataSource = "default",
                Schema = partition,
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.StandardTableMappings,
                Versioned = true,
            },
            jsonOptions: jsonOptions,
            ct: ct);

        // 3. Partition root + main content node + _Activity satellite + optional _Thread.
        await InsertMeshNodeAsync(dataSource, partition, "mesh_nodes",
            namespacePart: "", id: partition, nodeType: "User", name: partition,
            mainNode: partition,
            lastModified: DateTimeOffset.UtcNow,
            content: null, jsonOptions: jsonOptions, ct: ct);

        var mainPath = $"{partition}/{docId}";
        await InsertMeshNodeAsync(dataSource, partition, "mesh_nodes",
            namespacePart: partition, id: docId, nodeType: "Markdown", name: docName,
            mainNode: mainPath,
            lastModified: DateTimeOffset.UtcNow,
            content: null, jsonOptions: jsonOptions, ct: ct);

        await InsertMeshNodeAsync(dataSource, partition, "activities",
            namespacePart: $"{partition}/{docId}/_Activity",
            id: $"a-{partition}",
            nodeType: "Activity",
            name: $"act-{partition}",
            mainNode: mainPath,
            lastModified: activityLastModified,
            content: new ActivityLog("DataUpdate") { HubPath = mainPath },
            jsonOptions: jsonOptions, ct: ct);

        if (createThreadByUser is not null)
        {
            var threadPath = $"{partition}/_Thread/t-{partition}";
            await InsertMeshNodeAsync(dataSource, partition, "threads",
                namespacePart: $"{partition}/_Thread",
                id: $"t-{partition}",
                nodeType: "Thread",
                name: $"Thread in {partition}",
                mainNode: threadPath,
                lastModified: threadLastModified ?? activityLastModified,
                content: new { createdBy = createThreadByUser },
                jsonOptions: jsonOptions, ct: ct);
        }
    }

    private static async Task InsertMeshNodeAsync(
        Npgsql.NpgsqlDataSource dataSource,
        string schema,
        string table,
        string namespacePart,
        string id,
        string nodeType,
        string name,
        string mainNode,
        DateTimeOffset lastModified,
        object? content,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        var contentJson = content is null ? null
            : JsonSerializer.Serialize(content, content.GetType(), jsonOptions);
        await using var cmd = dataSource.CreateCommand(
            $"""
            INSERT INTO "{schema}"."{table}"
                (namespace, id, name, node_type, last_modified, version, state, content, main_node)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9)
            ON CONFLICT (namespace, id) DO UPDATE SET
                name = EXCLUDED.name,
                node_type = EXCLUDED.node_type,
                last_modified = EXCLUDED.last_modified,
                version = EXCLUDED.version,
                state = EXCLUDED.state,
                content = EXCLUDED.content,
                main_node = EXCLUDED.main_node
            """);
        cmd.Parameters.AddWithValue(namespacePart);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue((object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue(nodeType);
        cmd.Parameters.AddWithValue(lastModified);
        cmd.Parameters.AddWithValue(1L);
        cmd.Parameters.AddWithValue((short)MeshNodeState.Active);
        cmd.Parameters.AddWithValue((object?)contentJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue(mainNode);
        await cmd.ExecuteNonQueryAsync(ct);
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
