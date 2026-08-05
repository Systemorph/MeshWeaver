using System.Reactive.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.AI;
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
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// The paywall, gated with the EXACT node shapes the Store's <c>PluginGate</c> writes — NOT the
/// test fixture's permission helper. This distinction is the whole point of the class:
/// <c>PaywallIdentityMatrixTests</c> seeded its gate through <c>fixture.AccessControl.Grant</c>
/// and every deny case passed even while production served gated paid lessons to unentitled
/// users, because production's gate is expressed as <c>AccessAssignment</c> NODES
/// (<c>roles:[{role:Viewer, denied:true}]</c> for the <c>Public</c> subject) that
/// <c>PermissionEvaluator</c> must fold — a different code path entirely.
///
/// <para>The decisive production measurement (memex, 2026-08-05, same MCP credential, same
/// second): <c>search</c> correctly hid the gated lessons while <c>get</c> by exact path served
/// them in full. The SQL fold and <c>PermissionEvaluator</c> disagreed. The disagreement:
/// the API token attaches the user's DB roles (e.g. the portal-admin role) as CLAIM roles, and
/// the evaluator folded claim roles into every node's effective permissions AFTER the per-scope
/// deny subtraction — a global, undeniable Read on the entire mesh, invisible to the SQL path
/// (which never sees claims). <see cref="DbRoleClaim_DoesNotGrantNodeRead"/> is that exact
/// scenario and MUST stay red against any evaluator that folds claim roles into node data
/// permissions.</para>
///
/// <para>The access model this pins (AGENTS.md): a platform role grants the PLATFORM gates,
/// deliberately NOT cross-partition data access — "it must not read a course it has not bought".
/// Claim roles decide the API-token CAPABILITY (<see cref="Permission.Api"/>); node data access
/// comes only from AccessAssignment nodes and policies, same as the SQL path.</para>
/// </summary>
[Collection("PostgreSql")]
public class PaywallRealGateShapeTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Buyer = "gs_buyer";
    private const string Visitor = "gs_visitor";

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

    /// <summary>
    /// A Viewer AccessAssignment node in EXACTLY the shape <c>PluginGate.ViewerAssignment</c>
    /// (MeshWeaver.Plugins, Store/Plugin/Source) writes: the node lands in
    /// <c>{scope}/_Access/{subject}_Access</c> with <c>mainNode = {scope}</c>, content
    /// <c>{$type:AccessAssignment, accessObject, roles:[{$type:RoleAssignment, role:Viewer(, denied)}]}</c>.
    /// Do not "simplify" this to a fixture permission helper — the fidelity IS the test.
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

    // Unique per run (the PostgreSql fixture database persists across runs — a constant name
    // collides with the previous run's plugin and the seeding CreateNode completes empty),
    // static so every test instance of the class shares the one seeded gate.
    private static readonly string Plugin = "GateShape" + Guid.NewGuid().ToString("N")[..8];
    private static readonly string Gated = $"{Plugin}/PaidLesson";

    /// <summary>
    /// Seeds ONE plugin with the full PluginGate shape, once per test-class mesh: root
    /// Public + Anonymous Viewer grants (the cover is the one public page), Public + Anonymous
    /// Viewer DENIES on the gated child (a synced child is born gated), and the buyer's root
    /// Viewer grant (what <c>PluginGate.Enroll</c> writes — entitlement is a grant at the ROOT,
    /// which the child deny for Public must NOT strip).
    ///
    /// <para>Seeded ONCE for the whole class, lazily from the first test body (a top-level
    /// Space cannot be created during mesh INIT — SetupAccessRightsAsync-time creates of a
    /// partition-owning node complete empty; only assignment nodes work there). One seed
    /// rather than seven keeps the process-wide security-access scope streams small — the
    /// per-test-seed version timed out its fold barriers on loaded CI shards. The reads under
    /// test still run per test with explicit caller identities.</para>
    /// </summary>
    private static Task? _seeded;

    private Task EnsureSeeded() => _seeded ??= SeedOnce();

    private async Task SeedOnce()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(MeshNode.FromPath(Plugin) with
        { Name = Plugin, NodeType = SpaceNodeType.NodeType, Content = new Space() })
            .Should().Within(90.Seconds()).Emit();
        await meshService.CreateNode(MeshNode.FromPath(Gated) with
        { Name = "Paid Lesson", NodeType = "Markdown" })
            .Should().Within(90.Seconds()).Emit();

        foreach (var node in new[]
        {
            ViewerAssignment(Plugin, WellKnownUsers.Public, denied: false),
            ViewerAssignment(Plugin, WellKnownUsers.Anonymous, denied: false),
            ViewerAssignment(Gated, WellKnownUsers.Public, denied: true),
            ViewerAssignment(Gated, WellKnownUsers.Anonymous, denied: true),
            ViewerAssignment(Plugin, Buyer, denied: false),
        })
        {
            await meshService.CreateNode(node).Should().Within(90.Seconds()).Emit();
        }

        // Barrier: wait until the evaluator's synced access streams have FOLDED the seeded
        // nodes — the Public deny at the child and the buyer's root grant. Without this the
        // read can race the query cache: a too-early snapshot denies EVERYONE, which fails the
        // buyer case and lets every deny case pass vacuously. Polling the evaluator itself is
        // the honest barrier — it is the same substrate the read gate consults.
        await Mesh.GetEffectivePermissions(Gated, WellKnownUsers.Public)
            .Where(p => !p.HasFlag(Permission.Read))
            .Should().Within(120.Seconds()).Emit();
        await Mesh.GetEffectivePermissions(Gated, Buyer)
            .Where(p => p.HasFlag(Permission.Read))
            .Should().Within(120.Seconds()).Emit();
    }

    /// <summary>Reads through the same entry point the MCP `get` tool uses.</summary>
    private async Task<string> Read(AccessContext? identity, string path)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetCircuitContext(identity);
        try
        {
            return await new MeshOperations(Mesh).Get(path).Should().Within(60.Seconds()).Emit();
        }
        finally
        {
            access.SetCircuitContext(null);
        }
    }

    /// <summary>
    /// 🚨 THE PRODUCTION BYPASS. The caller is authenticated, has NO grant and NO entitlement in
    /// this plugin — but their API token carries their DB role ("Admin" on their own portal) as a
    /// claim role. A claim role is a PLATFORM capability; it must not read a course the user has
    /// not bought. Before the fix the evaluator folded it into every node's permissions after the
    /// deny subtraction: global, undeniable Read — while `search` (SQL, claim-blind) denied the
    /// same node. This is `get @AgenticPrimerDe/02-CodeWunsch` returning 79,650 chars, as a test.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task DbRoleClaim_DoesNotGrantNodeRead()
    {
        await EnsureSeeded();
        var gated = Gated;
        var result = await Read(
            new AccessContext { ObjectId = Visitor, Name = Visitor, Roles = ["Admin"] }, gated);
        Output.WriteLine(result);
        result.Should().NotContain($"\"path\":\"{gated}\"",
            "a DB/platform role attached as a claim must not read gated partition data — " +
            "platform roles grant the platform gates, never a course the user has not bought");
    }

    /// <summary>Same caller shape with the modest role — the common MCP token.</summary>
    [Fact(Timeout = 180000)]
    public async Task ViewerClaim_DoesNotGrantNodeRead()
    {
        await EnsureSeeded();
        var gated = Gated;
        var result = await Read(
            new AccessContext { ObjectId = Visitor, Name = Visitor, Roles = ["Viewer"] }, gated);
        Output.WriteLine(result);
        result.Should().NotContain($"\"path\":\"{gated}\"",
            "a claim role must never out-rank the gate's Public deny on the child");
    }

    /// <summary>Authenticated, bought nothing, no claims — inherits Public's child deny.</summary>
    [Fact(Timeout = 180000)]
    public async Task AuthenticatedNotEntitled_IsDenied()
    {
        await EnsureSeeded();
        var gated = Gated;
        var result = await Read(new AccessContext { ObjectId = Visitor, Name = Visitor }, gated);
        Output.WriteLine(result);
        result.Should().NotContain($"\"path\":\"{gated}\"",
            "the real gate shape (Public root grant + Public child deny nodes) must deny an " +
            "unentitled signed-in user on the exact-path read, as the SQL path already does");
    }

    /// <summary>
    /// The buyer: entitlement is the root Viewer grant PluginGate.Enroll writes. The child deny
    /// targets the Public subject, not the buyer — the buyer's own grant must survive it.
    /// This is the case that catches an over-strict fix (deny-by-role stripping every subject).
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task Buyer_CanReadGatedContent()
    {
        await EnsureSeeded();
        var gated = Gated;
        var result = await Read(new AccessContext { ObjectId = Buyer, Name = Buyer }, gated);
        Output.WriteLine(result);
        result.Should().Contain($"\"path\":\"{gated}\"",
            "the buyer's root Viewer grant is the entitlement — the Public child deny must not " +
            "strip it");
    }

    /// <summary>The cover stays public: the root has Public+Anonymous Viewer grants and no deny.</summary>
    [Fact(Timeout = 180000)]
    public async Task Cover_VisitorAllowed()
    {
        await EnsureSeeded();
        var plugin = Plugin;
        var result = await Read(new AccessContext { ObjectId = Visitor, Name = Visitor }, plugin);
        Output.WriteLine(result);
        result.Should().Contain($"\"path\":\"{plugin}\"",
            "the cover is the one public page of a gated plugin — an over-strict claim fix must " +
            "not close it");
    }

    /// <summary>
    /// The claim roles' LEGITIMATE job survives: an API token's claims still decide the
    /// <see cref="Permission.Api"/> CAPABILITY. A token whose claims carry an Api-bearing role
    /// may read what its NODE permissions allow (here: the public cover via the Public grant);
    /// removing claim roles from node permissions must not brick every MCP token.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task ApiToken_WithClaimRoles_StillReadsPublicContent()
    {
        await EnsureSeeded();
        var plugin = Plugin;
        var result = await Read(
            new AccessContext
            {
                ObjectId = Visitor, Name = Visitor, Roles = ["Viewer"], IsApiToken = true
            }, plugin);
        Output.WriteLine(result);
        result.Should().Contain($"\"path\":\"{plugin}\"",
            "an API token with an Api-bearing claim role must still read node-granted public " +
            "content — claims gate the API capability, nodes gate the data");
    }

    /// <summary>And the same token is still denied the gated child — capability is not data.</summary>
    [Fact(Timeout = 180000)]
    public async Task ApiToken_WithClaimRoles_IsStillDeniedGatedContent()
    {
        await EnsureSeeded();
        var gated = Gated;
        var result = await Read(
            new AccessContext
            {
                ObjectId = Visitor, Name = Visitor, Roles = ["Editor"], IsApiToken = true
            }, gated);
        Output.WriteLine(result);
        result.Should().NotContain($"\"path\":\"{gated}\"",
            "the Api capability lets the token call the API — it grants no Read on gated data");
    }
}
