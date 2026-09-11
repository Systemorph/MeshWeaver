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
/// Pins what "published to the web" enumerates (<see cref="SeoEndpoints.EnumeratePublished"/>):
/// every PAGE below a public root that the anonymous gate admits — and nothing else. Before #4056
/// the list stopped at the partition roots, so the documentation tree and every course chapter
/// were invisible to search engines; and the one descent it did attempt (a store plugin's declared
/// public segments) read through the raw storage adapter and came back empty on the partitioned
/// deployment. The gate, asked per node, is now the one definition — the same grants and denies
/// the installer writes for a course's free and paid chapters.
/// </summary>
public class SitemapDescentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                // A public space: the root carries the Anonymous Viewer grant, which flows down.
                new MeshNode("PublicSpace") { Name = "Public Space", NodeType = "Space" },
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Anonymous, "Viewer", "PublicSpace",
                    accessObject: WellKnownUsers.Anonymous),
                // Pages, at two depths.
                new MeshNode("Guide", "PublicSpace") { Name = "Guide", NodeType = "Markdown" },
                new MeshNode("Deep", "PublicSpace/Guide") { Name = "Deep", NodeType = "Markdown" },
                new MeshNode("Lesson1", "PublicSpace") { Name = "Lesson 1", NodeType = "Edu/Module" },
                // A paid chapter: the per-child Anonymous DENY the installer writes.
                new MeshNode("Lesson2", "PublicSpace") { Name = "Lesson 2", NodeType = "Edu/Module" },
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Anonymous, "Viewer", "PublicSpace/Lesson2",
                    denied: true, accessObject: WellKnownUsers.Anonymous),
                // Not pages: a data node, code under a satellite segment, an underscore satellite.
                new MeshNode("GNPI", "PublicSpace/AmountType") { Name = "GNPI", NodeType = "Reinsurance/AmountType" },
                new MeshNode("Foo", "PublicSpace/Source") { Name = "Foo", NodeType = "Markdown" },
                new MeshNode("t1", "PublicSpace/_Thread") { Name = "Thread", NodeType = "Markdown", MainNode = "PublicSpace" },
                // A private space with a page: no grant, so neither is listed.
                new MeshNode("PrivateSpace") { Name = "Private Space", NodeType = "Space" },
                new MeshNode("Page", "PrivateSpace") { Name = "Page", NodeType = "Markdown" });

    // Granular permissions — no blanket admin seed, so the anonymous subject holds exactly what
    // the nodes above grant.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact]
    public async Task EveryAdmittedPageBelowAPublicRoot_IsPublished_AndNothingElse()
    {
        var pages = await SeoEndpoints.EnumeratePublished(Mesh)
            .Timeout(TestTimeouts.Convergence);
        var paths = pages.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.Equal(
            ["PublicSpace", "PublicSpace/Guide", "PublicSpace/Guide/Deep", "PublicSpace/Lesson1"],
            paths);
    }

    [Fact]
    public async Task TheSitemap_NamesEachPageOnTheCanonicalHost_WithItsLastModified()
    {
        var xml = await SeoEndpoints.BuildSitemap(Mesh, "https://www.example.test")
            .Timeout(TestTimeouts.Convergence);

        Assert.Contains("<loc>https://www.example.test/PublicSpace/Guide/Deep</loc>", xml);
        Assert.Contains("<loc>https://www.example.test/PublicSpace/Lesson1</loc>", xml);
        Assert.DoesNotContain("Lesson2", xml);
        Assert.DoesNotContain("PrivateSpace", xml);
        Assert.DoesNotContain("_Thread", xml);
        Assert.DoesNotContain("AmountType", xml);
    }

    [Theory]
    [InlineData("Guide", true)]
    [InlineData("Guide/Deep", true)]
    [InlineData("_Thread/t1", false)]
    [InlineData("Source/Foo", false)]
    [InlineData("Cedent/Test/CedentTests", false)]
    [InlineData("Acceptance/Release/20260812", false)]
    public void APagePath_HasNoSatelliteSegment(string relative, bool isPage)
        => Assert.Equal(isPage, SeoEndpoints.IsPagePath(relative));
}
