using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
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
/// Stage 9b — Mixed providers as the prod portal stacks them: PG writable
/// catch-all + EmbeddedResource(Doc) read-only seed. Verifies the
/// no-Matches() try-then-claim routing: read fan-out picks the correct
/// source; writes that land on PG-claimed namespaces don't accidentally
/// route to a read-only provider; writes to unknown namespaces lazy-create
/// the PG schema.
/// </summary>
[Collection("PostgreSql")]
public class MixedPgStaticTests(PostgreSqlFixture fixture, ITestOutputHelper output)
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
            .AddGraph()
            .AddDocumentation();
    }

    [Fact(Timeout = 60000)]
    public async Task Read_Doc_FromEmbedded_Not_PG()
    {
        var ct = TestTimeout;
        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath("Doc")
            .Where(r => r is not null).Take(1).Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync().ToTask(ct);
        resolution.Should().NotBeNull("EmbeddedResourcePartitionStorageProvider owns /Doc");
        resolution!.Prefix.Should().Be("Doc");
    }

    [Fact(Timeout = 60000)]
    public async Task Write_FreshPartition_RoutesToPg()
    {
        var ct = TestTimeout;
        var ns = $"pg9b_{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode("note", ns)
        {
            NodeType = "Markdown",
            Name = "note",
            State = MeshNodeState.Active,
        };
        var saved = await meshService.CreateNode(node).Timeout(15.Seconds()).FirstAsync().ToTask(ct);
        saved.Should().NotBeNull(
            "writable PG catch-all must accept a fresh namespace; static read-only providers must decline");
        saved.Path.Should().Be($"{ns}/note");

        var workspace = Mesh.GetWorkspace();
        var readBack = await workspace.GetMeshNodeStream($"{ns}/note")
            .Where(n => n is not null).Take(1).Timeout(10.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .FirstAsync().ToTask(ct);
        readBack.Should().NotBeNull("read-back from PG must see the new node");
    }
}
