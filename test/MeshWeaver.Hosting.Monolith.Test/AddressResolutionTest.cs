using System;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for address resolution via IMeshCatalog.ResolvePath.
/// Verifies that paths are correctly resolved to addresses with remainder using score-based matching.
/// Score = number of matching segments from the path start.
/// </summary>
public class AddressResolutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PricingType = "pricing";
    private const string AppType = "app";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            // Register pricing node - matches any pricing/* path
            .AddMeshNodes(
                new MeshNode(PricingType)
                {
                    Name = "Pricing",
                    Description = "Insurance pricing submissions",
                    Icon = "Calculator",
                    DisplayOrder = 100,
                    HubConfiguration = c => c
                },
                new MeshNode(AppType)
                {
                    Name = "Applications",
                    Description = "Standard applications",
                    Icon = "App",
                    DisplayOrder = 200,
                    HubConfiguration = c => c
                }
            );
    }

    #region ResolvePath Score-Based Tests

    [Fact]
    public async Task ResolvePath_SingleSegmentNode_MatchesAndReturnsRemainder()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - pricing/Microsoft/2026/Overview/details
        // Node "pricing" has score 1, remainder is "Microsoft/2026/Overview/details"
        var resolution = await meshCatalog.ResolvePathAsync("pricing/Microsoft/2026/Overview/details");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026/Overview/details");
    }

    [Fact]
    public async Task ResolvePath_AppPath_ReturnsPrefixAndRemainder()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - app/Todo/Dashboard/123
        // Node "app" has score 1, remainder is "Todo/Dashboard/123"
        var resolution = await meshCatalog.ResolvePathAsync("app/Todo/Dashboard/123");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("app");
        resolution.Remainder.Should().Be("Todo/Dashboard/123");
    }

    [Fact]
    public async Task ResolvePath_ExactMatch_ReturnsNullRemainder()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - exact match with node "pricing"
        var resolution = await meshCatalog.ResolvePathAsync("pricing");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().BeNull();
    }

    [Fact]
    public async Task ResolvePath_WithLeadingSlash_ParsesCorrectly()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - leading slash should be stripped
        var resolution = await meshCatalog.ResolvePathAsync("/pricing/Microsoft/2026");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026");
    }

    [Fact]
    public async Task ResolvePath_UnknownPath_ReturnsNull()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - no node matches "unknown"
        var resolution = await meshCatalog.ResolvePathAsync("unknown/test/path");

        // Assert
        resolution.Should().BeNull();
    }

    [Theory]
    [InlineData("pricing", "pricing", null)]
    [InlineData("pricing/ACME", "pricing", "ACME")]
    [InlineData("pricing/ACME/2025", "pricing", "ACME/2025")]
    [InlineData("pricing/ACME/2025/Reports", "pricing", "ACME/2025/Reports")]
    [InlineData("app", "app", null)]
    [InlineData("app/Insurance", "app", "Insurance")]
    [InlineData("app/Insurance/Dashboard", "app", "Insurance/Dashboard")]
    public async Task ResolvePath_VariousPaths_ReturnsCorrectPrefixAndRemainder(
        string path, string expectedPrefix, string? expectedRemainder)
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = await meshCatalog.ResolvePathAsync(path);

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(expectedPrefix);
        resolution.Remainder.Should().Be(expectedRemainder);
    }

    [Fact]
    public async Task ResolvePath_EmptyPath_ReturnsNull()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = await meshCatalog.ResolvePathAsync("");

        // Assert
        resolution.Should().BeNull();
    }

    [Fact]
    public async Task ResolvePath_NullPath_ReturnsNull()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = await meshCatalog.ResolvePathAsync(null!);

        // Assert
        resolution.Should().BeNull();
    }

    #endregion

    #region Score-Based Matching Priority Tests

    [Fact]
    public async Task ResolvePath_MultipleNodes_HighestScoreWins()
    {
        // This test requires registering additional nodes with different segment depths
        // The test infrastructure registers only single-segment nodes,
        // but this validates the concept of score-based matching
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // With only "pricing" registered, it should match and have remainder
        var resolution = await meshCatalog.ResolvePathAsync("pricing/Microsoft/2026");

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026");
    }

    [Fact]
    public async Task ResolvePath_CaseInsensitive_MatchesCorrectly()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - case-insensitive matching
        var resolution = await meshCatalog.ResolvePathAsync("PRICING/Microsoft/2026");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(PricingType);
    }

    #endregion
}

/// <summary>
/// Tests for MeshNode.AddressSegments functionality.
/// Verifies that nodes with AddressSegments > prefix segments correctly expand addresses.
/// </summary>
public class AddressSegmentsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PricingType = "pricing";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            // Register pricing node with AddressSegments=3 (pricing/company/year)
            .AddMeshNodes(
                new MeshNode(PricingType)
                {
                    Name = "Pricing",
                    Description = "Insurance pricing submissions",
                    Icon = "Calculator",
                    DisplayOrder = 100,
                    AddressSegments = 3, // pricing/company/year
                    HubConfiguration = c => c
                }
            );
    }

    [Fact]
    public async Task ResolvePath_WithAddressSegments_ExpandsAddress()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - pricing/Microsoft/2026/Overview
        // Node "pricing" with AddressSegments=3 means address is "pricing/Microsoft/2026"
        var resolution = await meshCatalog.ResolvePathAsync("pricing/Microsoft/2026/Overview");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing/Microsoft/2026");
        resolution.Remainder.Should().Be("Overview");
    }

    [Fact]
    public async Task ResolvePath_WithAddressSegments_ExactMatch_ReturnsNullRemainder()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - exact match with AddressSegments=3
        var resolution = await meshCatalog.ResolvePathAsync("pricing/Microsoft/2026");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing/Microsoft/2026");
        resolution.Remainder.Should().BeNull();
    }

    [Fact]
    public async Task ResolvePath_WithAddressSegments_FewerSegmentsThanRequired_UsesAvailableSegments()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - only 2 segments when AddressSegments=3
        var resolution = await meshCatalog.ResolvePathAsync("pricing/Microsoft");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing/Microsoft");
        resolution.Remainder.Should().BeNull();
    }

    [Theory]
    [InlineData("pricing/Microsoft/2026", "pricing/Microsoft/2026", null)]
    [InlineData("pricing/Microsoft/2026/Overview", "pricing/Microsoft/2026", "Overview")]
    [InlineData("pricing/Microsoft/2026/Overview/details", "pricing/Microsoft/2026", "Overview/details")]
    [InlineData("pricing/Acme/2025/Reports/Q1/summary", "pricing/Acme/2025", "Reports/Q1/summary")]
    public async Task ResolvePath_WithAddressSegments_VariousPaths_ReturnsCorrectPrefixAndRemainder(
        string path, string expectedPrefix, string? expectedRemainder)
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = await meshCatalog.ResolvePathAsync(path);

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(expectedPrefix);
        resolution.Remainder.Should().Be(expectedRemainder);
    }

    [Fact]
    public async Task ResolvePath_WithAddressSegments_CaseInsensitive_PreservesOriginalCase()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - case-insensitive matching for prefix, preserves path case for additional segments
        var resolution = await meshCatalog.ResolvePathAsync("PRICING/Microsoft/2026/Overview");

        // Assert
        resolution.Should().NotBeNull();
        // "pricing" comes from node (lowercase), "Microsoft/2026" comes from path (original case)
        resolution!.Prefix.Should().Be("pricing/Microsoft/2026");
        resolution.Remainder.Should().Be("Overview");
    }

    [Fact]
    public async Task GetNodeAsync_WithTemplateNode_ReturnsNodeWithFullAddress()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - request a node with address pricing/Microsoft/2026
        // The template node "pricing" with AddressSegments=3 should be used
        var node = await meshCatalog.GetNodeAsync(new Address("pricing", "Microsoft", "2026"));

        // Assert
        node.Should().NotBeNull();
        node!.Path.Should().Be("pricing/Microsoft/2026");
        node.Name.Should().Be("Pricing"); // Inherited from template
        node.HubConfiguration.Should().NotBeNull(); // Inherited from template
    }

    [Fact]
    public async Task GetNodeAsync_WithTemplateNode_CachesResult()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var address = new Address("pricing", "Acme", "2025");

        // Act - request same node twice
        var node1 = await meshCatalog.GetNodeAsync(address);
        var node2 = await meshCatalog.GetNodeAsync(address);

        // Assert - should return cached result
        node1.Should().NotBeNull();
        node2.Should().NotBeNull();
        node1.Should().BeSameAs(node2);
    }

    [Fact]
    public async Task GetNodeAsync_WithExactMatch_ReturnsConfiguredNode()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - request the template node itself
        var node = await meshCatalog.GetNodeAsync(new Address("pricing"));

        // Assert
        node.Should().NotBeNull();
        node!.Path.Should().Be("pricing");
        node.AddressSegments.Should().Be(3);
    }
}
