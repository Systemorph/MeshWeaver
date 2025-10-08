using MeshWeaver.Data;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Extension methods for configuring the Insurance application.
/// </summary>
public static class InsuranceApplicationExtensions
{
    /// <summary>
    /// Configures the root Insurance application hub with dimension data.
    /// </summary>
    public static MessageHubConfiguration ConfigureInsuranceApplication(this MessageHubConfiguration configuration)
        => configuration
            .AddData(data =>
            {
                return data.AddSource(src => src
                    .WithType<LineOfBusiness>(t => t.WithInitialData(_ => Task.FromResult(SampleDataProvider.GetLinesOfBusiness())))
                    .WithType<Country>(t => t.WithInitialData(_ => Task.FromResult(SampleDataProvider.GetCountries())))
                    .WithType<LegalEntity>(t => t.WithInitialData(_ => Task.FromResult(SampleDataProvider.GetLegalEntities())))
                    .WithType<Currency>(t => t.WithInitialData(_ => Task.FromResult(SampleDataProvider.GetCurrencies())))
                );
            });

    /// <summary>
    /// Configures the pricing catalog hub that lists all pricings.
    /// </summary>
    public static MessageHubConfiguration ConfigurePricingCatalogApplication(this MessageHubConfiguration configuration)
    {
        return configuration
            .AddData(data =>
            {
                return data.AddSource(src => src
                    .WithType<Pricing>(t => t.WithInitialData(_ => Task.FromResult(SampleDataProvider.GetSamplePricings())))
                );
            })
            .AddLayout(l => l
                .WithView(nameof(LayoutAreas.PricingCatalogLayoutArea.PricingCatalog),
                    LayoutAreas.PricingCatalogLayoutArea.PricingCatalog)
            );
    }

    /// <summary>
    /// Configures a single pricing hub that contains the pricing details and risks.
    /// </summary>
    public static MessageHubConfiguration ConfigureSinglePricingApplication(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(typeof(InsuranceApplicationExtensions))
            .AddData(data =>
            {
                var pricingId = data.Hub.Address.Id;
                var allPricings = SampleDataProvider.GetSamplePricings();

                return data.AddSource(src => src
                    .WithType<Pricing>(t => t.WithInitialData(ct =>
                    {
                        var pricing = allPricings.FirstOrDefault(p => p.Id == pricingId);
                        return Task.FromResult<IEnumerable<Pricing>>(pricing is null ? [] : [pricing]);
                    }))
                    .WithType<PropertyRisk>(t => t.WithInitialData(ct =>
                    {
                        // Initially empty - risks can be imported or created
                        return Task.FromResult(Enumerable.Empty<PropertyRisk>());
                    }))
                    .WithType<ExcelImportConfiguration>(t => t.WithInitialData(ct =>
                    {
                        // Initially empty - configurations can be added dynamically
                        return Task.FromResult(Enumerable.Empty<ExcelImportConfiguration>());
                    }))
                );
            })
            .AddLayout(l => l
                .WithView(nameof(LayoutAreas.PricingOverviewLayoutArea.Overview),
                    LayoutAreas.PricingOverviewLayoutArea.Overview)
                .WithView(nameof(LayoutAreas.PropertyRisksLayoutArea.PropertyRisks),
                    LayoutAreas.PropertyRisksLayoutArea.PropertyRisks)
                .WithView(nameof(LayoutAreas.ImportConfigsLayoutArea.ImportConfigs),
                    LayoutAreas.ImportConfigsLayoutArea.ImportConfigs)
            );
    }
}
