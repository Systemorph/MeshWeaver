using MeshWeaver.Data;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Insurance.Domain.Services;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Extension methods for configuring the Insurance application.
/// </summary>
public static class InsuranceApplicationExtensions
{
    /// <summary>
    /// Configures the root Insurance application hub with dimension data and pricing catalog.
    /// </summary>
    public static MessageHubConfiguration ConfigureInsuranceApplication(this MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(PricingAddress))
            .AddData(data =>
            {
                var svc = data.Hub.ServiceProvider.GetRequiredService<IPricingService>();
                return data.AddSource(src => src
                    .WithType<LineOfBusiness>(t => t.WithInitialData(_ => Task.FromResult(SampleDataProvider.GetLinesOfBusiness())))
                    .WithType<Country>(t => t.WithInitialData(_ => Task.FromResult(SampleDataProvider.GetCountries())))
                    .WithType<LegalEntity>(t => t.WithInitialData(_ => Task.FromResult(SampleDataProvider.GetLegalEntities())))
                    .WithType<Currency>(t => t.WithInitialData(_ => Task.FromResult(SampleDataProvider.GetCurrencies())))
                    .WithType<Pricing>(t => t.WithInitialData(_ => Task.FromResult<IEnumerable<Pricing>>(svc.GetCatalog())))
                );
            })
            .AddLayout(l => l
                .WithView(nameof(LayoutAreas.PricingCatalogLayoutArea.Pricings),
                    LayoutAreas.PricingCatalogLayoutArea.Pricings)
            );

    /// <summary>
    /// Configures a single pricing hub that contains the pricing details and risks.
    /// </summary>
    public static MessageHubConfiguration ConfigureSinglePricingApplication(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(typeof(InsuranceApplicationExtensions))
            .AddData(data =>
            {
                var svc = data.Hub.ServiceProvider.GetRequiredService<IPricingService>();
                var pricingId = data.Hub.Address.Id;

                return data.AddSource(src => src
                    .WithType<Pricing>(t => t.WithInitialData(async ct =>
                    {
                        var pricing = await svc.GetHeaderAsync(pricingId);
                        return pricing is null ? [] : [pricing];
                    }))
                    .WithType<PropertyRisk>(t => t.WithInitialData(async ct =>
                        (IEnumerable<PropertyRisk>)await svc.GetRisksAsync(pricingId, ct)))
                    .WithType<ExcelImportConfiguration>(t => t.WithInitialData(async ct =>
                        await svc.GetImportConfigurationsAsync(pricingId).ToArrayAsync(ct)))
                );
            })
            .AddLayout(l => l
                .WithView(nameof(LayoutAreas.PricingOverviewLayoutArea.Overview),
                    LayoutAreas.PricingOverviewLayoutArea.Overview)
                .WithView(nameof(LayoutAreas.SubmissionLayoutArea.Submission),
                    LayoutAreas.SubmissionLayoutArea.Submission)
                .WithView(nameof(LayoutAreas.PropertyRisksLayoutArea.PropertyRisks),
                    LayoutAreas.PropertyRisksLayoutArea.PropertyRisks)
                .WithView(nameof(LayoutAreas.RiskMapLayoutArea.RiskMap),
                    LayoutAreas.RiskMapLayoutArea.RiskMap)
                .WithView(nameof(LayoutAreas.ImportConfigsLayoutArea.ImportConfigs),
                    LayoutAreas.ImportConfigsLayoutArea.ImportConfigs)
            );
    }
}
