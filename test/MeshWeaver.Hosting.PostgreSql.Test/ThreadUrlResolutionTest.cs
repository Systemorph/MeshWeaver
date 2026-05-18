using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// End-to-end repros for the prod outage:
/// <c>https://memex.meshweaver.cloud/rbuergi/_Thread/hello-2a76</c> never
/// finishes loading. The thread row exists in <c>rbuergi.threads</c> and is
/// returned by MCP search, yet the Blazor page hangs on the loading spinner.
///
/// <para>The Blazor router hands the URL to
/// <see cref="IPathResolver.ResolvePath(string)"/>. If resolution returns
/// null (or hangs), <c>AreaPage</c> can't bind a hub address and the layout
/// area subscription never fires. That is the symptom this suite reproduces
/// with a real PG-backed mesh — same shape as <c>Memex.Portal.Distributed</c>:
/// <see cref="AddPartitionedPostgreSqlPersistence(IServiceCollection, string)"/>,
/// no InMemory pollution.</para>
///
/// <para><b>What was missing in our coverage.</b> We had:</para>
/// <list type="bullet">
///   <item><see cref="ThreadPathResolutionTest"/> — exercises the per-schema
///     <c>PostgreSqlStorageAdapter</c> directly via <c>ReadAsync</c> /
///     <c>FindBestPrefixMatchAsync</c>. Passes even when the routing /
///     fan-out chain is broken upstream.</item>
///   <item><see cref="PathResolutionTests"/> — exercises the routing adapter
///     <c>ResolvePath</c> on its own.</item>
///   <item><see cref="PgOnlyProdShapeTests"/> — exercises
///     <see cref="IPathResolver"/> for a <c>_Access</c> satellite only.</item>
/// </list>
/// <para>None of the above hits <see cref="IPathResolver"/> for EVERY
/// satellite shape in <see cref="PartitionDefinition.StandardTableMappings"/>.
/// That is the gap.</para>
///
/// <para>The <see cref="Theory"/> below parameterises over every satellite
/// segment + matching <c>NodeType</c>. Two case shapes per type (mirroring
/// the original <c>_Thread</c> repro):</para>
/// <list type="number">
///   <item>Resolving the EXISTING satellite node returns it with the right
///     NodeType.</item>
///   <item>Resolving a NON-EXISTING satellite path under the same user does
///     NOT silently fall back to a wrong node (typical regression: returns
///     the partition-root User node, hiding the missing-thread state).</item>
/// </list>
/// </summary>
[Collection("PostgreSql")]
public class ThreadUrlResolutionTest(PostgreSqlFixture fixture, ITestOutputHelper output)
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
            .AddRowLevelSecurity()
            .AddGraph()
            // AI registers Thread / ThreadMessage NodeTypes (with satellite
            // routing rule + AI type-registry entries that <c>Thread</c>
            // content needs for the polymorphic deserializer). Without
            // these, MCP search returns the row but PathResolutionService's
            // ObserveQuery emits an unparseable <c>$type</c>-mismatched
            // payload and the merged Initial never lands.
            .AddAI();
    }

    /// <summary>
    /// Every satellite/code segment in
    /// <see cref="PartitionDefinition.StandardTableMappings"/>. The triple is
    /// <c>(segment, nodeType, contentNeeded)</c>:
    /// <list type="bullet">
    ///   <item><c>segment</c> — the path segment that pins the satellite
    ///     table (e.g. <c>_Thread</c>, <c>Source</c>).</item>
    ///   <item><c>nodeType</c> — the NodeType the stored row carries. Must
    ///     match what <c>PartitionDefinition.NodeTypeToSuffix</c> reverses.</item>
    ///   <item><c>contentNeeded</c> — when true, the satellite type requires
    ///     a concrete Content for the row to round-trip. Today only
    ///     <c>Thread</c> / <c>ThreadMessage</c> are gated this way (Thread
    ///     content is polymorphic and the row would refuse to write without
    ///     it); the other satellites accept Content=null.</item>
    /// </list>
    /// </summary>
    public static TheoryData<string, string, bool> SatelliteCases() => new()
    {
        // Threads share the "threads" table — both Thread + ThreadMessage map to _Thread routing.
        { "_Thread", "Thread", true },
        // _Activity → activities table
        { "_Activity", "Activity", false },
        // _UserActivity → user_activities table (per-user feed)
        { "_UserActivity", "UserActivity", false },
        // _Access → access table (the EffectivePermission regression we shipped)
        { "_Access", "AccessAssignment", false },
        // _Comment → annotations table
        { "_Comment", "Comment", false },
        // _Approval → annotations table (shared with comments)
        { "_Approval", "Approval", false },
        // _Tracking → annotations table (TrackedChange satellite)
        { "_Tracking", "TrackedChange", false },
        // Source / Test → code table. These are PRIMARY content (not
        // satellites), but routing-wise they share the satellite-routing
        // surface and the same resolve contract holds.
        { "Source", "Code", false },
        { "Test", "Code", false },
    };

    /// <summary>
    /// For each satellite/code segment in
    /// <see cref="PartitionDefinition.StandardTableMappings"/>: create the
    /// user partition root + a satellite node at <c>user/{segment}/{id}</c>,
    /// then ask <see cref="IPathResolver"/> to resolve that exact URL the
    /// way the Blazor router does. Assertions pin the contracts the prod
    /// page relies on: <c>Prefix</c> equals the full path, <c>Remainder</c>
    /// is null (exact match), and the matched node carries the right
    /// <c>NodeType</c> so the router picks the right layout area.
    /// </summary>
    [Theory(Timeout = 120000)]
    [MemberData(nameof(SatelliteCases))]
    public async Task ResolvePath_SatelliteByFullPath_ReturnsSatelliteNode(
        string segment, string nodeType, bool contentNeeded)
    {
        var ct = TestTimeout;
        var user = $"pg9b_sat_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var id = $"x{Guid.NewGuid():N}"[..10];
        var ns = $"{user}/{segment}";
        var fullPath = $"{ns}/{id}";

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using var _systemScope = accessService.ImpersonateAsSystem();

        await meshService.CreateNode(new MeshNode(user)
        {
            NodeType = "User",
            Name = user,
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        var satellite = new MeshNode(id, ns)
        {
            NodeType = nodeType,
            Name = id,
            MainNode = user,
            State = MeshNodeState.Active,
            Content = contentNeeded ? new MeshThread { CreatedBy = user } : null,
        };
        await meshService.CreateNode(satellite)
            .Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(fullPath)
            .Where(r => r is not null)
            .Take(1)
            .Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync().ToTask(ct);

        resolution.Should().NotBeNull(
            "Blazor navigation to /{0} must resolve to the {1} MeshNode — the prod " +
            "symptom that drove this test is the loading spinner that never finishes " +
            "because resolution returns null / never emits.", fullPath, nodeType);
        resolution!.Prefix.Should().Be(fullPath, "the URL maps exactly onto the stored node's path");
        resolution.Remainder.Should().BeNull("exact-match resolution leaves no trailing segments");
        resolution.Node.Should().NotBeNull("AreaPage reads NodeType off the matched node to pick the layout area");
        resolution.Node!.NodeType.Should().Be(nodeType,
            "without NodeType={0} the router can't bind the right layout area — " +
            "the page would fall through to the generic / 404 fallback", nodeType);
    }

    /// <summary>
    /// Companion contract: navigating to a non-existent satellite under the
    /// same User partition must NOT silently resolve to the User node (which
    /// would render the user dashboard instead of a 404 / not-found state).
    /// This is the failure mode where the user clicks an old / stale link
    /// and is dumped onto an unrelated page — invisible in CI until a
    /// customer complains.
    /// </summary>
    [Theory(Timeout = 120000)]
    [MemberData(nameof(SatelliteCases))]
    public async Task ResolvePath_MissingSatellite_DoesNotFallBackToWrongNodeType(
        string segment, string nodeType, bool _)
    {
        var ct = TestTimeout;
        var user = $"pg9b_miss_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var missingPath = $"{user}/{segment}/never-existed";

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using var _systemScope = accessService.ImpersonateAsSystem();

        await meshService.CreateNode(new MeshNode(user)
        {
            NodeType = "User",
            Name = user,
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(missingPath)
            .Take(1)
            .Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync().ToTask(ct);

        // We accept either null (best — caller renders 404) or a resolution
        // pointing at a non-satellite ancestor with the missing tail in
        // Remainder. What we MUST NOT do is return Prefix={missingPath} or a
        // matched node whose NodeType claims to be the satellite type —
        // that would render someone else's data.
        if (resolution is not null)
        {
            resolution.Prefix.Should().NotBe(missingPath,
                "we never created a {0} at this path", nodeType);
            if (resolution.Node is { NodeType: { } nt })
            {
                nt.Should().NotBe(nodeType,
                    "no {0} MeshNode lives at {1}", nodeType, missingPath);
            }
        }
    }

    /// <summary>
    /// Special-case for the prod URL <c>/rbuergi/_Thread/hello-2a76</c>:
    /// keep the exact path-shape the user clicked through. The parameterised
    /// theory covers <c>_Thread</c> via a generated id; this fixed-name test
    /// is the one a grep on the prod-symptom URL surfaces, so a regression
    /// is searchable by URL.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ResolvePath_ProdShape_UserSlashUnderscoreThreadSlashId_Resolves()
    {
        var ct = TestTimeout;
        var user = $"pg9b_prod_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var threadId = "hello-2a76";
        var threadPath = $"{user}/_Thread/{threadId}";

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using var _systemScope = accessService.ImpersonateAsSystem();

        await meshService.CreateNode(new MeshNode(user)
        {
            NodeType = "User",
            Name = user,
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        await meshService.CreateNode(new MeshNode(threadId, $"{user}/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            Name = "hello?",
            MainNode = user,
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = user },
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(threadPath)
            .Where(r => r is not null).Take(1).Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync().ToTask(ct);

        resolution.Should().NotBeNull(
            "this is the exact URL prod fails on: https://memex.meshweaver.cloud/{0}",
            threadPath);
        resolution!.Prefix.Should().Be(threadPath);
        resolution.Node!.NodeType.Should().Be(ThreadNodeType.NodeType);
    }

    /// <summary>
    /// Nested ThreadMessage URL — the 4-segment shape
    /// <c>user/_Thread/threadId/msgId</c> the prod delegation pattern emits.
    /// Both Thread and ThreadMessage live in the same <c>threads</c>
    /// satellite table; the routing must pick the right row for the deep
    /// path. Pre-fix the cross-schema UNION returned arbitrary rows in the
    /// threads table when the path-IN filter was missing; this test
    /// explicitly demonstrates the contract still holds after the fix.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ResolvePath_NestedThreadMessage_FourSegmentUrl_ReturnsMessageNode()
    {
        var ct = TestTimeout;
        var user = $"pg9b_msg_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var threadId = $"t{Guid.NewGuid():N}"[..10];
        var msgId = $"m{Guid.NewGuid():N}"[..10];
        var threadPath = $"{user}/_Thread/{threadId}";
        var msgPath = $"{threadPath}/{msgId}";

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using var _systemScope = accessService.ImpersonateAsSystem();

        await meshService.CreateNode(new MeshNode(user)
        {
            NodeType = "User",
            Name = user,
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        await meshService.CreateNode(new MeshNode(threadId, $"{user}/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            Name = "parent",
            MainNode = user,
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = user },
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        await meshService.CreateNode(new MeshNode(msgId, threadPath)
        {
            NodeType = "ThreadMessage",
            Name = "hello msg",
            MainNode = user,
            State = MeshNodeState.Active,
            Content = new ThreadMessage { Role = "user", Text = "hello" },
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(msgPath)
            .Where(r => r is not null).Take(1).Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync().ToTask(ct);

        resolution.Should().NotBeNull(
            "navigation to a 4-segment thread-message URL must resolve to the message node — " +
            "the prod delegation pattern emits this shape and the page must bind the right hub");
        resolution!.Prefix.Should().Be(msgPath, "the URL maps exactly onto the stored message's path");
        resolution.Node.Should().NotBeNull();
        resolution.Node!.NodeType.Should().Be("ThreadMessage");
    }
}
