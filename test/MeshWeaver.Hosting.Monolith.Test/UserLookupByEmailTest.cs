using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for looking up User nodes by content.email,
/// matching the pattern used by UserContextMiddleware.TryLoadMeshUserAsync.
/// </summary>
public class UserLookupByEmailTest : MonolithMeshTestBase
{
    private readonly string _cacheDirectory;

    public UserLookupByEmailTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverUserLookupTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData)
            .AddUserData()
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .AddGraph();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (Directory.Exists(_cacheDirectory))
        {
            try { Directory.Delete(_cacheDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    [Fact(Timeout = 10000)]
    public async Task ContentEmailQuery_FindsUserByEmail()
    {
        // Arrange - Roland.json has content.email = "rbuergi@systemorph.com"
        var email = "rbuergi@systemorph.com";
        var query = $"nodeType:User namespace:User content.email:{email} limit:1";

        // Act - use ImpersonateAsHub scope like the middleware does
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        MeshNode[] results;
        using (accessService.ImpersonateAsHub(Mesh))
        {
            results = await MeshQuery.QueryAsync<MeshNode>(
                query, ct: TestContext.Current.CancellationToken
            ).ToArrayAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Output.WriteLine($"Query: '{query}'");
        Output.WriteLine($"Found {results.Length} results");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} (NodeType={r.NodeType})");

        results.Should().HaveCount(1, "Should find exactly one User node for this email");
        results[0].Name.Should().Be("Roland Buergi");
        results[0].NodeType.Should().Be("User");
    }

    [Fact(Timeout = 10000)]
    public async Task ContentEmailQuery_NonExistentEmail_ReturnsEmpty()
    {
        // Arrange - email that doesn't exist in any User node
        var email = "nonexistent@example.com";
        var query = $"nodeType:User namespace:User content.email:{email} limit:1";

        // Act
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        MeshNode[] results;
        using (accessService.ImpersonateAsHub(Mesh))
        {
            results = await MeshQuery.QueryAsync<MeshNode>(
                query, ct: TestContext.Current.CancellationToken
            ).ToArrayAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Output.WriteLine($"Query: '{query}'");
        Output.WriteLine($"Found {results.Length} results");

        results.Should().BeEmpty("Non-existent email should return no results");
    }

    [Fact(Timeout = 10000)]
    public async Task ContentEmailQuery_NameCanOverrideClaim()
    {
        // Arrange - simulate the middleware flow:
        // claim says name is "R. Buergi" but mesh User node says "Roland Buergi"
        var claimEmail = "rbuergi@systemorph.com";
        var claimName = "R. Buergi";

        // Act - look up mesh user by email (middleware pattern)
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        MeshNode? meshUser = null;
        using (accessService.ImpersonateAsHub(Mesh))
        {
            await foreach (var node in MeshQuery.QueryAsync<MeshNode>(
                $"nodeType:User namespace:User content.email:{claimEmail} limit:1",
                ct: TestContext.Current.CancellationToken))
            {
                meshUser = node;
                break;
            }
        }

        // Override name if found
        var finalName = meshUser is not null && !string.IsNullOrEmpty(meshUser.Name)
            ? meshUser.Name
            : claimName;

        // Assert
        Output.WriteLine($"Claim name: '{claimName}'");
        Output.WriteLine($"Mesh user name: '{meshUser?.Name}'");
        Output.WriteLine($"Final name: '{finalName}'");

        meshUser.Should().NotBeNull("Should find Roland in the mesh");
        finalName.Should().Be("Roland Buergi", "Mesh user name should override the claim name");
        finalName.Should().NotBe(claimName);
    }
}
