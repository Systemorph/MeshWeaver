using System.Reactive.Linq;
using Memex.Portal.Shared.Api;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins #4080: the sitemap's root enumeration must not depend on a result WINDOW. Before the fix,
/// <see cref="SeoEndpoints.EnumeratePublished"/> asked the mesh for <c>nodeType:{type} is:main
/// limit:500</c> and filtered to top-level paths CLIENT-side afterwards — so once a candidate type
/// had more than 500 mains mesh-wide (every course installed into a user partition is a
/// <c>Space</c> main; every store plugin copy is a <c>Store/Plugin</c>), which roots landed inside
/// the window was decided by storage order, and a public root could silently drop out of the
/// sitemap. Nothing errored; the endpoint is fail-open to fewer URLs, which is what made it
/// invisible. The query language already pushes the root filter down — <c>namespace:</c> with an
/// EMPTY value is <c>namespace = ''</c> on every backend — so the enumeration now asks for
/// top-level mains only, with no cap and no client-side filter.
///
/// <para>The rig plants MORE non-root <c>Space</c> mains than the old window held, all created
/// BEFORE the one public root, so on the unfixed code the root fell outside the window and the
/// sitemap was empty. That is the falsification: this test was run against the pre-fix
/// enumeration and failed on exactly that assertion.</para>
/// </summary>
public class SitemapRootWindowTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Strictly more than the window the old enumeration asked for.</summary>
    private const int NonRootSpaceMains = 520;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(Nodes().ToArray());

    private static IEnumerable<MeshNode> Nodes()
    {
        // A private space holding hundreds of nested Space mains — the shape of a user partition
        // with many installed courses. None of these is a root, and none is public.
        yield return new MeshNode("Crowd") { Name = "Crowd", NodeType = "Space" };
        for (var i = 0; i < NonRootSpaceMains; i++)
            yield return new MeshNode($"Course{i:D4}", "Crowd") { Name = $"Course {i}", NodeType = "Space" };

        // The ONE public root, created after the crowd so that any window shorter than the crowd
        // never reaches it.
        yield return new MeshNode("PublicSpace") { Name = "Public Space", NodeType = "Space" };
        yield return AssignmentNodeFactory.UserRole(
            WellKnownUsers.Anonymous, "Viewer", "PublicSpace",
            accessObject: WellKnownUsers.Anonymous);
        yield return new MeshNode("Guide", "PublicSpace") { Name = "Guide", NodeType = "Markdown" };
    }

    // Granular permissions — no blanket admin seed, so the anonymous subject holds exactly what
    // the nodes above grant.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact]
    public async Task APublicRoot_IsPublished_HoweverManyNonRootMainsOfItsTypeExist()
    {
        var surface = await SeoEndpoints.EnumeratePublished(Mesh)
            .Timeout(TestTimeouts.Convergence);
        // As in SitemapDescentTest: a window defect and an undecided gate both end in a short
        // list, so the assertion states which one this is NOT (#4751).
        Assert.Null(surface.Undecided);
        var paths = surface.Pages.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.Equal(["PublicSpace", "PublicSpace/Guide"], paths);
    }
}
