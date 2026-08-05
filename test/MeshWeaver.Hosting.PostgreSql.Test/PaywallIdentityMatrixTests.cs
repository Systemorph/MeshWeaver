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
/// The paywall, across EVERY identity shape a read can arrive with. One gated node, four callers:
/// no identity at all, Anonymous, signed-in-but-unentitled, and the buyer.
///
/// <para>The fourth column is the one that catches an over-strict gate; the FIRST is the one that
/// catches the bypass — a read with no ambient identity must NOT be served from the cache's shared
/// system-identity upstream.</para>
/// </summary>
[Collection("PostgreSql")]
public class PaywallIdentityMatrixTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Buyer = "pw_buyer";
    private const string Visitor = "pw_visitor";

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

    private async Task<(string Plugin, string Gated)> Seed(string suffix)
    {
        var plugin = $"PwCourse{suffix}";
        var gated = $"{plugin}/PaidLesson";
        var ct = TestContext.Current.CancellationToken;

        await NodeFactory.CreateNode(MeshNode.FromPath(plugin) with
        { Name = plugin, NodeType = SpaceNodeType.NodeType, Content = new Space() })
            .Should().Within(90.Seconds()).Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath(gated) with
        { Name = "Paid Lesson", NodeType = "Markdown" })
            .Should().Within(90.Seconds()).Emit();

        var ac = fixture.AccessControl;
        await ac.Grant(plugin, WellKnownUsers.Public, "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant(gated, WellKnownUsers.Public, "Read", isAllow: false, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant(gated, WellKnownUsers.Anonymous, "Read", isAllow: false, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant(plugin, Buyer, "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        return (plugin, gated);
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

    /// <summary>🚨 NO AMBIENT IDENTITY — the production shape. Must not serve gated content.</summary>
    [Fact(Timeout = 180000)]
    public async Task NoIdentity_IsDeniedGatedContent()
    {
        var (_, gated) = await Seed("None");
        var result = await Read(null, gated);
        Output.WriteLine(result);
        result.Should().NotContain($"\"path\":\"{gated}\"",
            "a read with NO ambient identity must fail closed, not fall through to the cache's " +
            "shared system-identity upstream");
    }

    /// <summary>Anonymous — explicitly denied on the child.</summary>
    [Fact(Timeout = 180000)]
    public async Task Anonymous_IsDeniedGatedContent()
    {
        var (_, gated) = await Seed("Anon");
        var result = await Read(
            new AccessContext { ObjectId = WellKnownUsers.Anonymous, Name = "Guest", IsVirtual = true }, gated);
        Output.WriteLine(result);
        result.Should().NotContain($"\"path\":\"{gated}\"", "anonymous must never see gated content");
    }

    /// <summary>Signed in, bought nothing — inherits Public's deny.</summary>
    [Fact(Timeout = 180000)]
    public async Task AuthenticatedButNotPurchased_IsDenied()
    {
        var (_, gated) = await Seed("Visitor");
        var result = await Read(new AccessContext { ObjectId = Visitor, Name = Visitor }, gated);
        Output.WriteLine(result);
        result.Should().NotContain($"\"path\":\"{gated}\"",
            "a signed-in user who bought nothing must hit the paywall");
    }

    /// <summary>Purchased — must still get in.</summary>
    [Fact(Timeout = 180000)]
    public async Task Purchased_CanRead()
    {
        var (_, gated) = await Seed("Buyer");
        var result = await Read(new AccessContext { ObjectId = Buyer, Name = Buyer }, gated);
        Output.WriteLine(result);
        result.Should().Contain(gated, "the buyer's entitlement must open the gated child");
    }

    /// <summary>The cover stays public for a visitor — the funnel's one open page.</summary>
    [Fact(Timeout = 180000)]
    public async Task Cover_StaysPublicForVisitor()
    {
        var (plugin, _) = await Seed("Cover");
        var result = await Read(new AccessContext { ObjectId = Visitor, Name = Visitor }, plugin);
        Output.WriteLine(result);
        result.Should().Contain(plugin, "the cover must stay readable to a not-yet-subscribed visitor");
    }
}
