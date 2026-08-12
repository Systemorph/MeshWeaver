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
/// 🚨 PINS THE PAYWALL ON THE EXACT-PATH READ.
///
/// <para>Gated content must be denied to a signed-in user who has no entitlement — on the
/// <b>single-node read by path</b>, not merely on queries. The query fold already enforced this
/// (<see cref="PerSubjectAccessFoldTests"/>); the exact-path read did not.</para>
///
/// <para><b>The hole.</b> <c>MeshNodeStreamCache.GetStreamRaw</c> reads
/// <c>AccessService.Context</c> AT SUBSCRIBE TIME to decide whether to apply its per-user read
/// gate, and deliberately passes through to the shared system-identity upstream when that comes
/// back null (background / hub-principal flows). <c>AccessContext</c> is an <c>AsyncLocal</c> and
/// is cleared across <c>.Subscribe</c> hops — so the identity was routinely gone by then, and a
/// lost identity did not fail closed: it read EVERYTHING.</para>
///
/// <para>Measured on memex 2026-08-05: <c>get @AgenticPrimerDe/02-CodeWunsch</c> returned 79,650
/// characters of a PAID course lesson to a signed-in user with no entitlement and no grant, while
/// the gating data was entirely correct (<c>Public|…/02-CodeWunsch|Read|is_allow=false</c> present,
/// and the SQL query path hid the same node). Only the exact-path read leaked it — the symptom
/// filed on 2026-07-29 as "children vanish from listings but direct get by path still works".</para>
/// </summary>
[Collection("PostgreSql")]
public class GatedNodeExactPathReadTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Buyer = "gated_buyer";      // entitled
    private const string Visitor = "gated_visitor";  // signed in, bought nothing

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
    /// Seeds a FRESH plugin per test. The nodes and grants must be unique per case: xUnit runs the
    /// methods in a class sequentially against the SAME mesh, so a shared path makes the second
    /// CreateNode complete without emitting ("already exists") — which fails the seed and, worse,
    /// would let a deny-assertion pass VACUOUSLY because the node was never created at all.
    /// </summary>
    private async Task<(string Plugin, string GatedChild)> Seed(string suffix)
    {
        var Plugin = $"GatedCourse{suffix}";
        var GatedChild = $"{Plugin}/PaidLesson";
        var ct = TestContext.Current.CancellationToken;

        await NodeFactory.CreateNode(MeshNode.FromPath(Plugin) with
        {
            Name = Plugin,
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Within(90.Seconds()).Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath(GatedChild) with
        {
            Name = "Paid Lesson",
            NodeType = "Markdown"
        }).Should().Within(90.Seconds()).Emit();

        var ac = fixture.AccessControl;
        // The store's gating shape: the cover is readable by everyone signed in; the paid child
        // is DENIED to Public. An entitlement is a per-user allow at the ROOT.
        await ac.Grant(Plugin, WellKnownUsers.Public, "Read", isAllow: true, ct)
            .Should().Within(30.Seconds()).Emit();
        await ac.Grant(GatedChild, WellKnownUsers.Public, "Read", isAllow: false, ct)
            .Should().Within(30.Seconds()).Emit();
        await ac.Grant(Plugin, Buyer, "Read", isAllow: true, ct)
            .Should().Within(30.Seconds()).Emit();

        return (Plugin, GatedChild);
    }

    private async Task<string> GetAs(string userId, string path)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetHostIdentity(new AccessContext { ObjectId = userId, Name = userId });
        try
        {
            return await new MeshOperations(Mesh).Get(path)
                .Should().Within(60.Seconds()).Emit();
        }
        finally
        {
            access.SetHostIdentity(null);
        }
    }

    /// <summary>
    /// 🚨 THE PAYWALL. A signed-in visitor who bought nothing must NOT receive the gated node's
    /// content from an exact-path read. Denial surfaces as "Not found" — deliberately, so the
    /// response does not disclose that a paid node exists at that path.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task ExactPathRead_OfGatedNode_IsDeniedForAnUnentitledUser()
    {
        var (_, GatedChild) = await Seed("Deny");

        // The evaluator agrees the visitor has no Read — proving any leak is the READ PATH,
        // not the permission logic (same structure as AnonymousSpaceReadSecurityTests).
        await Mesh.GetEffectivePermissions(GatedChild, Visitor)
            .Should().Within(60.Seconds()).Match(p => !p.HasFlag(Permission.Read));

        var result = await GetAs(Visitor, GatedChild);
        Output.WriteLine(result);

        result.Should().NotContain($"\"path\":\"{GatedChild}\"",
            "an unentitled signed-in user must not receive gated content from an exact-path read — " +
            "this is the paywall, and the read must fail closed when the identity is lost");
    }

    /// <summary>
    /// The other half: an ENTITLED buyer still reads it. A paywall that denies everyone is not a
    /// fix — this is what would break if the gate were made unconditionally strict.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task ExactPathRead_OfGatedNode_StillWorksForAnEntitledUser()
    {
        var (_, GatedChild) = await Seed("Buyer");

        var result = await GetAs(Buyer, GatedChild);
        Output.WriteLine(result);

        result.Should().Contain(GatedChild,
            "the buyer's root allow entitles them to the gated child (per-subject fold: the Public " +
            "deny binds only Public)");
    }

    /// <summary>The public cover stays readable to a visitor — the funnel's one open page.</summary>
    [Fact(Timeout = 180000)]
    public async Task ExactPathRead_OfTheCover_StaysPublic()
    {
        var (Plugin, _) = await Seed("Cover");

        var result = await GetAs(Visitor, Plugin);
        Output.WriteLine(result);

        result.Should().Contain(Plugin,
            "the cover is the one page a not-yet-subscribed visitor must always see");
    }
}
