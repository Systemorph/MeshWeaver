using FluentAssertions;
using MeshWeaver.Insurance.Domain.Services;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Insurance.Test;

public class PricingCatalogTests(ITestOutputHelper output) : InsuranceTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<IPricingService, InMemoryPricingService>()
            );
    }

    [Fact]
    public async Task GetPricingCatalog_ShouldReturnPricings()
    {
        // Act - Get the pricing catalog from the Insurance hub
        var pricings = await GetPricingsAsync();

        // Assert - Verify that the catalog contains pricings
        pricings.Should().NotBeNull("catalog should not be null");
        pricings.Should().NotBeEmpty("catalog should contain sample pricings");

        // Verify that pricings have required fields
        pricings.All(p => !string.IsNullOrWhiteSpace(p.Id)).Should().BeTrue("all pricings should have an Id");
        pricings.All(p => !string.IsNullOrWhiteSpace(p.InsuredName)).Should().BeTrue("all pricings should have an InsuredName");
        pricings.All(p => !string.IsNullOrWhiteSpace(p.Status)).Should().BeTrue("all pricings should have a Status");

        // Output summary
        Output.WriteLine($"Successfully retrieved {pricings.Count} pricings from catalog");
        foreach (var pricing in pricings)
        {
            Output.WriteLine($"  - {pricing.Id}: {pricing.InsuredName} ({pricing.Status}) - {pricing.LineOfBusiness}/{pricing.Country}");
        }
    }

    [Fact]
    public async Task GetPricingCatalog_ShouldHaveValidDimensions()
    {
        // Act
        var pricings = await GetPricingsAsync();

        // Assert - Verify dimension fields are populated
        pricings.Should().NotBeEmpty();

        pricings.All(p => !string.IsNullOrWhiteSpace(p.LineOfBusiness)).Should().BeTrue("all pricings should have a LineOfBusiness");
        pricings.All(p => !string.IsNullOrWhiteSpace(p.Country)).Should().BeTrue("all pricings should have a Country");
        pricings.All(p => !string.IsNullOrWhiteSpace(p.LegalEntity)).Should().BeTrue("all pricings should have a LegalEntity");
        pricings.All(p => !string.IsNullOrWhiteSpace(p.Currency)).Should().BeTrue("all pricings should have a Currency");

        // Output dimension information
        Output.WriteLine("Pricing dimensions:");
        Output.WriteLine($"  Lines of Business: {string.Join(", ", pricings.Select(p => p.LineOfBusiness).Distinct())}");
        Output.WriteLine($"  Countries: {string.Join(", ", pricings.Select(p => p.Country).Distinct())}");
        Output.WriteLine($"  Legal Entities: {string.Join(", ", pricings.Select(p => p.LegalEntity).Distinct())}");
        Output.WriteLine($"  Currencies: {string.Join(", ", pricings.Select(p => p.Currency).Distinct())}");
    }

    [Fact]
    public async Task GetPricingCatalog_ShouldHaveValidDates()
    {
        // Act
        var pricings = await GetPricingsAsync();

        // Assert
        pricings.Should().NotBeEmpty();

        foreach (var pricing in pricings)
        {
            pricing.InceptionDate.Should().NotBeNull(
                $"pricing {pricing.Id} should have an inception date");
            pricing.ExpirationDate.Should().NotBeNull(
                $"pricing {pricing.Id} should have an expiration date");

            if (pricing.InceptionDate.HasValue && pricing.ExpirationDate.HasValue)
            {
                pricing.ExpirationDate.Value.Should().BeAfter(pricing.InceptionDate.Value,
                    $"pricing {pricing.Id} expiration date should be after inception date");
            }

            pricing.UnderwritingYear.Should().NotBeNull(
                $"pricing {pricing.Id} should have an underwriting year");
            pricing.UnderwritingYear.Should().BeGreaterThan(2000,
                $"pricing {pricing.Id} should have a valid underwriting year");
        }

        Output.WriteLine($"All {pricings.Count} pricings have valid dates");
    }

    [Fact]
    public async Task PricingHub_ShouldStartSuccessfully()
    {
        // This test verifies that the pricing hub initializes correctly
        // by successfully retrieving the catalog without errors

        // Act
        var pricings = await GetPricingsAsync();

        // Assert - Hub started if we can get data
        pricings.Should().NotBeNull("hub should start and return catalog");

        // Verify the hub is accessible
        Mesh.Should().NotBeNull("mesh should be initialized");
        Mesh.Address.Should().NotBeNull("mesh should have an address");

        Output.WriteLine($"Pricing hub started successfully");
        Output.WriteLine($"Hub Address: {Mesh.Address}");
        Output.WriteLine($"Retrieved {pricings.Count} pricings from catalog");
    }
}
