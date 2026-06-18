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
using MeshWeaver.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Verifies the static-repo import contract (Doc/Architecture/StaticRepoImport.md):
/// <list type="bullet">
///   <item>materializes every source node (children + satellites) through the SINGLE canonical
///     verb <c>CreateOrUpdateNodeRequest</c> — content + prerender persisted, round-tripped through PG;</item>
///   <item>creates a proper <c>Space</c> partition root as a standard step (welcome page);</item>
///   <item>provisions ONLY the lowercased schema (no verbatim/capital ghost — the atioz regression);</item>
///   <item>is idempotent (re-run with unchanged source = no-op);</item>
///   <item>on a CHANGED source, <b>updates existing nodes and increments their Version</b> (the bug the
///     old stream-<c>Overwrite</c> path hit: re-asserting the same Version was dropped as not-newer);</item>
///   <item>fills content over a pre-existing content-NULL row (the migration-backfill shadow → the
///     exact "/Doc shows no content" repro);</item>
///   <item>prunes nodes absent from the source (full-replace).</item>
/// </list>
/// </summary>
[Collection("PostgreSql")]
public class StaticRepoImporterTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    private const string WelcomeMarker = "Explore the test repo";

    // Mutable source so one test can re-import a CHANGED repo (drives the update/prune cases).
    // The field initializer uses no instance members (CS0236-safe) and runs before the base ctor
    // calls ConfigureMesh, so _source is ready in time. Capitalized partition so the lowercase-schema
    // invariant is observable. _partition is a property (reads _source) to avoid the field-order trap.
    private readonly FakeRepoSource _source = NewSource("Srt" + Guid.NewGuid().ToString("N")[..10]);
    private string _partition => _source.Partition;

    private static FakeRepoSource NewSource(string partition) => new(partition)
    {
        Root = new MeshNode(partition)
        {
            Name = "Test Repo", NodeType = "Space", State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# Test Repo\n\n{WelcomeMarker} or start a chat." }
        },
        Nodes =
        [
            new MeshNode("Page1", partition)
            {
                NodeType = "Markdown", Name = "Page 1", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "# Page 1\n\nA page." }
            }
        ]
    };

    private IObservable<long> SchemaCount(string schema, CancellationToken ct) =>
        _fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s",
            new[] { ("s", (object)schema) }, ct);

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
                services.AddSingleton<IStaticRepoSource>(_source);
                return services;
            })
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType();
    }

    private Task<IList<StaticRepoImportResult>> Import() =>
        StaticRepoImporter.ImportAll(Mesh).ToList().Should().Within(120.Seconds()).Emit();

    private Task<MeshNode?> Read(string path) =>
        Mesh.GetMeshNodeStream(path).Where(n => n is not null).Should().Within(30.Seconds()).Emit();

    [Fact(Timeout = 120000)]
    public async Task Import_CreatesSpaceRoot_AndChildContent_LowercaseSchemaOnly()
    {
        var ct = TestContext.Current.CancellationToken;

        (await Import()).Single(r => r.Partition == _partition).Outcome.Should().Be("Imported");

        // Space root persisted, welcome reconstructed onto PreRenderedHtml (PG read mirror).
        var root = await Mesh.GetMeshNodeStream(_partition).Where(n => n is { NodeType: "Space" })
            .Should().Within(30.Seconds()).Emit();
        root!.PreRenderedHtml.Should().Contain(WelcomeMarker);

        // Child page persisted WITH content + prerender (the thing /Doc needs).
        var page = await Read($"{_partition}/Page1");
        page!.NodeType.Should().Be("Markdown");
        (page.Content as MarkdownContent)!.Content.Should().Contain("A page.");
        page.PreRenderedHtml.Should().NotBeNullOrWhiteSpace("markdown prerender must round-trip from PG");

        // Only the lowercased schema — never the verbatim capital one (atioz ghost).
        await SchemaCount(_partition.ToLowerInvariant(), ct).Should().Within(30.Seconds()).Be(1L);
        await SchemaCount(_partition, ct).Should().Within(30.Seconds()).Be(0L);

        // Idempotent re-run = no re-import.
        (await Import()).Single(r => r.Partition == _partition).Outcome.Should().NotBe("Imported");
    }

    /// <summary>
    /// The startup import runs with NO logged-in user (boot, grain activation, fan-out) — yet every
    /// write (the <c>_Activity/import-*</c> lock, the partition root, the content nodes) must still
    /// persist, because the import hub runs as <see cref="PostingIdentity.System"/>. Before the fix
    /// the import hub defaulted to <c>User</c>; on its own action block the caller's
    /// <c>ImpersonateAsSystem</c> AsyncLocal was absent, so the writes hit the never-null AccessContext
    /// guard and FAILED CLOSED — nothing landed, yet the parent kept the <c>LastCompilationActivityPath</c>
    /// / activity reference → progress readers subscribed to a non-existent node → the
    /// "NotFound for …/_Activity/import…" resubscribe storm (atioz 2026-06-18).
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task Import_WithNoUserIdentity_StillPersists_BecauseImportHubIsSystem()
    {
        // Drop the auto-logged-in user (MonolithMeshTestBase logs rbuergi in) — simulate the boot
        // import with no ambient identity.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(null);
        accessService.SetCircuitContext(null);

        // Reaching "Imported" already proves the _Activity/import-* lock CreateNode persisted under
        // System — a User import hub would have failed it closed (mis-reported AlreadyRunning / faulted).
        (await Import()).Single(r => r.Partition == _partition).Outcome.Should().Be(
            "Imported",
            "the import hub runs as System, so its activity-lock + node writes persist with no user");

        // Partition root + content land — only possible if the import hub's writes carried an identity.
        var root = await Mesh.GetMeshNodeStream(_partition).Where(n => n is { NodeType: "Space" })
            .Should().Within(30.Seconds()).Emit();
        root.Should().NotBeNull("the partition root must persist with no user — the import hub is System");
        (await Read($"{_partition}/Page1")).Should().NotBeNull(
            "imported content must persist under the System import hub, not fail closed");
    }

    [Fact(Timeout = 120000)]
    public async Task Reimport_ChangedContent_Updates_AndIncrementsVersion()
    {
        (await Import()).Single(r => r.Partition == _partition).Outcome.Should().Be("Imported");
        var v1 = (await Read($"{_partition}/Page1"))!;
        v1.Version.Should().BeGreaterThan(0);

        // Change the source → new fingerprint → re-import → CreateOrUpdate UPDATE path.
        _source.Nodes =
        [
            new MeshNode("Page1", _partition)
            {
                NodeType = "Markdown", Name = "Page 1", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "# Page 1\n\nEDITED body." }
            }
        ];
        (await Import()).Single(r => r.Partition == _partition).Outcome.Should().Be("Imported");

        var v2 = await Mesh.GetMeshNodeStream($"{_partition}/Page1")
            .Where(n => n?.Content is MarkdownContent mc && mc.Content.Contains("EDITED"))
            .Should().Within(30.Seconds()).Emit();
        v2!.Version.Should().BeGreaterThan(v1.Version,
            "the canonical CreateOrUpdate update must increment Version (the old stream-Overwrite re-asserted the same Version and was dropped)");
        (v2.Content as MarkdownContent)!.Content.Should().Contain("EDITED body.");
    }

    [Fact(Timeout = 120000)]
    public async Task Import_FillsContent_OverPreExistingContentNullRow()
    {
        // First import creates Page1 with content; then blank its content directly in PG to simulate
        // the migration backfill's content-NULL shadow row, and re-import: the upsert must REFILL it.
        await Import();
        (await Read($"{_partition}/Page1")).Should().NotBeNull();

        var schema = _partition.ToLowerInvariant();
        await _fixture.DataSource.ExecuteNonQuery(
            $"UPDATE \"{schema}\".mesh_nodes SET content = NULL WHERE id = 'Page1'",
            TestContext.Current.CancellationToken).Should().Within(30.Seconds()).Emit();

        // Change source content so the fingerprint differs → re-import runs over the NULL row.
        _source.Nodes =
        [
            new MeshNode("Page1", _partition)
            {
                NodeType = "Markdown", Name = "Page 1", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "# Page 1\n\nRefilled body." }
            }
        ];
        (await Import()).Single(r => r.Partition == _partition).Outcome.Should().Be("Imported");

        // Wait for the SPECIFIC refilled content, not just any MarkdownContent. The raw
        // `UPDATE content = NULL` above bypassed the workspace cache (psql writes don't
        // invalidate it), so the cache still holds the ORIGINAL MarkdownContent — a bare
        // `is MarkdownContent` predicate races and grabs that stale body before the
        // re-import propagates. Filtering on "Refilled body." makes the wait deterministic
        // (and still surfaces a genuine refill failure as a 30s timeout). Mirrors
        // Reimport_ChangedContent_Updates_AndIncrementsVersion.
        var refilled = await Mesh.GetMeshNodeStream($"{_partition}/Page1")
            .Where(n => n?.Content is MarkdownContent mc && mc.Content.Contains("Refilled body."))
            .Should().Within(30.Seconds()).Emit();
        (refilled!.Content as MarkdownContent)!.Content.Should().Contain("Refilled body.");
    }

    [Fact(Timeout = 120000)]
    public async Task Reimport_WithoutNode_PrunesIt()
    {
        _source.Nodes =
        [
            new MeshNode("Page1", _partition) { NodeType = "Markdown", Name = "Page 1", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "one" } },
            new MeshNode("Page2", _partition) { NodeType = "Markdown", Name = "Page 2", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "two" } }
        ];
        await Import();
        (await Read($"{_partition}/Page2")).Should().NotBeNull();

        // Drop Page2 from the source and re-import — full-replace must prune it.
        _source.Nodes =
        [
            new MeshNode("Page1", _partition) { NodeType = "Markdown", Name = "Page 1", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "one-still" } }
        ];
        (await Import()).Single(r => r.Partition == _partition).Outcome.Should().Be("Imported");

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var remaining = await meshService.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{_partition} scope:descendants"))
            .Take(1).Should().Within(30.Seconds()).Emit();
        remaining.Items.Should().NotContain(n => n.Id == "Page2", "Page2 was removed from the source → pruned");
    }

    /// <summary>Mutable in-memory repo: children + a customizable Space root.</summary>
    private sealed class FakeRepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; set; } = [];
        public MeshNode? Root { get; set; }
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
    }
}
