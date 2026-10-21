#pragma warning disable CS1591

using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A node DECLARED in configuration is row-filtered in a query exactly as a stored one is.</b>
///
/// <para>The static query provider served every declared node (<c>AddMeshNodes</c>,
/// <see cref="IStaticNodeProvider"/>) to every viewer, filtering only satellite paths
/// (<c>_Access</c>, <c>_Thread</c>, …). A POINT read of the same node was already denied, so the two
/// read seams disagreed: a viewer with no grant on a partition listed a node there through
/// <see cref="IMeshService.Query{T}"/> — with the viewer stamped explicitly on the request — that
/// <c>GetMeshNodeStream</c> refused them.</para>
///
/// <para>The fixture declares one ordinary (non-satellite) node in a partition only
/// <see cref="Reader"/> holds Read on. <see cref="Stranger"/> must get no row; the reader must — the
/// control without which an empty answer proves nothing.</para>
/// </summary>
public class DeclaredNodeIsRowFilteredInQueriesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "declaredrls-private";
    private const string SecretId = "declaredrls-secret";
    private const string SecretPath = Partition + "/" + SecretId;
    private const string Reader = "declaredrls-reader";
    private const string Stranger = "declaredrls-stranger";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role in every
    // default partition, under which every viewer could read the partition and the filter would be vacuous.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(SecretId, Partition) { NodeType = "Markdown", Name = SecretId },
                AssignmentNodeFactory.UserRole(Reader, "Viewer", Partition));

    private Task<IReadOnlyList<string>> QueryAs(string viewer, string query)
        => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(new MeshQueryRequest { Query = query, UserId = viewer })
            .FirstAsync()
            .Select(change => (IReadOnlyList<string>)change.Items.Select(n => n.Path).ToList())
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("path:" + SecretPath)]
    [InlineData("namespace:" + Partition)]
    [InlineData("namespace:" + Partition + " scope:descendants nodeType:Markdown")]
    public async Task AViewerWithNoGrant_GetsNoDeclaredRow(string query)
    {
        var rows = await QueryAs(Stranger, query);
        Output.WriteLine($"stranger [{query}]: {string.Join(", ", rows)}");

        rows.Should().NotContain(SecretPath,
            "the stranger holds no grant on the partition — a point read of the node is denied, and a query must not answer differently");
    }

    [Theory]
    [InlineData("path:" + SecretPath)]
    [InlineData("namespace:" + Partition)]
    [InlineData("namespace:" + Partition + " scope:descendants nodeType:Markdown")]
    public async Task AViewerWithRead_StillGetsTheDeclaredRow(string query)
    {
        var rows = await QueryAs(Reader, query);
        Output.WriteLine($"reader [{query}]: {string.Join(", ", rows)}");

        rows.Should().Contain(SecretPath,
            "the reader holds Viewer on the partition — if this fails, the stranger's empty answer proves nothing");
    }

    /// <summary>
    /// The definitions stay vocabulary: a viewer with no grant anywhere still resolves a built-in role
    /// and a type registration — no grant is ever written on them, so filtering them would empty every
    /// role and type picker for everyone but an admin.
    /// </summary>
    [Theory]
    [InlineData("path:Role/Viewer", "Role/Viewer")]
    [InlineData("path:Markdown", "Markdown")]
    public async Task AViewerWithNoGrant_StillResolvesTheDefinitions(string query, string expected)
    {
        var rows = await QueryAs(Stranger, query);
        Output.WriteLine($"stranger [{query}]: {string.Join(", ", rows)}");

        rows.Should().Contain(expected, "a definition is platform vocabulary, served to every viewer");
    }

    /// <summary>The point read the query must agree with: the fold denies the stranger and admits the reader.</summary>
    [Fact]
    public async Task ThePointReadVerdictIsTheOneTheQueryFollows()
    {
        var ct = TestContext.Current.CancellationToken;
        var stranger = await Mesh.GetEffectivePermissions(SecretPath, Stranger).FirstAsync().Timeout(Budget).Await(ct);
        var reader = await Mesh.GetEffectivePermissions(SecretPath, Reader).FirstAsync().Timeout(Budget).Await(ct);
        Output.WriteLine($"stranger={stranger} reader={reader}");

        stranger.HasFlag(Permission.Read).Should().BeFalse("the stranger holds no grant on the partition");
        reader.HasFlag(Permission.Read).Should().BeTrue("the reader holds Viewer on the partition");
    }
}
