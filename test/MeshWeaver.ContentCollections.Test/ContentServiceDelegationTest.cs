using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

public class ContentServiceDelegationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string _parentPath = Path.Combine(AppContext.BaseDirectory, "Files", "Parent");
    private readonly string _childPath = Path.Combine(AppContext.BaseDirectory, "Files", "Child");

    protected override MessageHubConfiguration ConfigureRouter(MessageHubConfiguration configuration)
    {
        // Register ParentCollection at router level so both host and client can access it
        return configuration
            .AddContentCollections()
            .WithFileSystemContentCollection("ParentCollection", _ => _parentPath);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return configuration
            .AddContentCollections()
            .WithFileSystemContentCollection("ChildCollection", _ => _childPath);
    }

    [Fact]
    public async Task ContentService_ShouldDelegateToParent()
    {
        // Arrange
        var parentHub = Router; // Use router as the top-level hub
        var childHub = GetClient();

        Output.WriteLine($"Parent hub address: {parentHub.Address}");
        Output.WriteLine($"Parent hub's parent: {parentHub.Configuration.ParentHub?.Address}");
        Output.WriteLine($"Child hub address: {childHub.Address}");
        Output.WriteLine($"Child hub parent: {childHub.Configuration.ParentHub?.Address}");

        // Check parent chain
        var current = childHub;
        var depth = 0;
        while (current != null && depth < 10)
        {
            Output.WriteLine($"  Chain[{depth}]: {current.Address}");
            var cs = current.ServiceProvider.GetService<IContentService>();
            Output.WriteLine($"    Has ContentService: {cs != null}");
            if (cs != null)
            {
                var colls = await cs.GetCollectionsAsync(TestContext.Current.CancellationToken);
                Output.WriteLine($"    Collections: {string.Join(", ", colls.Select(c => c.Collection))}");
            }
            Output.WriteLine($"    ParentHub: {current.Configuration.ParentHub?.Address}");
            Output.WriteLine($"    ParentHub == current: {current.Configuration.ParentHub == current}");
            if (current.Configuration.ParentHub == current || current.Configuration.ParentHub == null)
                break;
            current = current.Configuration.ParentHub;
            depth++;
        }
        Output.WriteLine($"  Total depth: {depth}");

        var parentContentService = parentHub.ServiceProvider.GetRequiredService<IContentService>();
        var childContentService = childHub.ServiceProvider.GetRequiredService<IContentService>();

        Output.WriteLine($"Parent ContentService instance: {parentContentService.GetHashCode()}");
        Output.WriteLine($"Child ContentService instance: {childContentService.GetHashCode()}");
        Output.WriteLine($"ContentServices are same: {ReferenceEquals(parentContentService, childContentService)}");

        // Debug: Check what collections each service has
        var parentCollections = await parentContentService.GetCollectionsAsync(TestContext.Current.CancellationToken);
        var childCollections = await childContentService.GetCollectionsAsync(TestContext.Current.CancellationToken);
        Output.WriteLine($"Parent content service has {parentCollections.Count} collections: {string.Join(", ", parentCollections.Select(c => c.Collection))}");
        Output.WriteLine($"Child content service has {childCollections.Count} collections: {string.Join(", ", childCollections.Select(c => c.Collection))}");

        // Act & Assert - Parent hub can only reach ParentCollection
        var parentCollectionFromParent = await parentContentService.GetCollectionAsync("ParentCollection", TestContext.Current.CancellationToken);
        Output.WriteLine($"Parent collection from parent: {parentCollectionFromParent?.Collection}");
        parentCollectionFromParent.Should().NotBeNull("parent hub should have ParentCollection");

        var childCollectionFromParent = await parentContentService.GetCollectionAsync("ChildCollection", TestContext.Current.CancellationToken);
        Output.WriteLine($"Child collection from parent: {childCollectionFromParent?.Collection}");
        childCollectionFromParent.Should().BeNull("parent hub should NOT have ChildCollection");

        // Act & Assert - Child hub can reach both collections
        Output.WriteLine($"About to get ParentCollection from child...");
        var parentCollectionFromChild = await childContentService.GetCollectionAsync("ParentCollection", TestContext.Current.CancellationToken);
        Output.WriteLine($"Parent collection from child: {parentCollectionFromChild?.Collection}");
        Output.WriteLine($"  Got from child, hash: {parentCollectionFromChild?.GetHashCode()}");

        // Also try getting it directly from parent to compare
        var directFromParent = await parentContentService.GetCollectionAsync("ParentCollection", TestContext.Current.CancellationToken);
        Output.WriteLine($"  Got directly from parent, hash: {directFromParent?.GetHashCode()}");
        Output.WriteLine($"  Are they the same: {ReferenceEquals(parentCollectionFromChild, directFromParent)}");

        parentCollectionFromChild.Should().NotBeNull("child hub should have ParentCollection via delegation");

        var childCollectionFromChild = await childContentService.GetCollectionAsync("ChildCollection", TestContext.Current.CancellationToken);
        childCollectionFromChild.Should().NotBeNull("child hub should have its own ChildCollection");

        // Debug: Check instance details
        Output.WriteLine($"parentCollectionFromParent instance: {parentCollectionFromParent!.GetHashCode()}");
        Output.WriteLine($"parentCollectionFromChild instance: {parentCollectionFromChild!.GetHashCode()}");
        Output.WriteLine($"Are they reference equal: {ReferenceEquals(parentCollectionFromParent, parentCollectionFromChild)}");

        // Act & Assert - The ParentCollection from child should be reference equal to the one from parent
        ReferenceEquals(parentCollectionFromParent, parentCollectionFromChild).Should().BeTrue(
            "child hub should get the same ParentCollection instance from parent (reference equality)");

        // Act & Assert - Verify we can actually read content from the collections
        var parentContent = await parentContentService.GetContentAsync("ParentCollection", "test.txt", TestContext.Current.CancellationToken);
        parentContent.Should().NotBeNull("parent collection should have test.txt");
        using (var reader = new StreamReader(parentContent!))
        {
            var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            content.Should().Contain("Parent collection test file");
        }

        var childContent = await childContentService.GetContentAsync("ChildCollection", "test.txt", TestContext.Current.CancellationToken);
        childContent.Should().NotBeNull("child collection should have test.txt");
        using (var reader = new StreamReader(childContent!))
        {
            var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            content.Should().Contain("Child collection test file");
        }

        // Act & Assert - Child can read parent's files via delegation
        var parentContentFromChild = await childContentService.GetContentAsync("ParentCollection", "test.txt", TestContext.Current.CancellationToken);
        parentContentFromChild.Should().NotBeNull("child should be able to read from ParentCollection via delegation");
        using (var reader = new StreamReader(parentContentFromChild!))
        {
            var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            content.Should().Contain("Parent collection test file");
        }
    }
}
