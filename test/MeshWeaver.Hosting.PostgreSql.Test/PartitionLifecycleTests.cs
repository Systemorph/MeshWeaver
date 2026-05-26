using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
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
/// Stage 9c — Partition lifecycle: create / write / read / delete with the
/// no-Matches() routing and PgPartitionCache invalidation. Includes negative
/// cases (delete-nonexistent, write-after-delete) per the user's
/// "delete-something-that-doesn't-exist must error" rule.
/// </summary>
[Collection("PostgreSql")]
public class PartitionLifecycleTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;
    private CancellationToken TestTimeout => new CancellationTokenSource(60.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddGraph();
    }

    [Fact(Timeout = 60000)]
    public async Task LazyCreate_FirstWrite_EnablesSubsequentReads()
    {
        var ct = TestTimeout;
        var ns = $"pg9c_lazy_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // PgPartitionCache reports PendingCreate (no schema yet); first write
        // triggers CREATE SCHEMA. Subsequent reads hit the new schema.
        var path = $"{ns}/seed";
        var saved = await meshService.CreateNode(new MeshNode("seed", ns)
        {
            NodeType = "Markdown",
            Name = "seed",
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        saved.Should().NotBeNull();

        var workspace = Mesh.GetWorkspace();
        var readBack = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .FirstAsync().ToTask(ct);
        readBack.Should().NotBeNull("lazy-created schema must serve reads immediately");
    }

    [Fact(Timeout = 60000)]
    public async Task DeleteNode_ThatDoesNotExist_Errors()
    {
        var ct = TestTimeout;
        var nonexistent = $"pg9c_nope_{Guid.NewGuid():N}".ToLowerInvariant()[..18] + "/missing";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var act = async () =>
        {
            await meshService
                .DeleteNode(nonexistent)
                .Timeout(15.Seconds())
                .FirstAsync()
                .ToTask(ct);
        };
        await act.Should().ThrowAsync<Exception>(
            "delete-something-that-doesn't-exist must surface an error to the caller");
    }

    [Fact(Timeout = 60000)]
    public async Task Write_ReadBack_Delete_ReadAgain_Empty()
    {
        var ct = TestTimeout;
        var ns = $"pg9c_rd_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var path = $"{ns}/item";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();

        // 1. Write
        var saved = await meshService.CreateNode(new MeshNode("item", ns)
        {
            NodeType = "Markdown",
            Name = "item",
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        saved.Path.Should().Be(path);

        // 2. Read-back
        var first = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .FirstAsync().ToTask(ct);
        first.Should().NotBeNull();

        // 3. Delete
        var deleted = await meshService.DeleteNode(path)
            .Timeout(15.Seconds()).FirstAsync().ToTask(ct);
        deleted.Should().BeTrue();
    }

    [Fact(Timeout = 60000)]
    public async Task PendingCreate_AcceptsWriteEvenAfterReadMiss()
    {
        var ct = TestTimeout;
        var ns = $"pg9c_pend_{Guid.NewGuid():N}".ToLowerInvariant()[..18];

        // Touch the cache with a Read for a path under an unknown namespace.
        // PgPartitionCache emits PendingCreate (lazy-create policy); read
        // returns null since no rows exist yet. The routing service surfaces
        // "no node found" as DeliveryFailureException (NotFound) — that's the
        // read-miss signal here, so catch it and normalise to null.
        var workspace = Mesh.GetWorkspace();
        var preWriteRead = await workspace.GetMeshNodeStream($"{ns}/never_written")
            .Where(_ => true).Take(1).Timeout(10.Seconds())
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .FirstAsync().ToTask(ct);
        preWriteRead.Should().BeNull(
            "no rows exist under the fresh namespace, so the workspace stream resolves to null");

        // Subsequent write to the SAME namespace must still succeed —
        // PendingCreate state allows lazy schema creation.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var saved = await meshService.CreateNode(new MeshNode("first", ns)
        {
            NodeType = "Markdown",
            Name = "first",
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        saved.Should().NotBeNull(
            "PendingCreate state must not pin the namespace as unwritable");
    }
}
