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

    // 🚨 PER TEST INSTANCE, never static. xUnit constructs a NEW instance — and
    // MonolithMeshTestBase a NEW mesh — for every [Fact], so a gate seeded on one test's mesh
    // is read by the next test through a COLD evaluator that nobody ever waited on. The
    // previous shape hid that behind a process-static `Task _seeded` + static Plugin/Gated:
    // process-wide mutable state (AGENTS.md → "No static collections — ever"), it pinned the
    // whole class's data to the FIRST test's mesh, left the other six asserting against an
    // unsynchronised evaluator, and — because a faulted Task stays cached — turned ONE
    // timed-out fold into SEVEN identical red tests with the same misleading stack
    // (CI 31083356138). One gate per test, seeded and folded on the mesh that reads it.
    private string Plugin { get; } = "GateShape" + Guid.NewGuid().ToString("N")[..8];
    private string Gated => $"{Plugin}/PaidLesson";

    /// <summary>
    /// Seeds this test's plugin with the full PluginGate shape: root Public + Anonymous Viewer
    /// grants (the cover is the one public page), Public + Anonymous Viewer DENIES on the gated
    /// child (a synced child is born gated), and the buyer's root Viewer grant (what
    /// <c>PluginGate.Enroll</c> writes — entitlement is a grant at the ROOT, which the child
    /// deny for Public must NOT strip).
    ///
    /// <para>Seeded lazily from the test body, never from mesh INIT — a top-level Space cannot
    /// be created during <c>SetupAccessRightsAsync</c> (partition-owning creates complete empty
    /// there; only assignment nodes work).</para>
    /// </summary>
    private async Task Seed()
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

        await AwaitGateFolded();
    }

    /// <summary>
    /// Barrier: block until the evaluator's synced access streams have actually FOLDED the
    /// seeded gate. Without it the read races the query cache — a too-early snapshot denies
    /// EVERYONE, which fails the buyer case and lets every deny case pass VACUOUSLY. The
    /// evaluator is the honest barrier: it is the same substrate the read gate consults.
    ///
    /// <para>🚨 EVERY wait here is POSITIVE, and the order is a dependency chain. A
    /// deny-shaped wait (<c>Where(p =&gt; !p.HasFlag(Read))</c>) is satisfied by the FIRST
    /// emission of a cold scope stream — <see cref="Permission.None"/> before anything has
    /// loaded — so on its own it certifies nothing and sails straight through. That is exactly
    /// how CI 31083356138 reported its 120 s timeout on the BUYER wait while the "Public is
    /// denied" wait above it had already passed on an empty snapshot: the deny wait was never a
    /// barrier at all. Step 1 (a grant that only exists once the root assignments fold) is what
    /// makes step 2's deny meaningful — by then the inherited root grant IS in the fold, so
    /// losing Read on the child can only come from the child's DENY landing.</para>
    ///
    /// <para>Budgets are sized to FIT the declared <c>[Fact(Timeout = 180000)]</c>
    /// (3 × 45 s &lt; 180 s). The previous 2 × 120 s could not both elapse inside the Fact, so
    /// the second wait was silently truncated by the Fact timeout instead of reporting — the
    /// mirror image of the halved compile budget fixed in 459b4403c.</para>
    /// </summary>
    /// <remarks>
    /// 🚨 Every wait uses <c>.Match(pred)</c>, NEVER <c>.Where(pred).Emit()</c>. The two are
    /// equivalent when they pass and worlds apart when they fail: <c>Where</c> filters BEFORE the
    /// assertion sees the stream, so the assertion can only report "the observable emitted nothing
    /// at all" — true of the filtered stream whether the fold was wedged or simply folded to the
    /// wrong permission. <c>Match</c> hands the predicate to the assertion, which taps the
    /// UNFILTERED source and names the last permission actually observed.
    ///
    /// <para>That distinction is the whole diagnosis here. This barrier has flaked repeatedly on
    /// CI shard 5 (#825/#849/#853) and every report was the same contentless timeout, so each
    /// investigation had to start from zero. The permissions this fold DID produce are the
    /// evidence: <c>Permission.None</c> means the scope walk never folded, while a non-None value
    /// missing <c>Read</c> means it folded but the grant under test was absent — a stale query
    /// snapshot, a different root cause with a different fix.</para>
    /// </remarks>
    private async Task AwaitGateFolded()
    {
        var budget = 45.Seconds();

        // 1) The root grants folded: Public can read the cover.
        await Mesh.GetEffectivePermissions(Plugin, WellKnownUsers.Public)
            .Should().Within(budget).Match(p => p.HasFlag(Permission.Read),
                $"Public must inherit Read on the cover {Plugin}");

        // 2) The child deny folded and beats the inherited root grant.
        await Mesh.GetEffectivePermissions(Gated, WellKnownUsers.Public)
            .Should().Within(budget).Match(p => !p.HasFlag(Permission.Read),
                $"the child DENY on {Gated} must strip Public's inherited Read");

        // 3) The buyer's root grant folded and SURVIVED the child deny.
        await Mesh.GetEffectivePermissions(Gated, Buyer)
            .Should().Within(budget).Match(p => p.HasFlag(Permission.Read),
                $"the buyer's root grant must survive the child deny on {Gated}");
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
        await Seed();
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
        await Seed();
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
        await Seed();
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
        await Seed();
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
        await Seed();
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
        await Seed();
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
        await Seed();
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
