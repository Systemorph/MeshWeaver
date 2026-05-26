using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Realistic path-resolution test against an EXISTING running Postgres
/// (the local Aspire <c>memex-postgres</c> container or any pre-populated
/// instance whose connection string is provided via
/// <c>MESHWEAVER_LOCAL_PG_CS</c>). Unlike <see cref="UserPartitionResolutionTests"/>
/// this does NOT pre-register the partition via <c>RegisterPartition</c> —
/// it lets the runtime subscription wire up exactly like prod
/// (<see cref="PostgreSqlPartitionSubscriptionHostedService"/> watches
/// <c>Admin/Partition/*</c> and populates <c>_partitions</c>).
///
/// <para>If the test fails: that's the prod symptom reproduced. The user
/// schema exists, the <c>Admin/Partition/{username}</c> row exists in
/// admin.mesh_nodes, but <c>IPathResolver.ResolvePath("{username}")</c>
/// returns null because the subscription hasn't propagated to the silo /
/// SP that's doing the resolve.</para>
///
/// <para>Skipped when the env var is unset so CI (which has no local
/// Aspire DB) doesn't fail. Run locally via:</para>
/// <code>
/// set MESHWEAVER_LOCAL_PG_CS=Host=127.0.0.1;Port=59327;Database=memex;Username=postgres;Password=...
/// dotnet test test/MeshWeaver.Hosting.PostgreSql.Test --filter "FullyQualifiedName~LocalAspirePathResolutionTests"
/// </code>
/// </summary>
public class LocalAspirePathResolutionTests : MonolithMeshTestBase
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    public LocalAspirePathResolutionTests(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>
    /// Mirrors the prod portal's persistence + graph wiring. Reads the
    /// connection string inside this method (NOT a field initializer) because
    /// <see cref="MonolithMeshTestBase"/>'s base constructor invokes
    /// <c>ConfigureMesh</c> BEFORE the derived constructor body runs — any
    /// field set in <c>: base(output)</c> body initialization is still null
    /// at this point.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
            ?? "Host=localhost;Database=test;Username=postgres;Password=postgres";
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(connectionString))
            .AddGraph();
    }

    /// <summary>
    /// Resolve <c>{username}</c> (the username read from
    /// <c>MESHWEAVER_LOCAL_USER</c>, default <c>rbuergi</c>) against the
    /// running DB. The partition must already exist (rows in
    /// <c>admin.mesh_nodes</c> at <c>Admin/Partition/{username}</c> and
    /// <c>{username}.mesh_nodes</c> at <c>ns='' id={username}</c>) — this
    /// test verifies the routing layer SEES it, not that the data exists.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ResolvePath_ExistingUserPartition_FromLiveSubscription()
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

        var pathResolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

        // Give the PostgreSqlPartitionSubscriptionHostedService a moment to
        // open the Admin/Partition stream and emit Initial. Without this
        // wait, ResolvePath races the subscription startup — exactly the
        // prod-startup race we're trying to surface.
        var resolution = await pathResolver.ResolvePath(username)
            .Where(r => r is not null)
            .Take(1)
            .Timeout(30.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync()
            .ToTask(ct);

        Output.WriteLine(resolution is null
            ? $"FAILED: ResolvePath('{username}') → null after 30s. This is the prod symptom."
            : $"OK: ResolvePath('{username}') → Prefix={resolution.Prefix} Remainder={resolution.Remainder} NodeType={resolution.Node?.NodeType}");

        resolution.Should().NotBeNull(
            "the prod portal hits this exact codepath on the user's home page — " +
            "if it returns null in this realistic setup, /{0} shows 'No node found' in the browser.", username);
        resolution!.Prefix.Should().Be(username);
        resolution.Node.Should().NotBeNull();
        resolution.Node!.NodeType.Should().Be("User",
            "post-V20 layout: the bare partition-root row is a User identity");
    }
}
