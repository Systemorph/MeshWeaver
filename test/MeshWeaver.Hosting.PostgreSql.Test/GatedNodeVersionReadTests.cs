using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
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

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// 🚨 PINS THE PAYWALL ON THE VERSION-HISTORY READ.
///
/// <para>The per-partition version tables (<c>mesh_node_history</c>) are a SECOND read surface
/// for a node's FULL content — every historical snapshot carries metadata + <c>Content</c>. The
/// exact-path read (<see cref="GatedNodeExactPathReadTests"/> / <see cref="PaywallRealGateShapeTests"/>),
/// queries and diagnostics all enforce the per-user Read gate and mask denial as absence; the
/// version read path (<c>VersionPlugin.GetVersions/GetVersion</c>, backing the MCP
/// <c>get_versions</c> / <c>get_version</c> tools) went straight to <c>IVersionQuery</c> with NO
/// gate, so any authenticated user who knew or guessed a path could read paywalled content
/// (including compile internals) out of history. Found during the #1105/#1130 investigation.</para>
///
/// <para>The fix routes every version read through the SAME effective-permission predicate as
/// the live read (<c>hub.GetEffectivePermissions</c> requiring <see cref="Permission.Read"/>)
/// and masks denial as the EXACT absence answer — "No version history found…" / "Version N not
/// found…" — so a deny is indistinguishable from a missing node and the version tools never
/// become an existence oracle for gated paths.</para>
///
/// <para>The gate is seeded in the REAL production shape — <c>AccessAssignment</c> NODES, the
/// same nodes <c>PluginGate</c> writes — because the evaluator the read gate consults folds
/// assignment nodes, not the fixture's SQL <c>access_control</c> shortcut (see
/// <see cref="PaywallRealGateShapeTests"/> for why that fidelity IS the test).</para>
/// </summary>
[Collection("PostgreSql")]
public class GatedNodeVersionReadTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Buyer = "vgated_buyer";      // entitled
    private const string Visitor = "vgated_visitor";  // signed in, bought nothing
    private const string SecretName = "PaidLessonSecretName";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString)
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

    // Per test instance, never static — every [Fact] gets a fresh mesh, so the gate must be
    // seeded and folded on the mesh that reads it (see PaywallRealGateShapeTests).
    private string Course { get; } = "GatedVer" + Guid.NewGuid().ToString("N")[..8];
    private string Gated => $"{Course}/PaidLesson";

    /// <summary>
    /// A Viewer AccessAssignment node in EXACTLY the shape <c>PluginGate.ViewerAssignment</c>
    /// writes — same fidelity as <see cref="PaywallRealGateShapeTests"/>.
    /// </summary>
    private static MeshNode ViewerAssignment(string scope, string subject, bool denied)
    {
        var role = new JsonObject { ["$type"] = "RoleAssignment", ["role"] = "Viewer" };
        if (denied)
            role["denied"] = true;
        return new MeshNode($"{subject}_Access", $"{scope}/_Access")
        {
            Name = denied ? $"{subject} — Viewer DENIED (gated)" : $"{subject} — Viewer",
            NodeType = "AccessAssignment",
            MainNode = scope,
            Content = new JsonObject
            {
                ["$type"] = "AccessAssignment",
                ["accessObject"] = subject,
                ["displayName"] = subject,
                ["roles"] = new JsonArray { role },
            },
        };
    }

    /// <summary>
    /// Seeds the course with the full PluginGate shape (public cover, Public/Anonymous DENY on
    /// the paid child, buyer's root Viewer grant) and blocks until the evaluator has FOLDED it —
    /// positive waits in dependency order, exactly the <c>AwaitGateFolded</c> barrier of
    /// <see cref="PaywallRealGateShapeTests"/>. Without the barrier a too-early snapshot denies
    /// EVERYONE: the buyer case fails and every deny case passes VACUOUSLY.
    /// </summary>
    private async Task Seed()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(MeshNode.FromPath(Course) with
        { Name = Course, NodeType = SpaceNodeType.NodeType, Content = new Space() })
            .Should().Within(90.Seconds()).Emit();
        await meshService.CreateNode(MeshNode.FromPath(Gated) with
        { Name = SecretName, NodeType = "Markdown" })
            .Should().Within(90.Seconds()).Emit();

        foreach (var node in new[]
        {
            ViewerAssignment(Course, WellKnownUsers.Public, denied: false),
            ViewerAssignment(Course, WellKnownUsers.Anonymous, denied: false),
            ViewerAssignment(Gated, WellKnownUsers.Public, denied: true),
            ViewerAssignment(Gated, WellKnownUsers.Anonymous, denied: true),
            ViewerAssignment(Course, Buyer, denied: false),
        })
        {
            await meshService.CreateNode(node).Should().Within(90.Seconds()).Emit();
        }

        var budget = 45.Seconds();
        // 1) The root grants folded: Public can read the cover.
        await Mesh.GetEffectivePermissions(Course, WellKnownUsers.Public)
            .Should().Within(budget).Match(p => p.HasFlag(Permission.Read),
                $"Public must inherit Read on the cover {Course}");
        // 2) The child deny folded and beats the inherited root grant.
        await Mesh.GetEffectivePermissions(Gated, WellKnownUsers.Public)
            .Should().Within(budget).Match(p => !p.HasFlag(Permission.Read),
                $"the child DENY on {Gated} must strip Public's inherited Read");
        // 3) The buyer's root grant folded and SURVIVED the child deny.
        await Mesh.GetEffectivePermissions(Gated, Buyer)
            .Should().Within(budget).Match(p => p.HasFlag(Permission.Read),
                $"the buyer's root grant must survive the child deny on {Gated}");
    }

    /// <summary>Calls a version tool under <paramref name="userId"/>'s circuit identity — the
    /// same identity surface the MCP tools re-establish via <c>AsCaller</c>.</summary>
    private async Task<string> RunAs(string userId, Func<Task<string>> tool)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetHostIdentity(new AccessContext { ObjectId = userId, Name = userId });
        try
        {
            return await tool();
        }
        finally
        {
            access.SetHostIdentity(null);
        }
    }

    /// <summary>
    /// Waits (bounded) until the history trigger's snapshot rows for the gated child are visible
    /// through <see cref="IVersionQuery"/>, and returns the newest version number. This is the
    /// vacuity guard: the deny assertions below would pass trivially if no version rows existed.
    /// </summary>
    private async Task<long> WaitForVersionRows()
    {
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var versions = await Observable.Interval(TimeSpan.FromMilliseconds(200))
            .StartWith(0L)
            .SelectMany(_ => versionQuery.GetVersions(Gated).ToList())
            .Where(list => list.Count > 0)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .ToTask(TestContext.Current.CancellationToken);
        return versions.Max(v => v.Version);
    }

    /// <summary>
    /// 🚨 THE PAYWALL, on the version LIST. An unentitled signed-in visitor must get the exact
    /// absence answer — not the version rows, whose summaries already leak the node's name.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task VersionList_OfGatedNode_IsMaskedAsAbsent_ForAnUnentitledUser()
    {
        await Seed();
        await WaitForVersionRows();

        var result = await RunAs(Visitor, () => new VersionPlugin(Mesh).GetVersions(Gated));
        Output.WriteLine(result);

        result.Should().Be($"No version history found for '{Gated}'.",
            "denial must be byte-identical to absence — anything else is an existence oracle " +
            "for gated paths, and any version row already leaks the node's name");
        result.Should().NotContain(SecretName);
    }

    /// <summary>
    /// 🚨 THE PAYWALL, on the version CONTENT read — the actual leak: a full historical snapshot
    /// (metadata + Content) served to a user the live read denies.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task VersionContent_OfGatedNode_IsMaskedAsAbsent_ForAnUnentitledUser()
    {
        await Seed();
        var newest = await WaitForVersionRows();

        // Sanity: the entitled buyer CAN read that exact version through the same plugin —
        // proving the deny below is the gate, not a broken reader.
        var buyerRead = await RunAs(Buyer, () => new VersionPlugin(Mesh).GetVersion(Gated, newest));
        buyerRead.Should().Contain(SecretName,
            "the buyer's root grant entitles them to the gated child's history");

        var result = await RunAs(Visitor, () => new VersionPlugin(Mesh).GetVersion(Gated, newest));
        Output.WriteLine(result);

        result.Should().Be($"Version {newest} not found for '{Gated}'.",
            "an unentitled signed-in user must not receive gated content from a version read — " +
            "the version tables are the same paywalled content, one write behind");
        result.Should().NotContain(SecretName);
    }

    /// <summary>
    /// The other half: an ENTITLED buyer still lists versions. A gate that denies everyone is
    /// not a fix — this is what would break if the gate were made unconditionally strict (or if
    /// the identity capture fell back to Anonymous for a signed-in caller).
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task VersionList_OfGatedNode_StillWorksForAnEntitledUser()
    {
        await Seed();
        await WaitForVersionRows();

        var result = await RunAs(Buyer, () => new VersionPlugin(Mesh).GetVersions(Gated));
        Output.WriteLine(result);

        result.Should().NotBe($"No version history found for '{Gated}'.",
            "the buyer's root grant entitles them to the gated child's version history");
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array,
            "an entitled read returns the JSON version listing");
        doc.RootElement.GetArrayLength().Should().BeGreaterThan(0);
    }
}
