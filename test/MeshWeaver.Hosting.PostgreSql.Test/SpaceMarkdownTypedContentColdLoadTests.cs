using System;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// 🚨 Root-cause pin for the 2026-06-12 atioz "Markdown nodes under a Space render empty"
/// incident (image catalog2-20260612, <c>AgenticPension/Overview</c>): the symptom looked
/// like a load-path typing regression, but the COLD per-node-hub load path types content
/// correctly (first test, green throughout — it falsifies the load-path hypothesis and
/// stays as the regression guard). The ACTUAL defect: the rows in PG carried
/// <c>content.$type = "MarkdownConfiguration"</c> — an agent-invented discriminator that
/// exists in NO registry (the registered Markdown content type is
/// <see cref="MarkdownContent"/>). The write boundary accepted it silently; from then on
/// every load correctly degraded the content to an untyped
/// <see cref="System.Text.Json.JsonElement"/> (bad-data tolerance) — node renders EMPTY,
/// <c>edit_content</c> refuses ("content is JsonElement, not editable text"), recycle
/// can't heal, merge-patches keep the broken discriminator alive. Second test pins the
/// root fix: such a create must FAIL CLOSED with a speaking error
/// (<c>ContentDiscriminatorValidator</c>).
/// <para>
/// First test, full cycle: create Space → create Markdown child with TYPED
/// <see cref="MarkdownContent"/> → verify the PG row carries <c>$type</c> → dispose the
/// per-node hub (exactly what MCP <c>recycle</c> posts) → read back through the freshly
/// activated hub → the content MUST come back as <see cref="MarkdownContent"/>.
/// </para>
/// </summary>
[Collection("PostgreSql")]
public class SpaceMarkdownTypedContentColdLoadTests(PostgreSqlFixture fixture, ITestOutputHelper output)
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
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType();
    }

    [Fact(Timeout = 120000)]
    public async Task MarkdownChild_UnderSpace_ColdHubReload_ContentStaysTyped()
    {
        var ct = TestContext.Current.CancellationToken;
        var spaceId = $"pgtyped{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // 1. Space partition root — same shape as AgenticPension on atioz.
        var space = await meshService.CreateNode(new MeshNode(spaceId)
        {
            NodeType = SpaceNodeType.NodeType,
            Name = spaceId,
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Should().Within(45.Seconds()).Emit();
        space.Path.Should().Be(spaceId);

        // 2. Markdown child with TYPED MarkdownContent (the AgenticPension/Overview shape).
        var childPath = $"{spaceId}/Overview";
        var child = await meshService.CreateNode(new MeshNode("Overview", spaceId)
        {
            NodeType = "Markdown",
            Name = "Overview",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = "# Overview\n\nTyped body." },
        }).Should().Within(30.Seconds()).Emit();
        child.Path.Should().Be(childPath);

        // 3. The persisted row must carry the polymorphic discriminator — pins that the
        //    WRITE was correct (the atioz verification: $type present in PG). The save is
        //    debounced (200 ms), so poll the row reactively until it lands.
        var discriminator = await Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
            .SelectMany(_ => _fixture.DataSource.Probe(
                    $"""SELECT content->>'$type' FROM "{spaceId}".mesh_nodes WHERE id = 'Overview'""",
                    [],
                    rdr => rdr.IsDBNull(0) ? null : rdr.GetString(0), ct)
                .Catch((Exception _) => Observable.Return<string?>(null)))
            .Where(t => t is not null)
            .Should().Within(30.Seconds()).Emit();
        discriminator.Should().Be(nameof(MarkdownContent),
            "the persisted JSON must be self-describing — without $type nothing can type it on reload");

        // 4. Warm baseline: a read against the still-live per-node hub must be typed.
        //    (This is the read the existing suites cover — it does NOT exercise the
        //    cold load path the portal hits after a pod restart / recycle.)
        var warm = await Mesh.GetMeshNodeStream(childPath)
            .Where(n => n is not null).Take(1)
            .Should().Within(30.Seconds()).Emit();
        warm!.Content.Should().BeOfType<MarkdownContent>("the warm read serves the typed in-process content");

        // 5. COLD reload through the portal's own path: dispose the per-node hub
        //    (exactly what MCP `recycle` posts), wait until it is actually gone, then
        //    read back — the fresh activation runs per-node-hub init → MeshDataSource →
        //    storage-adapter read from PG. Routing hosts the hub under the SEGMENTED
        //    address (Address(prefix.Split('/'))), so probe with the same key.
        var hostedAddress = new Address(childPath.Split('/'));
        var hubBefore = Mesh.GetHostedHub(hostedAddress, HostedHubCreation.Never);
        hubBefore.Should().NotBeNull("the warm read activates the per-node hub");

        Mesh.Post(new DisposeRequest(), o => o.WithTarget(hostedAddress));

        await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Select(_ => Mesh.GetHostedHub(hostedAddress, HostedHubCreation.Never))
            .Should().Within(30.Seconds()).Match(
                h => h is null
                    || !ReferenceEquals(h, hubBefore)
                    || h.RunLevel > MessageHubRunLevel.Started,
                "DisposeRequest must tear the per-node hub down so the next read is COLD");

        // 6. Owner round-trip read (GetDataRequest → freshly activated hub). Retry on a
        //    cadence: a read landing while the old hub is mid-teardown can fail or return
        //    null until routing re-creates the hub.
        var reloaded = await Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
            .SelectMany(_ => ReadNode(childPath))
            .Where(n => n is not null)
            .Should().Within(60.Seconds()).Emit();

        reloaded!.Content.Should().BeOfType<MarkdownContent>(
            "a cold per-node-hub load must resolve the $type discriminator back to the registered " +
            $"domain type; got '{reloaded.Content?.GetType().Name ?? "null"}' — the atioz symptom " +
            "was an untyped JsonElement that rendered the node empty");
        ((MarkdownContent)reloaded.Content!).Content.Should().Contain("Typed body.");
    }

    /// <summary>
    /// 🚨 The ACTUAL atioz defect, pinned at its root: the write boundary ACCEPTED a
    /// Markdown-node create whose content carried the agent-invented discriminator
    /// <c>$type: "MarkdownConfiguration"</c> — a type that exists in NO registry (the
    /// registered Markdown content type is <see cref="MarkdownContent"/>). Once
    /// persisted, the content is un-typeable forever: every load degrades it to an
    /// untyped JsonElement, the node renders empty, <c>edit_content</c> refuses, and
    /// recycling can't heal it. The write must FAIL CLOSED with a speaking error that
    /// names the bad discriminator, so the writing agent corrects the shape immediately.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task MarkdownChild_CreateWithUnresolvableContentDiscriminator_FailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var spaceId = $"pgbadt{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Space partition root — valid.
        await meshService.CreateNode(new MeshNode(spaceId)
        {
            NodeType = SpaceNodeType.NodeType,
            Name = spaceId,
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Should().Within(45.Seconds()).Emit();

        // The exact shape the atioz agent submitted (MCP create deserializes the
        // node JSON on the hub; an unknown $type lands as a raw JsonElement).
        var agentShape = JsonSerializer.Deserialize<JsonElement>(
            """{"$type":"MarkdownConfiguration","markdown":"# Broken\n\nNever typeable."}""");

        var notification = await meshService.CreateNode(new MeshNode("BrokenOverview", spaceId)
            {
                NodeType = "Markdown",
                Name = "Broken Overview",
                State = MeshNodeState.Active,
                Content = agentShape,
            })
            .Take(1)
            .Materialize()
            .Should().Within(30.Seconds())
            .Match(n => n.Kind == System.Reactive.NotificationKind.OnError);
        notification.Exception.Should().NotBeNull(
            "a Markdown create whose content discriminator resolves to NO registered type must fail " +
            "closed — accepting it persists an untyped blob that renders empty and cannot be edited " +
            "(the atioz 2026-06-12 '$type: MarkdownConfiguration' regression)");
        notification.Exception!.Message.Should().Contain("MarkdownConfiguration",
            "the rejection must name the unresolvable discriminator so the caller can fix the shape");

        // And nothing may have leaked into the partition's main table.
        var rows = await _fixture.DataSource.ScalarLong(
                $"""SELECT count(*) FROM "{spaceId}".mesh_nodes WHERE id = 'BrokenOverview'""", ct)
            .Should().Within(20.Seconds()).Emit();
        rows.Should().Be(0L, "a rejected create must not write a row");
    }
}
