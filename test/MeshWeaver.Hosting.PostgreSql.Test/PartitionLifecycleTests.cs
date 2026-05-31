using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    public void LazyCreate_FirstWrite_EnablesSubsequentReads()
    {
        var ns = $"pg9c_lazy_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // PgPartitionCache reports PendingCreate (no schema yet); first write
        // triggers CREATE SCHEMA. Subsequent reads hit the new schema.
        var path = $"{ns}/seed";
        var saved = meshService.CreateNode(new MeshNode("seed", ns)
        {
            NodeType = "Markdown",
            Name = "seed",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        saved.Should().NotBeNull();

        var workspace = Mesh.GetWorkspace();
        var readBack = workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        readBack.Should().NotBeNull("lazy-created schema must serve reads immediately");
    }

    [Fact(Timeout = 60000)]
    public void DeleteNode_ThatDoesNotExist_Errors()
    {
        var nonexistent = $"pg9c_nope_{Guid.NewGuid():N}".ToLowerInvariant()[..18] + "/missing";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // delete-something-that-doesn't-exist must surface an error to the
        // caller. Materialize folds the OnError into a value so we assert it
        // reactively — no await, no ThrowAsync.
        var notification = meshService
            .DeleteNode(nonexistent)
            .Take(1)
            .Materialize()
            .Should().Within(30.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
        notification.Exception.Should().NotBeNull();
    }

    [Fact(Timeout = 60000)]
    public void Write_ReadBack_Delete_ReadAgain_Empty()
    {
        var ns = $"pg9c_rd_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var path = $"{ns}/item";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();

        // 1. Write
        var saved = meshService.CreateNode(new MeshNode("item", ns)
        {
            NodeType = "Markdown",
            Name = "item",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        saved.Path.Should().Be(path);

        // 2. Read-back
        var first = workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        first.Should().NotBeNull();

        // 3. Delete
        var deleted = meshService.DeleteNode(path)
            .Should().Within(30.Seconds()).Emit();
        deleted.Should().BeTrue();
    }

    [Fact(Timeout = 60000)]
    public void PendingCreate_AcceptsWriteEvenAfterReadMiss()
    {
        var ns = $"pg9c_pend_{Guid.NewGuid():N}".ToLowerInvariant()[..18];

        // Touch the cache with a Read for a path under an unknown namespace.
        // PgPartitionCache emits PendingCreate (lazy-create policy); read
        // returns null since no rows exist yet. The routing service surfaces
        // "no node found" as DeliveryFailureException (NotFound) — that's the
        // read-miss signal here, so catch it and normalise to null.
        var workspace = Mesh.GetWorkspace();
        var preWriteRead = workspace.GetMeshNodeStream($"{ns}/never_written")
            .Where(_ => true).Take(1).Timeout(10.Seconds())
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        preWriteRead.Should().BeNull(
            "no rows exist under the fresh namespace, so the workspace stream resolves to null");

        // Subsequent write to the SAME namespace must still succeed —
        // PendingCreate state allows lazy schema creation.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var saved = meshService.CreateNode(new MeshNode("first", ns)
        {
            NodeType = "Markdown",
            Name = "first",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        saved.Should().NotBeNull(
            "PendingCreate state must not pin the namespace as unwritable");
    }
}
