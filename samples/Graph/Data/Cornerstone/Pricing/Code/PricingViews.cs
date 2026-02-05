// <meshweaver>
// Id: PricingViews
// DisplayName: Cornerstone Pricing Views
// </meshweaver>

using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
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
/// Views for Cornerstone Pricing nodes.
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

    // Color definitions for Structure diagram
    private static readonly string PricingColor = "#2c7bb6";
    private static readonly string PricingTextColor = "#ffffff";
    private static readonly string AcceptanceColor = "#fdae61";
    private static readonly string AcceptanceTextColor = "#000000";
    private static readonly string SectionColor = "#abd9e9";
    private static readonly string SectionTextColor = "#000000";

    /// <summary>
    /// Builds a navigation toolbar for pricing views.
    /// </summary>
    private static UiControl BuildToolbar(string pricingPath, string activeView)
    {
        return Controls.Toolbar
            .WithView(ToolbarButton("Overview", $"/{pricingPath}/Overview", activeView == "Overview"))
            .WithView(ToolbarButton("Submission", $"/{pricingPath}/Submission", activeView == "Submission"))
            .WithView(ToolbarButton("Risks", $"/{pricingPath}/PropertyRisks", activeView == "PropertyRisks"))
            .WithView(ToolbarButton("Map", $"/{pricingPath}/RiskMap", activeView == "RiskMap"))
            .WithView(ToolbarButton("Structure", $"/{pricingPath}/Structure", activeView == "Structure"))
            .WithView(ToolbarButton("Import", $"/{pricingPath}/ImportConfigs", activeView == "ImportConfigs"));
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
        var nodeStream = host.Workspace.GetStream<MeshNode>();

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
    /// Structure view showing reinsurance hierarchy as a Mermaid flowchart.
    /// </summary>
    [Display(GroupName = "Data", Order = 3)]
    public static IObservable<UiControl?> Structure(LayoutAreaHost host, RenderingContext _)
    {
        var pricingPath = host.Hub.Address.ToString();
        var pricingId = host.Hub.Address.Id;

        var nodeStream = host.Workspace.GetStream<MeshNode>();
        var acceptanceStream = host.Workspace.GetStream<ReinsuranceAcceptance>();
        var sectionStream = host.Workspace.GetStream<ReinsuranceSection>();

        return Observable.CombineLatest(
            nodeStream,
            acceptanceStream,
            sectionStream,
            (nodes, acceptances, sections) => (nodes, acceptances, sections))
            .Select(data =>
            {
                var node = data.nodes?.FirstOrDefault();
                var pricing = ExtractPricing(node);
                var acceptanceList = data.acceptances?.ToList() ?? new List<ReinsuranceAcceptance>();
                var sectionList = data.sections?.ToList() ?? new List<ReinsuranceSection>();

                if (!acceptanceList.Any() && !sectionList.Any())
                {
                    return (UiControl?)Controls.Stack
                        .WithView(BuildToolbar(pricingPath, "Structure"))
                        .WithView(Controls.Markdown("# Reinsurance Structure\n\n*No reinsurance acceptances loaded. Import or add acceptances to begin.*"));
                }

                var diagram = BuildMermaidDiagram(pricingId, pricing, acceptanceList, sectionList);
                var mermaidControl = new MarkdownControl($"```mermaid\n{diagram}\n```")
                    .WithStyle(style => style.WithWidth("100%").WithHeight("600px"));

                return (UiControl?)Controls.Stack
                    .WithView(BuildToolbar(pricingPath, "Structure"))
                    .WithView(Controls.Title("Reinsurance Structure", 1))
                    .WithView(mermaidControl);
            })
            .StartWith((UiControl?)Controls.Stack
                .WithView(BuildToolbar(pricingPath, "Structure"))
                .WithView(Controls.Markdown("# Reinsurance Structure\n\n*Loading...*")));
    }

    private static string BuildMermaidDiagram(string pricingId, Pricing? pricing, List<ReinsuranceAcceptance> acceptances, List<ReinsuranceSection> sections)
    {
        var sb = new StringBuilder();

        sb.AppendLine("flowchart TD");
        sb.AppendLine("    classDef leftAlign text-align:left");

        // Add the pricing node
        var pricingContent = new StringBuilder();
        pricingContent.Append($"<b>Pricing: {pricingId}</b>");

        if (pricing?.PrimaryInsurance != null)
        {
            pricingContent.Append($"<br/>Primary: {pricing.PrimaryInsurance}");
        }

        if (pricing?.BrokerName != null)
        {
            pricingContent.Append($"<br/>Broker: {pricing.BrokerName}");
        }

        sb.AppendLine($"    pricing[\"{pricingContent}\"]");
        sb.AppendLine($"    style pricing fill:{PricingColor},color:{PricingTextColor},stroke:#333,stroke-width:1px");
        sb.AppendLine($"    class pricing leftAlign");

        // Group sections by acceptanceId
        var sectionsByAcceptance = sections
            .Where(s => s.AcceptanceId != null)
            .GroupBy(s => s.AcceptanceId)
            .ToDictionary(g => g.Key!, g => g.ToList());

        // Add acceptances and their sections
        foreach (var acceptance in acceptances.OrderBy(a => a.Name ?? a.Id))
        {
            RenderAcceptance(sb, acceptance);

            if (sectionsByAcceptance.TryGetValue(acceptance.Id, out var acceptanceSections))
            {
                foreach (var section in acceptanceSections.OrderBy(s => s.LineOfBusiness).ThenBy(s => s.Attach))
                {
                    RenderSection(sb, section, acceptance.Id);
                }
            }
        }

        return sb.ToString();
    }

    private static void RenderAcceptance(StringBuilder sb, ReinsuranceAcceptance acceptance)
    {
        string acceptanceId = SanitizeId(acceptance.Id);
        var acceptanceName = acceptance.Name ?? acceptance.Id;

        var acceptanceContent = new StringBuilder();
        acceptanceContent.Append($"<b>{acceptanceName}</b>");

        if (acceptance.EPI > 0)
            acceptanceContent.Append($"<br/>EPI: {acceptance.EPI:N0}");

        if (acceptance.Rate > 0)
            acceptanceContent.Append($"<br/>Rate: {acceptance.Rate:P2}");

        if (acceptance.Share > 0)
            acceptanceContent.Append($"<br/>Share: {acceptance.Share:P2}");

        if (acceptance.Cession > 0)
            acceptanceContent.Append($"<br/>Cession: {acceptance.Cession:P2}");

        if (acceptance.Brokerage > 0)
            acceptanceContent.Append($"<br/>Brokerage: {acceptance.Brokerage:P2}");

        if (acceptance.Commission > 0)
            acceptanceContent.Append($"<br/>Commission: {acceptance.Commission:P2}");

        sb.AppendLine($"    acc_{acceptanceId}[\"{acceptanceContent}\"]");
        sb.AppendLine($"    style acc_{acceptanceId} fill:{AcceptanceColor},color:{AcceptanceTextColor},stroke:#333,stroke-width:1px");
        sb.AppendLine($"    class acc_{acceptanceId} leftAlign");
        sb.AppendLine($"    pricing --> acc_{acceptanceId}");
    }

    private static void RenderSection(StringBuilder sb, ReinsuranceSection section, string acceptanceId)
    {
        string sectionId = SanitizeId(section.Id);
        string sanitizedAcceptanceId = SanitizeId(acceptanceId);

        var sectionContent = new StringBuilder();
        sectionContent.Append($"<b>{section.Name ?? section.LineOfBusiness ?? section.Id}</b><br/>");

        if (!string.IsNullOrEmpty(section.LineOfBusiness) && section.LineOfBusiness != section.Name)
        {
            sectionContent.Append($"LoB: {section.LineOfBusiness}<br/>");
        }

        sectionContent.Append($"Attach: {section.Attach:N0}<br/>");
        sectionContent.Append($"Limit: {section.Limit:N0}<br/>");

        if (section.AggAttach.HasValue && section.AggAttach.Value > 0)
        {
            sectionContent.Append($"AAD: {section.AggAttach.Value:N0}<br/>");
        }

        if (section.AggLimit.HasValue && section.AggLimit.Value > 0)
        {
            sectionContent.Append($"AAL: {section.AggLimit.Value:N0}");
        }

        sb.AppendLine($"    sec_{sectionId}[\"{sectionContent}\"]");
        sb.AppendLine($"    style sec_{sectionId} fill:{SectionColor},color:{SectionTextColor},stroke:#333,stroke-width:1px");
        sb.AppendLine($"    class sec_{sectionId} leftAlign");
        sb.AppendLine($"    acc_{sanitizedAcceptanceId} --> sec_{sectionId}");
    }

    private static string SanitizeId(string id)
    {
        return id.Replace("-", "_").Replace(" ", "_").Replace(".", "_");
    }

    /// <summary>
    /// Submission view showing file browser for submission files.
    /// </summary>
    [Display(GroupName = "Files", Order = 4)]
    public static IObservable<UiControl?> Submission(LayoutAreaHost host, RenderingContext _)
    {
        var pricingPath = host.Hub.Address.ToString();
        var pricingId = host.Hub.Address.Id;
        var nodeStream = host.Workspace.GetStream<MeshNode>();

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
    /// </summary>
    [Display(GroupName = "Cards", Order = 0)]
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var pricingPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>();

        return nodeStream.Select(nodes =>
        {
            var node = nodes?.FirstOrDefault();
            var pricing = ExtractPricing(node);

            if (pricing is null)
            {
                var loadingHtml = $@"<a href='/{pricingPath}' style='display:block;text-decoration:none;color:inherit;padding:16px;border:1px solid #e0e0e0;border-radius:8px;background:#fafafa;min-width:280px;max-width:320px;'>
  <em>Loading...</em>
</a>";
                return (UiControl?)Controls.Html(loadingHtml);
            }

            var status = PricingStatus.GetById(pricing.Status);

            var coveragePeriod = pricing.InceptionDate.HasValue && pricing.ExpirationDate.HasValue
                ? $"{pricing.InceptionDate:MMM yyyy} - {pricing.ExpirationDate:MMM yyyy}"
                : "Coverage period not set";

            var html = $@"<a href='/{pricingPath}' style='display:block;text-decoration:none;color:inherit;padding:16px;border:1px solid #e0e0e0;border-radius:8px;background:#fafafa;min-width:280px;max-width:320px;cursor:pointer;'>
  <div style='display:flex;flex-direction:column;gap:8px;'>
    <div style='display:flex;justify-content:space-between;align-items:flex-start;'>
      <div style='font-size:1.1em;font-weight:600;color:#333;'>{System.Net.WebUtility.HtmlEncode(pricing.InsuredName)}</div>
      <div style='background:{GetStatusColor(status.Id)};color:white;padding:2px 8px;border-radius:4px;font-size:0.8em;white-space:nowrap;'>{status.Emoji} {status.Name}</div>
    </div>
    <div style='color:#666;font-size:0.9em;'>{coveragePeriod}</div>
    <div style='display:flex;gap:16px;color:#888;font-size:0.85em;'>
      <span>{pricing.LineOfBusiness ?? "N/A"}</span>
      <span>{pricing.Country ?? "N/A"}</span>
      <span>{pricing.Currency ?? "N/A"}</span>
    </div>
  </div>
</a>";

            return (UiControl?)Controls.Html(html);
        });
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
internal class PropertyRiskDetailsModel
{
    public string? Id { get; set; }
    public int? SourceRow { get; set; }
    public string? LocationName { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public double TsiBuilding { get; set; }
    public double TsiContent { get; set; }
    public double TsiBi { get; set; }
    public string? Currency { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}
