using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Verifies the standard static-repo import contract (Doc/Architecture/StaticRepoImport.md): the
/// importer materializes a partition's children AND — as a STANDARD step — creates a proper
/// <c>Space</c> partition root, so the partition is routable + listable with a landing page. The
/// custom <see cref="IStaticRepoSource.PartitionRoot"/> welcome must round-trip through PG and the
/// only schema created must be the lowercased one (no ghost capital schema — the atioz regression).
/// </summary>
[Collection("PostgreSql")]
public class StaticRepoImporterTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    // Capitalized partition so the lowercase-schema invariant is observable: schema must be the
    // lowercased name, and the verbatim capital schema must NEVER be conjured.
    private readonly string _partition = "Srt" + Guid.NewGuid().ToString("N")[..10];

    private const string WelcomeMarker = "Explore the test repo";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString);
                services.AddSingleton<IStaticRepoSource>(new FakeRepoSource(_partition));
                return services;
            })
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType();
    }

    private IObservable<long> SchemaCount(string schema, CancellationToken ct) =>
        _fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s",
            new[] { ("s", (object)schema) }, ct);

    [Fact(Timeout = 120000)]
    public void Import_CreatesSpaceRoot_WithWelcome_LowercaseSchemaOnly()
    {
        var ct = TestContext.Current.CancellationToken;

        // First import: materializes the child + creates the Space root.
        var results = StaticRepoImporter.ImportAll(Mesh)
            .ToList().Should().Within(120.Seconds()).Emit();
        var mine = results.Single(r => r.Partition == _partition);
        mine.Outcome.Should().Be("Imported");

        // The Space partition root exists, served from PG, with the custom welcome reconstructed
        // onto PreRenderedHtml (the PG read fix) — what the Space Overview renders.
        var root = Mesh.GetMeshNodeStream(_partition)
            .Where(n => n is { NodeType: "Space" })
            .Should().Within(30.Seconds()).Emit();
        root.Should().NotBeNull();
        root!.NodeType.Should().Be("Space");
        root.PreRenderedHtml.Should().NotBeNullOrWhiteSpace(
            "the Space Overview renders MeshNode.PreRenderedHtml — it must round-trip through PG");
        root.PreRenderedHtml.Should().Contain(WelcomeMarker);

        // Only the lowercased schema was created — never the verbatim capital one (atioz ghost).
        SchemaCount(_partition.ToLowerInvariant(), ct).Should().Within(30.Seconds()).Be(1L);
        SchemaCount(_partition, ct).Should().Within(30.Seconds()).Be(0L);

        // Re-run with the same source is idempotent — it must NOT re-import. Either "Skipped"
        // (short-circuit saw the Succeeded activity) or "AlreadyRunning" (the eventually-consistent
        // short-circuit query raced the fire-and-forget MarkSucceeded and fell through to the
        // content-addressed CreateNode lock, which faulted benignly) — both mean "no re-import".
        var second = StaticRepoImporter.ImportAll(Mesh)
            .ToList().Should().Within(60.Seconds()).Emit();
        second.Single(r => r.Partition == _partition).Outcome.Should().NotBe("Imported");
    }

    /// <summary>One child page + a custom Space root with a welcome marker — the Doc-style shape.</summary>
    private sealed class FakeRepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        [
            new MeshNode("Page1", partition)
            {
                NodeType = "Markdown",
                Name = "Page 1",
                State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "# Page 1\n\nA page." }
            }
        ];

        public MeshNode? PartitionRoot => new(partition)
        {
            Name = "Test Repo",
            NodeType = "Space",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# Test Repo\n\n{WelcomeMarker} or start a chat." }
        };
    }
}
