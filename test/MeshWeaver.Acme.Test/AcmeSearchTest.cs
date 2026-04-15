using System;
using Memex.Portal.Shared;
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
/// Tests that the ACME organization root node is findable via search.
/// Validates the scope:subtree fix in SearchHub.ExecuteTextSearchAsync.
/// </summary>
public class AcmeSearchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
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
            .AddOrganizationType()
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

    [Fact(Timeout = 60000)]
    public async Task SubtreeSearch_FindsOrganizationRootNode()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var results = await meshService
            .QueryAsync<MeshNode>("*ACME* scope:subtree is:main limit:50")
            .ToListAsync();

        results.Should().Contain(n => n.Path == "ACME" && n.NodeType == "Organization",
            "scope:subtree should include the ACME root node itself");
    }

    [Fact(Timeout = 60000)]
    public async Task DescendantsSearch_MissesOrganizationRootNode()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var results = await meshService
            .QueryAsync<MeshNode>("*ACME* scope:descendants is:main limit:50")
            .ToListAsync();

        results.Should().NotContain(n => n.Path == "ACME",
            "scope:descendants should NOT include the ACME root node (documents the bug behavior)");
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
        // SecurePersistenceServiceDecorator.HasReadAccessAsync → OrganizationAccessRule
        // → SecurityService.HasPermissionAsync("ACME", userId, Permission.Read)
        // The Public_Access.json at ACME/_Access grants Viewer to Public,
        // and SecurityService merges Public permissions as floor for authenticated users.
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var userId = TestUsers.Admin.ObjectId;

        var hasRead = await securityService.HasPermissionAsync(
            "ACME", userId, Permission.Read, CancellationToken.None);

        hasRead.Should().BeTrue(
            "authenticated users should have Read access to ACME via Public_Access.json Viewer role");

        // Also verify the node is actually fetchable (not just permission-granted)
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = await meshService.QueryAsync<MeshNode>("path:ACME").FirstOrDefaultAsync();
        node.Should().NotBeNull("ACME node should exist");
        node!.NodeType.Should().Be("Organization");
    }
}
