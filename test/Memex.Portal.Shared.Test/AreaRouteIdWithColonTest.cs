#pragma warning disable CS1591

using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #6815: the MCP area route <c>{node}/area/{Name}/{Id}</c> must carry an id that contains a colon.
/// Reference ids are free text — the OgCard gallery embeds <c>url=https://github.com/…</c> — and
/// <c>MeshOperations.TryParseAreaRoute</c> used to refuse ANY path with a <c>':'</c> (to leave the
/// legacy <c>{path}/area:Name</c> form to the unified resolver), so the read fell through to that
/// resolver, which split at the colon and answered "No node found at '{node}/area/{Name}'".
/// The route now wins whenever its <c>area</c> segment lies wholly before the first colon; the
/// legacy colon form still resolves through the unified path.
/// </summary>
public class AreaRouteIdWithColonTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Node = "AreaRouteColonProbe";
    private const string Area = "Echo";
    private const string ColonId = "url=https://github.com/Systemorph/MeshWeaver";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(Node) { Name = "Probe" })
            .ConfigureDefaultNodeHub(config => config.AddLayout(layout => layout
                .WithView(Area, (LayoutAreaHost host, RenderingContext _) =>
                    Controls.Markdown($"ECHO[{host.Reference.Id}]"))));

    [Fact]
    public void Parse_IdWithColon_KeepsTheAreaRoute()
    {
        MeshOperations.TryParseAreaRoute($"{Node}/area/{Area}/{ColonId}", out var nodePath, out var area, out var id)
            .Should().BeTrue("the area segment precedes the first colon, so the colon belongs to the id");
        nodePath.Should().Be(Node);
        area.Should().Be(Area);
        id.Should().Be(ColonId);
    }

    [Fact]
    public void Parse_InteriorAreaSegment_WithColonId_SplitsAtTheDeepestMarker()
    {
        MeshOperations.TryParseAreaRoute($"Root/area/Child/area/{Area}/{ColonId}", out var nodePath, out var area, out var id)
            .Should().BeTrue("a colon in a free-text id does not turn the route into the legacy form");
        nodePath.Should().Be("Root/area/Child");
        area.Should().Be(Area);
        id.Should().Be(ColonId);
    }

    [Theory]
    [InlineData(Node + "/area:" + Area)]
    [InlineData(Node + "/area:" + Area + "/legacy-id")]
    [InlineData(Node + "/area:content:logo.svg")]
    [InlineData(Node + "/data:Collection/area/x")]
    // An INTERIOR `area` segment of the node path is not the marker when a legacy keyword follows.
    [InlineData("Root/area/Child/area:" + Area + "/legacy-id")]
    [InlineData("Root/area/Child/area:" + Area)]
    [InlineData("Root/area/Child/data:Collection")]
    [InlineData("Root/area/Child/content:logo.svg")]
    public void Parse_LegacyColonForm_IsLeftToTheUnifiedResolver(string path)
        => MeshOperations.TryParseAreaRoute(path, out _, out _, out _)
            .Should().BeFalse("a colon at or before the area marker is the legacy colon form");

    [HubFact]
    public async Task Get_AreaRoute_WithColonInId_RendersThatId()
    {
        var answer = await new MeshOperations(Mesh).Get($"@{Node}/area/{Area}/{ColonId}")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        answer.Should().Contain($"ECHO[{ColonId}]",
            "the route must render area '{0}' of node '{1}' with the full colon-carrying id; got: {2}", Area, Node, answer);
    }

    [HubFact]
    public async Task Get_LegacyColonForm_StillResolves()
    {
        var answer = await new MeshOperations(Mesh).Get($"@{Node}/area:{Area}/legacy-id")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        answer.Should().NotStartWith("Error", "the legacy colon form keeps resolving through the unified path; got: {0}", answer);
        answer.Should().NotStartWith("Not found", "got: {0}", answer);
    }
}
