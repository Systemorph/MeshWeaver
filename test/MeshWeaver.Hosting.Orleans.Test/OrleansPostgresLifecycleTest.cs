using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Documentation;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Stage 9e — Orleans 2-silo cross-silo partition lifecycle:
///
/// <list type="number">
///   <item>Partition created on Silo A → visible on Silo B within seconds
///     (pg_notify partition_changes invalidates B's PgPartitionCache, next
///     read re-probes information_schema and finds the new schema).</item>
///   <item>Same partition's bare path resolves on both silos.</item>
///   <item>Round-trips a write through one silo's hub and read through the
///     other's — partition cache must be consistent across silos.</item>
/// </list>
///
/// <para>Both silos use <see cref="PostgresLifecycleSiloConfigurator"/>
/// pointing at the same Postgres DB. The PgPartitionNotifyListener wired
/// in PostgreSqlExtensions runs on both silos so both LISTEN
/// partition_changes.</para>
/// </summary>
public class OrleansPostgresLifecycleTest(ITestOutputHelper output)
    : OrleansTestBase<OrleansPostgresLifecycleTest.PostgresLifecycleSiloConfigurator>(output)
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    protected override short InitialSilosCount => 2;

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

    [Fact(Timeout = 60000)]
    public async Task CreatePartition_OnSiloA_Visible_OnSiloB()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        var ns = $"pg9e_{Guid.NewGuid():N}".ToLowerInvariant()[..14];

        // Take SP from each silo separately so we exercise cross-silo state.
        var siloA = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
        var siloB = ((InProcessSiloHandle)Cluster.Silos[1]).SiloHost.Services;

        var meshA = siloA.GetRequiredService<IMeshService>();
        var resolverB = siloB.GetRequiredService<IPathResolver>();

        // Silo A creates the partition via the canonical Admin/Partition write
        // + a bare partition-root node (mirrors UserOnboardingService).
        await meshA.CreateNode(new MeshNode(ns, "Admin/Partition")
        {
            NodeType = "Partition",
            Name = ns,
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = ns,
                DataSource = "default",
                Schema = ns,
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.StandardTableMappings,
                Versioned = true,
            },
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        await meshA.CreateNode(new MeshNode(ns)
        {
            NodeType = "User",
            Name = ns,
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        // Silo B — within seconds (pg_notify invalidation + re-probe), the
        // bare partition root must be routable.
        var resolution = await resolverB.ResolvePath(ns)
            .Where(r => r is not null)
            .Take(1)
            .Timeout(45.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync()
            .ToTask(ct);

        resolution.Should().NotBeNull(
            "partition created on silo A must be visible on silo B via pg_notify partition_changes");
        resolution!.Prefix.Should().Be(ns);
    }

    [Fact(Timeout = 60000)]
    public async Task Write_OnSiloA_Read_OnSiloB()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        var ns = $"pg9e_rw_{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var siloA = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
        var siloB = ((InProcessSiloHandle)Cluster.Silos[1]).SiloHost.Services;

        var meshA = siloA.GetRequiredService<IMeshService>();
        var path = $"{ns}/note";
        var saved = await meshA.CreateNode(new MeshNode("note", ns)
        {
            NodeType = "Markdown",
            Name = "note",
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        saved.Should().NotBeNull();

        // Silo B's adapter must see the row (Postgres mesh_node_changes
        // broadcast + workspace catalog plus the schema-existence
        // invalidation from partition_changes).
        var storageB = siloB.GetRequiredService<IStorageAdapter>();
        MeshNode? readBack = null;
        for (var i = 0; i < 30 && readBack is null; i++)
        {
            try { readBack = await storageB.Read(path, System.Text.Json.JsonSerializerOptions.Default).FirstAsync().ToTask(ct); }
            catch { /* during partition init readBack may transiently throw */ }
            if (readBack is null) await Task.Delay(500, ct);
        }
        readBack.Should().NotBeNull(
            "after partition_changes invalidation + mesh_node_changes propagation, silo B must see the write");
    }

    public class PostgresLifecycleSiloConfigurator : ISiloConfigurator, IHostConfigurator
    {
        public static readonly string AssemblyStoreRoot =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mw-orleans-pg9e-{Guid.NewGuid():N}");

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
