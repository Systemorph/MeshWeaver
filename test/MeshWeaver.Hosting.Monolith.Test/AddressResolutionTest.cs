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
/// Verifies that paths are correctly resolved to addresses with remainder.
/// </summary>
public class AddressResolutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PricingType = "pricing";
    private const string AppType = "app";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .AddMeshNamespace(new MeshNamespace(PricingType, "Pricing")
            {
                Description = "Insurance pricing submissions",
                IconName = "Calculator",
                DisplayOrder = 100,
                MinSegments = 2, // company + year (e.g., pricing/Microsoft/2026)
                Factory = address =>
                    address.Type == PricingType
                        ? new MeshNode(address, address.ToString())
                        {
                            HubConfiguration = _ => _
                        }
                        : null
            })
            .AddMeshNamespace(new MeshNamespace(AppType, "Applications")
            {
                Description = "Standard applications",
                IconName = "App",
                DisplayOrder = 200,
                MinSegments = 1
            });
    }

    #region ResolvePath Tests

    [Fact]
    public void ResolvePath_PricingPath_ReturnsAddressAndRemainder()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - pricing/Microsoft/2026/Overview/details
        var resolution = meshCatalog.ResolvePath("pricing/Microsoft/2026/Overview/details");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Address.ToString().Should().Be("pricing/Microsoft/2026");
        resolution.Remainder.Should().Be("Overview/details");
    }

    [Fact]
    public void ResolvePath_AppPath_ReturnsAddressAndRemainder()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - app/Todo/Dashboard/123
        var resolution = meshCatalog.ResolvePath("app/Todo/Dashboard/123");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Address.ToString().Should().Be("app/Todo");
        resolution.Remainder.Should().Be("Dashboard/123");
    }

    [Fact]
    public void ResolvePath_WithoutRemainder_ReturnsNullRemainder()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - app/Todo (no remainder)
        var resolution = meshCatalog.ResolvePath("app/Todo");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Address.ToString().Should().Be("app/Todo");
        resolution.Remainder.Should().BeNull();
    }

    [Fact]
    public void ResolvePath_WithLeadingSlash_ParsesCorrectly()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - /pricing/Microsoft/2026/Overview
        var resolution = meshCatalog.ResolvePath("/pricing/Microsoft/2026/Overview");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Address.ToString().Should().Be("pricing/Microsoft/2026");
        resolution.Remainder.Should().Be("Overview");
    }

    [Fact]
    public void ResolvePath_UnknownPath_ReturnsNull()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolvePath("unknown/test/path");

        // Assert
        resolution.Should().BeNull();
    }

    [Theory]
    [InlineData("pricing/ACME/2025", "pricing/ACME/2025", null)]
    [InlineData("pricing/ACME/2025/Reports", "pricing/ACME/2025", "Reports")]
    [InlineData("pricing/ACME/2025/Reports/quarterly", "pricing/ACME/2025", "Reports/quarterly")]
    [InlineData("app/Insurance", "app/Insurance", null)]
    [InlineData("app/Insurance/Dashboard", "app/Insurance", "Dashboard")]
    [InlineData("app/Insurance/Claims/C-123/details", "app/Insurance", "Claims/C-123/details")]
    public void ResolvePath_VariousPaths_ReturnsCorrectAddressAndRemainder(
        string path, string expectedAddress, string? expectedRemainder)
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolvePath(path);

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Address.ToString().Should().Be(expectedAddress);
        resolution.Remainder.Should().Be(expectedRemainder);
    }

    [Fact]
    public void ResolvePath_PricingWithClientAndYear_HasCorrectAddress()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - pricing/Microsoft/2026 should have Type=pricing, Id=Microsoft/2026
        var resolution = meshCatalog.ResolvePath("pricing/Microsoft/2026");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Address.Type.Should().Be(PricingType);
        resolution.Address.ToString().Should().Be("pricing/Microsoft/2026");
        resolution.Remainder.Should().BeNull();
    }

    #endregion

    #region ResolveAddress Tests

    [Fact]
    public void ResolveAddress_RegisteredNamespace_ReturnsResolution()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolveAddress(PricingType, "Microsoft/2026");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Address.Type.Should().Be(PricingType);
        resolution.Address.Id.Should().Be("Microsoft/2026");
    }

    [Fact]
    public void ResolveAddress_UnregisteredNamespace_ReturnsNull()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolveAddress("unknown", "test");

        // Assert
        resolution.Should().BeNull();
    }

    [Fact]
    public void ResolveAddress_AppTypeWithoutId_ReturnsNull()
    {
        // Arrange - app requires MinSegments=1, so just "app" won't match
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolveAddress(AppType);

        // Assert - app requires at least one segment, so type-only won't match
        resolution.Should().BeNull();
    }

    [Theory]
    [InlineData(PricingType, "ACME/2025", "pricing/ACME/2025")]
    [InlineData(PricingType, "Microsoft/2026", "pricing/Microsoft/2026")]
    [InlineData(AppType, "Todo", "app/Todo")]
    [InlineData(AppType, "Insurance", "app/Insurance")]
    public void ResolveAddress_VariousAddresses_ReturnsCorrectToString(
        string addressType, string id, string expectedString)
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolveAddress(addressType, id);

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Address.ToString().Should().Be(expectedString);
    }

    #endregion

    #region GetNamespacesAsync Tests

    [Fact]
    public async Task GetNamespacesAsync_ReturnsAllRegisteredNamespaces()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var namespaces = await meshCatalog.GetNamespacesAsync(TestContext.Current.CancellationToken);

        // Assert
        namespaces.Should().HaveCountGreaterThanOrEqualTo(2);
        namespaces.Should().Contain(n => n.Prefix == PricingType);
        namespaces.Should().Contain(n => n.Prefix == AppType);
    }

    #endregion
}
