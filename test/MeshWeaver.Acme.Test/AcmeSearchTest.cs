using System;
using MeshWeaver.Blazor.Portal;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
/// Tests that the ACME space root node is findable via search.
/// Validates the scope:subtree fix in SearchHub.ExecuteTextSearchAsync.
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
    /// QueryAsync goes through the catalog index which is populated asynchronously
    /// during persistence init. Locally this completes within the implicit test
    /// startup window; CI is slower and the first call can return an empty set
    /// before the FileSystem partition's recursive scan finishes. Poll the query
    /// over a short window so the tests aren't flaky in environments where the
    /// scan hasn't yet seen the ACME organization node.
    /// </summary>
    private async Task<List<MeshNode>> QueryUntilAcmeIndexedAsync(string query, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        // 50s polling window inside the [Fact(Timeout = 60000)] cap. The
        // FileSystem partition's catalog scan can take 20-40s on cold CI
        // runners (Linux, fresh testhost, swap-pressured); a 20s budget hit
        // its ceiling consistently. 50s leaves enough headroom for the test
        // assertion + Output.WriteLine + dispose without re-flaking the cap.
        var deadline = DateTime.UtcNow.AddSeconds(50);
        List<MeshNode> results = new();
        while (DateTime.UtcNow < deadline)
        {
            results = await meshService.QueryAsync<MeshNode>(query, ct: ct).ToListAsync(ct);
            if (results.Any(n => n.Path == "ACME" && n.NodeType == "Space"))
                return results;
            await Task.Delay(200, ct);
        }
        return results;
    }

    [Fact(Timeout = 60000)]
    public async Task SubtreeSearch_FindsOrganizationRootNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = await QueryUntilAcmeIndexedAsync(
            "*ACME* scope:subtree is:main limit:50", ct);

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
        var ct = TestContext.Current.CancellationToken;
        var results = await QueryUntilAcmeIndexedAsync(
            "*ACME* scope:descendants is:main limit:50", ct);

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
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var results = await meshService
            .QueryAsync<MeshNode>("*ACME* scope:subtree context:search is:main limit:50")
            .ToListAsync();

        results.Should().Contain(n => n.Path == "ACME",
            "portal search with context:search should find the ACME root node");
    }

    [Fact(Timeout = 60000)]
    public async Task AcmeOrganization_IsAccessibleToAuthenticatedUser()
    {
        // This tests what the portal does when navigating to /ACME:
        // SecurePersistenceServiceDecorator.HasReadAccessAsync → SpaceAccessRule
        // → SecurityService.HasPermissionAsync("ACME", userId, Permission.Read)
        // The Public_Access.json at ACME/_Access grants Viewer to Public,
        // and SecurityService merges Public permissions as floor for authenticated users.
        // SecurityService loads AccessAssignment satellites asynchronously via the
        // synced query — locally this completes within test startup, CI is slower
        // and the first HasPermissionAsync call returns false before the scan
        // catches the ACME/_Access partition. Poll over a short window.
        var ct = TestContext.Current.CancellationToken;
        var userId = TestUsers.Admin.ObjectId;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        var hasRead = false;
        while (DateTime.UtcNow < deadline)
        {
            hasRead = await Mesh.HasPermissionAsync("ACME", userId, Permission.Read, ct);
            if (hasRead) break;
            await Task.Delay(200, ct);
        }
        hasRead.Should().BeTrue(
            "authenticated users should have Read access to ACME via Public_Access.json Viewer role");

        // Also verify the node is actually fetchable (not just permission-granted).
        // Path resolution for the ACME root reads samples/Graph/Data/ACME/index.md
        // whose YAML frontmatter declares `NodeType: Space`. On CI Linux
        // runners the FIRST read can return a node with NodeType="Markdown"
        // (the parser's fallback when frontmatter hasn't been bound yet, or a
        // cache miss that returns the JSON-shape default before the markdown
        // parser runs). Poll until the typed Space shape lands so the
        // assertion isn't flaky on cold cache.
        var nodeDeadline = DateTime.UtcNow.AddSeconds(25);
        MeshNode? node = null;
        while (DateTime.UtcNow < nodeDeadline)
        {
            node = await ReadNodeAsync("ACME");
            if (node?.NodeType == "Space") break;
            await Task.Delay(200, ct);
        }
        node.Should().NotBeNull("ACME node should exist");
        node!.NodeType.Should().Be("Space");
    }
}
