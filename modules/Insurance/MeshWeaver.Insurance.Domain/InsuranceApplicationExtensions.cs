using System.Reactive.Linq;
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
    /// Adds Insurance domain services to the service collection.
    /// </summary>
    private static IServiceCollection AddInsuranceDomainServices(this IServiceCollection services)
    {
        // Register pricing service
        services.AddSingleton<IGeocodingService, GoogleGeocodingService>();

        return services;
    }

    /// <summary>
    /// Configures the root Insurance application hub with dimension data and pricing catalog.
    /// </summary>
    public static MessageHubConfiguration ConfigureInsuranceApplication(this MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(PricingAddress), typeof(ImportConfiguration), typeof(ExcelImportConfiguration), typeof(Structure), typeof(ImportRequest), typeof(CollectionSource), typeof(GeocodingRequest), typeof(GeocodingResponse))
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
            .WithServices(AddInsuranceDomainServices)
            .AddContentCollection(sp =>
            {
                var hub = sp.GetRequiredService<IMessageHub>();
                var addressId = hub.Address.Id;
                var conf = sp.GetRequiredService<IConfiguration>();

                // Get the global Submissions configuration from appsettings
                var globalConfig = conf.GetSection("Submissions").Get<ContentCollectionConfig>();
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
                    : Path.Combine(globalConfig.BasePath ?? "", subPath);

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
            .AddImport()
            .WithHandler<GeocodingRequest>(HandleGeocodingRequest);
    }

    private static async Task<IMessageDelivery> HandleGeocodingRequest(
        IMessageHub hub,
        IMessageDelivery<GeocodingRequest> request,
        CancellationToken ct)
    {
        try
        {
            // Get the geocoding service
            var geocodingService = hub.ServiceProvider.GetRequiredService<IGeocodingService>();

            // Get the current property risks from the workspace
            var workspace = hub.GetWorkspace();
            var riskStream = workspace.GetStream<PropertyRisk>();
            if (riskStream == null)
            {
                var errorResponse = new GeocodingResponse
                {
                    Success = false,
                    GeocodedCount = 0,
                    Error = "No property risks found in workspace"
                };
                hub.Post(errorResponse, o => o.ResponseFor(request));
                return request.Processed();
            }

            var risks = await riskStream.FirstAsync();
            var riskList = risks?.ToList() ?? new List<PropertyRisk>();

            if (!riskList.Any())
            {
                var errorResponse = new GeocodingResponse
                {
                    Success = false,
                    GeocodedCount = 0,
                    Error = "No property risks available to geocode"
                };
                hub.Post(errorResponse, o => o.ResponseFor(request));
                return request.Processed();
            }

            // Geocode the risks
            var geocodingResponse = await geocodingService.GeocodeRisksAsync(riskList, ct);

            // If successful and we have updated risks, update the workspace
            if (geocodingResponse.Success && geocodingResponse.UpdatedRisks != null && geocodingResponse.UpdatedRisks.Any())
            {
                // Update the workspace with the geocoded risks
                var dataChangeRequest = new DataChangeRequest
                {
                    Updates = geocodingResponse.UpdatedRisks.ToList()
                };

                await hub.AwaitResponse(dataChangeRequest, o => o.WithTarget(hub.Address), ct);
            }

            // Post the response
            hub.Post(geocodingResponse, o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            var errorResponse = new GeocodingResponse
            {
                Success = false,
                GeocodedCount = 0,
                Error = $"Geocoding failed: {ex.Message}"
            };
            hub.Post(errorResponse, o => o.ResponseFor(request));
        }

        return request.Processed();
    }
}
