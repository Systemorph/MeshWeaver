using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// Partition lifecycle under the <b>no-lazy-create</b> contract: a partition's schema is
/// created ONLY by provisioning a partition-owning object (User / Space) — modelled in these
/// tests by <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/>, exactly what
/// <c>OwnsPartitionProvisioningValidator</c> calls. A write into a partition that was never
/// provisioned is <b>refused</b> (faults), never lazily schema-created (the atioz ghost-schema
/// fix). Once provisioned, ordinary write / read / delete work. See
/// <c>Doc/Architecture/PartitionStorageRouting.md</c>.
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
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddGraph();
    }

    /// <summary>
    /// Provision a partition the platform way — run every storage provider's
    /// <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/> (PG → the
    /// <c>ensure_partition_schema</c> DDL). This is the schema-creation a Space/User performs
    /// on create. Tests may block on the composed observable (§2a).
    /// </summary>
    private void ProvisionPartition(string ns) =>
        Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(p => p.EnsurePartitionProvisioned(ns))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .ToTask()
            .GetAwaiter()
            .GetResult();

    [Fact(Timeout = 60000)]
    public void ProvisionedPartition_Write_EnablesSubsequentReads()
    {
        var ns = $"pg9c_prov_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        ProvisionPartition(ns);   // the one schema-creation path (what a Space/User create does)

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
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
        readBack.Should().NotBeNull("a provisioned partition serves reads immediately");
    }

    [Fact(Timeout = 60000)]
    public void UnprovisionedPartition_Write_IsRefused_NoGhostSchema()
    {
        // No lazy create: writing into a partition that was never provisioned must NOT
        // conjure a schema — the write faults ("no partition, no write").
        var ns = $"pg9c_unprov_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var notification = meshService
            .CreateNode(new MeshNode("orphan", ns)
            {
                NodeType = "Markdown",
                Name = "orphan",
                State = MeshNodeState.Active,
            })
            .Take(1)
            .Materialize()
            .Should().Within(30.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
        notification.Exception.Should().NotBeNull();

        // 🚨 The invariant: no ghost schema was conjured for the unprovisioned namespace.
        _fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s",
            new[] { ("s", (object)ns) })
            .Should().Within(30.Seconds()).Be(0L);
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
        ProvisionPartition(ns);

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
}
