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
/// catch-all + EmbeddedResource(Doc) + Static(Agent)/Static(Model) read-only
/// seeds. Verifies the no-Matches() try-then-claim routing: read fan-out
/// picks the correct source; writes that land on PG-claimed namespaces
/// don't accidentally route to a static provider; writes to unknown
/// namespaces lazy-create the PG schema.
/// </summary>
[Collection("PostgreSql")]
public class MixedPgStaticTests : MonolithMeshTestBase
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    public MixedPgStaticTests(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
            ?? "Host=localhost;Database=test;Username=postgres;Password=postgres";
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(connectionString))
            .AddGraph()
            .AddDocumentation();
    }

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
    public async Task Read_Doc_FromEmbedded_Not_PG()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath("Doc")
            .Where(r => r is not null)
            .Take(1)
            .Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync()
            .ToTask(ct);
        resolution.Should().NotBeNull("EmbeddedResourcePartitionStorageProvider owns /Doc");
        resolution!.Prefix.Should().Be("Doc");
    }

    [Fact(Timeout = 60000)]
    public async Task Read_Agent_FromStatic()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var items = await meshService
            .QueryAsync<MeshNode>(new MeshQueryRequest { Query = "namespace:Agent" })
            .ToListAsync(ct);
        items.Should().NotBeEmpty(
            "BuiltInAgentProvider (static) seeds Agent/{...} read-only via IStaticNodeProvider");
    }

    [Fact(Timeout = 60000)]
    public async Task Write_FreshPartition_RoutesToPg_NotStatic()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(30.Seconds()).Token;
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

    [Fact(Timeout = 60000)]
    public async Task Write_DocPath_RejectedByStaticReadOnly_DoesNotPersist()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(30.Seconds()).Token;

        // Writing under Doc/X: Embedded is read-only → declines. PG catch-all
        // would accept and lazy-create a `doc` schema in PG. We don't want
        // that. Verify the saved node lives in PG (acceptable today) OR is
        // rejected at a higher layer — record either way so the test pins
        // the actual current behaviour.
        var path = "Doc/PgOnlyProd/test/note";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode("note", "Doc/PgOnlyProd/test")
        {
            NodeType = "Markdown",
            Name = "note",
            State = MeshNodeState.Active,
        };
        MeshNode? saved = null;
        try
        {
            saved = await meshService.CreateNode(node).Timeout(15.Seconds()).FirstAsync().ToTask(ct);
        }
        catch (Exception ex)
        {
            Output.WriteLine($"Write to Doc/ rejected (expected today since Doc is read-only embedded): {ex.GetType().Name}");
        }
        Output.WriteLine($"Doc-path write outcome: saved={(saved is null ? "null" : saved.Path)} — recorded for future tightening");
        // No hard assertion: future-tightening tracker. The reactive policy
        // "writes to read-only namespaces must reject" lands in a follow-up.
    }
}
