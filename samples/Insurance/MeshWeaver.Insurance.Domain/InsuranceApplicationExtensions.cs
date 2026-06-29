using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Import;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Insurance.Domain.Completion;
using MeshWeaver.Insurance.Domain.Services;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Domain;
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

    extension(MessageHubConfiguration configuration)
    {
        /// <summary>
        /// Configures the root Insurance application hub with dimension data and pricing catalog.
        /// </summary>
        public MessageHubConfiguration ConfigureInsuranceApplication()
            => configuration
                .AddEmbeddedResourceContentCollection("Insurance", typeof(InsuranceApplicationExtensions).Assembly, "Content")
                .WithTypes(typeof(ImportConfiguration), typeof(ExcelImportConfiguration), typeof(ReinsuranceAcceptance), typeof(ReinsuranceSection), typeof(ImportRequest), typeof(CollectionSource), typeof(GeocodingRequest), typeof(GeocodingResponse))
                .WithServices(services => services.AddScoped<IAutocompleteProvider, PricingAutocompleteProvider>())
                .AddData(data =>
                {
                    var svc = data.Hub.ServiceProvider.GetRequiredService<IPricingService>();
                    return data.AddSource(src => src
                        .WithType<LineOfBusiness>(t => t.WithInitialData(() => Observable.Return(SampleDataProvider.GetLinesOfBusiness())))
                        .WithType<Country>(t => t.WithInitialData(() => Observable.Return(SampleDataProvider.GetCountries())))
                        .WithType<LegalEntity>(t => t.WithInitialData(() => Observable.Return(SampleDataProvider.GetLegalEntities())))
                        .WithType<Currency>(t => t.WithInitialData(() => Observable.Return(SampleDataProvider.GetCurrencies())))
                        .WithType<Pricing>(t => t.WithInitialData(() => Observable.Return<IEnumerable<Pricing>>(svc.GetCatalog())))
                    );
                })
                .AddLayout(l => l
                    .WithView(nameof(LayoutAreas.PricingCatalogLayoutArea.Pricings),
                        LayoutAreas.PricingCatalogLayoutArea.Pricings)
                )
                .AddContentCollections();

        /// <summary>
        /// Configures a single pricing hub that contains the pricing details and risks.
        /// Addresses have format: pricing/company/year (e.g., pricing/Microsoft/2026)
        /// </summary>
        public MessageHubConfiguration ConfigureSinglePricingApplication()
        {
            return configuration
                .WithServices(AddInsuranceDomainServices)
                .AddContentCollection(sp =>
                {
                    var hub = sp.GetRequiredService<IMessageHub>();
                    var segments = hub.Address.Segments;
                    var conf = sp.GetRequiredService<IConfiguration>();

                    // Segments[0] = pricing, Segments[1] = company, Segments[2] = year
                    if (segments.Length < 3)
                        throw new InvalidOperationException($"Invalid address format: {hub.Address}. Expected format: pricing/company/year");

                    var company = segments[1];
                    var year = segments[2];
                    var subPath = $"{company}/{year}";

                    // Get the global Submissions configuration from appsettings, or create a default one
                    var globalConfig = conf.GetSection("Submissions").Get<ContentCollectionConfig>();

                    // If no configuration exists, create a default FileSystem-based collection
                    if (globalConfig == null)
                    {
                        // Default to a "Submissions" folder in the current directory
                        var defaultBasePath = Path.Combine(Directory.GetCurrentDirectory(), "Submissions");
                        globalConfig = new ContentCollectionConfig
                        {
                            SourceType = FileSystemStreamProvider.SourceType,
                            Name = "Submissions",
                            BasePath = defaultBasePath,
                            DisplayName = "Submission Files"
                        };
                    }

                    // Create localized config with modified name and basepath
                    var pricingId = hub.Address.Id; // company/year
                    var localizedName = GetLocalizedCollectionName("Submissions", pricingId);
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
                            // Bridge each genuine async service leaf reactively (.ToObservable) — IObservable, no Task surface.
                            .WithType<Pricing>(t => t.WithInitialData(() => svc.GetHeaderAsync(pricingId).ToObservable()
                                .Select(pricing => (IEnumerable<Pricing>)(pricing is null ? Array.Empty<Pricing>() : new[] { pricing }))))
                            .WithType<PropertyRisk>(t => t.WithInitialData(() => svc.GetRisksAsync(pricingId, default).ToObservable()))
                            .WithType<ReinsuranceAcceptance>(t => t.WithInitialData(() => Observable.Return(Enumerable.Empty<ReinsuranceAcceptance>())))
                            .WithType<ReinsuranceSection>(t => t.WithInitialData(() => Observable.Return(Enumerable.Empty<ReinsuranceSection>())))
                            .WithType<ExcelImportConfiguration>(t => t.WithInitialData(() =>
                                svc.GetImportConfigurationsAsync(pricingId).ToArrayAsync().AsTask().ToObservable()
                                    .Select(a => (IEnumerable<ExcelImportConfiguration>)a)))
                            // Add dimension data mappings
                            .WithType<LineOfBusiness>(t => t.WithInitialData(() => Observable.Return(SampleDataProvider.GetLinesOfBusiness())))
                            .WithType<Country>(t => t.WithInitialData(() => Observable.Return(SampleDataProvider.GetCountries())))
                            .WithType<LegalEntity>(t => t.WithInitialData(() => Observable.Return(SampleDataProvider.GetLegalEntities())))
                            .WithType<Currency>(t => t.WithInitialData(() => Observable.Return(SampleDataProvider.GetCurrencies())))
                        )
                        // Configure default data reference: data/pricing/pricingId returns the main Pricing entity
                        .WithDefaultDataReference(workspace =>
                            workspace.GetObservable<Pricing>().Select(p => p.FirstOrDefault()))
                        // Configure content provider for file access via data/pricing/pricingId/Submissions/path
                        .WithContentProvider("Submissions", GetLocalizedCollectionName("Submissions", pricingId));
                })
                .AddLayout(l => l
                    .WithDefaultArea(nameof(LayoutAreas.PricingOverviewLayoutArea.Overview))
                    .WithView(nameof(LayoutAreas.PricingOverviewLayoutArea.Overview),
                        LayoutAreas.PricingOverviewLayoutArea.Overview)
                    .WithView(nameof(LayoutAreas.SubmissionLayoutArea.Submission),
                        LayoutAreas.SubmissionLayoutArea.Submission)
                    .WithView(nameof(LayoutAreas.PropertyRisksLayoutArea.PropertyRisks),
                        LayoutAreas.PropertyRisksLayoutArea.PropertyRisks)
                    .WithView(nameof(LayoutAreas.RiskMapLayoutArea.RiskMap),
                        LayoutAreas.RiskMapLayoutArea.RiskMap)
                    .WithView(nameof(LayoutAreas.ReinsuranceAcceptanceLayoutArea.Structure),
                        LayoutAreas.ReinsuranceAcceptanceLayoutArea.Structure)
                    .WithView(nameof(LayoutAreas.ImportConfigsLayoutArea.ImportConfigs),
                        LayoutAreas.ImportConfigsLayoutArea.ImportConfigs)
                    .AddDomainLayoutAreas()
                )
                .AddImport()
                .WithHandler<GeocodingRequest>(HandleGeocodingRequest);
        }
    }

    // Sync handler — compose IObservable chain, Subscribe posts the response.
    // No await on the workspace stream (would deadlock the hub pump); the geocoding
    // HTTP call is bridged via Observable.FromAsync at the EXTERNAL boundary (a
    // pure HTTP client wrapper, not hub-touching — see GoogleGeocodingService).
    // See Doc/Architecture/AsynchronousCalls.md.
    private static IMessageDelivery HandleGeocodingRequest(
        IMessageHub hub,
        IMessageDelivery<GeocodingRequest> request)
    {
        var geocodingService = hub.ServiceProvider.GetRequiredService<IGeocodingService>();
        var riskStream = hub.GetWorkspace().GetStream<PropertyRisk>();
        if (riskStream == null)
        {
            hub.Post(
                new GeocodingResponse { Success = false, GeocodedCount = 0, Error = "No property risks found in workspace" },
                o => o.ResponseFor(request));
            return request.Processed();
        }

        riskStream
            .Select(risks => risks?.ToList() ?? new List<PropertyRisk>())
            .Take(1)
            .SelectMany(riskList =>
            {
                if (riskList.Count == 0)
                    return Observable.Return(new GeocodingResponse
                    {
                        Success = false,
                        GeocodedCount = 0,
                        Error = "No property risks available to geocode"
                    });

                // Reactive service — the HTTP fan-out runs inside its bounded Http I/O queue.
                return geocodingService.GeocodeRisks(riskList);
            })
            .Subscribe(
                geocodingResponse =>
                {
                    if (geocodingResponse is { Success: true, UpdatedRisks: not null }
                        && geocodingResponse.UpdatedRisks.Any())
                    {
                        hub.Post(
                            new DataChangeRequest { Updates = geocodingResponse.UpdatedRisks.ToList() },
                            o => o.WithTarget(hub.Address));
                    }
                    hub.Post(geocodingResponse, o => o.ResponseFor(request));
                },
                ex =>
                    hub.Post(
                        new GeocodingResponse { Success = false, GeocodedCount = 0, Error = $"Geocoding failed: {ex.Message}" },
                        o => o.ResponseFor(request)));

        return request.Processed();
    }

}
