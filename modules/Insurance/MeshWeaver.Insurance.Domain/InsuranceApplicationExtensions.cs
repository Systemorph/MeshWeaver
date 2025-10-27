using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Import;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Insurance.Domain.Services;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static MeshWeaver.ContentCollections.ContentCollectionsExtensions;

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
            .WithTypes(typeof(PricingAddress), typeof(ImportConfiguration), typeof(ExcelImportConfiguration), typeof(Structure), typeof(ImportRequest), typeof(CollectionSource))
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
            .AddContentCollection(sp =>
            {
                var hub = sp.GetRequiredService<IMessageHub>();
                var addressId = hub.Address.Id;
                var configuration = sp.GetRequiredService<IConfiguration>();

                // Get the global Submissions configuration from appsettings
                var globalConfig = configuration.GetSection("Submissions").Get<ContentCollectionConfig>();
                if (globalConfig == null)
                    throw new InvalidOperationException("Submissions collection not found in configuration");

                // Parse addressId in format {company}-{uwy}
                var parts = addressId.Split('-');
                if (parts.Length != 2)
                    throw new InvalidOperationException($"Invalid address format: {addressId}. Expected format: {{company}}-{{uwy}}");

                var company = parts[0];
                var uwy = parts[1];
                var subPath = $"{company}/{uwy}";

                // Create localized config with modified name and basepath
                var localizedName = GetLocalizedCollectionName("Submissions", addressId);
                var fullPath = string.IsNullOrEmpty(subPath)
                    ? globalConfig.BasePath ?? ""
                    : System.IO.Path.Combine(globalConfig.BasePath ?? "", subPath);

                return globalConfig with
                {
                    Name = localizedName,
                    BasePath = fullPath
                };
            })
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
                    .WithType<Structure>(t => t.WithInitialData(_ => Task.FromResult(Enumerable.Empty<Structure>())))
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
            )
            .AddImport();
    }
}
