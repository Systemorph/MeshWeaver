using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Documentation;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
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
/// Same wiring as <c>Memex.Portal.Distributed</c>: Orleans silo +
/// <c>AddPartitionedPostgreSqlPersistence</c> against the running local
/// Aspire <c>memex-postgres</c> container (connection string via
/// <c>MESHWEAVER_LOCAL_PG_CS</c> env var). Reproduces the exact prod
/// codepath that the running portal hits when you navigate to
/// <c>/{username}</c>.
///
/// <para>Pre-fix: <c>RoutingGrain</c> calls <c>IPathResolver.ResolvePath("rbuergi")</c>
/// → null → "No node found at 'rbuergi'". Post-fix: the per-partition lazy
/// information_schema lookup finds the rbuergi schema, the query returns
/// the User row, the route succeeds.</para>
///
/// <para>Skipped on CI (no Aspire DB). Run locally:</para>
/// <code>
/// set MESHWEAVER_LOCAL_PG_CS=Host=127.0.0.1;Port=...;Database=memex;Username=postgres;Password=...
/// dotnet test test/MeshWeaver.Hosting.Orleans.Test --filter "FullyQualifiedName~OrleansPostgresPathResolutionTest"
/// </code>
/// </summary>
public class OrleansPostgresPathResolutionTest(ITestOutputHelper output)
    : OrleansTestBase<OrleansPostgresPathResolutionTest.PostgresSiloConfigurator>(output)
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    [Fact(Timeout = 60000)]
    public async Task ResolvePath_UserPartition_InOrleansSilo()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        if (string.IsNullOrEmpty(connectionString))
        {
            Output.WriteLine(
                $"SKIPPED: set ${ConnectionStringEnvVar} to a running Postgres connection " +
                "string (e.g. the local Aspire `memex-postgres` container) to enable this test.");
            return;
        }

        var username = Environment.GetEnvironmentVariable("MESHWEAVER_LOCAL_USER") ?? "rbuergi";
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        // The TestCluster.ServiceProvider is the CLIENT SP — not where the
        // silo's IPartitionStorageProvider lives. Reach into the silo host
        // directly. This mirrors OrleansDynamicCompilationTest's pattern.
        var siloSp = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
        var pathResolver = siloSp.GetRequiredService<IPathResolver>();
        var storage = siloSp.GetService<IStorageAdapter>();
        Output.WriteLine($"DIAG storage adapter: {storage?.GetType().Name ?? "null"}");
        foreach (var p in siloSp.GetServices<IPartitionStorageProvider>())
            Output.WriteLine($"DIAG partition provider: {p.GetType().Name} Name={p.Name}");
        foreach (var q in siloSp.GetServices<IMeshQueryProvider>())
            Output.WriteLine($"DIAG query provider: {q.GetType().Name}");

        var resolution = await pathResolver.ResolvePath(username)
            .Take(1)
            .Timeout(30.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync()
            .ToTask(ct);

        Output.WriteLine(resolution is null
            ? $"FAILED: ResolvePath('{username}') → null. This is the prod symptom."
            : $"OK: Prefix={resolution.Prefix} Remainder={resolution.Remainder} NodeType={resolution.Node?.NodeType}");

        resolution.Should().NotBeNull(
            "the running portal's RoutingGrain hits this exact codepath. If null here, " +
            "/{0} shows 'No node found' in the browser.", username);
        resolution!.Prefix.Should().Be(username);
        resolution.Remainder.Should().BeNull();
        resolution.Node.Should().NotBeNull();
    }

    /// <summary>
    /// Silo configurator that mirrors the production
    /// <c>Memex.Portal.Distributed</c> wiring against the live Postgres DB
    /// supplied via env var.
    /// </summary>
    public class PostgresSiloConfigurator : ISiloConfigurator, IHostConfigurator
    {
        public static readonly string AssemblyStoreRoot =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mw-orleans-pg-{Guid.NewGuid():N}");

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
