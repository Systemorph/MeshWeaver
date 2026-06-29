using System;
using MeshWeaver.Graph;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Blazor.Portal.Components;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Acme.Test;

/// <summary>
/// Tests that the ACME space root node is findable via search — both the mesh
/// query engine directly and the portal search box's reactive composer
/// (<see cref="MeshSearch.Suggestions"/>).
/// </summary>
public class AcmeSearchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Read-only queries against the shared ACME sample graph — no node
    // mutation, no permission changes. Sharing the SP cuts ~6 SP rebuilds
    // and reuses the dynamic NodeType DLL cache across [Fact]s.
    protected override bool ShareMeshAcrossTests => true;

    // Stable cache directory so compiled dynamic NodeType DLLs survive across
    // test runs. The timestamped-subdir cache layout (a3ab9909e) writes each
    // compile to a unique subdir so a stale DLL pinned by a prior process's
    // ALC is never touched — no file-lock collision.
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverAcmeSearchTests",
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
            .AddSpaceType()
            .AddRowLevelSecurity()
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

    /// <summary>
    /// Query goes through the catalog index which is populated asynchronously
    /// during persistence init. Locally this completes within the implicit test
    /// startup window; CI is slower and the Initial snapshot can be empty before the
    /// FileSystem partition's recursive scan finishes. Accumulate the live deltas
    /// (Initial / Reset / Added / Updated) into a running list and block until the
    /// ACME Space node appears, so the tests aren't flaky in environments where the
    /// scan hasn't yet seen the ACME organization node.
    /// </summary>
    private async Task<IReadOnlyList<MeshNode>> QueryUntilAcmeIndexed(string query)
    {
        var byPath = new Dictionary<string, MeshNode>(StringComparer.Ordinal);
        return await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>(), (_, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    byPath.Clear();
                foreach (var n in change.Items)
                {
                    if (n.Path is not { } p) continue;
                    if (change.ChangeType is QueryChangeType.Removed)
                        byPath.Remove(p);
                    else
                        byPath[p] = n;
                }
                return byPath.Values.ToList();
            })
            // 50s window inside the [Fact(Timeout = 60000)] cap. The FileSystem
            // partition's catalog scan can take 20-40s on cold CI runners.
            .Should().Within(50.Seconds())
            .Match(results => results.Any(n => n.Path == "ACME" && n.NodeType == "Space"));
    }

    [Fact(Timeout = 60000)]
    public async Task SubtreeSearch_FindsOrganizationRootNode()
    {
        var results = await QueryUntilAcmeIndexed(
            "*ACME* scope:subtree is:main limit:50");

        results.Should().Contain(n => n.Path == "ACME" && n.NodeType == "Space",
            "scope:subtree should include the ACME root node itself");
    }

    [Fact(Timeout = 60000)]
    public async Task DescendantsSearch_FindsOrganizationRootNode()
    {
        // Updated 2026-04-24: was DescendantsSearch_MissesOrganizationRootNode and asserted
        // the bug behavior. The query engine was fixed elsewhere; scope:descendants now
        // returns the ACME root node when its name matches the wildcard. Test now asserts
        // the corrected behavior so a future regression of the original bug is caught.
        var results = await QueryUntilAcmeIndexed(
            "*ACME* scope:descendants is:main limit:50");

        results.Should().Contain(n => n.Path == "ACME" && n.NodeType == "Space",
            "scope:descendants should include the ACME root node (NodeType: Space)");
    }

    [Fact]
    public void AcmeIndexLinks_ResolveWithoutPathDuplication()
    {
        var indexPath = Path.Combine(TestPaths.SamplesGraphData, "ACME", "index.md");
        var markdown = File.ReadAllText(indexPath);

        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(null, "ACME");
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var links = document.Descendants<LinkInline>()
            .Where(l => !l.IsImage && l.Url != null)
            .Select(l => l.Url!)
            .ToList();

        links.Should().NotBeEmpty("ACME index.md should contain links");
        links.Should().NotContain(url => url.Contains("ACME/ACME", StringComparison.OrdinalIgnoreCase),
            "links should not contain duplicated ACME path prefix");
    }

    [Fact(Timeout = 60000)]
    public async Task PortalSearch_WithContextSearch_FindsAcme()
    {
        var results = await QueryUntilAcmeIndexed(
            "*ACME* scope:subtree context:search is:main limit:50");

        results.Should().Contain(n => n.Path == "ACME",
            "portal search with context:search should find the ACME root node");
    }

    /// <summary>
    /// Repro for "no matter what I type in search, I cannot see anything": the
    /// top-bar <c>SearchBar</c> composes its suggestion stream via
    /// <see cref="MeshSearch.Suggestions"/>, an <c>IObservable</c> of the WHOLE
    /// suggestion collection. The free-text branch goes through the progressive
    /// <see cref="IMeshService.Query"/> snapshot surface (CombineLatest +
    /// per-source StartWith), so the search re-emits as each source converges and
    /// the caller binds the entire collection per emission — no channels, no
    /// async-enumerable streaming that could drop results.
    ///
    /// The query engine itself returns ACME (asserted first, which also lets the
    /// async catalog index settle), so this isolates the search composition from
    /// index lag: if the engine has ACME, the bound collection must contain it.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SearchSuggestions_FreeText_BindsWholeCollectionContainingMatch()
    {
        // Gate on the engine actually returning ACME so the assertion below can't
        // be excused by the catalog scan not having indexed it yet.
        var indexed = await QueryUntilAcmeIndexed(
            "*ACME* scope:descendants context:search is:main limit:50");
        indexed.Should().Contain(n => n.Path == "ACME",
            "precondition: the mesh query engine must return the ACME node");

        // Subscribe to the search box's suggestion stream and wait for a bound
        // snapshot that contains ACME. MeshSearch.Suggestions merges every source
        // progressively; the assertion accepts the first snapshot in which the
        // match appears (later sources only add/re-order).
        var snapshot = await MeshSearch
            .Suggestions(MeshQuery, "ACME", contextPath: null, maxResults: 10)
            .Should().Within(30.Seconds())
            .Match(list => list.Any(s => s.Path == "ACME"));

        snapshot.Should().Contain(s => s.Path == "ACME",
            "free-text search must bind a whole collection that includes the matching node");
    }

    [Fact(Timeout = 60000)]
    public async Task AcmeOrganization_IsAccessibleToAuthenticatedUser()
    {
        // This tests what the portal does when navigating to /ACME:
        // SecurePersistenceServiceDecorator.HasReadAccessAsync → SpaceAccessRule
        // → CheckPermission("ACME", userId, Permission.Read)
        // The Public_Access.json at ACME/_Access grants Viewer to Public,
        // and SecurityService merges Public permissions as floor for authenticated users.
        // SecurityService loads AccessAssignment satellites asynchronously via the
        // synced query — locally this completes within test startup, CI is slower
        // and the first emission can be false before the scan catches the
        // ACME/_Access partition. Block for the granted emission over a short window.
        var userId = TestUsers.Admin.ObjectId;
        var hasRead = await Mesh.CheckPermission("ACME", userId, Permission.Read)
            .Should().Within(25.Seconds()).Match(granted => granted);
        hasRead.Should().BeTrue(
            "authenticated users should have Read access to ACME via Public_Access.json Viewer role");

        // Also verify the node is actually fetchable (not just permission-granted).
        // Path resolution for the ACME root reads samples/Graph/Data/ACME/index.md
        // whose YAML frontmatter declares `NodeType: Space`. On CI Linux
        // runners the FIRST read can return a node with NodeType="Markdown"
        // (the parser's fallback when frontmatter hasn't been bound yet, or a
        // cache miss that returns the JSON-shape default before the markdown
        // parser runs). Block until the typed Space shape lands so the
        // assertion isn't flaky on cold cache.
        var node = await ReadNode("ACME")
            .Should().Within(25.Seconds()).Match(n => n?.NodeType == "Space");
        node.Should().NotBeNull("ACME node should exist");
        node!.NodeType.Should().Be("Space");
    }
}
