using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Memex.Portal.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Acme.Test;

/// <summary>
/// Tests for Organization Overview rendering and search scope behavior.
/// Verifies that organization pages render correctly with only MeshNode data
/// (no Organization entity required) and that search scopes work as expected.
/// </summary>
public class OrganizationOverviewTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverOrgOverviewTests",
        ".mesh-cache");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        Directory.CreateDirectory(SharedCacheDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddAcme()
            .AddOrganizationType()
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = SharedCacheDirectory;
                    o.EnableDiskCache = true;
                });
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddGraph();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    /// <summary>
    /// Organization Overview should render when only a MeshNode exists (no Organization entity).
    /// This is the core fix: file-based sample orgs like ACME only have MeshNode data.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Overview_ShouldRenderWithMeshNodeOnly()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var orgAddress = new Address("ACME");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            orgAddress,
            reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(10.Seconds())
            .FirstAsync();

        control.Should().NotBeNull("Organization Overview should render with MeshNode only");
        control.Should().BeOfType<StackControl>("Overview returns a StackControl container");
    }

    /// <summary>
    /// The rendered Overview should contain child areas (header, divider, children section).
    /// This proves the org view built its full structure from MeshNode.Name.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Overview_ShouldHaveChildAreas()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var orgAddress = new Address("ACME");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            orgAddress,
            reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl { Areas.Count: > 0 })
            .Timeout(10.Seconds())
            .FirstAsync();

        var stack = control.Should().BeOfType<StackControl>().Subject;
        Output.WriteLine($"Overview stack has {stack.Areas.Count} child areas");

        // BuildOrganizationView creates: headerRow, divider <hr>, optional markdown body, children catalog
        // At minimum we expect headerRow + divider + children = 3 areas
        stack.Areas.Count.Should().BeGreaterThanOrEqualTo(3,
            "Overview should contain header, divider, and children section");
    }

    /// <summary>
    /// Search with scope:subtree should include the root organization node itself.
    /// This verifies the SearchHub fix (changed from scope:descendants to scope:subtree).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Search_SubtreeScope_IncludesRootOrganizationNode()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var ct = TestContext.Current.CancellationToken;

        var query = "ACME scope:subtree";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync<MeshNode>(
                MeshQueryRequest.FromQuery(query), null, ct)
            .ToListAsync(ct);

        Output.WriteLine($"Found {results.Count} results:");
        foreach (var node in results.Take(10))
            Output.WriteLine($"  - {node.Path} ({node.NodeType})");

        results.Should().Contain(n => n.Path == "ACME",
            "scope:subtree should include the root node itself");
    }

    /// <summary>
    /// Search with scope:descendants should exclude the root node (only children).
    /// This is a regression guard for the original behavior.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Search_DescendantsScope_ExcludesRootNode()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var ct = TestContext.Current.CancellationToken;

        var query = "ACME scope:descendants path:ACME";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync<MeshNode>(
                MeshQueryRequest.FromQuery(query), null, ct)
            .ToListAsync(ct);

        Output.WriteLine($"Found {results.Count} results:");
        foreach (var node in results.Take(10))
            Output.WriteLine($"  - {node.Path} ({node.NodeType})");

        // scope:descendants should NOT include the root ACME node itself
        results.Where(n => n.Path == "ACME").Should().BeEmpty(
            "scope:descendants should exclude the root node, returning only children");
    }

    /// <summary>
    /// The pre-rendered HTML for ACME's index.md should NOT contain doubled paths like /ACME/ACME/.
    /// The @-prefix in links like (@ACME/CustomerOnboarding) is a UCR marker meaning "absolute path",
    /// so it should resolve to /ACME/CustomerOnboarding, not /ACME/ACME/CustomerOnboarding.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Overview_PreRenderedHtml_ShouldNotContainDoubledPaths()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var ct = TestContext.Current.CancellationToken;

        var acmeNode = await meshQuery.QueryAsync<MeshNode>(
                MeshQueryRequest.FromQuery("path:ACME"), null, ct)
            .FirstOrDefaultAsync(ct);

        acmeNode.Should().NotBeNull("ACME node should exist");
        acmeNode!.PreRenderedHtml.Should().NotBeNullOrEmpty("ACME should have pre-rendered HTML");

        Output.WriteLine($"PreRenderedHtml length: {acmeNode.PreRenderedHtml!.Length}");

        // Must NOT contain doubled path
        acmeNode.PreRenderedHtml.Should().NotContain("/ACME/ACME/",
            "@ prefix means absolute UCR path — should not be resolved relatively");

        // Must contain correct link
        acmeNode.PreRenderedHtml.Should().Contain("href=\"/ACME/CustomerOnboarding\"",
            "link to CustomerOnboarding should be absolute /ACME/CustomerOnboarding");
    }

    /// <summary>
    /// All @-prefixed links in ACME's index.md should resolve to the correct absolute paths.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Overview_Links_ShouldResolveToCorrectPaths()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var ct = TestContext.Current.CancellationToken;

        var acmeNode = await meshQuery.QueryAsync<MeshNode>(
                MeshQueryRequest.FromQuery("path:ACME"), null, ct)
            .FirstOrDefaultAsync(ct);

        acmeNode.Should().NotBeNull();
        var html = acmeNode!.PreRenderedHtml;
        html.Should().NotBeNullOrEmpty();

        Output.WriteLine("Checking all expected links in PreRenderedHtml:");

        var expectedLinks = new[]
        {
            "/ACME/CustomerOnboarding",
            "/ACME/ProductLaunch",
            "/ACME/Documentation/GettingStarted",
            "/ACME/Documentation",
            "/ACME/Documentation/Architecture",
            "/ACME/Documentation/AIAgentIntegration",
            "/ACME/Documentation/UnifiedReferences"
        };

        foreach (var link in expectedLinks)
        {
            Output.WriteLine($"  Checking for href=\"{link}\"");
            html.Should().Contain($"href=\"{link}\"",
                $"link {link} should be present in pre-rendered HTML");
        }
    }

    /// <summary>
    /// Navigating to ACME/CustomerOnboarding should render (not spin).
    /// Sanity check that the sub-page node is valid and renderable.
    /// CustomerOnboarding is a Project node — use null area to get the default.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task SubPage_CustomerOnboarding_ShouldRender()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference((string?)null);
        var address = new Address("ACME/CustomerOnboarding");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address,
            reference);

        var control = await stream
            .GetControlStream("")
            .Where(c => c != null)
            .Timeout(10.Seconds())
            .FirstAsync();

        control.Should().NotBeNull("CustomerOnboarding sub-page should render");
    }

    /// <summary>
    /// When navigating to an Organization without specifying an area,
    /// the default area should resolve to Overview (NamedAreaControl pointing to "Overview"),
    /// not SearchArea.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DefaultArea_ShouldResolveToOverview()
    {
        var workspace = GetClient().GetWorkspace();
        // No explicit area — use default (null area triggers default resolution)
        var reference = new LayoutAreaReference((string?)null);
        var orgAddress = new Address("ACME");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            orgAddress,
            reference);

        // When Area is null, the server resolves the default and stores a
        // NamedAreaControl at the empty ("") key pointing to the resolved area name.
        var control = await stream
            .GetControlStream("")
            .Where(c => c != null)
            .Timeout(10.Seconds())
            .FirstAsync();

        control.Should().NotBeNull("default area should render content");
        var namedArea = control.Should().BeOfType<NamedAreaControl>().Subject;
        namedArea.Area.Should().Be("Overview",
            "default area should resolve to Overview, not SearchArea");
    }

}
