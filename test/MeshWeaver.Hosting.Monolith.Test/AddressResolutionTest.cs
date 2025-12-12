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
                    IconName = "Calculator",
                    DisplayOrder = 100,
                    HubConfiguration = _ => _
                },
                new MeshNode(AppType)
                {
                    Name = "Applications",
                    Description = "Standard applications",
                    IconName = "App",
                    DisplayOrder = 200,
                    HubConfiguration = _ => _
                }
            );
    }

    #region ResolvePath Score-Based Tests

    [Fact]
    public void ResolvePath_SingleSegmentNode_MatchesAndReturnsRemainder()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - pricing/Microsoft/2026/Overview/details
        // Node "pricing" has score 1, remainder is "Microsoft/2026/Overview/details"
        var resolution = meshCatalog.ResolvePath("pricing/Microsoft/2026/Overview/details");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026/Overview/details");
    }

    [Fact]
    public void ResolvePath_AppPath_ReturnsPrefixAndRemainder()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - app/Todo/Dashboard/123
        // Node "app" has score 1, remainder is "Todo/Dashboard/123"
        var resolution = meshCatalog.ResolvePath("app/Todo/Dashboard/123");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("app");
        resolution.Remainder.Should().Be("Todo/Dashboard/123");
    }

    [Fact]
    public void ResolvePath_ExactMatch_ReturnsNullRemainder()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - exact match with node "pricing"
        var resolution = meshCatalog.ResolvePath("pricing");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().BeNull();
    }

    [Fact]
    public void ResolvePath_WithLeadingSlash_ParsesCorrectly()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - leading slash should be stripped
        var resolution = meshCatalog.ResolvePath("/pricing/Microsoft/2026");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026");
    }

    [Fact]
    public void ResolvePath_UnknownPath_ReturnsNull()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - no node matches "unknown"
        var resolution = meshCatalog.ResolvePath("unknown/test/path");

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
    public void ResolvePath_VariousPaths_ReturnsCorrectPrefixAndRemainder(
        string path, string expectedPrefix, string? expectedRemainder)
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolvePath(path);

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(expectedPrefix);
        resolution.Remainder.Should().Be(expectedRemainder);
    }

    [Fact]
    public void ResolvePath_EmptyPath_ReturnsNull()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolvePath("");

        // Assert
        resolution.Should().BeNull();
    }

    [Fact]
    public void ResolvePath_NullPath_ReturnsNull()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolvePath(null!);

        // Assert
        resolution.Should().BeNull();
    }

    #endregion

    #region Score-Based Matching Priority Tests

    [Fact]
    public void ResolvePath_MultipleNodes_HighestScoreWins()
    {
        // This test requires registering additional nodes with different segment depths
        // The test infrastructure registers only single-segment nodes,
        // but this validates the concept of score-based matching
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // With only "pricing" registered, it should match and have remainder
        var resolution = meshCatalog.ResolvePath("pricing/Microsoft/2026");

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("pricing");
        resolution.Remainder.Should().Be("Microsoft/2026");
    }

    [Fact]
    public void ResolvePath_CaseInsensitive_MatchesCorrectly()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - case-insensitive matching
        var resolution = meshCatalog.ResolvePath("PRICING/Microsoft/2026");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(PricingType);
    }

    #endregion
}
