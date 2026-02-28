// <meshweaver>
// Id: PricingViews
// DisplayName: ACME Insurance Pricing Views
// </meshweaver>

using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using static MeshWeaver.ContentCollections.ContentCollectionsExtensions;

/// <summary>
/// Views for ACME Insurance Pricing nodes.
/// </summary>
public static class PricingViews
{
    /// <summary>
    /// Registers all pricing views with the layout definition.
    /// </summary>
    public static LayoutDefinition AddPricingViews(this LayoutDefinition layout) =>
        layout
            .WithView("Overview", Overview)
            .WithView("PropertyRisks", PropertyRisks)
            .WithView("RiskMap", RiskMap)
            .WithView("Structure", Structure)
            .WithView("Submission", Submission)
            .WithView("ImportConfigs", ImportConfigs)
            .WithView("Thumbnail", Thumbnail);

    /// <summary>
    /// Builds a navigation toolbar for pricing views.
    /// </summary>
    private static UiControl BuildToolbar(string pricingPath, string activeView)
    {
        return Controls.Toolbar
            .WithView(ToolbarButton("Overview", $"/{pricingPath}/Overview", activeView == "Overview"))
            .WithView(ToolbarButton("Submission", $"/{pricingPath}/Submission", activeView == "Submission"))
            .WithView(ToolbarButton("Property Risks", $"/{pricingPath}/PropertyRisks", activeView == "PropertyRisks"))
            .WithView(ToolbarButton("Risk Map", $"/{pricingPath}/RiskMap", activeView == "RiskMap"))
            .WithView(ToolbarButton("Structure", $"/{pricingPath}/Structure", activeView == "Structure"))
            .WithView(ToolbarButton("Import Configs", $"/{pricingPath}/ImportConfigs", activeView == "ImportConfigs"));
    }

    private static ButtonControl ToolbarButton(string text, string href, bool isActive)
    {
        var button = Controls.Button(text).WithNavigateToHref(href);
        return isActive
            ? button.WithAppearance(Appearance.Accent)
            : button.WithAppearance(Appearance.Stealth);
    }

    /// <summary>
    /// Extracts Pricing from a MeshNode, handling both typed content and JsonElement.
    /// </summary>
    private static Pricing? ExtractPricing(MeshNode? node)
    {
        if (node?.Content is null)
            return null;

        if (node.Content is Pricing pricing)
            return pricing;

        if (node.Content is JsonElement json)
        {
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<Pricing>(json.GetRawText(), options);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Overview view showing pricing header details.
    /// </summary>
    [Display(GroupName = "Details", Order = 0)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var pricingPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>());

        if (nodeStream is null)
            return Observable.Return((UiControl?)Controls.Markdown("# Pricing Overview\n\n*Unable to load pricing data.*"));

        return nodeStream.Select(nodes =>
        {
            var node = nodes?.FirstOrDefault();
            var pricing = ExtractPricing(node);

            if (pricing is null)
            {
                return (UiControl?)Controls.Stack
                    .WithView(BuildToolbar(pricingPath, "Overview"))
                    .WithView(Controls.Markdown("# Pricing Overview\n\n*Loading pricing data...*"));
            }

            var status = PricingStatus.GetById(pricing.Status);
            var statusDisplay = $"{status.Emoji} {status.Name}";

            var coveragePeriod = pricing.InceptionDate.HasValue && pricing.ExpirationDate.HasValue
                ? $"{pricing.InceptionDate:yyyy-MM-dd} to {pricing.ExpirationDate:yyyy-MM-dd}"
                : "Not specified";

            var markdown = $@"# {pricing.InsuredName}

**Status:** {statusDisplay}

## Coverage Period
{coveragePeriod}

## Classification
| Field | Value |
|-------|-------|
| Line of Business | {pricing.LineOfBusiness ?? "N/A"} |
| Country | {pricing.Country ?? "N/A"} |
| Currency | {pricing.Currency ?? "N/A"} |
| Underwriting Year | {pricing.UnderwritingYear?.ToString() ?? "N/A"} |

## Parties
| Role | Name |
|------|------|
| Primary Insurance | {pricing.PrimaryInsurance ?? "N/A"} |
| Broker | {pricing.BrokerName ?? "N/A"} |

## Description
{pricing.Description ?? "*No description provided.*"}
";

            return (UiControl?)Controls.Stack
                .WithView(BuildToolbar(pricingPath, "Overview"))
                .WithView(Controls.Markdown(markdown));
        });
    }

    /// <summary>
    /// Property Risks view with DataGrid display.
    /// </summary>
    [Display(GroupName = "Data", Order = 1)]
    public static IObservable<UiControl?> PropertyRisks(LayoutAreaHost host, RenderingContext _)
    {
        var pricingPath = host.Hub.Address.ToString();
        var riskStream = host.Workspace.GetStream<PropertyRisk>();

        return riskStream.Select(risks =>
        {
            var riskList = risks?.ToList() ?? new List<PropertyRisk>();

            if (!riskList.Any())
            {
                return (UiControl?)Controls.Stack
                    .WithView(BuildToolbar(pricingPath, "PropertyRisks"))
                    .WithView(Controls.Markdown("# Property Risks\n\n*No risks loaded. Import or add risks to begin.*"));
            }

            var dataGrid = RenderRisksDataGrid(host, riskList);

            return (UiControl?)Controls.Stack
                .WithView(BuildToolbar(pricingPath, "PropertyRisks"))
                .WithView(Controls.Title("Property Risks", 1))
                .WithView(dataGrid);
        });
    }

    private static UiControl RenderRisksDataGrid(LayoutAreaHost host, IReadOnlyCollection<PropertyRisk> risks)
    {
        var riskDetails = risks.Select(r => new PropertyRiskDetailsModel
        {
            Id = r.Id,
            SourceRow = r.SourceRow,
            LocationName = r.LocationName,
            Address = r.Address,
            City = r.City,
            State = r.State,
            Country = r.Country,
            TsiBuilding = r.TsiBuilding,
            TsiContent = r.TsiContent,
            TsiBi = r.TsiBi,
            Currency = r.Currency,
            Latitude = r.GeocodedLocation?.Latitude,
            Longitude = r.GeocodedLocation?.Longitude,
        }).ToList();

        var id = Guid.NewGuid().ToString();

        host.RegisterForDisposal(Observable.Return(riskDetails)
            .Subscribe(data => host.UpdateData(id, data)));

        var dataGrid = new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
            .WithColumn(new PropertyColumnControl<int?> { Property = nameof(PropertyRiskDetailsModel.SourceRow).ToCamelCase() }.WithTitle("Row"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.LocationName).ToCamelCase() }.WithTitle("Location"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.Address).ToCamelCase() }.WithTitle("Address"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.City).ToCamelCase() }.WithTitle("City"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.State).ToCamelCase() }.WithTitle("State"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.Country).ToCamelCase() }.WithTitle("Country"))
            .WithColumn(new PropertyColumnControl<double> { Property = nameof(PropertyRiskDetailsModel.TsiBuilding).ToCamelCase() }.WithTitle("TSI Building").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<double> { Property = nameof(PropertyRiskDetailsModel.TsiContent).ToCamelCase() }.WithTitle("TSI Content").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<double> { Property = nameof(PropertyRiskDetailsModel.TsiBi).ToCamelCase() }.WithTitle("TSI BI").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.Currency).ToCamelCase() }.WithTitle("Currency"))
            .WithColumn(new PropertyColumnControl<double?> { Property = nameof(PropertyRiskDetailsModel.Latitude).ToCamelCase() }.WithTitle("Latitude").WithFormat("F4"))
            .WithColumn(new PropertyColumnControl<double?> { Property = nameof(PropertyRiskDetailsModel.Longitude).ToCamelCase() }.WithTitle("Longitude").WithFormat("F4"))
            .WithItemSize(40)
            .Resizable();

        return dataGrid;
    }

    /// <summary>
    /// Risk Map view showing geocoded locations on a Google Map.
    /// </summary>
    [Display(GroupName = "Data", Order = 2)]
    public static IObservable<UiControl?> RiskMap(LayoutAreaHost host, RenderingContext _)
    {
        var pricingPath = host.Hub.Address.ToString();

        return host.Workspace.GetStream<PropertyRisk>()!
            .Select(risks =>
            {
                var riskList = risks?.ToList() ?? new List<PropertyRisk>();
                var geocodedRisks = riskList.Where(r => r.GeocodedLocation?.Latitude != null && r.GeocodedLocation?.Longitude != null).ToList();

                if (!riskList.Any())
                {
                    return (UiControl?)Controls.Stack
                        .WithView(BuildToolbar(pricingPath, "RiskMap"))
                        .WithView(Controls.Markdown("# Risk Map\n\n*No risks loaded. Import or add risks to begin.*"));
                }

                if (!geocodedRisks.Any())
                {
                    return (UiControl?)Controls.Stack
                        .WithView(BuildToolbar(pricingPath, "RiskMap"))
                        .WithView(Controls.Markdown($"# Risk Map\n\n*No geocoded risks found. {riskList.Count} risk(s) available but none have valid coordinates.*\n\n*Use the geocoding feature to resolve addresses to coordinates.*"));
                }

                var mapControl = BuildGoogleMapControl(geocodedRisks);

                return (UiControl?)Controls.Stack
                    .WithView(BuildToolbar(pricingPath, "RiskMap"))
                    .WithView(Controls.Title("Risk Map", 1))
                    .WithView(mapControl);
            });
    }

    private static GoogleMapControl BuildGoogleMapControl(IReadOnlyCollection<PropertyRisk> risks)
    {
        var riskList = risks.Where(r => r.GeocodedLocation?.Latitude is not null && r.GeocodedLocation?.Longitude is not null).ToList();

        LatLng center;
        if (riskList.Any())
        {
            var avgLat = riskList.Average(r => r.GeocodedLocation!.Latitude!.Value);
            var avgLng = riskList.Average(r => r.GeocodedLocation!.Longitude!.Value);
            center = new LatLng(avgLat, avgLng);
        }
        else
        {
            center = new LatLng(0, 0);
        }

        var markers = riskList.Select(r => new MapMarker
        {
            Position = new LatLng(r.GeocodedLocation!.Latitude!.Value, r.GeocodedLocation.Longitude!.Value),
            Title = ((r.LocationName ?? r.Address) + " " + (r.City ?? "")).Trim(),
            Id = r.Id,
            Data = r
        }).ToList();

        var mapOptions = new MapOptions
        {
            Center = center,
            Zoom = riskList.Any() ? 6 : 2,
            MapTypeId = "roadmap",
            ZoomControl = true,
            MapTypeControl = true,
            StreetViewControl = false,
            FullscreenControl = true
        };

        return new GoogleMapControl()
        {
            Options = mapOptions,
            Markers = markers,
            Id = "risk-map"
        }.WithStyle(style => style.WithHeight("500px").WithWidth("100%"));
    }

    /// <summary>
    /// Structure view showing reinsurance layers and sections in data grid tables.
    /// </summary>
    [Display(GroupName = "Data", Order = 3)]
    public static IObservable<UiControl?> Structure(LayoutAreaHost host, RenderingContext _)
    {
        var pricingPath = host.Hub.Address.ToString();

        var acceptanceStream = host.Workspace.GetStream<ReinsuranceAcceptance>();
        var sectionStream = host.Workspace.GetStream<ReinsuranceSection>();

        return Observable.CombineLatest(
            acceptanceStream,
            sectionStream,
            (acceptances, sections) => (acceptances, sections))
            .Select(data =>
            {
                var acceptanceList = data.acceptances?.ToList() ?? new List<ReinsuranceAcceptance>();
                var sectionList = data.sections?.ToList() ?? new List<ReinsuranceSection>();

                if (!acceptanceList.Any() && !sectionList.Any())
                {
                    return (UiControl?)Controls.Stack
                        .WithView(BuildToolbar(pricingPath, "Structure"))
                        .WithView(Controls.Markdown("# Reinsurance Structure\n\n*No reinsurance acceptances loaded. Import or add acceptances to begin.*"));
                }

                var acceptancesGrid = RenderAcceptancesDataGrid(host, acceptanceList);
                var sectionsGrid = RenderSectionsDataGrid(host, sectionList);

                return (UiControl?)Controls.Stack
                    .WithView(BuildToolbar(pricingPath, "Structure"))
                    .WithView(Controls.Title("Reinsurance Structure", 1))
                    .WithView(Controls.Title("Layers", 2))
                    .WithView(acceptancesGrid)
                    .WithView(Controls.Title("Sections", 2))
                    .WithView(sectionsGrid);
            })
            .StartWith((UiControl?)Controls.Stack
                .WithView(BuildToolbar(pricingPath, "Structure"))
                .WithView(Controls.Markdown("# Reinsurance Structure\n\n*Loading...*")));
    }

    private static UiControl RenderAcceptancesDataGrid(LayoutAreaHost host, IReadOnlyCollection<ReinsuranceAcceptance> acceptances)
    {
        var acceptanceDetails = acceptances.Select(a => new AcceptanceDetailsModel
        {
            Id = a.Id,
            Name = a.Name,
            EPI = a.EPI,
            Rate = a.Rate,
            Brokerage = a.Brokerage,
            Commission = a.Commission
        }).OrderBy(a => a.Name ?? a.Id).ToList();

        var id = Guid.NewGuid().ToString();

        host.RegisterForDisposal(Observable.Return(acceptanceDetails)
            .Subscribe(data => host.UpdateData(id, data)));

        var dataGrid = new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(AcceptanceDetailsModel.Id).ToCamelCase() }.WithTitle("ID"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(AcceptanceDetailsModel.Name).ToCamelCase() }.WithTitle("Layer Name"))
            .WithColumn(new PropertyColumnControl<double> { Property = nameof(AcceptanceDetailsModel.EPI).ToCamelCase() }.WithTitle("EPI").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<double> { Property = nameof(AcceptanceDetailsModel.Rate).ToCamelCase() }.WithTitle("Rate").WithFormat("P4"))
            .WithColumn(new PropertyColumnControl<double> { Property = nameof(AcceptanceDetailsModel.Brokerage).ToCamelCase() }.WithTitle("Brokerage").WithFormat("P2"))
            .WithColumn(new PropertyColumnControl<double> { Property = nameof(AcceptanceDetailsModel.Commission).ToCamelCase() }.WithTitle("Commission").WithFormat("P2"))
            .WithItemSize(40)
            .Resizable();

        return dataGrid;
    }

    private static UiControl RenderSectionsDataGrid(LayoutAreaHost host, IReadOnlyCollection<ReinsuranceSection> sections)
    {
        var sectionDetails = sections.Select(s => new SectionDetailsModel
        {
            Id = s.Id,
            AcceptanceId = s.AcceptanceId,
            Name = s.Name,
            LineOfBusiness = s.LineOfBusiness,
            Attach = s.Attach,
            Limit = s.Limit,
            AggAttach = s.AggAttach,
            AggLimit = s.AggLimit
        }).OrderBy(s => s.AcceptanceId).ThenBy(s => s.Name).ToList();

        var id = Guid.NewGuid().ToString();

        host.RegisterForDisposal(Observable.Return(sectionDetails)
            .Subscribe(data => host.UpdateData(id, data)));

        var dataGrid = new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(SectionDetailsModel.Id).ToCamelCase() }.WithTitle("ID"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(SectionDetailsModel.AcceptanceId).ToCamelCase() }.WithTitle("Layer"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(SectionDetailsModel.Name).ToCamelCase() }.WithTitle("Section Name"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(SectionDetailsModel.LineOfBusiness).ToCamelCase() }.WithTitle("LoB"))
            .WithColumn(new PropertyColumnControl<decimal> { Property = nameof(SectionDetailsModel.Attach).ToCamelCase() }.WithTitle("Attachment").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<decimal> { Property = nameof(SectionDetailsModel.Limit).ToCamelCase() }.WithTitle("Limit").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<decimal?> { Property = nameof(SectionDetailsModel.AggAttach).ToCamelCase() }.WithTitle("Agg. Attach").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<decimal?> { Property = nameof(SectionDetailsModel.AggLimit).ToCamelCase() }.WithTitle("Agg. Limit").WithFormat("N0"))
            .WithItemSize(40)
            .Resizable();

        return dataGrid;
    }

    /// <summary>
    /// Submission view showing file browser for submission files.
    /// </summary>
    [Display(GroupName = "Files", Order = 4)]
    public static IObservable<UiControl?> Submission(LayoutAreaHost host, RenderingContext _)
    {
        var pricingPath = host.Hub.Address.ToString();
        var pricingId = host.Hub.Address.Id;
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>());

        if (nodeStream is null)
            return Observable.Return((UiControl?)Controls.Markdown("# Submission\n\n*Unable to load submission data.*"));

        // Get content service and collection config outside the Select to avoid disposal issues
        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var localizedCollectionName = GetLocalizedCollectionName("Submissions", pricingId);
        var collectionConfig = contentService?.GetCollectionConfig(localizedCollectionName);

        return nodeStream.Select(nodes =>
        {
            var node = nodes?.FirstOrDefault();
            var pricing = ExtractPricing(node);

            if (pricing is null)
            {
                return (UiControl?)Controls.Stack
                    .WithView(BuildToolbar(pricingPath, "Submission"))
                    .WithView(Controls.Markdown("# Submission\n\n*Loading...*"));
            }

            var fileBrowser = new FileBrowserControl(localizedCollectionName);
            if (collectionConfig != null)
            {
                fileBrowser = fileBrowser
                    .WithCollectionConfiguration(collectionConfig)
                    .CreatePath();
            }

            return (UiControl?)Controls.Stack
                .WithView(BuildToolbar(pricingPath, "Submission"))
                .WithView(Controls.Title($"Submission - {pricing.InsuredName}", 1))
                .WithView(fileBrowser);
        });
    }

    /// <summary>
    /// ImportConfigs view showing Excel import configurations.
    /// </summary>
    [Display(GroupName = "Configuration", Order = 5)]
    public static IObservable<UiControl?> ImportConfigs(LayoutAreaHost host, RenderingContext _)
    {
        var pricingPath = host.Hub.Address.ToString();
        var pricingId = host.Hub.Address.Id;
        var cfgStream = host.Workspace.GetStream<ExcelImportConfiguration>();

        return cfgStream.Select(cfgs =>
        {
            var list = cfgs?.ToList() ?? new List<ExcelImportConfiguration>();

            if (list.Count == 0)
            {
                return (UiControl?)Controls.Stack
                    .WithView(BuildToolbar(pricingPath, "ImportConfigs"))
                    .WithView(Controls.Markdown("# Import Configurations\n\n*No import configurations found for this pricing.*"));
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var parts = new List<string> { "# Import Configurations" };
            parts.Add($"\n**Pricing:** {pricingId}\n");

            foreach (var cfg in list.OrderBy(x => x.Name))
            {
                parts.Add($"\n## {cfg.Name}");
                var json = JsonSerializer.Serialize(cfg, options);
                parts.Add($"```json\n{json}\n```");
            }

            var md = string.Join("\n", parts);

            return (UiControl?)Controls.Stack
                .WithView(BuildToolbar(pricingPath, "ImportConfigs"))
                .WithView(Controls.Markdown(md));
        });
    }

    /// <summary>
    /// Thumbnail view for catalog display.
    /// Note: Uses Controls.Html for styled card layout as there's no generic Card control
    /// in MeshWeaver.Layout that supports the required domain-specific fields (status badge,
    /// coverage period, etc.). This is an acceptable pattern for complex styled components.
    /// </summary>
    [Display(GroupName = "Cards", Order = 0)]
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var pricingPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>());

        if (nodeStream is null)
            return Observable.Return((UiControl?)Controls.Html(BuildPricingCardHtml(pricingPath, null, null, null)));

        return nodeStream.Select(nodes =>
        {
            var node = nodes?.FirstOrDefault();
            var pricing = ExtractPricing(node);

            if (pricing is null)
            {
                return (UiControl?)Controls.Html(BuildPricingCardHtml(pricingPath, null, null, null));
            }

            var status = PricingStatus.GetById(pricing.Status);
            var coveragePeriod = pricing.InceptionDate.HasValue && pricing.ExpirationDate.HasValue
                ? $"{pricing.InceptionDate:MMM yyyy} - {pricing.ExpirationDate:MMM yyyy}"
                : "Coverage period not set";

            return (UiControl?)Controls.Html(BuildPricingCardHtml(pricingPath, pricing, status, coveragePeriod));
        });
    }

    /// <summary>
    /// Builds the HTML for a pricing card thumbnail.
    /// </summary>
    private static string BuildPricingCardHtml(string pricingPath, Pricing? pricing, PricingStatus? status, string? coveragePeriod)
    {
        if (pricing is null)
        {
            return $@"<a href='/{pricingPath}' class='pricing-card pricing-card--loading'>
  <em>Loading...</em>
</a>
<style>
.pricing-card {{ display:block;text-decoration:none;color:inherit;padding:16px;border:1px solid #e0e0e0;border-radius:8px;background:#fafafa;min-width:280px;max-width:320px;cursor:pointer; }}
.pricing-card:hover {{ border-color:#ccc;background:#f5f5f5; }}
</style>";
        }

        var insuredName = System.Net.WebUtility.HtmlEncode(pricing.InsuredName ?? "Unknown");
        var statusColor = GetStatusColor(status?.Id ?? "Draft");
        var statusEmoji = status?.Emoji ?? "";
        var statusName = status?.Name ?? "Unknown";
        var lob = pricing.LineOfBusiness ?? "N/A";
        var country = pricing.Country ?? "N/A";
        var currency = pricing.Currency ?? "N/A";

        return $@"<a href='/{pricingPath}' class='pricing-card'>
  <div class='pricing-card__content'>
    <div class='pricing-card__header'>
      <div class='pricing-card__title'>{insuredName}</div>
      <div class='pricing-card__status' style='background:{statusColor};'>{statusEmoji} {statusName}</div>
    </div>
    <div class='pricing-card__period'>{coveragePeriod}</div>
    <div class='pricing-card__meta'>
      <span>{lob}</span>
      <span>{country}</span>
      <span>{currency}</span>
    </div>
  </div>
</a>
<style>
.pricing-card {{ display:block;text-decoration:none;color:inherit;padding:16px;border:1px solid #e0e0e0;border-radius:8px;background:#fafafa;min-width:280px;max-width:320px;cursor:pointer; }}
.pricing-card:hover {{ border-color:#ccc;background:#f5f5f5; }}
.pricing-card__content {{ display:flex;flex-direction:column;gap:8px; }}
.pricing-card__header {{ display:flex;justify-content:space-between;align-items:flex-start; }}
.pricing-card__title {{ font-size:1.1em;font-weight:600;color:#333; }}
.pricing-card__status {{ color:white;padding:2px 8px;border-radius:4px;font-size:0.8em;white-space:nowrap; }}
.pricing-card__period {{ color:#666;font-size:0.9em; }}
.pricing-card__meta {{ display:flex;gap:16px;color:#888;font-size:0.85em; }}
</style>";
    }

    private static string GetStatusColor(string statusId)
    {
        return statusId switch
        {
            "Draft" => "#6c757d",
            "Quoted" => "#0d6efd",
            "Bound" => "#198754",
            "Declined" => "#dc3545",
            "Expired" => "#6c757d",
            _ => "#6c757d"
        };
    }
}

/// <summary>
/// Display model for property risk details in the data grid.
/// </summary>
internal record PropertyRiskDetailsModel
{
    public string? Id { get; init; }
    public int? SourceRow { get; init; }
    public string? LocationName { get; init; }
    public string? Address { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? Country { get; init; }
    public double TsiBuilding { get; init; }
    public double TsiContent { get; init; }
    public double TsiBi { get; init; }
    public string? Currency { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
}

/// <summary>
/// Display model for reinsurance acceptance (layer) details in the data grid.
/// </summary>
internal record AcceptanceDetailsModel
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public double EPI { get; init; }
    public double Rate { get; init; }
    public double Brokerage { get; init; }
    public double Commission { get; init; }
}

/// <summary>
/// Display model for reinsurance section details in the data grid.
/// </summary>
internal record SectionDetailsModel
{
    public string? Id { get; init; }
    public string? AcceptanceId { get; init; }
    public string? Name { get; init; }
    public string? LineOfBusiness { get; init; }
    public decimal Attach { get; init; }
    public decimal Limit { get; init; }
    public decimal? AggAttach { get; init; }
    public decimal? AggLimit { get; init; }
}
